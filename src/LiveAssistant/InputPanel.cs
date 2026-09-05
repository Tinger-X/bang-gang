using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>消息输入区：附件区 + 文本框 + 工具行（发送/文件）。</summary>
internal sealed class InputPanel : Panel
{
    private readonly FlowLayoutPanel _draft;
    private readonly TextBox _box;
    private readonly Label _hint;
    private readonly Label _ph;
    private readonly IconButton _send, _attach;
    public List<Attachment> Draft { get; } = new();
    public event Action? SendRequested;

    public string Text { get => _box.Text; set => _box.Text = value; }
    public bool HasContent => _box.Text.Trim().Length > 0 || Draft.Count > 0;

    public InputPanel()
    {
        BackColor = Theme.InputBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Height = 150;

        _draft = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 72,
            Padding = new Padding(10, 6, 10, 2),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            BackColor = Theme.InputBg,
        };
        _draft.Visible = false;
        Controls.Add(_draft);

        _box = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Font = Theme.UI(12.5f),
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextMain,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            AcceptsReturn = true,
            AcceptsTab = false,
        };
        _box.KeyDown += OnBoxKeyDown;
        _box.TextChanged += (_, _) => UpdatePh();
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

        _hint = new Label
        {
            AutoSize = true,
            Font = Theme.UI(9.5f),
            ForeColor = Theme.TextMuted,
            Text = "Enter 发送 · Shift+Enter 换行 · 可拖入/粘贴文件与图片",
        };
        Controls.Add(_hint);

        _send = new IconButton(IconButton.Kind.Send, Theme.InputBg);
        new ToolTip().SetToolTip(_send, "发送 (Enter)");
        _send.Click += (_, _) => { if (HasContent) SendRequested?.Invoke(); };
        Controls.Add(_send);

        _attach = new IconButton(IconButton.Kind.Paperclip, Theme.InputBg);
        new ToolTip().SetToolTip(_attach, "添加文件 / 图片");
        _attach.Click += (_, _) => PickFiles();
        Controls.Add(_attach);

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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_hint == null) return; // 构造期间子控件尚未建立
        int bot = Height - 28;
        _hint.Location = new Point(14, bot);
        _send.Location = new Point(Width - 40, bot - 4);
        _attach.Location = new Point(Width - 78, bot - 4);
        UpdatePh();
    }

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要添加的文件 / 图片" };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
            foreach (string f in dlg.FileNames) AddFile(f);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 0, 0, Width, 0);
    }

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
                foreach (string f in Clipboard.GetFileDropList())
                    AddFile(f);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string SaveClipboardImage(Image img)
    {
        string dir = Path.Combine(Path.GetTempPath(), "LiveAssistant");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
        img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        return p;
    }

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
        _draft.Visible = Draft.Count > 0;
        _draft.ResumeLayout();
        // 文本框留出合适空间
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

    private void UpdatePh()
    {
        if (_ph == null || _box == null) return;
        bool show = _box.Text.Length == 0;
        _ph.Visible = show;
        if (show)
            _ph.Location = new Point(_box.Left + 4, _box.Top + (int)(_box.Font.Height * 0.25f) + 3);
    }

    // ---------- 输入区随主题 ----------
    public void RefreshTheme()
    {
        BackColor = Theme.InputBg;
        _draft.BackColor = Theme.InputBg;
        _box.BackColor = Theme.InputBg;
        _box.ForeColor = Theme.TextMain;
        _hint.ForeColor = Theme.TextMuted;
        _ph.BackColor = Theme.InputBg;
        _ph.ForeColor = Theme.TextMuted;
        foreach (Control c in _draft.Controls)
        {
            (c as DraftChip)?.RefreshTheme();
        }
        Invalidate();
    }

    public void RelayoutAfterTheme()
    {
        if (Visible) LayoutDraftArea();
    }

    private void LayoutDraftArea() { }
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
