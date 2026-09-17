using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace BangGang;

/// <summary>
/// 聊天消息区：垂直排布气泡，滚动由自己管。
///
/// **不用 <c>AutoScroll</c>**，两条理由都是踩出来的：
///
/// 1. 它给面板挂的是**系统原生**滚动条 —— 灰底槽 + 两端箭头，和本应用另外两处自绘的
///    细圆角滑块（<see cref="ConvListBox"/>、<c>ScrollArea</c>）完全不是一套东西。
/// 2. 更糟的是它把**横向滚动条**也带了出来。老代码写的是
///    <c>AutoScrollMinSize.Width = 客户区宽 − 26</c>，本意是「左右各留 26」，可竖直滚动条
///    一出现就吃掉 ~17px 客户区宽度，那个数于是永远比真实客户区宽 —— 横条常驻。而横条一
///    出现客户区高度又变，和竖条互相触发；侧栏动画每帧都在改对话区宽度，于是整块跟着闪。
///    换句话说，「底部那条横条」和「收展侧栏时闪烁」是同一个根因的两副面孔。
///
/// 现在自己记一个 <see cref="_offset"/>，气泡按「逻辑坐标 − 偏移」摆位，滑块自己画。
///
/// <para>
/// <b>气泡不是控件，是本控件画上去的。</b>（0.9.0 改的，此前每个
/// <see cref="MessageBubble"/> 都是一个真实的子 HWND。）
/// </para>
///
/// <para>
/// 原因只有一个，但它决定了一切：**子窗口不在父控件的双缓冲里**。父面板重画时先铺自己的
/// 底色，子窗口随后才被系统逐个 blit 上去 —— 中间那一瞬就是侧栏动画里用户看到的破损。
/// <see cref="ControlStyles.OptimizedDoubleBuffer"/> 早就设了，可它在子 HWND 面前等于没设。
/// 再加上每一帧给 N 个气泡 <c>SetBounds</c>、N 个窗口逐个上报，就是那份卡顿。
/// </para>
///
/// <para>
/// 现在整块只有这一个绘制者：一次 <c>Invalidate</c>、一次双缓冲合成、一次原子 blit。
/// 于是 <see cref="OnPaint"/> 的那个 flag 才**第一次真正生效**，不必再上
/// <c>WS_EX_COMPOSITED</c>。
/// </para>
///
/// <para>
/// 代价是探针不能再靠 <c>EnumChildWindows</c> 数气泡了。替代信号是 Debug 下的
/// <c>ui-rows.json</c>（见 <see cref="DumpRows"/>）：每次布局落定写一份行几何，
/// 顺带捎上帧耗时。它比枚举子窗口**更稳**（不受动画帧时序影响，没有「读到一半在动」的
/// 问题），而且顺带成了「卡顿真的减轻了」唯一的量化证据 —— 在这之前没有任何探针量得到帧耗时。
/// </para>
///
/// <para>
/// <b>动画期间不重排文字，只挪位置</b>（<see cref="LiveResize"/> / <see cref="SettleLayout"/>）：
/// 重排（<see cref="ReflowRows"/>）要对每一行跑一遍 <c>Markdown.Measure</c>，那是这一整条路上
/// 最贵的一步，而侧栏动画期间**可用宽度每帧都在变**，逐帧重排等于逐帧把全部消息重新折行一遍。
/// 位置跟着走就是了 —— 文字不折行这件事用户看不出来，掉帧他看得出来。
/// </para>
/// </summary>
internal sealed class ChatView : Panel, IThemed, IMessageFilter
{
    /// <summary>气泡到对话区左右边缘的留白。用户气泡靠右、助手气泡靠左，两边共用这一个数。</summary>
    private const int PadL = 26, PadR = 26;

    /// <summary>第一行距上缘、最后一行距下缘的留白。</summary>
    private const int PadTop = 26, PadBottom = 26;

    private const int RowGap = 14;

    /// <summary>气泡**总宽**最多占对话区宽度的这个比例（用户要求 90%）。</summary>
    private const float MaxWidthRatio = 0.90f;

