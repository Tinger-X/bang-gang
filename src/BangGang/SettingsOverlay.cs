using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// “设置”内部覆盖层：占据主窗口内容，不再弹出独立窗口 / 托盘图标。
/// 分段式顶部导航（快捷键 / LLM 接入 / 界面设置），底部 保存/取消。
/// </summary>
internal sealed class SettingsOverlay : Panel
{
    public event Action<AppSettings>? Applied;

    private readonly AppSettings _current = new();

    private readonly List<(string action, KeyCap cap)> _rows = new();
    private TextBox _chatUrl = null!, _chatKey = null!, _chatModel = null!;
    private TextBox _sttUrl = null!, _sttKey = null!, _sttModel = null!;
    private ColorSwatch _chatBg = null!, _sideBg = null!, _text = null!, _muted = null!, _accent = null!;
    private TrackBar _opacity = null!;
    private Label _opacityVal = null!;

    private Panel _pageHost = null!;
    private readonly Button _segShortcut, _segLlm, _segUi;

    public SettingsOverlay()
    {
        BackColor = Theme.SideBg;
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        // 顶部条：标题 + 关闭
        var head = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.PanelBg };
        var logo = new Label { Text = "帮", BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.UI(13f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Size = new Size(30, 30), Location = new Point(20, 13) };
        var title = new Label { Text = "设置", Font = Theme.UI(15f, FontStyle.Bold), ForeColor = Theme.TextMain, AutoSize = true, Location = new Point(58, 17) };
        var close = new IconButton(IconButton.Kind.Close, Theme.PanelBg) { Location = new Point(Width - 44, 13), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Ui.SetToolTip(close, "关闭");
        close.Click += (_, _) => Visible = false;
        head.Controls.Add(logo);
        head.Controls.Add(title);
        head.Controls.Add(close);
        Controls.Add(head);
        head.Resize += (_, _) => close.Location = new Point(head.Width - 44, 13);
        close.Location = new Point(head.Width - 44, 13);

        // 顶部导航（分段按钮）
        var seg = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Theme.SideBg, Padding = new Padding(26, 12, 26, 0) };
        _segShortcut = SegButton(seg, "快捷键");
        _segLlm = SegButton(seg, "LLM 接入");
        _segUi = SegButton(seg, "界面设置");
        seg.Controls.Add(_segShortcut);
        seg.Controls.Add(_segLlm);
        seg.Controls.Add(_segUi);
        seg.Resize += (_, _) => LayoutSegs(seg);
        LayoutSegs(seg);
        Controls.Add(seg);

        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SideBg };
        Controls.Add(_pageHost);
        BuildPages(_pageHost);

        // 底部：保存 / 取消
        var foot = new Panel { Dock = DockStyle.Bottom, Height = 66, BackColor = Theme.SideBg };
        var cancel = Flat("取消", secondary: true);
        var save = Flat("保存", secondary: false);
        save.Click += (_, _) => { Collect(out var r); Applied?.Invoke(r); Visible = false; };
        cancel.Click += (_, _) => Visible = false;
        foot.Controls.Add(cancel);
        foot.Controls.Add(save);
        foot.Resize += (_, _) => { cancel.Location = new Point(foot.Width - 250, 16); save.Location = new Point(foot.Width - 146, 16); };
        cancel.Location = new Point(foot.Width - 250, 16);
        save.Location = new Point(foot.Width - 146, 16);
        Controls.Add(foot);

