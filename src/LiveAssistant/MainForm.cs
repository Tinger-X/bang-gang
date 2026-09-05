using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>
/// 主窗口：1200×800 无边框 LLM 聊天主界面。
/// 左侧栏（品牌 / 搜索 / 会话列表）+ 右侧对话主区（气泡式 Markdown、输入框支持文件图片）。
/// 全局快捷键由设置驱动：默认 Alt+X 显隐、Alt+C 选区截屏进输入框、Alt+V 按住录音并自动把文件加入输入框。
/// 整窗对一切共享/录屏/截屏不可见（WDA_EXCLUDEFROMCAPTURE）。
/// 布局采用显式坐标（固定窗口尺寸），避免 Dock 顺序歧义。
/// </summary>
public class MainForm : Form
{
    public const string WindowTitle = "直播助手";
    public const string AppVersion = "v0.5.0";

    private const uint Affinity = Native.WDA_EXCLUDEFROMCAPTURE;
    private const int SideW = 304;
    private const int ChromeH = 38;

    private readonly AppSettings _settings;
    private readonly List<Conversation> _conversations = new();
    private Conversation? _active;

    // UI（显式布局）
    private readonly ChromeBar _chrome;
    private readonly Panel _sidebar;
    private readonly BrandBlock _brand;
    private readonly Panel _convHead;
    private readonly TextBox _searchBox;
    private readonly IconButton _searchIcon;
    private readonly IconButton _gear;
    private readonly ConvListBox _convList;
    private readonly Panel _mainArea;
    private readonly WelcomeView _welcome;
    private readonly Panel _chatUI;
    private readonly Label _convTitle;
    private readonly ChatView _chatView;
    private readonly InputPanel _input;

    // 定时器
    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly System.Windows.Forms.Timer _pttTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private AudioMixRecorder? _recorder;
    private bool _overlayActive;

    public MainForm()
    {
        _settings = AppSettings.Load(); // 内部已 ApplyTheme

        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 800);
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = Theme.WindowOpacity;
        BackColor = Theme.SideBg;
        DoubleBuffered = true;
        ApplyRoundRegion();

        // ---- 顶部工具条 ----
        _chrome = new ChromeBar();
        _chrome.DragRequested += BeginWindowDrag;
        _chrome.CloseRequested += Close;
        Controls.Add(_chrome);

        // ---- 左侧栏 ----
        _sidebar = new Panel { BackColor = Theme.SideBg };
        Controls.Add(_sidebar);

        _brand = new BrandBlock();
        _sidebar.Controls.Add(_brand);

        _convHead = new Panel { BackColor = Theme.SideBg };
        var label = new Label
        {
            Text = "对话", Font = Theme.UI(13f, FontStyle.Bold), ForeColor = Theme.TextMain,
            AutoSize = true, Location = new Point(20, 10),
        };
        var plus = new IconButton(IconButton.Kind.Plus) { Location = new Point(SideW - 42, 6) };
        new ToolTip().SetToolTip(plus, "新建对话");
        plus.Click += (_, _) => NewConversation();
        _convHead.Controls.Add(label);
        _convHead.Controls.Add(plus);
        _sidebar.Controls.Add(_convHead);

        _searchBox = new TextBox { BorderStyle = BorderStyle.FixedSingle, Font = Theme.UI(11.5f), ForeColor = Theme.TextMain, BackColor = Theme.InputBg };
        _searchBox.TextChanged += (_, _) => RebindConversations();
        _searchIcon = new IconButton(IconButton.Kind.Search) { Location = new Point(4, 4) };
        _gear = new IconButton(IconButton.Kind.Gear) { Location = new Point(SideW - 42, 4) };
        new ToolTip().SetToolTip(_gear, "设置");
        _gear.Click += (_, _) => ShowSettings();
        _sidebar.Controls.Add(_searchIcon);
        _sidebar.Controls.Add(_searchBox);
        _sidebar.Controls.Add(_gear);

        _convList = new ConvListBox();
        _convList.ConversationActivated += ActivateConversation;
        _convList.ConversationDeleted += DeleteConversation;
        _sidebar.Controls.Add(_convList);

        // ---- 右侧主区 ----
        _mainArea = new Panel { BackColor = Theme.ChatBg };
        Controls.Add(_mainArea);