    // ---- 自绘滑块：和 ConvListBox / ScrollArea 同一套几何与配色，三处必须一致 ----
    private const int BarW = 6;
    private const int BarGap = 3;

    /// <summary>滚一格走多少像素。与 <see cref="ConvListBox.ScrollBy"/> 取同一个数。</summary>
    private const int WheelStep = 54;

    private const int WM_MOUSEWHEEL = 0x020A;

    private readonly List<MessageBubble> _rows = new();
    public Conversation? Conv { get; private set; }

    /// <summary>点开了某条消息里的图片（主窗口据此弹出放大浮层）。</summary>
    public event Action<Attachment>? ImagePressed;

    /// <summary>向下滚了多少像素（≥0）。气泡的 y = 逻辑坐标 − 这个数。</summary>
    private int _offset;

    /// <summary>全部内容（含上下留白）的总高，滑块的长度按它算。</summary>
    private int _contentH;

    private bool _hoverBar;
    private bool _dragBar;

    /// <summary>指针当前停在第几行（-1 = 没停在任何一行上）。</summary>
    private int _hot = -1;

    /// <summary>上一次算出来的滑块矩形。滑块挪位时要把**新旧两块一起**作废，
    /// 只重画新位置会在旧位置留一段残影（同 <c>InputPanel.ContentInset</c> 那条）。</summary>
    private Rectangle _barPrev = Rectangle.Empty;

    /// <summary>
    /// 等待动画的时钟。**只在真有气泡在等的时候跑**（见 <see cref="UpdateWaitTimer"/>）。
    ///
    /// 时钟放在这里而不是气泡里：气泡不是控件、没有消息循环，而这个控件本来就是
    /// 唯一知道「现在有哪几条消息」的那一个。
    /// </summary>
    private readonly System.Windows.Forms.Timer _waitTimer = new() { Interval = 110 };