        _segShortcut.Focus();
    }

    public void ReloadFrom(AppSettings s)
    {
        _current.CopyFrom(s);
        // 快捷键
        foreach (var (action, cap) in _rows)
        {
            var sc = _current.Shortcuts.FirstOrDefault(x => x.Action == action);
            if (sc != null) cap.Set(sc);
        }
        _chatUrl.Text = _current.ChatApiUrl;
        _chatKey.Text = _current.ChatApiKey;
        _chatModel.Text = _current.ChatModel;
        _sttUrl.Text = _current.SttApiUrl;
        _sttKey.Text = _current.SttApiKey;
        _sttModel.Text = _current.SttModel;
        _chatBg.SetColor(Color.FromArgb(_current.ChatBg));
        _sideBg.SetColor(Color.FromArgb(_current.SideBg));
        _text.SetColor(Color.FromArgb(_current.TextColor));
        _muted.SetColor(Color.FromArgb(_current.TextMutedColor));
        _accent.SetColor(Color.FromArgb(_current.Accent));
        _opacity.Value = (int)Math.Round(_current.Opacity * 100);
    }

    private void LayoutSegs(Panel seg)
    {
        int[] widths = { 92, 108, 108 };
        int x = 26, gap = 8;
        foreach (var (b, w) in new[] { (_segShortcut, widths[0]), (_segLlm, widths[1]), (_segUi, widths[2]) })
        {
            b.Bounds = new Rectangle(x, 12, w, 36);
            x += w + gap;
        }
    }

    private Button SegButton(Panel parent, string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Default,
            Font = Theme.UI(12f),
            BackColor = Theme.PanelBg,
            ForeColor = Theme.TextMain,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => SelectPage(text);
        parent.Controls.Add(b);
        return b;
    }

    private void SelectPage(string which)
    {
        foreach (Control p in _pageHost.Controls) p.Visible = p.Name == which;
        foreach (var (name, btn) in new[] { ("快捷键", _segShortcut), ("LLM 接入", _segLlm), ("界面设置", _segUi) })
        {
            bool sel = name == which;
            btn.BackColor = sel ? Theme.Accent : Theme.PanelBg;
            btn.ForeColor = sel ? Color.White : Theme.TextMain;
        }
    }

    private static Button Flat(string text, bool secondary)
    {
        return new Button
        {
            Text = text,
            Size = new Size(96, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = secondary ? Color.White : Theme.Accent,
            ForeColor = secondary ? Theme.TextMain : Color.White,
            Font = Theme.UI(12f),
            Cursor = Cursors.Default,
        };
    }

    // ---------- 三个页面 ----------

    private void BuildPages(Panel host)
    {
        host.Controls.Add(BuildShortcutsPage());
        host.Controls.Add(BuildLlmPage());
        host.Controls.Add(BuildUiPage());
        SelectPage("快捷键");
    }

    private Control BuildShortcutsPage()
    {
        var page = new Panel { Name = "快捷键", Dock = DockStyle.Fill, Padding = new Padding(30, 8, 30, 8), BackColor = Theme.SideBg };
        var hint = new Label { Text = "点击输入框后按下新的组合键即可更换；至少含一个修饰键（Ctrl / Alt / Shift）。", Dock = DockStyle.Bottom, Height = 30, Font = Theme.UI(9.5f), ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft };
        page.Controls.Add(hint);

        var tbl = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 3 };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        string[,] items = { { "hide", "显示 / 隐藏窗口" }, { "shot", "选区截屏" }, { "record", "按住录音" } };
        for (int i = 0; i < items.GetLength(0); i++)
        {
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            var cap = new KeyCap(items[i, 0]) { Dock = DockStyle.Fill };
            _rows.Add((items[i, 0], cap));
            tbl.Controls.Add(new Label { Text = items[i, 1], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UI(12f), ForeColor = Theme.TextMain }, 0, i);
            tbl.Controls.Add(cap, 1, i);
        }
        page.Controls.Add(tbl);

        var reset = Flat("一键恢复默认快捷键", secondary: true);
        reset.Click += (_, _) =>
        {
            var defs = AppSettings.DefaultShortcuts();
            foreach (var (action, cap) in _rows)
            {
                var d = defs.First(x => x.Action == action);
                cap.Set(d);
            }
        };
        page.Controls.Add(reset);
        return page;
    }

    private Control BuildLlmPage()
    {
        var page = new Panel { Name = "LLM 接入", Dock = DockStyle.Fill, Padding = new Padding(30, 8, 30, 8), BackColor = Theme.SideBg };
        var note = new Label { Text = "接口默认按 OpenAI 兼容协议填写（Base URL / API Key / 模型名）。", Dock = DockStyle.Bottom, Height = 30, Font = Theme.UI(9.5f), ForeColor = Theme.TextMuted };
        page.Controls.Add(note);

        var scroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SideBg };
        page.Controls.Add(scroll);
        var box = new Panel { Dock = DockStyle.Top, Height = 430, BackColor = Theme.SideBg, Padding = new Padding(0, 6, 0, 6) };
        scroll.Controls.Add(box);

        var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));

        void Section(string title)
        {
            int r = tbl.RowCount++;
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            var l = new Label { Text = title, Dock = DockStyle.Fill, Font = Theme.UI(12f, FontStyle.Bold), ForeColor = Theme.Accent, TextAlign = ContentAlignment.MiddleLeft };
            tbl.Controls.Add(l, 0, r);
            tbl.SetColumnSpan(l, 3);
        }
        TextBox Field(string label, int row, bool secret)
        {
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            tbl.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UI(11.5f), ForeColor = Theme.TextMain }, 0, row);
            var tb = new TextBox { Dock = DockStyle.Fill, Font = Theme.UI(11.5f), UseSystemPasswordChar = secret, BorderStyle = BorderStyle.FixedSingle };
            tbl.Controls.Add(tb, 1, row);
            if (secret)
            {
                var show = new Button { Text = "显示", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelBg, ForeColor = Theme.TextMain, Cursor = Cursors.Default };
                show.Click += (_, _) => tb.UseSystemPasswordChar = !tb.UseSystemPasswordChar;
                tbl.Controls.Add(show, 2, row);
            }
            else
            {
                tbl.Controls.Add(new Label { Dock = DockStyle.Fill }, 2, row);
            }
            return tb;
        }

        Section("对话 LLM");
        _chatUrl = Field("接口地址", 1, false);
        _chatKey = Field("API Key", 2, true);
        _chatModel = Field("模型名", 3, false);
        Section("语音转文字 (STT)");
        _sttUrl = Field("接口地址", 5, false);
        _sttKey = Field("API Key", 6, true);
        _sttModel = Field("模型名", 7, false);
        box.Controls.Add(tbl);
        return page;
    }

    private Control BuildUiPage()
    {
        var page = new Panel { Name = "界面设置", Dock = DockStyle.Fill, Padding = new Padding(30, 8, 30, 8), BackColor = Theme.SideBg };
        var tbl = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2 };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));

        _chatBg = AddColor(tbl, "聊天背景");
        _sideBg = AddColor(tbl, "侧栏背景");
        _text = AddColor(tbl, "文字颜色");
        _muted = AddColor(tbl, "次要文字");
        _accent = AddColor(tbl, "强调色");

        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        tbl.Controls.Add(new Label { Text = "应用透明度", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UI(11.5f), ForeColor = Theme.TextMain }, 0, 5);
        var sp = new Panel { Dock = DockStyle.Fill };
        _opacity = new TrackBar { Minimum = 50, Maximum = 100, Width = 280, Height = 30, TickStyle = TickStyle.None };
        _opacityVal = new Label { Text = "100%", Width = 70, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UI(11f), ForeColor = Theme.TextMain };
        _opacity.ValueChanged += (_, _) => _opacityVal.Text = (_opacity.Value / 100.0).ToString("0%");
        sp.Controls.Add(_opacity);
        sp.Controls.Add(_opacityVal);
        _opacity.Location = new Point(0, 10);
        _opacityVal.Location = new Point(292, 10);
        tbl.Controls.Add(sp, 1, 5);

        page.Controls.Add(tbl);
        return page;
    }

    private ColorSwatch AddColor(TableLayoutPanel tbl, string label)
    {
        int r = tbl.RowCount++;
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        tbl.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UI(11.5f), ForeColor = Theme.TextMain }, 0, r);
        var sw = new ColorSwatch();
        tbl.Controls.Add(sw, 1, r);
        return sw;
    }

    private void Collect(out AppSettings r)
    {
        var s = new AppSettings();
        s.Shortcuts = _rows.Select(x => x.cap.Build()).ToList();
        s.ChatApiUrl = _chatUrl.Text.Trim();
        s.ChatApiKey = _chatKey.Text;
        s.ChatModel = _chatModel.Text.Trim();
        s.SttApiUrl = _sttUrl.Text.Trim();
        s.SttApiKey = _sttKey.Text;
        s.SttModel = _sttModel.Text.Trim();
        s.ChatBg = _chatBg.Color.ToArgb();
        s.SideBg = _sideBg.Color.ToArgb();
        s.TextColor = _text.Color.ToArgb();
        s.TextMutedColor = _muted.Color.ToArgb();
        s.Accent = _accent.Color.ToArgb();
        s.Opacity = _opacity.Value / 100.0;
        r = s;
    }

    public void ApplyTheme()
    {
        BackColor = Theme.SideBg;
        foreach (Control c in Controls)
        {
            if (c is Panel p) p.BackColor = c == _pageHost ? Theme.SideBg : Theme.PanelBg;
        }
        foreach (Control p in _pageHost.Controls) p.BackColor = Theme.SideBg;
        Invalidate(true);
    }

    // ---------- 内部小控件 ----------

    private sealed class KeyCap : TextBox
    {
        private readonly string _action;
        private ShortcutSetting _sc = new();
        public KeyCap(string action)
        {
            _action = action;
            ReadOnly = true;
            Font = Theme.UI(11.5f);
            Height = 32;
            Text = _sc.Label;
            Cursor = Cursors.Default;
            Width = 200;
        }
        public void Set(ShortcutSetting s)
        {
            _sc.Ctrl = s.Ctrl; _sc.Alt = s.Alt; _sc.Shift = s.Shift; _sc.Vk = s.Vk;
            Text = s.Label;
        }
        public ShortcutSetting Build() =>
            new ShortcutSetting { Action = _action, Ctrl = _sc.Ctrl, Alt = _sc.Alt, Shift = _sc.Shift, Vk = _sc.Vk };

        protected override void OnEnter(EventArgs e)
        {
            Text = "请按下新的组合键…";
            base.OnEnter(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool mod = e.Control || e.Alt || e.Shift;
            bool usable = ShortcutSetting.IsUsable((int)e.KeyCode, mod);
            if (e.KeyCode == Keys.Escape)
            {
                Text = _sc.Label;
                e.SuppressKeyPress = true;
                base.OnKeyDown(e);
                return;
            }
            if (usable)
            {
                _sc.Ctrl = e.Control; _sc.Alt = e.Alt; _sc.Shift = e.Shift; _sc.Vk = (int)e.KeyCode;
                Text = _sc.Label;
                e.SuppressKeyPress = true;
                Parent?.Focus();
            }
            else if (!mod) e.SuppressKeyPress = true;
            base.OnKeyDown(e);
        }
        protected override void OnLeave(EventArgs e)
        {
            if (Text.Contains("…")) Text = _sc.Label;
            base.OnLeave(e);
        }
    }

    private sealed class ColorSwatch : Control
    {
        public Color Color { get; private set; } = Color.White;
        public ColorSwatch() { Size = new Size(170, 30); Cursor = Cursors.Default; }
        public void SetColor(Color c) { Color = c; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var rc = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(rc, 8))
            using (var b = new SolidBrush(Color))
                g.FillPath(b, path);
            using (var p = new Pen(Theme.Border)) using (var p2 = Rounded(rc, 8))
                g.DrawPath(p, p2);
            g.DrawString("点击选择颜色", Theme.UI(9.5f), Brushes.Gray, 12, 7);
            base.OnPaint(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            using var dlg = new ColorDialog { Color = Color, FullOpen = true };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { Color = dlg.Color; Invalidate(); }
            base.OnMouseClick(e);
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
}
