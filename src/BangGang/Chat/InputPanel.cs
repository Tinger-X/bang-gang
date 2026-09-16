using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>消息输入区：附件区 + 一张圆角卡片（文本框 + 底部工具行）。</summary>
internal sealed class InputPanel : Panel, IMessageFilter
{
    private readonly FlowLayoutPanel _draft;
    private readonly TextBox _box;
    private readonly Label _hint;
    private readonly HintText _ph;
    private readonly IconButton _send, _attach;
    public List<Attachment> Draft { get; } = new();
    public event Action? SendRequested;

    /// <summary>模型正在回复时用户按了「暂停」。</summary>
    public event Action? StopRequested;

    /// <summary>
    /// 输入区面板自身的高度。<c>MainForm.ApplyLayout</c> 摆它、卡片按它算内部余量，
    /// 两处必须是同一个数 —— 差一像素卡片就会被窗口下缘切掉一条。
    /// </summary>
    public const int PanelH = 150;

    // ---------- 卡片几何。动任何一个都要重跑 tools/input-check.ps1 看一眼 ----------

    private const int CardMarginX = 16;       // 卡片到窗口左右缘
    private const int CardMarginTop = 8;
    private const int CardMarginBottom = 10;
    private const int CardRadius = 10;        // 比参考产品小一圈（需求里点名要小）
    private const int CardPadTop = 11;        // 文本框离卡片上缘
    private const int BottomRowH = 38;        // 底部工具行占的高度
    private const int BtnInset = 9;           // 工具行按钮离卡片左右边框
    private const int BtnSize = 28;
    private const int DraftH = 60;

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
            _contentInset = value;
            LayoutCard();
        }
    }

    public string Text { get => _box.Text; set => _box.Text = value; }
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
        _draft = new FlowLayoutPanel
        {
            Padding = new Padding(0, 2, 0, 2),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            BackColor = Theme.InputBg,
        };
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

        // 底部工具行的提示文字。**必须是 Label（Static）**：tools/settings-over-chat.ps1 就是靠
        // 「底行里那个宽 Static」认出它的，换成自绘控件那条探针会直接判失败。
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
            _draft.Bounds = new Rectangle(card.Left + CardPadX, card.Top + 4,
                                          card.Width - 2 * CardPadX, DraftH);
            boxTop = _draft.Bottom + 2;
        }
        else
        {
            _draft.Visible = false;
            boxTop = card.Top + CardPadTop;
        }
        if (boxBottom - boxTop < 24) boxTop = Math.Max(card.Top + 2, boxBottom - 24);

        // 高度封在三行上：超出的部分 EDIT 自己会滚（它会一直把光标所在行拉进可视区），
        // 我们只负责把它滚到哪儿画出来 —— 见 PaintBar。用 Font.Height 而不是拍一个像素数，
        // 行高就是 EDIT 排版时用的那一个，换字号 / 换 DPI 都不用跟着改。
        int lineH = Math.Max(1, _box.Font.Height);
        int boxH = Math.Min(MaxLines * lineH, boxBottom - boxTop);
        _box.Bounds = new Rectangle(card.Left + CardPadX, boxTop,
                                    card.Width - 2 * CardPadX, boxH);

        // 滑条摆在卡片给文本框留的右侧内边距里，不与文字重叠（见 BarW 的注释）。
        _barRect = new Rectangle(card.Right - BarRight - BarW, boxTop, BarW, boxH);
        _barVis = Math.Max(1, boxH / lineH);

        int btnY = rowTop + (BottomRowH - BtnSize) / 2;
        _attach.Location = new Point(card.Left + BtnInset, btnY);
        _send.Location = new Point(card.Right - BtnInset - BtnSize, btnY);

        // 提示文字占满两个按钮之间，居中显示；窗口太窄时靠 AutoEllipsis 收尾。
        // 下边缘让开卡片描边那条带（见 CardEdgeBand），否则它会把下边框整段刷平。
        int hLeft = _attach.Right + 8;
        int hRight = _send.Left - 8;
        if (hRight - hLeft < 40) { hLeft = card.Left; hRight = card.Right; }
        _hint.Bounds = new Rectangle(hLeft, rowTop, hRight - hLeft,
                                     Math.Max(16, card.Bottom - CardEdgeBand - rowTop));

        UpdatePh();
        UpdateBar();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCard();
    }

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
        _barVis = Math.Max(1, _box.Height / Math.Max(1, _box.Font.Height));
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
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要添加的文件 / 图片" };
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
    /// UI 线程一忙就读到已经走掉的鼠标位置（同一个坑见 <c>_ui.ps1</c> 里 Invoke-Drag 的注释）。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
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

    private static string SaveClipboardImage(Image img)
    {
        string dir = Path.Combine(Path.GetTempPath(), "BangGang");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
        img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        return p;
    }

    // ---------------- 附件草稿 ----------------

    public void AddFile(string path)
    {
        if (!File.Exists(path)) return;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var img = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
        Add(img.Contains(ext)
            ? Attachment.ForImage(Path.GetFileName(path), path)
            : Attachment.ForFile(Path.GetFileName(path), path));
    }

    public void Add(Attachment a)
    {
        Draft.Add(a);
        Sync();
    }

    public void Remove(Attachment a)
    {
        Draft.Remove(a);
        Sync();
    }

    private void Sync()
    {
        _draft.SuspendLayout();
        _draft.Controls.Clear();
        foreach (var a in Draft)
        {
            var chip = new DraftChip(a);
            chip.RemoveClicked += () => Remove(a);
            _draft.Controls.Add(chip);
        }
        _draft.ResumeLayout();
        LayoutCard();          // 附件区出现 / 消失会改文本框的上下位置
        UpdateSendState();
    }

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
        _draft.BackColor = Theme.InputBg;
        _box.BackColor = Theme.InputBg;
        _box.ForeColor = Theme.TextMain;
        _hint.BackColor = Theme.InputBg;
        _hint.ForeColor = Theme.TextMuted;
        _ph.BackColor = Theme.InputBg;
        _ph.ForeColor = Theme.TextMuted;
        foreach (Control c in _draft.Controls)
        {
            (c as DraftChip)?.RefreshTheme();
        }
        UpdateSendState();
        Invalidate();
    }
}