        _chatUI = new Panel { BackColor = Theme.ChatBg };
        _convTitle = new Label { TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UI(13f, FontStyle.Bold), ForeColor = Theme.TextMain, Padding = new Padding(20, 0, 0, 0), BackColor = Theme.PanelBg };
        _chatView = new ChatView();
        _input = new InputPanel();
        _input.SendRequested += SendFromInput;
        _chatUI.Controls.Add(_convTitle);
        _chatUI.Controls.Add(_chatView);
        _chatUI.Controls.Add(_input);

        _welcome = new WelcomeView { BackColor = Theme.ChatBg };
        _welcome.StartRequested += () => NewConversation();
        _mainArea.Controls.Add(_chatUI);
        _mainArea.Controls.Add(_welcome);

        _chatUI.Visible = false;

        // ---- 布局 ----
        ApplyLayout();

        _guardTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _guardTimer.Tick += (_, _) => EnsureAffinity();
        _guardTimer.Start();

        _pttTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _pttTimer.Tick += (_, _) => PttTick();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _chrome.SetStatus(""); };

        AllowDrop = true;
        DragEnter += Main_DragEnter;
        DragDrop += Main_DragDrop;
        _welcome.BringToFront();
    }

    // ---------------- 显式布局 ----------------

    private void ApplyLayout()
    {
        int W = ClientSize.Width, H = ClientSize.Height;
        _chrome.Bounds = new Rectangle(0, 0, W, ChromeH);

        int bodyH = H - ChromeH;
        _sidebar.Bounds = new Rectangle(0, ChromeH, SideW, bodyH);

        _brand.Bounds = new Rectangle(0, 0, SideW, 138);
        _convHead.Bounds = new Rectangle(0, 138, SideW, 40);

        _searchBox.Bounds = new Rectangle(40, 150, SideW - 90, 30);
        _searchIcon.Bounds = new Rectangle(8, 152, 26, 26);
        _gear.Bounds = new Rectangle(SideW - 38, 152, 26, 26);

        int listTop = 190;
        _convList.Bounds = new Rectangle(0, listTop, SideW, bodyH - listTop);

        _mainArea.Bounds = new Rectangle(SideW, ChromeH, W - SideW, bodyH);
        _welcome.Bounds = new Rectangle(0, 0, W - SideW, bodyH);
        _chatUI.Bounds = new Rectangle(0, 0, W - SideW, bodyH);

        int mw = W - SideW;
        _convTitle.Bounds = new Rectangle(0, 0, mw, 48);
        _input.Bounds = new Rectangle(0, bodyH - 150, mw, 150);
        _chatView.Bounds = new Rectangle(0, 48, mw, bodyH - 48 - 150);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_chrome == null || _mainArea == null) return; // 构造期间
        if (Width > 0)
        {
            ApplyRoundRegion();
            ApplyLayout();
        }
    }

    private void ApplyRoundRegion()
    {
        const int r = 8;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    // ---------------- 拖动 ----------------

    private void BeginWindowDrag()
    {
        Win32.ReleaseCapture();
        _ = Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
    }

    // ---------------- 会话管理 ----------------

    private void NewConversation()
    {
        var c = new Conversation();
        _conversations.Add(c);
        ActivateConversation(c);
    }

    private void ActivateConversation(Conversation c)
    {
        _active = c;
        _convTitle.Text = string.IsNullOrWhiteSpace(c.Title) ? "新对话" : c.Title;
        _chatView.Load(c);
        _chatUI.Visible = true;
        _welcome.Visible = false;
        RebindConversations();
        _input.FocusInput();
    }

    private void DeleteConversation(Conversation c)
    {
        _conversations.Remove(c);
        if (_active == c)
        {
            _active = null;
            _chatUI.Visible = false;
            _welcome.Visible = true;
            _chatView.Load(null!);
        }
        RebindConversations();
    }

    private void RebindConversations()
    {
        string q = _searchBox.Text?.Trim() ?? "";
        List<Conversation> list;
        if (q.Length == 0)
        {
            list = _conversations.OrderByDescending(x => x.UpdatedAt).ToList();
        }
        else
        {
            list = _conversations
                .Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || x.Messages.Any(m => m.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.UpdatedAt).ToList();
        }
        _convList.Rebind(list, _active?.Id);
    }

    private void SendFromInput()
    {
        if (_active == null) return;
        var m = _input.Flush();
        if (m == null) return;
        _active.Messages.Add(m);
        _chatView.AddMessage(m);
        _active.RefreshTitle();
        RebindConversations();
        _convTitle.Text = _active.Title;
        _chrome.SetStatus("已发送，等待模型回复…（尚未接入 LLM）");
        ScheduleDemoReply();
    }

    private void ScheduleDemoReply()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 450 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (_active == null) return;
            var reply = new ChatMessage
            {
                Role = "assistant",
                Text = "（**对话模型尚未接入**，此处为占位回复。）\n\n"
                     + "配置好 LLM 后，这里将显示模型的 Markdown 回复。\n\n"
                     + "- 支持列表\n- 支持 `行内代码`\n\n"
                     + "```\n也支持代码块排版\n```"
            };
            _active.Messages.Add(reply);
            _chatView.AddMessage(reply);
            _active.RefreshTitle();
            RebindConversations();
        };
        timer.Start();
    }

    private void EnsureActive()
    {
        if (_active == null) NewConversation();
    }

    // ---------------- 拖放 / 粘贴 进输入框 ----------------

    private void Main_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data!.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
    }

    private void Main_DragDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop)) return;
        EnsureActive();
        foreach (string f in (string[])e.Data.GetData(DataFormats.FileDrop)!)
            _input.AddFile(f);
        _chrome.SetStatus("已添加到输入框");
    }

    // ---------------- 设置 ----------------

    private void ShowSettings()
    {
        var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.CopyFrom(dlg.Result);
            _settings.Save();
            _settings.ApplyTheme();
            ApplyThemeUi();
            ReapplyHotkeys();
            _chrome.SetStatus("设置已保存");
            _statusTimer.Stop();
            _statusTimer.Start();
        }
    }

    private void ApplyThemeUi()
    {
        Opacity = Theme.WindowOpacity;
        BackColor = Theme.SideBg;
        _sidebar.BackColor = Theme.SideBg;
        _mainArea.BackColor = Theme.ChatBg;
        _chatUI.BackColor = Theme.ChatBg;
        _welcome.BackColor = Theme.ChatBg;
        _convList.BackColor = Theme.SideBg;
        _convHead.BackColor = Theme.SideBg;
        _brand.BackColor = Theme.SideBg;
        _convTitle.BackColor = Theme.PanelBg;
        _convTitle.ForeColor = Theme.TextMain;
        _searchBox.BackColor = Theme.InputBg;
        _searchBox.ForeColor = Theme.TextMain;
        _input.RefreshTheme();
        if (_active != null) _chatView.Load(_active);
        RebindConversations();
        _convList.Invalidate();
        _welcome.Invalidate();
        _chrome.Invalidate();
        Invalidate();
    }

    // ---------------- 防录屏 ----------------

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyAffinity();
        ReapplyHotkeys();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _guardTimer.Stop();
        _pttTimer.Stop();
        _statusTimer.Stop();
        UnregisterHotkeys();
        _recorder?.Stop();
        base.OnFormClosed(e);
    }

    private void ApplyAffinity()
    {
        if (!Native.SetWindowDisplayAffinity(Handle, Affinity))
            _chrome.SetStatus("防录屏设置失败（需 Win10 2004+）");
    }

    private void EnsureAffinity()
    {
        if (Native.GetWindowDisplayAffinity(Handle, out uint cur) && cur != Affinity)
            Native.SetWindowDisplayAffinity(Handle, Affinity);
    }

    // ---------------- 全局快捷键 ----------------

    private void ReapplyHotkeys()
    {
        UnregisterHotkeys();
        for (int i = 0; i < _settings.Shortcuts.Count && i < 3; i++)
        {
            var sc = _settings.Shortcuts[i];
            if (!ShortcutSetting.IsUsable(sc.Vk, sc.Ctrl || sc.Alt || sc.Shift)) continue;
            int id = 0x201 + i;
            bool ok = Win32.RegisterHotKey(Handle, id, sc.Modifiers() | Win32.MOD_NOREPEAT, (uint)sc.Vk);
            if (!ok) _chrome.SetStatus($"热键 {sc.Label} 注册失败（可能被占用）");
        }
    }

    private void UnregisterHotkeys()
    {
        if (!IsHandleCreated) return;
        for (int i = 0; i < 3; i++) _ = Win32.UnregisterHotKey(Handle, 0x201 + i);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32() - 0x201;
            if (id >= 0 && id < _settings.Shortcuts.Count)
            {
                switch (_settings.Shortcuts[id].Action)
                {
                    case "hide": ToggleVisible(); break;
                    case "shot": StartScreenshot(); break;
                    case "record": BeginPttRecording(); break;
                }
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void ToggleVisible()
    {
        if (Visible) Hide();
        else { Show(); Activate(); }
    }

    // ---------------- Alt+截图 / 录音（联动输入框） ----------------

    private void StartScreenshot()
    {
        if (_overlayActive) return;
        _overlayActive = true;
        try
        {
            using var overlay = new ScreenshotOverlayForm();
            if (overlay.ShowDialog() == DialogResult.OK)
            {
                using var img = ScreenGrab.CaptureRegion(overlay.SelectedRectangle);
                if (img != null)
                {
                    string path = SaveTempPng(img);
                    EnsureActive();
                    try { Clipboard.SetImage(img); } catch { }
                    _input.Add(new Attachment { Kind = "image", Name = $"截图_{DateTime.Now:HHmmss}.png", Path = path });
                    _chrome.SetStatus("✓ 截图已加入输入框");
                }
            }
        }
        finally { _overlayActive = false; }
    }

    private static string SaveTempPng(Image img)
    {
        string dir = Path.Combine(Path.GetTempPath(), "LiveAssistant");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
        img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        return p;
    }

    private void BeginPttRecording()
    {
        if (_recorder?.IsRecording == true) return;
        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc == null) return;
        if (!ComboDown(sc)) return;

        try
        {
            string path = Path.Combine(GetRecordingsDir(), $"录音_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            var rec = new AudioMixRecorder();
            rec.Start(path);
            _recorder = rec;
            _chrome.SetStatus(rec.SystemOnlyMic ? "● 录音中（仅系统声音）…松开结束" : "● 正在录音（系统+麦克风）…松开结束");
            _pttTimer.Start();
        }
        catch (Exception ex)
        {
            _chrome.SetStatus("✗ 无法开始录音：" + ex.Message);
        }
    }

    private void PttTick()
    {
        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc != null && ComboDown(sc)) return;
        _pttTimer.Stop();
        StopRecording();
    }

    private static bool ComboDown(ShortcutSetting sc)
    {
        return (Win32.GetAsyncKeyState(sc.Vk) & Win32.KEY_DOWN) != 0
            && (!sc.Ctrl || (Win32.GetAsyncKeyState(0x11) & Win32.KEY_DOWN) != 0)
            && (!sc.Alt || (Win32.GetAsyncKeyState(0x12) & Win32.KEY_DOWN) != 0)
            && (!sc.Shift || (Win32.GetAsyncKeyState(0x10) & Win32.KEY_DOWN) != 0);
    }

    private void StopRecording()
    {
        var rec = _recorder;
        if (rec == null) return;
        _recorder = null;
        try
        {
            rec.Stop();
            EnsureActive();
            _input.AddFile(rec.SavePath);
            string note = rec.SystemOnlyMic ? "（仅系统声音）" : "";
            _chrome.SetStatus("✓ 录音已保存并加入输入框 " + note);
        }
        catch (Exception ex)
        {
            _chrome.SetStatus("✗ 保存录音出错：" + ex.Message);
        }
    }

    private static string GetRecordingsDir()
    {
        foreach (string baseDir in new[] { AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            try
            {
                string d = Path.Combine(baseDir, "recordings");
                Directory.CreateDirectory(d);
                return d;
            }
            catch { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "recordings");
    }
}

/// <summary>顶部工具条：品牌 + 状态 + 关闭；整条可拖动。</summary>
internal sealed class ChromeBar : Panel
{
    public event Action? DragRequested;
    public event Action? CloseRequested;
    public string StatusText { get; private set; } = "";
    private readonly Label _status;
    private IconButton _close = null!;

    public ChromeBar()
    {
        Height = 38;
        BackColor = Theme.PanelBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        var brand = new Label
        {
            Text = "● 直播助手",
            AutoSize = true,
            Font = Theme.UI(10.5f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Location = new Point(16, 9),
        };
        Controls.Add(brand);

        _status = new Label
        {
            AutoSize = true,
            Font = Theme.UI(9.5f),
            ForeColor = Theme.TextMuted,
            Location = new Point(180, 11),
        };
        Controls.Add(_status);

        _close = new IconButton(IconButton.Kind.Close);
        new ToolTip().SetToolTip(_close, "关闭");
        _close.Click += (_, _) => CloseRequested?.Invoke();
        Controls.Add(_close);

        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        brand.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        Resize += (_, _) => _close.Location = new Point(Width - 40, 5);
        _close.Location = new Point(Width - 40, 5);
    }

    public void SetStatus(string s)
    {
        StatusText = s;
        _status.Text = s;
        _status.ForeColor = s.Contains('●') ? Color.Crimson : Theme.TextMuted;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>左侧栏顶部品牌块：LOGO / 名称 / 作者版权 / 版本。</summary>
internal sealed class BrandBlock : Panel
{
    public BrandBlock()
    {
        BackColor = Theme.SideBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Accent))
            g.FillEllipse(bg, 18, 24, 52, 52);
        using (var f = new Font("Microsoft YaHei UI", 24f, FontStyle.Bold))
        {
            string c = "直";
            var sz = g.MeasureString(c, f);
            using var b = new SolidBrush(Color.White);
            g.DrawString(c, f, b, 18 + (52 - sz.Width) / 2, 24 + (52 - sz.Height) / 2);
        }
        using (var name = new SolidBrush(Theme.TextMain))
        using (var sub = new SolidBrush(Theme.TextMuted))
        {
            g.DrawString("直播助手", Theme.UI(15f, FontStyle.Bold), name, 84, 30);
            g.DrawString("LLM 聊天 · 防录屏助手", Theme.UI(9.5f), sub, 84, 56);
            g.DrawString(MainForm.AppVersion + " · © 2026 Tinger", Theme.UI(9f), sub, 18, 96);
        }
        using var line = new Pen(Theme.Border);
        g.DrawLine(line, 12, Height - 1, Width - 12, Height - 1);
        base.OnPaint(e);
    }
}

/// <summary>无对话时的欢迎页。</summary>
internal sealed class WelcomeView : Panel
{
    public event Action? StartRequested;
    public WelcomeView()
    {
        BackColor = Theme.ChatBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e) => StartRequested?.Invoke();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = ClientRectangle;

        using (var b = new SolidBrush(Theme.UserBubble))
            g.FillEllipse(b, r.Width / 2f - 44, r.Height / 2f - 130, 88, 88);
        using (var f = new Font("Microsoft YaHei UI", 34f, FontStyle.Bold))
        {
            string icon = "✦";
            var sz = g.MeasureString(icon, f);
            using var tb = new SolidBrush(Theme.Accent);
            g.DrawString(icon, f, tb, r.Width / 2f - sz.Width / 2, r.Height / 2f - 122);
        }

        string[] lines =
        {
            "欢迎使用 直播助手",
            "从左侧选择一个会话，或点击下方开始新的对话",
            "支持 Markdown、文件 / 图片、拖入与粘贴",
        };
        float y = r.Height / 2f + 8;
        for (int i = 0; i < lines.Length; i++)
        {
            using var b = i == 0 ? new SolidBrush(Theme.TextMain) : new SolidBrush(Theme.TextMuted);
            using var f = Theme.UI(i == 0 ? 18f : 11.5f);
            var sz = g.MeasureString(lines[i], f);
            g.DrawString(lines[i], f, b, r.Width / 2f - sz.Width / 2, y);
            y += sz.Height + (i == 0 ? 14 : 6);
        }

        var btn = new Rectangle((int)(r.Width / 2f - 92), (int)y + 6, 184, 38);
        using (var p = Rounded(btn, 19))
        using (var bb = new SolidBrush(Theme.Accent))
            g.FillPath(bb, p);
        using (var bf = Theme.UI(12.5f, FontStyle.Bold))
        {
            var sz = g.MeasureString("＋ 新建对话", bf);
            g.DrawString("＋ 新建对话", bf, Brushes.White, btn.X + (btn.Width - sz.Width) / 2, btn.Y + 8);
        }
        base.OnPaint(e);
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