    public ChatView()
    {
        BackColor = Theme.ChatBg;
        _waitTimer.Tick += (_, _) => { foreach (var b in _rows) b.TickWait(); };
        // 特意**不**设 AutoScroll，见类注释。
        //
        // 这三个 flag 在 0.9.0 之前是「设了但没用」的状态（子 HWND 不参与父控件的缓冲）。
        // 气泡改成画上去的之后它们才真的生效 —— 所以别看到「早就有了」就把它们删掉。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle()
    {
        BackColor = Theme.ChatBg;
        foreach (var b in _rows) b.DropCache();   // 气泡的缓存里烘着旧主题的颜色
        Invalidate();
    }

    // ---------------- 内容 ----------------

    public void Load(Conversation? c)
    {
        Conv = c;
        foreach (var b in _rows) b.Dispose();
        _rows.Clear();
        _offset = 0;
        _hot = -1;
        if (c != null)
            foreach (var m in c.Messages) Add(c, m, m.Role == "user");
        ReflowRows(pinBottom: true);   // 换会话 / 恢复历史一律从底部看起
        // 旧气泡连同它们的等待状态一起没了，时钟要跟着熄 —— 重建出来的气泡一律不等。
        UpdateWaitTimer();
    }

    public void AddMessage(ChatMessage m) => AddMessage(m, false);

    /// <summary>
    /// 追加一条消息。<paramref name="waiting"/> = 这条是刚发出去、还没等到模型第一个字的
    /// 助手消息，先给它画上等待动画（见 <see cref="MessageBubble.SetWaiting"/>）。
    /// </summary>
    public void AddMessage(ChatMessage m, bool waiting)
    {
        Add(Conv, m, m.Role == "user");
        if (waiting && _rows.Count > 0) _rows[^1].SetWaiting(true);
        ReflowRows(pinBottom: true);
        UpdateWaitTimer();
    }

    /// <summary>
    /// 某条消息等到（或不再等）第一个字了。
    ///
    /// 找不到那个气泡时**什么都不做**，但计时器还是要重算一次：用户可能刚切走，
    /// 气泡已经跟着 <see cref="Load"/> 重建过了，时钟得跟着熄掉。
    /// </summary>
    public void SetWaiting(ChatMessage m, bool on)
    {
        var b = BubbleFor(m);
        if (b != null && b.SetWaiting(on)) ReflowRows(pinBottom: IsAtBottom());
        UpdateWaitTimer();
    }

    /// <summary>
    /// 有人在等，时钟就走；没人等就停。
    ///
    /// 必须停：这个计时器每 110ms 叫醒一次界面，而「等第一个字」是几秒钟的事，
    /// 一个画完就不动的界面没有理由每秒醒九次。
    /// </summary>
    private void UpdateWaitTimer()
    {
        bool any = false;
        foreach (var b in _rows) if (b.Waiting) { any = true; break; }
        if (any == _waitTimer.Enabled) return;
        if (any) _waitTimer.Start();
        else _waitTimer.Stop();
    }

    private void Add(Conversation? c, ChatMessage m, bool isUser)
    {
        _ = c;
        var b = new MessageBubble(m, isUser);
        // 气泡自己画不出整块对话区，它只知道「我这儿变了」—— 这里把那一句翻译成
        // 「作废我这块」和「重排一遍」两件事，气泡与本控件之间就只剩这两条线。
        b.ImagePressed += a => ImagePressed?.Invoke(a);
        b.Repaint += () => InvalidateRow(b);
        // 气泡长高 / 变矮（点开思考过程）必须重排：位置是本控件排的，下面的气泡不会自己让位。
        // **不能**在一次重排的过程中触发 —— MessageBubble.SetMaxInner 因此特意不报这个事件。
        b.Changed += () => ReflowRows(pinBottom: IsAtBottom());
        _rows.Add(b);
    }

    private void InvalidateRow(MessageBubble b)
    {
        if (IsDisposed || !IsHandleCreated) return;
        // 外扩 2px：气泡的圆角是抗锯齿画出来的，贴边那一圈像素落在外扩出来的半个像素上。
        Invalidate(Rectangle.Inflate(b.Rect, 2, 2));
    }

    /// <summary>
    /// 取某条消息现在**活着**的那个气泡（不在本视图里则返回 null）。
    ///
    /// 流式回复要按消息对象来找气泡、而不是把气泡的引用攥在手里：用户切走再切回来时
    /// <see cref="Load"/> 会把所有气泡销毁重建，攥着的那一个已经是死对象了。
    /// </summary>
    public MessageBubble? BubbleFor(ChatMessage m)
    {
        foreach (var b in _rows)
            if (ReferenceEquals(b.Msg, m)) return b;
        return null;
    }

    // ---------------- 滚动 ----------------

    /// <summary>还能往下滚多少像素。内容装得下时是 0。</summary>
    private int MaxOffset => Math.Max(0, _contentH - ClientSize.Height);

    /// <summary>
    /// 滚到底。
    ///
    /// 与「按需滚」的区别不在能不能滚，而在**什么时候算**：内容变短之后（换了会话、
    /// 窗口拉高）偏移量可能停在比最大值还大的地方，所以这个数一定要在**重新量完高度之后**
    /// 才算得出来，见 <see cref="PlaceRows"/> 的 <c>pinBottom</c>。
    /// </summary>
    public void ScrollBottom() => ReflowRows(pinBottom: true);

    /// <summary>
    /// 当前是不是贴着底部。流式回复每几十毫秒就长一行，如果每次无脑滚到底，
    /// 用户往上翻去看前面的内容时会被一直拽回底部，根本读不了。
    /// </summary>
    public bool IsAtBottom()
    {
        int max = MaxOffset;
        if (max <= 0) return true;
        return _offset >= max - 32;
    }

    /// <summary>滚到某个偏移（会钳在合法范围里）。滚轮与拖动滑块都走它。</summary>
    private void ScrollTo(int offset)
    {
        int want = Math.Clamp(offset, 0, MaxOffset);
        if (want == _offset) return;
        _offset = want;
        // 有人滚过之后，原来那个「指针停在第几行」就作废了：那一行已经挪到别处去了。
        DropHot();
        PlaceRows();
    }

    /// <summary>滚轮用。向上是正、向下是负，与 WinForms 的 delta 同号。</summary>
    public void ScrollBy(int dy) => ScrollTo(_offset + dy);

    /// <summary>
    /// 气泡长高了：重排一次，并且只在用户本来就贴着底部时才跟着滚下去。
    /// 流式回复每收到一小段就调它一次。
    /// </summary>
    public void NotifyRowGrew() => ReflowRows(pinBottom: IsAtBottom());

    /// <summary>外部（改设置、换主题之类）要求按当前宽度重排一次。</summary>
    public void Relayout() => ReflowRows();

    // ---------------- 布局 ----------------

    /// <summary>
    /// **贵的那一趟**：先定宽度、再让每行按新宽度重算自己的高度，最后摆位。
    ///
    /// 宽度必须单独走一趟，因为气泡的宽度是对话区给的（见 <see cref="MessageBubble.SetMaxInner"/>），
    /// 而宽度一变高度就跟着变 —— 一边摆一边量的话，总高会按「上一行的旧宽度」去算，
    /// 滑块的长度和滚动范围就全错了。
    ///
    /// 侧栏动画期间**不要**调这个，调 <see cref="PlaceRows"/>：动画每帧都在改宽度，
    /// 逐帧重排等于逐帧把全部消息重新折行一遍。
    /// </summary>
    /// <param name="pinBottom">
    /// 摆位之前先把偏移顶到最底。**必须在量完 <see cref="_contentH"/> 之后**才顶得准：
    /// 传进来的时候那个值还是上一轮的。
    /// </param>
    private void ReflowRows(bool pinBottom = false)
    {
        if (_rows.Count == 0)
        {
            _contentH = 0;
            _offset = 0;
            InvalidateBar();
            DumpRows();
            return;
        }

        int clientW = Math.Max(100, ClientSize.Width);

        // 气泡总宽 ≤ 对话区宽度的 90%，同时左右各留 PadL / PadR。两个限制谁先到就听谁的 ——
        // 窗口窄到 90% 已经放不下两边留白时，留白优先（不然气泡会顶到滑块上）。
        int maxBubbleW = Math.Max(180, Math.Min((int)(clientW * MaxWidthRatio), clientW - PadL - PadR));
        int inner = Math.Max(80, maxBubbleW - MessageBubble.PadX * 2);

        // SetMaxInner 内部有早退（内容没被上限卡住就什么都不做），动画结束后的这一趟
        // 因此大部分时候是白跑 —— 但那正是它该被调用的时刻，便宜不便宜另说。
        var t0 = Stamp;
        foreach (var b in _rows) b.SetMaxInner(inner);
        RecordLayout(Since(t0));

        PlaceRows(pinBottom);
    }

    /// <summary>
    /// **便宜的那一趟**：只按当前客户区宽度和每行**已经算好的**高度摆位置。
    ///
    /// 侧栏动画每帧走的就是它 —— 几个整数减法，外加一次 <c>Invalidate</c>。
    /// 宽度变了但文字没重排：右对齐的气泡该滑到哪儿就滑到哪儿，位置是跟手的；
    /// 只有折行还是按旧宽度来的，动画结束由 <see cref="SettleLayout"/> 补一次。
    /// </summary>
    private void PlaceRows(bool pinBottom = false)
    {
        int clientW = Math.Max(100, ClientSize.Width);

        var t0 = Stamp;
        int y = PadTop;
        foreach (var b in _rows) y += b.Height + RowGap;
        _contentH = y - RowGap + PadBottom;

        _offset = pinBottom ? MaxOffset : Math.Clamp(_offset, 0, MaxOffset);

        y = PadTop - _offset;
        foreach (var b in _rows)
        {
            // 用户气泡靠右。窗口窄到连 PadL 都保不住时钳在 PadL 上，宁可右边溢出一点点，
            // 也不让气泡左缘越过留白 —— 越过去就是压在滑块底下。
            int left = b.IsUser ? clientW - PadR - b.Width : PadL;
            b.Location = new Point(Math.Max(PadL, left), y);
            y += b.Height + RowGap;
        }
        RecordLayout(Since(t0));

        // 位置全变了 —— 之前记的「指针停在第几行」不再成立，而且要整块重画。
        DropHot();
        InvalidateBar();
        Invalidate();
        DumpRows();
    }

    /// <summary>把「指针停在第几行」清掉，并让它把悬浮态还回去（否则那一行会留着一块高亮）。</summary>
    private void DropHot()
    {
        if (_hot < 0) return;
        _rows[_hot].MouseLeave();
        _hot = -1;
    }

    // ---------------- 动画期：位置跟手、文字不重排 ----------------

    /// <summary>
    /// 侧栏正在做收展动画。为真时 <see cref="OnResize"/> 只 <see cref="PlaceRows"/>，
    /// 不 <see cref="ReflowRows"/>。
    ///
    /// 做成显式开关、由 <c>MainForm.ApplyLayout(liveResize)</c> 一路传进来，而不是在这里
    /// 「猜」现在是不是在动画中：猜的那种写法要么去问定时器（多一条反向依赖），
    /// 要么按时间窗判（动画时长一改就失准）。
    /// </summary>
    public bool LiveResize
    {
        get => _live;
        set
        {
            if (_live == value) return;
            _live = value;
            if (value)
            {
                // 一个新的动画开始：上一轮的两个计时桶一起清零，这样探针读到的
                // animMs / 平均帧耗时**只反映这一次动画**，不混进开机以来任何别的帧。
                _perfAnim.Reset();
                _animClock.Restart();
            }
            else StopAnimClock();
        }
    }

    private bool _live;

    private readonly System.Diagnostics.Stopwatch _animClock = new();

    private void StopAnimClock()
    {
        if (!_animClock.IsRunning) return;
        _animClock.Stop();
        _perfAnim.WallMs = _animClock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 动画收尾：先补上那一趟被跳过的重排，再回到「实时重排」的常态。
    /// 由 <c>MainForm.SideTick</c> 的最后一次 tick 调用。
    ///
    /// 顺序不能反：先 <see cref="ReflowRows"/> 再关开关的话，中间那一趟重排会按
    /// 「还在动画中」的规则只摆位 —— 文字就永远停在动画中途的折行上了。
    /// </summary>
    public void SettleLayout()
    {
        ReflowRows(pinBottom: IsAtBottom());
        LiveResize = false;      // 置假会把动画时长记下来（见 StopAnimClock）
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_live) PlaceRows();
        else ReflowRows();
    }

    // ---------------- 命中测试 ----------------

    /// <summary>指针落在第几行上（-1 = 空白处）。</summary>
    private int RowAt(Point p)
    {
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (_rows[i].Rect.Contains(p)) return i;
        return -1;
    }

    /// <summary>把对话区坐标换算成某一行的局部坐标（气泡自己按 (0,0) 起算）。</summary>
    private Point Local(int row, Point p)
    {
        var r = _rows[row].Location;
        return new Point(p.X - r.X, p.Y - r.Y);
    }

    // ---------------- 鼠标 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var bar = BarRect();
        bool over = !bar.IsEmpty && Rectangle.Inflate(bar, 4, 4).Contains(e.Location);
        if (over != _hoverBar) { _hoverBar = over; Invalidate(BarArea()); }

        if (_dragBar && !bar.IsEmpty)
        {
            int trackH = Height - 8;
            int y = Math.Clamp(e.Y - bar.Height / 2 - 4, 0, Math.Max(1, trackH - bar.Height));
            int span = trackH - bar.Height;
            ScrollTo(span <= 0 ? 0 : (int)Math.Round(y / (double)span * MaxOffset));
            return;
        }

        // 换了一行：旧的那一行要把悬浮态还回去（不然它的缩略图会一直压着那层暗色），
        // 新旧两块矩形一起作废 —— 只作废新的，旧位置会留着上一次的高亮。
        int hit = RowAt(e.Location);
        if (hit != _hot)
        {
            if (_hot >= 0) { _rows[_hot].MouseLeave(); InvalidateRow(_rows[_hot]); }
            _hot = hit;
            if (hit >= 0) InvalidateRow(_rows[hit]);
        }
        if (_hot >= 0) _rows[_hot].MouseMove(Local(_hot, e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBar)
        {
            _hoverBar = false;
            Invalidate(BarArea());
        }
        // 指针从气泡挪到滑块上时 OnMouseMove 还会来，那时 hit 已经是 -1 了；
        // 直接从气泡钻出窗口才会走到这里。
        if (_hot >= 0)
        {
            int old = _hot;
            _hot = -1;
            _rows[old].MouseLeave();
            InvalidateRow(_rows[old]);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var bar = BarRect();
        if (e.Button == MouseButtons.Left && !bar.IsEmpty && bar.Contains(e.Location)) _dragBar = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragBar = false;
        // 只处理左键：右键松手也往气泡里送的话，右键菜单会顺带把图片点开。
        if (e.Button != MouseButtons.Left) return;
        int hit = RowAt(e.Location);
        if (hit >= 0) _rows[hit].MouseUp(Local(hit, e.Location));
    }

    // ---------------- 滚轮 ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 滚轮走消息过滤器而不是 OnMouseWheel：WM_MOUSEWHEEL 是**投递到线程消息队列**的，
        // 过滤器在消息出队时就看得见它，落在哪个 HWND 上都一样 —— 于是不必赌
        // 「压在气泡上时会不会冒泡给父面板」。同 InputPanel.OnHandleCreated。
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// 光标压在对话区上时把滚轮吃掉。用消息里的**屏幕坐标**而不是 <c>Cursor.Position</c>：
    /// 后者要等这条消息被处理时才读，UI 线程一忙就读到已经走掉的鼠标位置。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        // 看图浮层开着的时候滚轮归它缩放，别拿来滚消息区（见 ImageViewer.AnyOpen）。
        if (ImageViewer.AnyOpen) return false;
        if (!Visible || !IsHandleCreated || MaxOffset <= 0) return false;

        long lp = m.LParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        if (!ClientRectangle.Contains(PointToClient(screen))) return false;

        int notches = (short)((long)m.WParam >> 16) / 120;
        if (notches == 0) return false;
        ScrollBy(-notches * WheelStep);
        return true;
    }

    // ---------------- 绘制 ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var t0 = Stamp;
        base.OnPaint(e);
        var g = e.Graphics;

        var clip = e.ClipRectangle;
        foreach (var b in _rows)
        {
            // 脏区之外的行直接跳过。滑块拖动时脏区就那么大，不必把几百条消息全贴一遍。
            if (!b.Rect.IntersectsWith(clip)) continue;
            // **不是** g.TranslateTransform + 原地画：GDI 文本不认 Graphics.Transform，
            // 挪了也白挪。气泡自己画进一张位图，这里 1:1 贴过来（见 MessageBubble.Paint）。
            b.Paint(g, b.Location);
        }

        if (Conv != null && _rows.Count == 0)
        {
            var r = ClientRectangle;
            using var f = Theme.UI(12f);
            string s = "开始对话吧 —— 在下方输入文字，或拖入 / 粘贴文件与图片";
            var sz = g.MeasureString(s, f);
            using var b = new SolidBrush(Theme.TextMuted);
            g.DrawString(s, f, b, (r.Width - sz.Width) / 2, (r.Height - sz.Height) / 2);
        }

        var bar = BarRect();
        if (!bar.IsEmpty)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = _hoverBar || _dragBar ? 0.40f : 0.26f;
            RP.Fill(g, bar, BarW / 2, Theme.Mix(Theme.ChatBg, Theme.TextMain, k));
        }