/// <summary>草稿附件小卡片（图片缩略图 / 文件图标 + 名称 + 删除）。</summary>
internal sealed class DraftChip : Control
{
    public Attachment A { get; }
    public event Action? RemoveClicked;
    private Image? _thumb;
    private bool _hover;
    private bool _overX;

    public DraftChip(Attachment a)
    {
        A = a;
        Height = 56;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _thumb = a.Kind == "image" ? a.LoadImage(44, 44) : null;
    }

    public void RefreshTheme()
    {
        Width = (int)Math.Max(120f, TextRenderer.MeasureText(ShortName(), Theme.UI(10f)).Width + 96);
        Invalidate();
    }

    private string ShortName()
    {
        string n = string.IsNullOrWhiteSpace(A.Name) ? Path.GetFileName(A.Path ?? "") : A.Name;
        if (n.Length > 16) n = n[..16] + "…";
        return n;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _overX = false; Invalidate(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = XRect().Contains(e.Location);
        if (over != _overX) { _overX = over; Invalidate(); }
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && XRect().Contains(e.Location)) RemoveClicked?.Invoke();
    }

    private Rectangle XRect() => new(Width - 24, 4, 20, 20);

    protected override void OnPaint(PaintEventArgs e)
    {
        RefreshTheme();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, 26 + 22);
        using (var path = Rounded(rc, 8))
        using (var b = new SolidBrush(Theme.AsstBubble))
            g.FillPath(b, path);

        if (_thumb != null)
            g.DrawImage(_thumb, 6, 4, 44, 44);
        else
        {
            // 文件图标
            using var pen = new Pen(Theme.Accent, 1.6f);
            var fb = new Rectangle(6, 6, 20, 24);
            using var fbPath = Rounded(fb, 3);
            g.DrawPath(pen, fbPath);
            g.DrawLine(pen, fb.Left + 4, fb.Bottom - 8, fb.Right - 4, fb.Bottom - 8);
            g.DrawLine(pen, fb.Left + 4, fb.Bottom - 12, fb.Right - 4, fb.Bottom - 12);
        }

        g.DrawString(ShortName(), Theme.UI(10f), new SolidBrush(Theme.TextMain), _thumb != null ? 56 : 32, 8);
        string kind = A.Kind == "image" ? "图片" : "文件";
        g.DrawString(kind, Theme.UI(8.5f), new SolidBrush(Theme.TextMuted), _thumb != null ? 56 : 32, 26);

        if (_hover || _overX)
        {
            var xr = XRect();
            using var xb = new SolidBrush(_overX ? Color.FromArgb(210, 200, 40, 40) : Color.FromArgb(120, 120, 120, 120));
            g.FillEllipse(xb, xr);
            using var pen = new Pen(Color.White, 1.6f);
            g.DrawLine(pen, xr.Left + 5, xr.Top + 5, xr.Right - 5, xr.Bottom - 5);
            g.DrawLine(pen, xr.Right - 5, xr.Top + 5, xr.Left + 5, xr.Bottom - 5);
        }
        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _thumb?.Dispose();
        base.Dispose(disposing);
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
