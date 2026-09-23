using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>消息输入区：附件区 + 一张圆角卡片（文本框 + 底部工具行）。</summary>
internal sealed class InputPanel : Panel, IMessageFilter
{
    private readonly DraftStrip _draft;
    private readonly TextBox _box;
    private readonly Label _hint;
    private readonly ContextMeter _meter;
    private readonly HintText _ph;
    private readonly IconButton _send, _attach;
    public List<Attachment> Draft { get; } = new();
    public event Action? SendRequested;

    /// <summary>模型正在回复时用户按了「暂停」。</summary>
    public event Action? StopRequested;

    /// <summary>
    /// 面板自身的高度变了（附件区出现 / 消失），要重新占地方了。
    /// <c>MainForm.ApplyLayout</c> 接这个事件重摆 —— 见 <see cref="PreferredHeight"/>。
    /// </summary>
    public event Action? LayoutChanged;

    /// <summary>要跟用户说一句（比如拒收了一个文件）。接到顶栏那条 3 秒的状态提示上。</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// 输入区面板自身的高度。**没有附件时的值**；有附件时还要再加一行
    /// <see cref="DraftStrip.RowH"/>，见 <see cref="PreferredHeight"/>。
    /// </summary>
    public const int PanelH = 150;

    /// <summary>
    /// 面板此刻该占多高：文本框那三行的份额是固定的，附件区是**在卡片里另加一行**，
    /// 而不是从文本框身上抠。
    ///
    /// 这一点是这个需求里最容易做错的地方：卡片高度是写死的（<see cref="PanelH"/>），
    /// 附件区一进来就把文本框挤扁，三行变一行 —— 用户刚提的「不足三行要完整显示、
    /// 超过三行才滚」在加了附件的瞬间就废了，而且他会以为是自己拖了文件才坏的。
    /// 所以卡片内部**不动**，让整块输入区往上长，长出来的正好是附件那一行。
    /// </summary>
    public int PreferredHeight => PanelH + (Draft.Count > 0 ? DraftStrip.RowH : 0);

    // ---------- 卡片几何 ----------

    private const int CardMarginX = 16;       // 卡片到窗口左右缘
    private const int CardMarginTop = 8;
    private const int CardMarginBottom = 10;
    private const int CardRadius = 10;        // 比参考产品小一圈（需求里点名要小）
    private const int CardPadTop = 11;        // 文本框离卡片上缘
    private const int BottomRowH = 38;        // 底部工具行占的高度
    private const int BtnInset = 9;           // 工具行按钮离卡片左右边框
    private const int BtnSize = 28;

    /// <summary>
    /// 卡片下缘那圈描边占掉的带宽，工具行里的控件要让开它。
    ///
    /// 底部工具行的提示文字是**不透明的 Label**（必须是 Static，见下面 <c>_hint</c> 的注释），
    /// 而行高是从 <c>card.Bottom</c> 往上量的 —— 矩形一直接到底，就会用填充色把卡片的下边框
    /// 整段刷平，只在两个圆角处剩两小截。用户报的「提示信息遮挡了输入框底部边框」就是它。
    /// 描边画在 <c>card.Bottom</c> 内缩 1px 处、笔宽 1~1.6px，抗锯齿还会往外糊半像素，
    /// 所以让开 4px。
    /// </summary>
    private const int CardEdgeBand = 4;

    // ---------- 文本框：最多三行，超出部分走右侧那条自绘滑条 ----------

    /// <summary>文本框最多显示的行数。超出的部分靠 <see cref="PaintBar"/> 那条滑条滚。</summary>
    private const int MaxLines = 3;

    /// <summary>
    /// 滑条：滑块宽 / 离卡片右缘 / 滑块最短长度 / 横向额外抓取范围。
    ///
    /// 位置落在卡片给文本框留的右侧内边距里（<see cref="CardPadX"/> 有 14px，滑条只吃掉靠外的
    /// 10px），**刻意不与文字重叠**：滑条一旦占掉文本框的宽度，它的出现就会改变换行、
    /// 从而改变行数，于是「行数够不够触发滑条」自己把自己推翻，一闪一闪。
    /// </summary>
    private const int BarW = 6;
    private const int BarRight = 4;
    private const int BarMinThumb = 24;
    private const int BarGrab = 4;

    /// <summary>
    /// 卡片内左右留白。**写成由 <see cref="CardRadius"/> 推出、不给独立数字**，是因为它有硬下限：
    /// 必须不小于半径。
    ///
    /// 输入框是原生 EDIT，用不透明的 <c>Theme.InputBg</c> 铺满自己的客户区（它不会透明），
    /// 只要伸进卡片的圆角区，就会把那圈圆弧盖成方角。留白 ≥ 半径时编辑框的左边缘整个落在
    /// 卡片的直边段上，圆角永远够不着 —— 这是「卡片比文本框宽多少」唯一的约束，
    /// 写成派生值就不会有人调半径时忘了跟着调它。
    /// </summary>
    private const int CardPadX = CardRadius + 4;

    private int _contentInset;
    private bool _focused;
    private int _hintW = -1;
    private int _meterW = -1;
    private int _lineH = -1;
    private Font? _lineHFont;   // 量 _lineH 时用的那个 Font 实例，换字体就得重量

