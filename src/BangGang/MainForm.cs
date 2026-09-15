using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 主窗口：1200×800 无边框 LLM 聊天主界面。
/// 左侧栏（品牌 / 搜索 / 会话列表）+ 右侧对话主区（气泡式 Markdown、输入框支持文件图片）。
/// 全局快捷键由设置驱动：默认 Alt+X 显隐、Alt+C 选区截屏进输入框、Alt+V 按住录音并自动把文件加入输入框。
/// 整窗对一切共享/录屏/截屏不可见（WDA_EXCLUDEFROMCAPTURE）。
/// 布局采用显式坐标（固定窗口尺寸），避免 Dock 顺序歧义。
/// </summary>
public class MainForm : Form
{
    public const string WindowTitle = "帮帮";
    public const string AppVersion = "v0.7.17";

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
    private readonly SearchField _search;
    private readonly IconButton _btnSettings;
    private readonly IconButton _btnNew;
    private readonly ConvListBox _convList;
    private readonly Panel _mainArea;
    private readonly WelcomeView _welcome;
    private readonly Panel _chatUI;
    private readonly Label _convTitle;
    private readonly ChatView _chatView;
    private readonly InputPanel _input;
    private readonly SettingsOverlay _settingsOverlay;
    private readonly WindowFrame _frame = new();

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
        _search = new SearchField { Location = new Point(10, 7), Size = new Size(SideW - 106, 32) };
        _search.Debounced += _ => RebindConversations();
        _convHead.Controls.Add(_search);

        _btnSettings = new IconButton(IconButton.Kind.Gear, Theme.SideBg) { Location = new Point(SideW - 86, 9) };
        _btnSettings.Click += (_, _) => OpenSettings();
        _convHead.Controls.Add(_btnSettings);

        _btnNew = new IconButton(IconButton.Kind.Plus, Theme.SideBg) { Location = new Point(SideW - 46, 9) };
        _btnNew.Click += (_, _) => NewConversation();
        _convHead.Controls.Add(_btnNew);
        _sidebar.Controls.Add(_convHead);

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
        _welcome.SuggestionRequested += s => { NewConversation(); _input.Text = s; _input.FocusInput(); };
        _mainArea.Controls.Add(_chatUI);
        _mainArea.Controls.Add(_welcome);

        _chatUI.Visible = false;

        // ---- 内部设置浮窗（固定居中、无蒙版；卡片以外的界面保持可见可用） ----
        _settingsOverlay = new SettingsOverlay();
        _settingsOverlay.Applied += OnSettingsApplied;
        _settingsOverlay.TopDragRequested += BeginWindowDrag;
        _settingsOverlay.DragStripHeight = ChromeH;
        _settingsOverlay.Visible = false;
        Controls.Add(_settingsOverlay);

        // ---- 布局 ----
        ApplyLayout();

        _guardTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _guardTimer.Tick += (_, _) => { EnsureAffinity(); WatchSystemTheme(); };
        _guardTimer.Start();