        RecordPaint(Since(t0));
        DumpRows();
    }

    // ---------------- 自绘滑块 ----------------

    /// <summary>滑块矩形。内容是纵向滚动的，所以只有竖的这一条。</summary>
    private Rectangle BarRect()
    {
        if (MaxOffset <= 0 || Height <= 0) return Rectangle.Empty;
        int trackH = Height - 8;
        int h = Math.Max(32, (int)Math.Round(trackH * (Height / (double)_contentH)));
        int y = 4 + (int)Math.Round((trackH - h) * (_offset / (double)MaxOffset));
        return new Rectangle(Width - BarW - BarGap, y, BarW, h);
    }

    /// <summary>滑块所在的那一条带（含左右各 4px 的富余，用来命中与作废）。</summary>
    private Rectangle BarArea() => new(Math.Max(0, Width - BarW - BarGap - 4), 0, BarW + 8, Height);

    /// <summary>
    /// 滑块的位置 / 大小可能变了 —— 新旧两块**一起**作废。
    /// 只作废新位置的话，旧位置会留着一截旧滑块（和 <c>InputPanel.ContentInset</c> 同一个坑）。
    /// </summary>
    private void InvalidateBar()
    {
        var now = BarRect();
        if (now == _barPrev) return;
        Rectangle dirty = _barPrev.IsEmpty ? now
                        : now.IsEmpty ? _barPrev
                        : Rectangle.Union(_barPrev, now);
        _barPrev = now;
        if (!dirty.IsEmpty) Invalidate(Rectangle.Inflate(dirty, 3, 3));
    }

    // ---------------- 行几何通道（Debug） ----------------

    /// <summary>
    /// 帧耗时的两个桶。分别记「动画中」和「平时」—— 分开记，探针读的时候就不必掐秒表去猜
    /// 自己读到的那几张帧是动画里的还是动画外的。
    /// </summary>
    private sealed class Perf
    {
        public int Frames, Layouts;
        public double PaintMs, LayoutMs, PaintMax, LayoutMax, WallMs;

        public void Reset()
        {
            Frames = Layouts = 0;
            PaintMs = LayoutMs = PaintMax = LayoutMax = WallMs = 0;
        }
    }

    private readonly Perf _perfAnim = new();
    private readonly Perf _perfLive = new();

    /// <summary>动画中记进 <see cref="_perfAnim"/>、平时记进 <see cref="_perfLive"/>。</summary>
    private Perf Bucket => _live ? _perfAnim : _perfLive;

    private void RecordPaint(double ms)
    {
        var p = Bucket;
        p.PaintMs += ms;
        p.Frames++;
        if (ms > p.PaintMax) p.PaintMax = ms;
    }

    private void RecordLayout(double ms)
    {
        var p = Bucket;
        p.LayoutMs += ms;
        p.Layouts++;
        if (ms > p.LayoutMax) p.LayoutMax = ms;
    }

    /// <summary>取一个时间戳。用 <see cref="System.Diagnostics.Stopwatch"/> 的原始计数而不是
    /// <c>DateTime</c>：后者受系统时钟调整影响，量帧耗时是会量出负数的。</summary>
    private long Stamp => System.Diagnostics.Stopwatch.GetTimestamp();

    private static double Since(long t0) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static readonly string RowsPath = Path.Combine(AppContext.BaseDirectory, "ui-rows.json");

    /// <summary>
    /// 把当前行几何写进 <c>ui-rows.json</c>，给探针读。
    ///
    /// 气泡不再是子窗口，<c>EnumChildWindows</c> 从此数不到它们，而**失明是安静的** ——
    /// 量到 0 行既可以读成「程序坏了」也可以读成「确实没有气泡」。这个文件就是替代信号。
    ///
    /// 只在 <see cref="CaptureGuard.Disabled"/>（即 Debug 且设了 <c>BANGGANG_SHOW_IN_CAPTURE=1</c>）
    /// 时写。写失败一律吞掉：这是一条调试通道，它断掉不能让程序跟着断。
    ///
    /// <c>animMs</c> 是**上一次收展动画的墙钟时长**，也是这次改动唯一真正的成绩单：
    /// 它把「逐帧重排 + N 个子窗口各自重画」整个算在里面。
    /// </summary>
    private void DumpRows()
    {
        if (!CaptureGuard.Disabled) return;
        try
        {
            var sb = new StringBuilder(768);
            // 本控件客户区原点相对**顶层窗口**左上角的偏移。探针拿它加上窗口此刻的
            // GetWindowRect，就得到行几何的屏幕坐标，去做点击 / 取像素，而不必自己去猜
            // 哪个子窗口是消息区 —— 消息区本身没有可辨认的特征。
            //
            // 这里刻意**不写绝对屏幕坐标**（RectangleToScreen）。窗口会在布局之后继续动
            // ——启动时 CenterScreen 落位、用户拖窗口、最大化——而这个文件不会因此重写，
            // 于是绝对坐标天生会过期，且过期的方式最坏：数字看着完全合理。实测这条路上
            // RectangleToScreen 给的就是 CenterScreen 落位**之前**的位置（写出来是
            // (8,-7)+偏移 = 264,79，而同一刻窗口的 GetWindowRect 是 360,120），探针照它
            // 点击会全程落在别的地方，最后报成程序的问题。
            // 逐级累加 Left/Top 是纯布局量，跟窗口此刻在屏幕上的哪儿无关，所以不会过期；
            // 换个窗口位置，探针重新锚一次就行（tools\_ui.ps1 的 Get-UiRows）。
            // 顶层窗口无边框，客户区原点就是窗口矩形原点，探针那两个坐标系因此是同一个。
            int offX = 0, offY = 0;
            var top = TopLevelControl;
            for (var c = (Control?)this; c != null && c != top; c = c.Parent)
            {
                offX += c.Left;
                offY += c.Top;
            }
            sb.Append("{\"viewOffX\":").Append(offX)
              .Append(",\"viewOffY\":").Append(offY)
              .Append(",\"clientW\":").Append(ClientSize.Width)
              .Append(",\"clientH\":").Append(ClientSize.Height)
              .Append(",\"offset\":").Append(_offset)
              .Append(",\"contentH\":").Append(_contentH)
              .Append(",\"live\":").Append(_live ? "true" : "false")
              .Append(",\"animMs\":").Append(Num(_perfAnim.WallMs))
              .Append(",\"animFrames\":").Append(_perfAnim.Frames)
              .Append(",\"animPaintMs\":").Append(Num(Avg(_perfAnim.PaintMs, _perfAnim.Frames)))
              .Append(",\"animPaintMaxMs\":").Append(Num(_perfAnim.PaintMax))
              .Append(",\"animLayoutMs\":").Append(Num(Avg(_perfAnim.LayoutMs, _perfAnim.Layouts)))
              .Append(",\"animLayoutMaxMs\":").Append(Num(_perfAnim.LayoutMax))
              .Append(",\"idleFrames\":").Append(_perfLive.Frames)
              .Append(",\"idlePaintMs\":").Append(Num(Avg(_perfLive.PaintMs, _perfLive.Frames)))
              .Append(",\"idlePaintMaxMs\":").Append(Num(_perfLive.PaintMax))
              .Append(",\"idleLayoutMs\":").Append(Num(Avg(_perfLive.LayoutMs, _perfLive.Layouts)))
              .Append(",\"rows\":[");
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"role\":\"").Append(r.IsUser ? "user" : "assistant")
                  .Append("\",\"x\":").Append(r.Location.X)
                  .Append(",\"y\":").Append(r.Location.Y)
                  .Append(",\"w\":").Append(r.Width)
                  .Append(",\"h\":").Append(r.Height)
                  .Append('}');
            }
            sb.Append("]}");
            File.WriteAllText(RowsPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* 调试通道，断掉不影响程序 */ }
    }

    private static double Avg(double sum, int n) => n <= 0 ? 0 : sum / n;

    /// <summary>
    /// 数字一律按不变文化格式化。跟着当前文化走的话，某些区域设置下小数点会写成逗号
    /// （<c>2,4</c>）—— JSON 里那是两个字段，探针会读到一棵完全不同的树。
    /// </summary>
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _waitTimer.Dispose();
            foreach (var b in _rows) b.Dispose();
            _rows.Clear();
        }
        base.Dispose(disposing);
    }
}