    // 滑条状态。几何在 LayoutCard 里算，行数 / 首行每次都要现读 EDIT（见 UpdateBar）。
    private Rectangle _barRect;
    private int _barTotal = 1;
    private int _barVis = MaxLines;
    private int _barFirst;
    private bool _barVisible;
    private bool _barHot;
    private bool _barDrag;
    private int _barGrab;      // 按下时鼠标相对滑块上边缘的偏移，拖动时保持这个手感

    /// <summary>
    /// 内容左边要为左侧栏让出多少像素。面板本身铺满整窗、**不随侧栏动画移动**
    /// （见 <c>MainForm.ApplyLayout</c>），收窄全靠这个内缩。
    ///
    /// 之所以不让面板自己缩：面板一移动，钉在它右缘的回形针 / 发送就被父控件整棵子树一起
    /// 搬走，而 WinForms 要等 <c>OnResize</c> 才把这两个孩子摆回原位 —— 中间那一小段
    /// 是 DWM 能合成出来的，表现成「收起侧栏时右下角图标左右抖一下」。
    /// 这里只动叶子：卡片左缘和文本框跟着内缩走，两个按钮钉在卡片右缘，谁都不会拖着别人。
    /// </summary>
    public int ContentInset
    {
        get => _contentInset;
        set
        {
            if (_contentInset == value) return;
            var before = CardRect();
            _contentInset = value;
            var after = CardRect();
            LayoutCard();

            // 卡片是自己画的，又跟着内缩走，所以**旧位置也得由自己作废**。
            //
            // 不作废会怎样：侧栏动画期间子控件每帧都被重新摆位，系统于是只把「子控件腾出来的
            // 那几条带」标成脏区，面板的 WM_PAINT 就被裁在那几条带里 —— 卡片只在带子里重画，
            // 而带子外面留着上一帧、上上帧的卡片描边。收起 / 展开一次，卡片左缘就攒下一串
            // 套在一起的圆角弧线（用户看到的「输入框左侧破损」），而且动画停下后不会自己消失。
            // 把新旧两个卡片矩形并起来作废，每帧画的就是整张卡片，弧线没有落脚的地方。
            var dirty = Rectangle.Union(before, after);
            dirty.Inflate(2, 2);      // 描边带抗锯齿，会往外糊半像素
            Invalidate(dirty);
        }
    }

    /// <summary>
    /// 输入区里的文字。和 <see cref="SearchField.Text"/> 一样，是 <see cref="Control.Text"/>
    /// 的**重写**而不是新加一个同名的：基类那条路上的调用方看到的应当是用户真正敲进去的字。
    ///
    /// <c>[AllowNull]</c> 是为了对上基类 setter 的参数标注（<c>[AllowNull] string?</c>），
    /// 不标就是 CS8765；文本仍可能是 null，所以下面照旧兜一手。
    /// </summary>
    [AllowNull]
    public override string Text { get => _box.Text; set => _box.Text = value ?? ""; }
    public bool HasContent => _box.Text.Trim().Length > 0 || Draft.Count > 0;

    private bool _busy;

