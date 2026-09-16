using System.Drawing.Drawing2D;

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
/// 现在自己记一个 <see cref="_offset"/>，子控件按「逻辑坐标 − 偏移」摆位，滑块自己画。
///
/// 气泡仍然是**真实的子控件**（不是由本控件画上去的）：探针靠枚举子窗口来数气泡、量气泡的
/// 尺寸和位置，这条不变量不能动 —— 把气泡改成「画出来的行」会让一批探针一起失明。
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

    /// <summary>向下滚了多少像素（≥0）。子控件的 y = 逻辑坐标 − 这个数。</summary>
    private int _offset;

    /// <summary>全部内容（含上下留白）的总高，滑块的长度按它算。</summary>
    private int _contentH;

    private bool _hoverBar;
    private bool _dragBar;

    /// <summary>上一次算出来的滑块矩形。滑块挪位时要把**新旧两块一起**作废，
    /// 只重画新位置会在旧位置留一段残影（同 <c>InputPanel.ContentInset</c> 那条）。</summary>
    private Rectangle _barPrev = Rectangle.Empty;

    public ChatView()
    {
        BackColor = Theme.ChatBg;
        // 特意**不**设 AutoScroll，见类注释。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle()
    {
        BackColor = Theme.ChatBg;
        Invalidate();
    }

    // ---------------- 内容 ----------------

    public void Load(Conversation? c)
    {
        Conv = c;
        SuspendLayout();
        foreach (var b in _rows) { b.Dispose(); }
        _rows.Clear();
        Controls.Clear();
        _offset = 0;
        if (c != null)
        {
            foreach (var m in c.Messages)
            {
                var b = new MessageBubble(m, m.Role == "user");
                b.ImagePressed += a => ImagePressed?.Invoke(a);
                b.HeightChanged += NotifyRowGrew;
                _rows.Add(b);
                Controls.Add(b);
            }
        }
        ResumeLayout();
        LayoutRows(pinBottom: true);   // 换会话 / 恢复历史一律从底部看起
    }

    public void Relayout() => LayoutRows();

    public void AddMessage(ChatMessage m)
    {
        var b = new MessageBubble(m, m.Role == "user");
        b.ImagePressed += a => ImagePressed?.Invoke(a);
        b.HeightChanged += NotifyRowGrew;
        _rows.Add(b);
        Controls.Add(b);
        LayoutRows(pinBottom: true);
        Invalidate();
    }

    /// <summary>
    /// 取某条消息现在**活着**的那个气泡（不在本视图里则返回 null）。
    ///
    /// 流式回复要按消息对象来找气泡、而不是把气泡的引用攥在手里：用户切走再切回来时
    /// <see cref="Load"/> 会把所有气泡销毁重建，攥着的那一个已经是死控件了 ——
    /// 往它身上写东西要么抛异常，要么静默什么都不显示（回复「不动」了，但历史里其实有）。
    /// </summary>
    public MessageBubble? BubbleFor(ChatMessage m)
    {
        foreach (var b in _rows)
            if (ReferenceEquals(b.Msg, m) && !b.IsDisposed) return b;
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
    /// 才算得出来，见 <see cref="LayoutRows"/> 的 <c>pinBottom</c>。
    /// </summary>
    public void ScrollBottom() => LayoutRows(pinBottom: true);

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
        LayoutRows();
    }

    /// <summary>滚轮用。向上是正、向下是负，与 WinForms 的 delta 同号。</summary>
    public void ScrollBy(int dy) => ScrollTo(_offset + dy);

    /// <summary>气泡长高了：重排一次，并且只在用户本来就贴着底部时才跟着滚下去。</summary>
    public void NotifyRowGrew() => LayoutRows(pinBottom: IsAtBottom());

    // ---------------- 布局 ----------------

    /// <summary>
    /// 重排。分两趟：**先定宽度、再算总高、最后摆位**。
    ///
    /// 宽度必须单独走一趟，因为气泡的宽度是对话区给的（见 <see cref="MessageBubble.SetMaxInner"/>），
    /// 而宽度一变高度就跟着变 —— 一边摆一边量的话，总高会按「上一行的旧宽度」去算，
    /// 滑块的长度和滚动范围就全错了。
    /// </summary>
    /// <param name="pinBottom">
    /// 摆位之前先把偏移顶到最底。**必须在量完 <see cref="_contentH"/> 之后**才顶得准：
    /// 传进来的时候那个值还是上一轮的。
    /// </param>
    private void LayoutRows(bool pinBottom = false)
    {
        if (_rows.Count == 0)
        {
            _contentH = 0;
            _offset = 0;
            InvalidateBar();
            return;
        }

        int clientW = Math.Max(100, ClientSize.Width);

        // 气泡总宽 ≤ 对话区宽度的 90%，同时左右各留 PadL / PadR。两个限制谁先到就听谁的 ——
        // 窗口窄到 90% 已经放不下两边留白时，留白优先（不然气泡会顶到滑块上）。
        int maxBubbleW = Math.Max(180, Math.Min((int)(clientW * MaxWidthRatio), clientW - PadL - PadR));
        int inner = Math.Max(80, maxBubbleW - MessageBubble.PadX * 2);

        foreach (var b in _rows) b.SetMaxInner(inner);

        int y = PadTop;
        foreach (var b in _rows) y += b.Height + RowGap;
        _contentH = y - RowGap + PadBottom;

        _offset = pinBottom ? MaxOffset : Math.Clamp(_offset, 0, MaxOffset);

        y = PadTop - _offset;
        foreach (var b in _rows)
        {
            int left = b.IsUser ? clientW - PadR - b.Width : PadL;
            b.Location = new Point(Math.Max(PadL, left), y);
            y += b.Height + RowGap;
        }
        InvalidateBar();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutRows();
    }

    // ---------------- 滚动条 ----------------

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
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBar)
        {
            _hoverBar = false;
            Invalidate(BarArea());
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
    }

    // ---------------- 滚轮 ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 滚轮走消息过滤器而不是 OnMouseWheel：WM_MOUSEWHEEL 是**投递到线程消息队列**的，
        // 过滤器在消息出队时就看得见它，落在哪个 HWND 上都一样 —— 于是不必赌
        // 「压在气泡上时，气泡那个非可选择控件会不会把这条消息冒泡给父面板」。
        // 同 InputPanel.OnHandleCreated。
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
        base.OnPaint(e);

        if (Conv != null && _rows.Count == 0)
        {
            var g = e.Graphics;
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
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = _hoverBar || _dragBar ? 0.40f : 0.26f;
            RP.Fill(g, bar, BarW / 2, Theme.Mix(Theme.ChatBg, Theme.TextMain, k));
        }
    }
}
