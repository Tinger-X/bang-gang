using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>设置窗口：快捷键 / LLM 接入 / 界面设置。</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _edit;
    public AppSettings Result => _edit;

    private readonly List<KeyRow> _rows = new();
    private TextBox _chatUrl = null!, _chatKey = null!, _chatModel = null!, _sttUrl = null!, _sttKey = null!, _sttModel = null!;
    private Button _chatShow = null!, _sttShow = null!;
    private ColorSwatch _bgSw = null!, _panelSw = null!, _textSw = null!, _mutedSw = null!, _accentSw = null!;
    private TrackBar _opacity = null!;
    private Label _opacityVal = null!;

    public SettingsForm(AppSettings current)
    {
        _edit = Clone(current);
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(660, 560);
        Font = Theme.UI(11f);
        BackColor = Color.FromArgb(248, 250, 253);
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

        tabs.TabPages.Add(BuildShortcutsPage());
        tabs.TabPages.Add(BuildLlmPage());
        tabs.TabPages.Add(BuildUiPage());
        Controls.Add(tabs);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        var save = new Button { Text = "保存", Size = new Size(96, 32), FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White };
        var cancel = new Button { Text = "取消", Size = new Size(96, 32), FlatStyle = FlatStyle.Flat };
        save.Click += (_, _) => { ApplyUiPreview(apply: true); DialogResult = DialogResult.OK; };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        bar.Controls.Add(cancel); bar.Controls.Add(save);
        save.Location = new Point(bar.Width - 110, 10);
        cancel.Location = new Point(bar.Width - 216, 10);
        Controls.Add(bar);

        AcceptButton = save;
        CancelButton = cancel;
    }

    private static AppSettings Clone(AppSettings s)
    {
        var n = new AppSettings();
        n.Shortcuts = s.Shortcuts.Select(x => new ShortcutSetting { Action = x.Action, Ctrl = x.Ctrl, Alt = x.Alt, Shift = x.Shift, Vk = x.Vk }).ToList();
        n.ChatApiUrl = s.ChatApiUrl; n.ChatApiKey = s.ChatApiKey; n.ChatModel = s.ChatModel;
        n.SttApiUrl = s.SttApiUrl; n.SttApiKey = s.SttApiKey; n.SttModel = s.SttModel;
        n.ChatBg = s.ChatBg; n.SideBg = s.SideBg; n.PanelBg = s.PanelBg;
        n.TextColor = s.TextColor; n.TextMutedColor = s.TextMutedColor; n.Accent = s.Accent; n.Opacity = s.Opacity;
        return n;
    }

    // ---------- 快捷键页 ----------
    private TabPage BuildShortcutsPage()
    {
        var page = new TabPage("快捷键");
        var pan = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16, 20, 16, 8) };
        pan.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        pan.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var a in new (string act, string label)[]
        {
            ("hide", "显示 / 隐藏窗口"),
            ("shot", "选区截屏"),
            ("record", "按住录音"),
        })
        {
            var sc = _edit.Shortcuts.FirstOrDefault(s => s.Action == a.act) ?? new ShortcutSetting { Action = a.act };
            var lbl = new Label { Text = a.label, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
            var box = new KeyCaptureBox(sc);
            _rows.Add(new KeyRow(a.act, lbl, box));
            pan.RowCount++;
            pan.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            pan.Controls.Add(lbl);
            pan.Controls.Add(box);
        }
        pan.Controls.Add(new Label { Text = "点击右侧输入框后，按下新的组合键即可更改。" }, 0, pan.RowCount - 1);
        pan.SetColumnSpan(pan.GetControlFromPosition(0, pan.RowCount - 1), 2);

        var reset = new Button { Text = "一键恢复默认快捷键", AutoSize = true, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Bottom };
        reset.Click += (_, _) =>
        {
            var defs = AppSettings.DefaultShortcuts();
            foreach (var r in _rows)
            {
                var d = defs.First(x => x.Action == r.Act);
                r.Box.Set(d);
            }
        };
        page.Controls.Add(pan);
        page.Controls.Add(reset);
        return page;
    }

    private sealed class KeyRow
    {
        public string Act;
        public Label Lbl;
        public KeyCaptureBox Box;
        public KeyRow(string a, Label l, KeyCaptureBox b) { Act = a; Lbl = l; Box = b; }
    }

    /// <summary>按键捕获输入框。</summary>
    private sealed class KeyCaptureBox : TextBox
    {
        private readonly ShortcutSetting _sc;
        public KeyCaptureBox(ShortcutSetting sc)
        {
            _sc = sc;
            ReadOnly = true;
            Font = Theme.UI(11.5f);
            Width = 220;
            Height = 30;
            Text = sc.Label;
        }
        public void Set(ShortcutSetting s)
        {
            _sc.Ctrl = s.Ctrl; _sc.Alt = s.Alt; _sc.Shift = s.Shift; _sc.Vk = s.Vk;
            Text = s.Label;
        }
        protected override void OnEnter(EventArgs e) { Text = "请按下新组合键…"; base.OnEnter(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool mod = e.Control || e.Alt || e.Shift;
            int vk = (int)e.KeyCode;
            bool usable = ShortcutSetting.IsUsable(vk, mod);
            if (e.KeyCode == Keys.Escape)
            {
                Text = _sc.Label; e.SuppressKeyPress = true; base.OnKeyDown(e); return;
            }
            if (usable)
            {
                _sc.Ctrl = e.Control; _sc.Alt = e.Alt; _sc.Shift = e.Shift; _sc.Vk = vk;
                Text = _sc.Label;
                e.SuppressKeyPress = true;
                Select(_sc.Label.Length, 0);
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

    // ---------- LLM 页 ----------
    private TabPage BuildLlmPage()
    {
        var page = new TabPage("LLM 接入");
        var pan = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(16, 18, 16, 8) };
        pan.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        pan.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pan.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));

        void Add(int row, string label, TextBox box)
        {
            pan.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            pan.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            pan.Controls.Add(box, 1, row);
            pan.SetColumnSpan(box, 2);
        }
        pan.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        pan.Controls.Add(new Label { Text = "对话 LLM", Font = Theme.UI(12f, FontStyle.Bold), ForeColor = Theme.Accent }, 0, 0);
        pan.SetColumnSpan(pan.GetControlFromPosition(0, 0), 3);

        _chatUrl = new TextBox { Dock = DockStyle.Fill, Text = _edit.ChatApiUrl };
        _chatKey = new TextBox { Dock = DockStyle.Fill, Text = _edit.ChatApiKey, UseSystemPasswordChar = true };
        _chatModel = new TextBox { Dock = DockStyle.Fill, Text = _edit.ChatModel };
        Add(1, "接口地址", _chatUrl);
        Add(2, "API Key", _chatKey);
        Add(3, "模型名", _chatModel);
        _chatShow = new Button { Text = "显示", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
        _chatShow.Click += (_, _) => { _chatKey.UseSystemPasswordChar = !_chatKey.UseSystemPasswordChar; };
        pan.SetColumn(_chatShow, 2); pan.SetRow(_chatShow, 2);

        pan.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        pan.Controls.Add(new Label { Text = "语音转文字 (STT)", Font = Theme.UI(12f, FontStyle.Bold), ForeColor = Theme.Accent }, 0, 4);
        pan.SetColumnSpan(pan.GetControlFromPosition(0, 4), 3);

        _sttUrl = new TextBox { Dock = DockStyle.Fill, Text = _edit.SttApiUrl };
        _sttKey = new TextBox { Dock = DockStyle.Fill, Text = _edit.SttApiKey, UseSystemPasswordChar = true };
        _sttModel = new TextBox { Dock = DockStyle.Fill, Text = _edit.SttModel };
        Add(5, "接口地址", _sttUrl);
        Add(6, "API Key", _sttKey);
        Add(7, "模型名", _sttModel);
        _sttShow = new Button { Text = "显示", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
        _sttShow.Click += (_, _) => { _sttKey.UseSystemPasswordChar = !_sttKey.UseSystemPasswordChar; };
        pan.SetColumn(_sttShow, 2); pan.SetRow(_sttShow, 6);

        page.Controls.Add(pan);
        return page;
    }

    // ---------- 界面设置页 ----------
    private TabPage BuildUiPage()
    {
        var page = new TabPage("界面设置");
        var pan = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16, 18, 16, 8) };
        pan.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        pan.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _bgSw = AddColorRow(pan, "聊天背景", Color.FromArgb(_edit.ChatBg));
        _panelSw = AddColorRow(pan, "侧栏背景", Color.FromArgb(_edit.SideBg));
        _textSw = AddColorRow(pan, "文字颜色", Color.FromArgb(_edit.TextColor));
        _mutedSw = AddColorRow(pan, "次要文字", Color.FromArgb(_edit.TextMutedColor));
        _accentSw = AddColorRow(pan, "强调色", Color.FromArgb(_edit.Accent));

        pan.Controls.Add(new Label { Text = "应用透明度", Anchor = AnchorStyles.Left }, 0, pan.RowCount);
        var sliderRow = new Panel { Dock = DockStyle.Fill, Height = 40 };
        _opacity = new TrackBar { Minimum = 50, Maximum = 100, TickStyle = TickStyle.None, Height = 30, Width = 240 };
        _opacity.Value = (int)Math.Round(_edit.Opacity * 100);
        _opacityVal = new Label { Text = _edit.Opacity.ToString("0%"), Width = 60, TextAlign = ContentAlignment.MiddleLeft };
        _opacity.ValueChanged += (_, _) => _opacityVal.Text = (_opacity.Value / 100.0).ToString("0%");
        sliderRow.Controls.Add(_opacity);
        sliderRow.Controls.Add(_opacityVal);
        _opacity.Location = new Point(0, 4);
        _opacityVal.Location = new Point(250, 4);
        pan.Controls.Add(sliderRow, 1, pan.RowCount);
        pan.RowCount++;
        pan.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        page.Controls.Add(pan);
        return page;
    }

    private static ColorSwatch AddColorRow(TableLayoutPanel pan, string label, Color c)
    {
        pan.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left }, 0, pan.RowCount);
        var sw = new ColorSwatch(c);
        pan.Controls.Add(sw, 1, pan.RowCount);
        pan.RowCount++;
        pan.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        return sw;
    }

    private void ApplyUiPreview(bool apply)
    {
        _edit.ChatApiUrl = _chatUrl.Text.Trim();
        _edit.ChatApiKey = _chatKey.Text;
        _edit.ChatModel = _chatModel.Text.Trim();
        _edit.SttApiUrl = _sttUrl.Text.Trim();
        _edit.SttApiKey = _sttKey.Text;
        _edit.SttModel = _sttModel.Text.Trim();
        _edit.ChatBg = _bgSw.Color.ToArgb();
        _edit.SideBg = _panelSw.Color.ToArgb();
        _edit.TextColor = _textSw.Color.ToArgb();
        _edit.TextMutedColor = _mutedSw.Color.ToArgb();
        _edit.Accent = _accentSw.Color.ToArgb();
        _edit.Opacity = _opacity.Value / 100.0;
    }

    private sealed class ColorSwatch : Control
    {
        public Color Color { get; private set; }
        public ColorSwatch(Color c)
        {
            Color = c;
            Size = new Size(120, 26);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using var path = new GraphicsPath();
            int d = 12; path.AddArc(0, 0, d, d, 180, 90); path.AddArc(Width - d, 0, d, d, 270, 90);
            path.AddArc(Width - d, Height - d, d, d, 0, 90); path.AddArc(0, Height - d, d, d, 90, 90);
            path.CloseFigure();
            using var b = new SolidBrush(Color); g.FillPath(b, path);
            using var p = new Pen(Theme.Border); g.DrawPath(p, path);
            g.DrawString("点击选择颜色", Theme.UI(9.5f), Brushes.Gray, 12, 5);
            base.OnPaint(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                using var dlg = new ColorDialog { Color = Color, FullOpen = true };
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    Color = dlg.Color;
                    Invalidate();
                }
            }
            base.OnMouseClick(e);
        }
    }
}