    /// <summary>
    /// 模型正在回复。为真时发送键变成「暂停」，并且**无论有没有草稿都可点**
    /// （它已经不是「发送」了，拿「没内容」去禁用它只会把用户困住）。
    /// </summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            UpdateSendState();
        }
    }

    public InputPanel()
    {
        BackColor = Theme.ChatBg;
        // 自绘的控件一律开 ResizeRedraw，理由与「不这么做会怎样」见 MainForm.WelcomeView。
        // 这里画的是整张卡片，缩放后不重画就会留着上一个尺寸的圆角和边框。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Height = PanelH;

        // 附件区。也在卡片里，所以底色用卡片的填充色而不是面板的。
        // 它是一整块自绘的条（见 DraftStrip 里那段「为什么不是一个附件一个控件」）。
        _draft = new DraftStrip();
        _draft.RemoveClicked += Remove;
        _draft.Visible = false;
        Controls.Add(_draft);

        // 文本框不用 Dock = Fill：卡片是画出来的、不是控件，Fill 会铺满整个面板把卡片边框盖掉。
        // 显式摆位也顺手绕开了「Dock = Fill 的兄弟吃掉父控件客户区」那个老坑。
        _box = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.None,
            AutoSize = false,
            Font = Theme.UI(12.5f),
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextMain,
            // 不用 ScrollBars.Vertical：那样会在卡片里常驻一条系统灰滚动条（而且 EDIT 一有滚动条
            // 就**永远**画着它，哪怕只有一行字），和卡片不是一套配色，一眼就能看出是贴上去的。
            // 改成滚动能力照旧、滑条自己画：滚轮走 PreFilterMessage -> EM_LINESCROLL，
            // 视觉那一条见 PaintBar。
            ScrollBars = ScrollBars.None,
            WordWrap = true,
            AcceptsReturn = true,
            AcceptsTab = false,
        };
        _box.KeyDown += OnBoxKeyDown;
        _box.KeyUp += (_, _) => UpdateBar();      // 方向键 / Home / End 会让 EDIT 自己滚
        _box.MouseUp += (_, _) => UpdateBar();    // 点一下放光标同理
        _box.TextChanged += (_, _) => { UpdatePh(); UpdateSendState(); UpdateBar(); };
        _box.Enter += (_, _) => { _focused = true; Invalidate(); };
        _box.Leave += (_, _) => { _focused = false; Invalidate(); };
        // 句柄重建（字体 / DPI 变化）会把 EDIT 的边距打回默认，所以挂在事件上重钉一次。
        _box.HandleCreated += (_, _) => Ui.PinEditTextLeft(_box);
        Controls.Add(_box);
        Ui.PinEditTextLeft(_box);

        // 占位层用 HintText 而不是 Label：Label 走 TextRenderer 时自带字体的 glyph overhang
        // 内边距，起点和 EDIT 对不齐（见 Ui.HintText 的注释）。配合 Ui.PinEditTextLeft 把 EDIT
        // 自己的左边距清零，两边共用同一个起点，**矩形也共用** —— 于是换字号时不会各调各的。
        _ph = new HintText
        {
            Font = _box.Font,               // 同一个 Font 实例，字号不可能再飘
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextMuted,
            Hint = "发消息给帮帮…",
        };
        _ph.MouseDown += (_, _) => { _box.Focus(); };
        Controls.Add(_ph);
        _ph.BringToFront();
        _box.Resize += (_, _) => UpdatePh();

        // 底部工具行的提示文字。
        //
        // **这里从「必须是一个 Label」变成了「Label + 一个自绘控件并存」**，说清楚为什么：
        // 原来那句「必须是 Label（Static），别换成自绘控件」真正依赖的是下面 LayoutCard 里
        // 讲的两条性质 —— **宽度是常量**、**动画里只平移不变尺寸**。这两条与「谁画」无关，
        // 换成自绘控件只要照做就同样成立。
        //
        // 之所以不直接把 Label 换掉：它承载的那串快捷键提示是现成且验证过的
        // （AutoEllipsis / MiddleCenter 全省事），而这一行现在有**两种内容**
        // —— 还没开口时显示快捷键、开口之后显示上下文仪表。两个控件共用
        // LayoutCard 算出来的同一个矩形、同一时刻只有一个 Visible，各干各的那一件。
        _hint = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            Font = Theme.UI(9.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Theme.InputBg,
            Text = "Enter 发送 · Shift+Enter 换行 · 可拖入/粘贴文件与图片",
        };
        Controls.Add(_hint);

        // 上下文仪表：和上面的提示文字占同一个矩形，默认不可见（还没开口时显示提示）。
        _meter = new ContextMeter { Visible = false };
        Controls.Add(_meter);

        // 左：添加附件。平时不画底衬，悬浮才浮出一个圆 —— 参考产品里那个「+」就是这个手感。
        _attach = new IconButton(IconButton.Kind.Plus)
        {
            Skin = IconButton.Look.Ghost,
            Size = new Size(BtnSize, BtnSize),
            BackdropSource = () => Theme.InputBg,
        };
        _attach.Click += (_, _) => PickFiles();
        Controls.Add(_attach);

        // 右：一枚实心圆，三种状态一个控件：
        //   不可点击  —— 没内容也不忙：中性灰 + Clickable = false（连悬浮都不亮）
        //   可点击    —— 有内容：强调色 + 上箭头
        //   暂停回复  —— 模型正在回复：强调色 + 方块，点它冒 StopRequested
        _send = new IconButton(IconButton.Kind.Send)
        {
            Skin = IconButton.Look.Solid,
            Size = new Size(BtnSize, BtnSize),
            BackdropSource = () => Theme.InputBg,
            Active = false,
            Clickable = false,
        };
        _send.Click += (_, _) =>
        {
            if (_busy) { StopRequested?.Invoke(); return; }
            if (HasContent) SendRequested?.Invoke();
        };
        Controls.Add(_send);

        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data!.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data!.GetDataPresent(DataFormats.FileDrop))
                foreach (string f in (string[])e.Data.GetData(DataFormats.FileDrop)!)
                    AddFile(f);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 滚轮那一条走消息过滤器，而不是给每个控件挂 MouseWheel：
        // WM_MOUSEWHEEL 是**投递到线程消息队列**的（不是 SendMessage 直送窗口过程），
        // 过滤器在消息出队时就看得见它，落在哪个 HWND 上都一样 —— 于是不必赌
        // 「EDIT 没滚动条时会不会把这条消息冒泡给父面板」。
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    // ---------------- 布局 ----------------

    /// <summary>卡片矩形：面板里扣掉四边留白，左缘再让开侧栏。</summary>
    private Rectangle CardRect()
    {
        int left = _contentInset + CardMarginX;
        int right = Width - CardMarginX;
        if (right - left < 120) right = left + 120;   // 面板被压得极窄时的防呆
        int top = CardMarginTop;
        int bottom = Height - CardMarginBottom;
        if (bottom - top < 60) bottom = top + 60;
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private void LayoutCard()
    {
        if (_hint == null) return;   // 构造期间子控件尚未建立
        var card = CardRect();

        int rowTop = card.Bottom - BottomRowH;
        int boxTop, boxBottom = rowTop - 3;

        if (Draft.Count > 0)
        {
            _draft.Visible = true;
            _draft.Bounds = new Rectangle(card.Left + CardPadX, card.Top + 2,
                                          card.Width - 2 * CardPadX, DraftStrip.RowH);
            boxTop = _draft.Bottom + 2;
        }
        else
        {
            _draft.Visible = false;
            boxTop = card.Top + CardPadTop;
        }
        if (boxBottom - boxTop < 24) boxTop = Math.Max(card.Top + 2, boxBottom - 24);

        // 高度封在三行上：超出的部分 EDIT 自己会滚（它会一直把光标所在行拉进可视区），
        // 我们只负责把它滚到哪儿画出来 —— 见 PaintBar。
        //
        // 这里有三处必须咬合：**行距**取 EDIT 排版真正用的那一个（Ui.EditLinePitch，
        // 不是 Font.Height，两者差 1px）、**高度取行距的整数倍**（多行 EDIT 只画完整装得下
        // 的行，矮 1px 就整整少一行）、**可见行数由同一个行距算**（见 UpdateBar）。
        // 任一处对不上，「盒子里能看见几行」和「代码以为能看见几行」就会分家 —— 表现出来
        // 就是用户报的「才两行就开始往上滚、底部明明还放得下一行却空着、也不出滚动条」。
        int lineH = LineH();
        int lines = Math.Clamp((boxBottom - boxTop) / lineH, 1, MaxLines);
        int boxH = lines * lineH;
        _box.Bounds = new Rectangle(card.Left + CardPadX, boxTop,
                                    card.Width - 2 * CardPadX, boxH);

        // 滑条摆在卡片给文本框留的右侧内边距里，不与文字重叠（见 BarW 的注释）。
        _barRect = new Rectangle(card.Right - BarRight - BarW, boxTop, BarW, boxH);
        _barVis = Math.Max(1, boxH / lineH);

        int btnY = rowTop + (BottomRowH - BtnSize) / 2;
        _attach.Location = new Point(card.Left + BtnInset, btnY);
        _send.Location = new Point(card.Right - BtnInset - BtnSize, btnY);

        // 提示文字居中显示 —— 但**宽度必须是个常量**，不能跟着卡片走。
        //
        // 以前这里是 hRight - hLeft，于是侧栏动画的每一帧都在改这个 Label 的宽度。宽度一变，
        // 整串字就得重新居中、重画一遍，而这个 Label 的 WM_PAINT 与父面板那一次重画是**两条
        // 独立的路径**（两个窗口各自的队列），谁先谁后不定：屏幕上于是交替出现「文字已经按新
        // 宽度居中了」和「文字还停在按旧宽度算出来的位置」两种帧 —— 用户报的
        // 「展开 / 收起时底部提示信息左右抖动、不是平滑过渡」就是它。
        //
        // 固定宽度之后，这个 Label 在动画里**只平移、不变尺寸**：居中偏移成了常量，
        // 内容跟尺寸无关，于是无论 Windows 是直接搬像素还是让它自己重画，屏幕上的结果都一样 ——
        // 没有可以抖的地方。文字仍然居中在卡片中间，因为按钮之间的中点就是卡片的中心。
        //
        // 下边缘让开卡片描边那条带（见 CardEdgeBand），否则它会把下边框整段刷平。
        int hLeft = _attach.Right + 8;
        int hRight = _send.Left - 8;
        if (hRight - hLeft < 40) { hLeft = card.Left; hRight = card.Right; }
        // 宽度取「提示文字」与「上下文仪表」两者最坏情况的较大值 —— 两个控件共用同一个矩形，
        // 谁 Visible 谁说了算，宽度天然是常量（理由见上面那一整段）。
        int want = Math.Max(HintWidth(), MeterWidth());
        int hintW = Math.Min(want, Math.Max(40, hRight - hLeft));
        int hCenter = (hLeft + hRight) / 2;
        var rowRect = new Rectangle(hCenter - hintW / 2, rowTop, hintW,
                                    Math.Max(16, card.Bottom - CardEdgeBand - rowTop));
        _hint.Bounds = rowRect;
        _meter.Bounds = rowRect;

        UpdatePh();
        UpdateBar();
    }

    /// <summary>
    /// 文本框里一行的行距 —— EDIT 排版真正用的那一个（见 <see cref="Ui.EditLinePitch"/>）。
    ///
    /// **盒子高度和可见行数必须共用这个数**：LayoutCard 拿它算盒子高度，UpdateBar 拿它算
    /// 「能看见几行」，两者用的若不是同一个行距，滑条就会在该出现的时候不出现。
    /// 量一次就够（字体在本面板里是常量），先记住量的是哪个字体实例。
    /// </summary>
    private int LineH()
    {
        var f = _box.Font;
        if (_lineH < 0 || !ReferenceEquals(f, _lineHFont))
        {
            _lineH = Ui.EditLinePitch(f);
            _lineHFont = f;
        }
        return _lineH;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCard();
    }

    /// <summary>
    /// 底部提示文字那个 Label 的宽度：**只由文字本身决定，与卡片宽度无关**
    /// （为什么必须是常量，见 <c>LayoutCard</c> 里那段）。量一次就够 —— 字符串是常量，
    /// 字号也不会中途变。
    /// </summary>
    private int HintWidth()
    {
        if (_hintW < 0)
            _hintW = TextRenderer.MeasureText(_hint.Text, _hint.Font).Width + 2 * HintPadX;
        return _hintW;
    }

    /// <summary>
    /// 上下文仪表那个矩形的宽度。**按最坏情况预留**（<c>100% · 999.9K/999.9K · 999.9 tok/s</c>），
    /// 而不是按当前数字 —— 数字每秒钟都在变，跟着量就等于每帧改宽度，
    /// 那正是上面 LayoutCard 里那段「宽度必须是常量」要避免的事。
    /// </summary>
    private int MeterWidth()
    {
        if (_meterW < 0)
            _meterW = ContextMeter.MeasureWorstWidth() + 2 * HintPadX;
        return _meterW;
    }

    /// <summary>
    /// 切到上下文仪表。<paramref name="info"/> 的 <c>Window</c> 非正数时退回快捷键提示
    /// （那就是「还没有第一轮对话」的状态）。
    /// </summary>
    public void SetContext(CtxInfo info)
    {
        bool on = info.Window > 0;
        _hint.Visible = !on;
        _meter.Visible = on;
        if (on) _meter.Set(info);
    }

    /// <summary>退回快捷键提示。切到没开过口的会话、回到欢迎页时调。</summary>
    public void ClearContext() => SetContext(default);

    /// <summary>
    /// 提示文字离标签左右边缘的留白。**这个数有下限，不是排版口味**。
    ///
    /// 标签在侧栏动画里只平移，而**平移腾出来的那一条**（往左移就是右侧那条）要等父面板重画
    /// 才被擦掉；在擦掉之前，屏幕上留着的是标签原来压在那儿的像素。文字居中时它离标签边缘
    /// 只有十几像素，那条带子就直接压在字形的尾巴上 —— 于是有一帧能看到「字的尾巴拖在后面」。
    ///
    /// 侧栏动画是时间驱动的 ease-out cubic（300ms，见 MainForm.Sidebar.cs），首帧位移最大：
    /// 曲线峰值速度 3 × 256px / 300ms ≈ 2.6px/ms，~31ms 的 tick 下一帧最多挪 ~79px，
    /// 文字只走一半即 ~40px；留白取 48 就永远够不着。留白之外的宽度由
    /// <c>LayoutCard</c> 按两个按钮之间的距离收窄。
    /// </summary>
    private const int HintPadX = 48;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var card = CardRect();
        // 卡片自身抬高 1px 给描边让位，描边才不会骑在圆角上被抗锯齿糊掉。
        var body = new Rectangle(card.X, card.Y, card.Width - 1, card.Height - 1);
        RP.Box(g, body, CardRadius, Theme.InputBg, BackColor);
        RP.Stroke(g, body, CardRadius, _focused ? Theme.Accent : Theme.Border, _focused ? 1.6f : 1f);
        PaintBar(g);
    }

    // ---------------- 竖向滑条 ----------------

    /// <summary>滑块的矩形：长度按「可见行 / 总行数」的比例，位置按「首行 / 可滚行数」。</summary>
    private Rectangle ThumbRect()
    {
        int h = _barRect.Height;
        if (h <= 0) return Rectangle.Empty;
        int thumb = (int)Math.Round((double)h * _barVis / Math.Max(1, _barTotal));
        thumb = Math.Clamp(thumb, Math.Min(BarMinThumb, h), h);
        int span = h - thumb;
        int maxFirst = Math.Max(1, _barTotal - _barVis);
        int y = _barRect.Y + (int)Math.Round((double)span * Math.Clamp(_barFirst, 0, maxFirst) / maxFirst);
        return new Rectangle(_barRect.X, y, _barRect.Width, thumb);
    }

    /// <summary>滑块反推行的行号：鼠标在 y 处按下时，滑块上边缘该落在哪儿。</summary>
    private int LineAt(int thumbTop)
    {
        int span = _barRect.Height - ThumbRect().Height;
        int maxFirst = Math.Max(1, _barTotal - _barVis);
        if (span <= 0) return 0;
        int rel = Math.Clamp(thumbTop - _barRect.Y, 0, span);
        return (int)Math.Round((double)rel * maxFirst / span);
    }

    /// <summary>滑条的命中区：6px 宽直接去点太费劲，横向放宽一点。</summary>
    private Rectangle BarHit() =>
        _barVisible && _barRect.Height > 0
            ? Rectangle.Inflate(_barRect, BarGrab, 2)
            : Rectangle.Empty;

    private int FirstVisible() => _box.IsHandleCreated
        ? Math.Max(0, (int)Win32.SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero))
        : 0;

    /// <summary>
    /// 重新读一遍 EDIT 的行数 / 首行，决定滑条显不显示、滑块画在哪。
    ///
    /// EDIT 自己滚动（打字、方向键、点一下放光标、滚轮）**不会发任何 WinForms 事件**，
    /// 所以调用点只能挂在「会造成滚动的那几个动作」上：TextChanged / KeyUp / MouseUp /
    /// 滚轮 / 拖动滑条本身。定时轮询也能做，但那是拿一个常驻定时器换几行回调。
    /// </summary>
    private void UpdateBar()
    {
        if (_box == null || !_box.IsHandleCreated || _barRect.Height <= 0) return;
        // 行数与可见行从这里现读，不缓存：LayoutCard 也调本方法，那时 _barVis 才是新的。
        // 可见行用 LineH()（= LayoutCard 算盒高用的那个行距），除下来正好是整数行 ——
        // 盒子高度已经是行距的整数倍，这里再截一次零不会有误差。
        _barVis = Math.Max(1, _box.Height / Math.Max(1, LineH()));
        _barTotal = Math.Max(1, (int)Win32.SendMessage(_box.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero));
        bool show = _barTotal > _barVis;
        int first = show ? FirstVisible() : 0;
        if (show == _barVisible && first == _barFirst) return;
        _barVisible = show;
        _barFirst = first;
        Invalidate(BarHit());
    }

    /// <summary>把 EDIT 滚到「首行 = first」。</summary>
    private void BarScrollTo(int first)
    {
        if (!_box.IsHandleCreated) return;
        int cur = FirstVisible();
        int want = Math.Clamp(first, 0, Math.Max(0, _barTotal - _barVis));
        if (want == cur) return;
        // 没有 EM_SETFIRSTVISIBLELINE 这种绝对定位的消息，只能按差值滚。
        Win32.SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(want - cur));
        _barFirst = want;
        Invalidate(BarHit());
    }

    /// <summary>
    /// 画滑条。轨道不画 —— 只在需要时浮出一枚圆角滑块，这也是现代滚动条的做法：
    /// 卡片上永远不多一条灰槽。颜色由卡片的填充色混出来，深浅两套主题都自动跟。
    /// </summary>
    private void PaintBar(Graphics g)
    {
        if (!_barVisible) return;
        var thumb = ThumbRect();
        if (thumb.Width <= 0 || thumb.Height <= 0) return;
        float k = _barDrag ? 0.62f : _barHot ? 0.48f : 0.30f;
        RP.Fill(g, thumb, BarW / 2, Theme.Mix(Theme.InputBg, Theme.TextMuted, k));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !BarHit().Contains(e.Location)) return;
        var thumb = ThumbRect();
        if (thumb.Contains(e.Location))
        {
            _barGrab = e.Y - thumb.Y;            // 抓住滑块本身：保持按下时的相对位置
        }
        else
        {
            _barGrab = thumb.Height / 2;
            BarScrollTo(LineAt(e.Y - _barGrab)); // 点在空白处：滑块直接跳过来
        }
        _barDrag = true;
        Capture = true;                          // 拖出滑条范围（压到文本框上）也要收得到移动
        Invalidate(BarHit());
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_barDrag)
        {
            BarScrollTo(LineAt(e.Y - _barGrab));
            return;
        }
        bool hot = BarHit().Contains(e.Location);
        if (hot == _barHot) return;
        _barHot = hot;
        Invalidate(BarHit());
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_barDrag) return;
        _barDrag = false;
        Capture = false;
        Invalidate(BarHit());
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_barHot) return;
        _barHot = false;
        Invalidate(BarHit());
    }

    private void PickFiles()
    {
        // 过滤器按白名单现拼（AttachTypes.All），**不写死**：白名单以后加类型时，
        // 对话框里自动就跟上了，不会出现「能拖进来但选不到」。
        // 留一条「所有文件」：白名单之外的东西也该看得见，选进来会被拒并说清理由，
        // 比在对话框里凭空消失、让用户以为文件不存在强。
        string star = string.Join(";", AttachTypes.All().Select(e => "*" + e));
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要添加的文件 / 图片",
            Filter = $"支持的文件（图片 / 文本 / 文档 / 音频）|{star}|所有文件|*.*",
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
            foreach (string f in dlg.FileNames) AddFile(f);
    }

    // ---------------- 滚轮 ----------------

    private const int WM_MOUSEWHEEL = 0x020A;
    private const int EM_LINESCROLL = 0x00B6;
    private const int EM_GETLINECOUNT = 0x00BA;
    private const int EM_GETFIRSTVISIBLELINE = 0x00CE;

    /// <summary>
    /// 文本框没有滚动条，滚轮就没人管了（那个 WS_VSCROLL 本来会让 EDIT 自己处理）。
    /// 这里补上：光标压在文本框上时，把滚轮折算成 EM_LINESCROLL 直接送给它。
    ///
    /// 用消息里的屏幕坐标而不是 <c>Cursor.Position</c>：后者要等这条消息被处理时才读，
    /// UI 线程一忙就读到已经走掉的鼠标位置。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        // 看图浮层开着的时候滚轮归它缩放，别拿来滚文本（见 ImageViewer.AnyOpen）。
        if (ImageViewer.AnyOpen) return false;
        // 设置浮窗开着的时候滚轮归它（盖在上面的兄弟控件，本控件的 Visible 照样是 true，
        // 见 SettingsOverlay.AnyOpen）。
        if (SettingsOverlay.AnyOpen) return false;
        if (!_box.IsHandleCreated || !_box.Visible) return false;

        long lp = m.LParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        var client = _box.PointToClient(screen);
        // 压在滑条上也得算数：它和文本框是同一块文本区，滚轮落在哪半边都该滚。
        if (!_box.ClientRectangle.Contains(client) && !BarHit().Contains(PointToClient(screen))) return false;

        int notches = (short)((long)m.WParam >> 16) / 120;
        if (notches == 0) return false;
        int step = SystemInformation.MouseWheelScrollLines;
        if (step <= 0) step = 3;                    // 0 = 不分行（只有整页），这里不做区分
        Win32.SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(-notches * step));
        UpdateBar();
        return true;
    }

    // ---------------- 键盘 / 粘贴 ----------------

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.V)
        {
            if (TryPasteClipboard())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            return;
        }
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            if (HasContent) SendRequested?.Invoke();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private bool TryPasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img == null) return false;
                string p = SaveClipboardImage(img);
                Add(new Attachment { Kind = "image", Name = "粘贴图片", Path = p });
                return true;
            }
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string? f in Clipboard.GetFileDropList())
                    if (f != null) AddFile(f);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 把粘贴进来的图落盘成附件。与截图走同一条路（<see cref="AttachmentStore.Store"/>，
    /// 内容寻址）：不再丢进 <c>%TEMP%</c>，否则重启后这条消息的图就没了；
    /// 也没了「同一张图贴两次占两份文件」。
    /// </summary>
    private static string SaveClipboardImage(Image img) => AttachmentStore.Store(img);

    // ---------------- 附件草稿 ----------------

    /// <summary>
    /// 按路径加一个附件，返回是否真的收下了。**只收白名单里的类型**（图片 / 明文文本 /
    /// office 文档 / 音频，见 <see cref="AttachTypes"/>），其余的回一句理由。
    ///
    /// 为什么不悄悄收下：附件最终是给模型读的，可执行文件、压缩包收进来，
    /// 用户只会纳闷「它怎么没看懂这个文件」。拒收 + 说清理由，比收下强。
    /// 拖入、按钮选、粘贴文件三条路都汇到这里，判一次就够。
    ///
    /// 返回值是给调用方用的：拖放一次进来好几个文件时，只能按「收下了几个」决定
    /// 要不要说那句「已添加到输入框」—— 无条件说的话，会把刚冒出来的拒收理由顶掉，
    /// 用户就只看到一句成功。
    /// </summary>
    public bool AddFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        var cat = AttachTypes.CatOf(path);
        if (cat == AttachCat.None)
        {
            Notice?.Invoke(AttachTypes.RejectReason(path));
            return false;
        }

        // 同一个文件拖两次是误操作，不是「想发两份」。按路径去重，并明说一句 ——
        // 什么都不做的话用户会以为拖拽没生效。
        string full = Path.GetFullPath(path);
        var dup = Draft.FirstOrDefault(a =>
            a.Path != null && string.Equals(SafeFull(a.Path), full, StringComparison.OrdinalIgnoreCase));
        if (dup != null)
        {
            Notice?.Invoke("这张已经在列表里了：" + DisplayNameOf(dup));
            return false;
        }

        string name = Path.GetFileName(path);
        var a = cat == AttachCat.Image ? Attachment.ForImage(name, path) : Attachment.ForFile(name, path);
        a.Size = AttachTypes.SizeOf(path);   // 抓一次，之后不再跟随磁盘（见 Attachment.Size）
        Add(a);
        return true;
    }

    private static string? SafeFull(string p)
    {
        try { return Path.GetFullPath(p); } catch { return null; }
    }

    private static string DisplayNameOf(Attachment a) =>
        string.IsNullOrWhiteSpace(a.Name) ? (a.Path ?? "(文件)") : a.Name;

    public void Add(Attachment a)
    {
        if (a.Size == 0 && a.Path != null) a.Size = AttachTypes.SizeOf(a.Path);
        Draft.Add(a);
        Sync();
    }

    /// <summary>
    /// 一条附件被从草稿里**单独拿掉**了（用户点了它右上角那个叉）。
    ///
    /// 存在的理由：托管附件（截图 / 粘贴图）的字节是我们落盘的，用户把它删掉又没发出去，
    /// 那个文件就再没人要了 —— 但**「再没人要」这件事只有主窗口判得了**：
    /// 同一张图可能正躺在另一条会话的草稿里，也可能已经被某条消息引用着。
    /// 输入框自己看不到那些，所以它只报「谁被拿掉了」，收不回收由主窗口定。
    ///
    /// **只有逐个删除会触发它。** <see cref="Flush"/> 把草稿变成消息时清空、
    /// <see cref="Apply"/> 装载另一条会话的草稿时清空，都**不走这里** ——
    /// 前者是「它们有主了」，后者是「它们归上一条会话的草稿管」，两种都不该回收。
    /// </summary>
    public event Action<Attachment>? AttachmentRemoved;

    public void Remove(Attachment a)
    {
        Draft.Remove(a);
        Sync();
        AttachmentRemoved?.Invoke(a);   // 放在 Sync 之后：此刻 Draft 里已经没有它了
    }

    /// <summary>
    /// 把草稿列表同步给附件区。
    ///
    /// <c>_draft.Visible</c> 与面板高度是**一起变的**：附件从 0 张变 1 张（或反过来）时，
    /// 面板要多占（少占）一行，得让 <c>MainForm.ApplyLayout</c> 重摆一次。只在
    /// 「有 / 没有」翻转的那一刻通知，加第二张时面板高度没变，重摆一次纯属浪费
    /// —— 那会连带重抓设置界面的底图。
    ///
    /// 「有没有」记在 <see cref="_hadDraft"/> 里，**不去读 <c>_draft.Visible</c>**：
    /// 那个 getter 返回的是「算上祖先的」有效可见性，欢迎页上整个 <c>_chatUI</c> 都是隐藏的，
    /// 于是赋值成 true 之后读回来还是 false，翻转永远检测不到 —— 表现成「加了附件，
    /// 输入区不长高，卡片被窗口下缘切掉一条」。
    /// </summary>
    private void Sync()
    {
        bool has = Draft.Count > 0;
        _draft.SetItems(Draft);
        _draft.Visible = has;
        LayoutCard();          // 附件区出现 / 消失会改文本框的上下位置
        UpdateSendState();
        if (has == _hadDraft) return;
        _hadDraft = has;
        LayoutChanged?.Invoke();
    }

    /// <summary>上一次同步时有没有附件。见 <see cref="Sync"/> 里为什么不能读 Visible。</summary>
    private bool _hadDraft;

    /// <summary>取出当前草稿为一条用户消息并清空。</summary>
    public ChatMessage? Flush()
    {
        string txt = _box.Text.Trim();
        if (txt.Length == 0 && Draft.Count == 0) return null;
        var m = new ChatMessage { Role = "user", Text = txt, Attachments = new List<Attachment>(Draft) };
        Draft.Clear();
        Sync();
        _box.Text = "";
        _box.Focus();
        return m;
    }

    public void FocusInput() => _box.Focus();

    /// <summary>
    /// 把输入框里现在的东西存成 <paramref name="c"/> 的草稿（正文 + 附件）。
    ///
    /// 草稿属于它被敲进去的那条对话：用户在 A 里打了一半、切到 B 去问另一件事、
    /// 再切回 A 时那半句还应当在。切走时存、切回来时装，两个调用点挨在
    /// <c>MainForm.ActivateConversation</c> 里 —— 那里是**离开一条对话的唯一出口**
    /// （另一个出口是把这条删掉，草稿跟着对话一起没，不必存）。
    ///
    /// 附件**拷一份**再存：面板里那个列表是活的，用户接着删一张附件、或者按回车发出去，
    /// 都不该顺手改掉已经留在那条对话上的那份。
    /// </summary>
    public void SaveDraftTo(Conversation c)
    {
        c.DraftText = _box.Text;
        c.DraftFiles = new List<Attachment>(Draft);
    }

    /// <summary>
    /// 把 <paramref name="c"/> 的草稿装进输入框。**null 表示空白** —— 「当前没有对话」
    /// （删掉当前这条、停在欢迎页）就是那种情况。
    /// </summary>
    public void LoadDraftFrom(Conversation? c) => Apply(c?.DraftText ?? "", c?.DraftFiles);

    /// <summary>
    /// 整体换掉输入框的内容。
    ///
    /// 不能在外面直接写 <c>Text</c> 与 <c>Draft</c>：附件区、面板高度（<see cref="PreferredHeight"/>
    /// 依赖 <c>Draft.Count</c>）、发送键的可用状态都要跟着一起变，这三样都归 <see cref="Sync"/> 管。
    /// 文本框自己那一摊（占位层、滑条）由构造函数里挂的 <c>TextChanged</c> 负责，赋值就够。
    ///
    /// 光标钉到末尾：切回来是接着往下打的，停在开头的话第一句会插在旧草稿前面。
    /// </summary>
    private void Apply(string text, IReadOnlyList<Attachment>? files)
    {
        _box.Text = text ?? "";
        Draft.Clear();
        if (files != null) Draft.AddRange(files);
        Sync();
        if (_box.IsHandleCreated && _box.TextLength > 0)
        {
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
        }
    }

    /// <summary>
    /// 实时转写期间整体重写输入框文字（= 用户自己敲的前缀 + 已定稿 + 中间结果）。
    /// 每次中间结果刷新都会整框替换，所以必须把前缀与定稿一起带上，不能只传增量。
    /// 光标钉到末尾：录音转写是追加式输入，用户松开按键后接着看到的就是最后那个字。
    /// </summary>
    public void SetDictation(string text)
    {
        _box.Text = text;
        if (_box.IsHandleCreated)
        {
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
            _box.ScrollToCaret();
        }
    }

    // ---------------- 状态刷新 ----------------

    /// <summary>
    /// 占位层与文本框**共用同一个矩形**。
    ///
    /// 以前是各自摆的：占位层用 <c>Theme.UI(12f)</c>、文本框用 <c>12.5f</c>，位置再按
    /// <c>Font.Height * 0.25 + 3</c> 现推一个偏移 —— 字号差了半磅，用户一输入就看见字「缩了一下」，
    /// 这正是需求里那条「输入的文本和 placeholder 的位置、大小需保持一致」。
    /// 现在字号取文本框自己的 <c>Font</c>、矩形也整块照搬：位置与大小都由构造保证，
    /// 不再有第二个可以飘的数字。横向对齐靠 <c>Ui.PinEditTextLeft</c> 把 EDIT 的左边距清零，
    /// 竖直方向两边都是从矩形上边缘往下排（EDIT 与 HintText 都是顶对齐）。
    /// </summary>
    private void UpdatePh()
    {
        if (_ph == null || _box == null) return;
        bool show = _box.Text.Length == 0;
        _ph.Visible = show;
        if (show) _ph.Bounds = _box.Bounds;
    }

    /// <summary>
    /// 发送键的三种状态。忙碌优先：那时它已经是「暂停」了，不该因为草稿被清空而变灰。
    /// </summary>
    private void UpdateSendState()
    {
        if (_send == null) return;
        bool on = _busy || HasContent;
        _send.Icon = _busy ? IconButton.Kind.Stop : IconButton.Kind.Send;
        _send.Active = on;
        _send.Clickable = on;
        _send.Invalidate();
    }

    // ---------- 输入区随主题 ----------
    public void RefreshTheme()
    {
        BackColor = Theme.ChatBg;
        _draft.RefreshTheme();
        _box.BackColor = Theme.InputBg;
        _box.ForeColor = Theme.TextMain;
        _hint.BackColor = Theme.InputBg;
        _hint.ForeColor = Theme.TextMuted;
        _meter.Restyle();
        _ph.BackColor = Theme.InputBg;
        _ph.ForeColor = Theme.TextMuted;
        UpdateSendState();
        Invalidate();
    }
}