        _pttTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _pttTimer.Tick += (_, _) => PttTick();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _chrome.SetStatus(""); };

        AllowDrop = true;
        DragEnter += Main_DragEnter;
        DragDrop += Main_DragDrop;
        _welcome.BringToFront();

        // ---- 可选的主题色窗口边框（画在所有内容之上） ----
        _frame.Visible = Theme.WindowBorder;
        Controls.Add(_frame);
        _frame.BringToFront();

        // 应用内鼠标一律为箭头指针，且对后续新增控件同样生效。
        Ui.EnforceArrowCursor(this);
    }

    // ---------------- 显式布局 ----------------

    private void ApplyLayout()
    {
        int W = ClientSize.Width, H = ClientSize.Height;
        _chrome.Bounds = new Rectangle(0, 0, W, ChromeH);

        int bodyH = H - ChromeH;
        _sidebar.Bounds = new Rectangle(0, ChromeH, SideW, bodyH);

        // 品牌块 / 对话功能区 / 对话列表三区紧邻，压缩中间空白
        _brand.Bounds = new Rectangle(0, 0, SideW, 100);
        _convHead.Bounds = new Rectangle(0, 100, SideW, 46);

        int listTop = 146;
        _convList.Bounds = new Rectangle(0, listTop, SideW, bodyH - listTop);

        _mainArea.Bounds = new Rectangle(SideW, ChromeH, W - SideW, bodyH);
        _welcome.Bounds = new Rectangle(0, 0, W - SideW, bodyH);
        _chatUI.Bounds = new Rectangle(0, 0, W - SideW, bodyH);

        _settingsOverlay.Bounds = new Rectangle(0, 0, W, H);
        _frame.Bounds = new Rectangle(0, 0, W, H);

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

    /// <summary>窗口改为全直角（不做圆角裁剪）。</summary>
    private void ApplyRoundRegion()
    {
        Region = null;
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
        string q = _search.Text.Trim();
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

    // ---------------- 设置（固定居中的浮窗） ----------------

    private void OpenSettings()
    {
        _settingsOverlay.ReloadFrom(_settings);
        _settingsOverlay.BringToFront();
        _settingsOverlay.Visible = true;
        _settingsOverlay.Focus();
    }

    private void OnSettingsApplied(AppSettings s)
    {
        _settings.CopyFrom(s);
        _settings.Save();
        _settings.ApplyTheme();
        ApplyThemeUi();
        ReapplyHotkeys();
        _chrome.SetStatus("设置已保存");
        _statusTimer.Stop();
        _statusTimer.Start();
        _settingsOverlay.RefreshBackdrop();   // 让“设置已保存”在浮窗打开时也看得见
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
        _search.ApplyTheme();
        _settingsOverlay.ApplyTheme();
        _input.RefreshTheme();
        Ui.RestyleTree(this);          // 顶栏、图标按钮等缓存过颜色的控件统一刷新
        if (_active != null) _chatView.Load(_active);
        RebindConversations();
        _convList.Invalidate();
        _welcome.Invalidate();
        _chrome.Invalidate();
        _frame.Visible = Theme.WindowBorder;
        _frame.Invalidate();
        Invalidate(true);
    }

    /// <summary>“跟随系统”时定期检查系统亮暗色是否变化，变了就整体换肤。</summary>
    private void WatchSystemTheme()
    {
        if (_settings.ThemeMode != "system") return;
        bool wantDark = _settings.ResolveDark();
        if (wantDark == Theme.Dark) return;
        _settings.ApplyTheme();
        ApplyThemeUi();
    }

    // ---------------- 防录屏 ----------------

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyAffinity();
        CaptureProtector.Install();   // 保护对话框等所有顶层窗口
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
        if (CaptureGuard.Disabled) return;   // 本地界面调试：允许被截图
        if (!Native.SetWindowDisplayAffinity(Handle, Affinity))
            _chrome.SetStatus("防录屏设置失败（需 Win10 2004+）");
    }

    private void EnsureAffinity()
    {
        if (CaptureGuard.Disabled) return;
        if (Native.GetWindowDisplayAffinity(Handle, out uint cur) && cur != Affinity)
            Native.SetWindowDisplayAffinity(Handle, Affinity);
    }

    /// <summary>
    /// Alt+X 隐藏后再显示时，DWM 会丢失 WDA_EXCLUDEFROMCAPTURE 的“排除”表面，
    /// 窗口被录屏/截图时会退化为黑框（等价于 WDA_MONITOR），而 GetWindowDisplayAffinity
    /// 仍返回 0x11，导致上面的守卫定时器无法察觉。因此在窗口每次变为可见时，
    /// 先清空再重设，强制 DWM 重建排除表面，确保“完全不可见”持续生效。
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && IsHandleCreated)
        {
            if (!CaptureGuard.Disabled)
            {
                Native.SetWindowDisplayAffinity(Handle, Native.WDA_NONE);
                ApplyAffinity();
            }
            // 重设显示亲和性会触发 DWM 重建该窗口的合成表面，可能把它从
            // TOPMOST 层级中挤下来（虽然 WS_EX_TOPMOST 样式还在）。Activate()
            // 只会把窗口提升到“当前层级”的顶部，无法恢复被移出的层级。
            // 因此每次显示后都显式钉回最上层，避免被其它应用覆盖。
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
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
                bool settingsOpen = _settingsOverlay.Visible;
                switch (_settings.Shortcuts[id].Action)
                {
                    case "hide": if (!settingsOpen) ToggleVisible(); break;
                    case "shot": if (!settingsOpen) StartScreenshot(); break;
                    case "record": if (!settingsOpen) ToggleOrHoldRecording(); break;
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

    /// <summary>设置浮窗打开时，Esc 在任何位置都能收起它（浮窗非模态，焦点可能不在浮窗内）。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _settingsOverlay.Visible)
        {
            _settingsOverlay.CloseByEscape();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
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
        string dir = Path.Combine(Path.GetTempPath(), "BangGang");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
        img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        return p;
    }

    /// <summary>热键触发录音：按住模式需检测按键是否仍按下；按下模式只切换开始/停止。</summary>
    private void ToggleOrHoldRecording()
    {
        if (_settings.RecordMode == "toggle")
        {
            if (_recorder?.IsRecording == true) StopRecording();
            else StartRecording();
            return;
        }

        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc == null || !ComboDown(sc)) return;
        StartRecording();
    }

    private void StartRecording()
    {
        if (_recorder?.IsRecording == true) return;
        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc == null) return;

        try
        {
            string path = Path.Combine(GetRecordingsDir(), $"录音_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            var rec = new AudioMixRecorder();
            rec.Start(path);
            _recorder = rec;
            string tail = _settings.RecordMode == "toggle" ? "再按一次结束" : "松开结束";
            _chrome.SetStatus((rec.SystemOnlyMic ? "● 录音中（仅系统声音）…" : "● 正在录音（系统+麦克风）…") + tail);
            if (_settings.RecordMode != "toggle") _pttTimer.Start();
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

/// <summary>
/// 可选的主题色窗口边框：只保留窗口最外圈 2px 的直角环形区域，
/// 因此既画在全部内容之上，又不会遮挡任何界面。
/// </summary>
internal sealed class WindowFrame : Control
{
    public WindowFrame()
    {
        Enabled = false;          // 不拦截鼠标
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 4 || Height <= 4) return;
        const int t = 2;
        using var outer = new GraphicsPath();
        outer.AddRectangle(new Rectangle(0, 0, Width, Height));
        using var inner = new GraphicsPath();
        inner.AddRectangle(new Rectangle(t, t, Width - t * 2, Height - t * 2));
        using var region = new Region(outer);
        region.Exclude(inner);
        var old = Region;
        Region = region.Clone();
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(Theme.Mix(Theme.Accent, Theme.PanelBg, 0.3f));
        e.Graphics.FillRectangle(b, ClientRectangle);
        base.OnPaint(e);
    }
}

/// <summary>顶部工具条：品牌 + 状态 + 关闭；整条可拖动。</summary>
internal sealed class ChromeBar : Panel, IThemed
{
    public event Action? DragRequested;
    public event Action? CloseRequested;
    public string StatusText { get; private set; } = "";
    private readonly Label _status;
    private readonly Label _brand;
    private IconButton _close = null!;

    public ChromeBar()
    {
        Height = 38;
        BackColor = Theme.PanelBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        _brand = new Label
        {
            Text = "● 帮帮",
            AutoSize = true,
            Font = Theme.UI(10.5f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            BackColor = Theme.PanelBg,
            Location = new Point(16, 9),
        };
        Controls.Add(_brand);

        _status = new Label
        {
            AutoSize = true,
            Font = Theme.UI(9.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Theme.PanelBg,
            Location = new Point(180, 11),
        };
        Controls.Add(_status);

        _close = new IconButton(IconButton.Kind.Close, Theme.PanelBg);
        _close.Click += (_, _) => CloseRequested?.Invoke();
        Controls.Add(_close);

        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        _brand.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        Resize += (_, _) => _close.Location = new Point(Width - 40, 5);
        _close.Location = new Point(Width - 40, 5);
    }

    /// <summary>主题切换后重新着色（顶栏也要跟随暗色）。</summary>
    public void Restyle()
    {
        BackColor = Theme.PanelBg;
        _brand.BackColor = Theme.PanelBg;
        _brand.ForeColor = Theme.Accent;
        _status.BackColor = Theme.PanelBg;
        SetStatus(StatusText);
        _close.Restyle();
        Invalidate();
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
        var tile = new Rectangle(18, 24, 52, 52);
        using (var bg = new SolidBrush(Theme.Accent))
            g.FillEllipse(bg, tile);
        using (var f = new Font("Microsoft YaHei UI", 24f, FontStyle.Bold))
        using (var path = new GraphicsPath())
        {
            // 用字形墨迹（GraphicsPath）而不是 MeasureString 来居中：
            // MeasureString 量到的行框含有上下留白，按它居中会把文字顶偏（下方空隙更大）。
            float em = f.Size * g.DpiY / 72f;
            path.AddString("帮", f.FontFamily, (int)f.Style, em, new PointF(0, 0), StringFormat.GenericTypographic);
            var ink = path.GetBounds();
            var m = new Matrix();
            m.Translate(tile.X + tile.Width / 2f - (ink.X + ink.Width / 2f),
                        tile.Y + tile.Height / 2f - (ink.Y + ink.Height / 2f));
            path.Transform(m);
            m.Dispose();
            using var b = new SolidBrush(Color.White);
            g.FillPath(b, path);
        }
        using (var name = new SolidBrush(Theme.TextMain))
        using (var sub = new SolidBrush(Theme.TextMuted))
        {
            g.DrawString("帮帮", Theme.UI(15f, FontStyle.Bold), name, 84, 30);
            g.DrawString("© 2026 Tinger  ·  " + MainForm.AppVersion, Theme.UI(9.5f), sub, 84, 56);
        }
        using var line = new Pen(Theme.Border);
        g.DrawLine(line, 12, Height - 1, Width - 12, Height - 1);
        base.OnPaint(e);
    }
}

/// <summary>无对话时的欢迎页（参考主流 LLM 聊天工具的空态设计）。</summary>
internal sealed class WelcomeView : Panel
{
    public event Action? StartRequested;
    public event Action<string>? SuggestionRequested;

    private static readonly (string label, string prompt)[] Chips =
    {
        ("写周报", "帮我写一份本周工作周报，请先列出要点："),
        ("翻译润色", "请把下面这段话翻译成英文，再润色得地道一些："),
        ("代码审查", "请审查下面这段代码，指出问题并给出改进："),
    };

    private readonly Rectangle[] _chipRects = new Rectangle[Chips.Length];
    private Rectangle _startBtn;
    private int _hover = -1;

    public WelcomeView()
    {
        BackColor = Theme.ChatBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < _chipRects.Length; i++)
            if (_chipRects[i].Contains(e.Location)) { h = i; break; }
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            for (int i = 0; i < _chipRects.Length; i++)
                if (_chipRects[i].Contains(e.Location)) { SuggestionRequested?.Invoke(Chips[i].prompt); return; }
            if (_startBtn.Contains(e.Location)) { StartRequested?.Invoke(); return; }
            StartRequested?.Invoke();
        }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        float cx = Width / 2f;
        float top = Math.Max(60f, Height / 2f - 200);

        // LOGO
        var logo = new Rectangle((int)cx - 46, (int)top, 92, 92);
        using (var bg = new SolidBrush(Theme.Accent))
            g.FillEllipse(bg, logo);
        using (var lf = new Font("Microsoft YaHei UI", 38f, FontStyle.Bold))
        {
            string ch = "帮";
            var sz = g.MeasureString(ch, lf);
            g.DrawString(ch, lf, Brushes.White, logo.X + (logo.Width - sz.Width) / 2, logo.Y + (logo.Height - sz.Height) / 2 - 3);
        }

        float y = top + logo.Height + 26;
        using (var t1 = Theme.UI(22f, FontStyle.Bold))
        using (var tb = new SolidBrush(Theme.TextMain))
        {
            string s = "你好，我是帮帮";
            var sz = g.MeasureString(s, t1);
            g.DrawString(s, t1, tb, cx - sz.Width / 2, y);
            y += sz.Height + 8;
        }
        using (var t2 = Theme.UI(12.5f))
        using (var tb2 = new SolidBrush(Theme.TextMuted))
        {
            string s = "有什么可以帮你？支持文字、文件与图片，回复自动排版 Markdown。";
            var sz = g.MeasureString(s, t2);
            g.DrawString(s, t2, tb2, cx - sz.Width / 2, y);
            y += sz.Height + 34;
        }

        // 建议快捷方式（类似参考工具的引导入口）
        float chipY = y;
        var sizes = Chips.Select(c => g.MeasureString(c.label, Theme.UI(11.5f))).ToArray();
        float gap = 12;
        float totalW = sizes.Sum(s => s.Width) + Chips.Length * 46 + gap * (Chips.Length - 1);
        float x = cx - totalW / 2;
        for (int i = 0; i < Chips.Length; i++)
        {
            float cw = sizes[i].Width + 46;
            var rect = new Rectangle((int)x, (int)chipY, (int)cw, 34);
            _chipRects[i] = rect;
            bool over = i == _hover;
            using (var path = Rounded(rect, 17))
            using (var bb = new SolidBrush(over ? Theme.UserBubble : Theme.AsstBubble))
            {
                g.FillPath(bb, path);
                if (over)
                {
                    using var bp = new Pen(Theme.Accent, 1.2f);
                    g.DrawPath(bp, path);
                }
            }
            using (var cf = Theme.UI(11.5f))
            {
                var sz = g.MeasureString(Chips[i].label, cf);
                g.DrawString(Chips[i].label, cf, new SolidBrush(Theme.TextMain), rect.X + (rect.Width - sz.Width) / 2, rect.Y + (rect.Height - sz.Height) / 2);
            }
            x += cw + gap;
        }
        y = chipY + 34 + 26;

        // 新建对话按钮
        var btn = new Rectangle((int)(cx - 90), (int)y, 180, 40);
        _startBtn = btn;
        using (var p = Rounded(btn, 20))
        using (var bb = new SolidBrush(Theme.Accent))
            g.FillPath(bb, p);
        using (var bf = Theme.UI(12.5f, FontStyle.Bold))
        {
            string t = "＋  新建对话";
            var sz = g.MeasureString(t, bf);
            g.DrawString(t, bf, Brushes.White, btn.X + (btn.Width - sz.Width) / 2, btn.Y + 9);
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
