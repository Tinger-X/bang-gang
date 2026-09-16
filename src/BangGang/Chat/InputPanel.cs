using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>消息输入区：附件区 + 一张圆角卡片（文本框 + 底部工具行）。</summary>
internal sealed class InputPanel : Panel, IMessageFilter
{
    private readonly FlowLayoutPanel _draft;
    private readonly TextBox _box;
    private readonly Label _hint;
    private readonly Label _ph;
    private readonly IconButton _send, _attach;
    public List<Attachment> Draft { get; } = new();
    public event Action? SendRequested;

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
            // 不用 ScrollBars.Vertical：那样会在卡片里常驻一条系统灰滚动条，和卡片不是一套配色，
            // 一眼就能看出是贴上去的。改成自己接鼠标滚轮 -> EM_LINESCROLL（见 PreFilterMessage），
            // 滚动能力不丢，画面上干净。
            ScrollBars = ScrollBars.None,
            WordWrap = true,
            AcceptsReturn = true,
            AcceptsTab = false,
        };
        _box.KeyDown += OnBoxKeyDown;
        _box.TextChanged += (_, _) => { UpdatePh(); UpdateSendState(); };
        _box.Enter += (_, _) => { _focused = true; Invalidate(); };
        _box.Leave += (_, _) => { _focused = false; Invalidate(); };
        Controls.Add(_box);

        _ph = new Label
        {
            AutoSize = true,
            BackColor = Theme.InputBg,
            Font = Theme.UI(12f),
            ForeColor = Theme.TextMuted,
            Text = "发消息给帮帮…",
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
        _attach = new IconButton(IconButton.Kind.Paperclip)
        {
            Skin = IconButton.Look.Ghost,
            Size = new Size(BtnSize, BtnSize),
            BackdropSource = () => Theme.InputBg,
        };
        _attach.Click += (_, _) => PickFiles();
        Controls.Add(_attach);

        // 右：发送。永远是一枚实心圆，有内容才点亮成强调色。
        _send = new IconButton(IconButton.Kind.Send)
        {
            Skin = IconButton.Look.Solid,
            Size = new Size(BtnSize, BtnSize),
            BackdropSource = () => Theme.InputBg,
            Active = false,
        };
        _send.Click += (_, _) => { if (HasContent) SendRequested?.Invoke(); };
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

        _box.Bounds = new Rectangle(card.Left + CardPadX, boxTop,
                                    card.Width - 2 * CardPadX, boxBottom - boxTop);

        int btnY = rowTop + (BottomRowH - BtnSize) / 2;
        _attach.Location = new Point(card.Left + BtnInset, btnY);
        _send.Location = new Point(card.Right - BtnInset - BtnSize, btnY);

        // 提示文字占满两个按钮之间，居中显示；窗口太窄时靠 AutoEllipsis 收尾。
        int hLeft = _attach.Right + 8;
        int hRight = _send.Left - 8;
        if (hRight - hLeft < 40) { hLeft = card.Left; hRight = card.Right; }
        _hint.Bounds = new Rectangle(hLeft, rowTop, hRight - hLeft, BottomRowH);

        UpdatePh();
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
        if (!_box.ClientRectangle.Contains(_box.PointToClient(screen))) return false;

        int notches = (short)((long)m.WParam >> 16) / 120;
        if (notches == 0) return false;
        int step = SystemInformation.MouseWheelScrollLines;
        if (step <= 0) step = 3;                    // 0 = 不分行（只有整页），这里不做区分
        Win32.SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(-notches * step));
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

    private void UpdatePh()
    {
        if (_ph == null || _box == null) return;
        bool show = _box.Text.Length == 0;
        _ph.Visible = show;
        if (show)
            _ph.Location = new Point(_box.Left + 4, _box.Top + (int)(_box.Font.Height * 0.25f) + 3);
    }

    /// <summary>发送按钮能不能点：有内容才点亮成强调色，否则是一枚中性灰圆。</summary>
    private void UpdateSendState()
    {
        if (_send == null) return;
        bool on = HasContent;
        if (_send.Active == on) return;
        _send.Active = on;
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
