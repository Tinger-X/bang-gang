using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 主窗口：默认 1200×800 无边框 LLM 聊天主界面。
/// 左侧栏（品牌 / 搜索 / 会话列表）+ 右侧对话主区（气泡式 Markdown、输入框支持文件图片）。
/// 全局快捷键由设置驱动：默认 Alt+X 显隐、Alt+C 选区截屏进输入框、Alt+V 按住录音并自动把文件加入输入框。
/// 整窗对一切共享/录屏/截屏不可见（WDA_EXCLUDEFROMCAPTURE）。
/// 布局采用显式坐标，全部由 <see cref="ApplyLayout"/> 从当前客户区尺寸算出，随窗口缩放实时重排。
/// 无边框所以没有系统给的边框可抓，缩放由 <see cref="PreFilterMessage"/> 自己接手，见那里的注释。
/// </summary>
public partial class MainForm : Form, IMessageFilter
{
    public const string WindowTitle = "帮帮";
    public const string AppVersion = "v0.9.22";

    private const uint Affinity = Native.WDA_EXCLUDEFROMCAPTURE;

    /// <summary>
    /// 左侧栏**展开时**的宽度。栏内从左到右依次是搜索框（<c>SideW - 106</c> 宽）和两个 28px
    /// 图标按钮（右边距分别为 86 / 46），所以改这个数之前先确认那三样还放得下。
    ///
    /// 栏内控件的几何一律按这个数算、固定不变，收起时的收窄由 <see cref="ApplyLayout"/>
    /// 只改父面板宽度完成 —— 父控件会裁掉超出的子控件，于是动画中途不会出现 0 宽 /
    /// 负宽的控件，也就不必到处写 <c>Math.Max(0, ...)</c>。
    /// </summary>
    private const int SideW = 256;

    /// <summary>侧栏收起 / 展开动画的帧间隔（毫秒）。</summary>
    private const int SideAnimMs = 18;

    /// <summary>
    /// 侧栏收起 / 展开动画的**墙钟总时长**（毫秒）。动画是时间驱动的：每一帧按
    /// 「已过时间 / 总时长」走 ease-out cubic 算出目标宽度（见 <c>MainForm.Sidebar.cs</c>
    /// 的 <c>SideTick</c>），所以 WM_TIMER 的节拍漂移（18ms 的请求实测落成 ~31ms 一拍）
    /// 只是让那一帧的采样点更远，墙钟总时长不变。之前「每帧消掉剩余距离的 28%」是
    /// 帧率驱动的 —— tick 一晚到，同样的步数就要花更长的墙钟，画面上就是节拍在抖。
    /// </summary>
    private const int SideAnimDurMs = 300;

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
    private readonly IconButton _btnSideToggle;
    private readonly IconButton _btnRename;
    private readonly TitleEditor _titleEdit;
    private readonly ChatView _chatView;
    private readonly InputPanel _input;
    private readonly SettingsOverlay _settingsOverlay;
    private readonly ImageViewer _viewer = new();
    private readonly WindowFrame _frame = new();

    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly System.Windows.Forms.Timer _pttTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _sideTimer;

    // 录音转写状态在 MainForm.Recording.cs（_dictation 等）
    private bool _overlayActive;

    public MainForm()
    {
        _settings = AppSettings.Load(); // 内部已 ApplyTheme

        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 800);
        MinimumSize = MinWindow;
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
        _chrome.MaximizeRequested += ToggleMaximize;
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

        // 主区铺满整窗（左右都到边，见 ApplyLayout），左侧栏得盖在它**上面**才露得出
        // 「侧栏那一截」，所以主区必须排在侧栏后面。这里本来就是后加的、天然在后面，
        // 写出来的目的是把这条不变量钉住 —— ApplyLayout 的铺满式布局依赖它。
        // 不能反过来调 _sidebar.BringToFront()：那是把侧栏排到同级最前，会一起盖住
        // _settingsOverlay 和 _frame 画在窗口左缘的那一条描边。
        Controls.SetChildIndex(_mainArea, Controls.Count - 1);

        _chatUI = new Panel { BackColor = Theme.ChatBg };
        _convTitle = new Label { TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Font = Theme.UI(13f, FontStyle.Bold), ForeColor = Theme.TextMain, BackColor = Theme.PanelBg };
        _chatView = new ChatView();
        _input = new InputPanel();
        _input.SendRequested += SendFromInput;
        _input.StopRequested += StopReply;
        // 附件列表区的出现 / 消失要改整块输入区的高度，得重摆一次（消息区跟着让位）。
        // 用 lambda 而不是方法组：ApplyLayout 现在多了一个可选参数，方法组转不成 Action。
        _input.LayoutChanged += () => ApplyLayout();
        // 拒收文件之类的提示走顶栏那条 3 秒状态条 —— 全应用就这一个「临时说一句」的出口。
        _input.Notice += FlashStatus;
        _chatView.ImagePressed += OpenImage;
        _chatUI.Controls.Add(_convTitle);
        _chatUI.Controls.Add(_chatView);
        _chatUI.Controls.Add(_input);

        // 顶栏左侧的「收起 / 展开左侧栏」。标题居中靠的是 Label 两侧对称的内边距，
        // 所以这个按钮的宽度必须和内边距对得上（见 ApplyLayout 里的 ConvTitlePadX）。
        //
        // BackdropOwner 必须显式指到标题条上：这个圆钮的孩子身份是 _chatUI，画却落在兄弟
        // 控件 _convTitle 上。不指的话 Restyle() 会取 ChatBg 去填四角，在标题条（PanelBg）上
        // 留下一圈异色的方块 —— 平时看不出来（构造时传的是对的 PanelBg），只有换主题后被
        // Restyle 重刷一次才露出来，正是用户看到的「非圆角部分没跟着主题走」。
        _btnSideToggle = new IconButton(IconButton.Kind.Collapse, Theme.PanelBg)
        {
            Location = new Point(10, 10),
            BackdropOwner = _convTitle,
        };
        _btnSideToggle.Click += (_, _) => ToggleSidebar();
        _chatUI.Controls.Add(_btnSideToggle);
        _btnSideToggle.BringToFront();      // 标题横跨整条，别把它压住

        // 顶栏右端的「修改标题」：标题条的三分结构里右边那一分，和左边那枚收起按钮对称
        // （几何见 ApplyLayout，两边的内边距是同一个 ConvTitlePadX）。
        //
        // BackdropOwner 同样要显式指到标题条上，理由与左边那枚一模一样（见上面那段注释）。
        _btnRename = new IconButton(IconButton.Kind.Edit, Theme.PanelBg)
        {
            Location = new Point(10, 10),
            BackdropOwner = _convTitle,
        };
        _btnRename.Click += (_, _) => BeginRenameTitle();
        _chatUI.Controls.Add(_btnRename);
        _btnRename.BringToFront();
        // 一条会话都还没开，自然没得改（见 CanRenameTitle）。
        RefreshRenameButton();

        // 就地改标题的输入条。常驻在控件树上、平时不可见（见 TitleEditor 的类注释），
        // 收起 / 展开靠 Visible，不在这里增删控件。
        _titleEdit = new TitleEditor();
        _titleEdit.Committed += CommitRename;
        _titleEdit.Cancelled += CancelRename;
        _chatUI.Controls.Add(_titleEdit);

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

        // 浮窗铺满整窗，会吃掉包括顶栏按钮在内的所有点击；把按钮的矩形交给它，
        // 由它在自己的 Region 上挖掉这几块，于是设置打开期间也能直接最大化 / 关闭。
        // 挂在事件上而不是布局里读一次：工具条改宽会重新摆按钮，缓存才不会过期。
        _chrome.ChromeButtonsMoved += () => _settingsOverlay.AppChromeHoles = _chrome.ChromeHoleBounds;
        _settingsOverlay.AppChromeHoles = _chrome.ChromeHoleBounds;

        // 用户在「放弃未保存的修改」上选了「放弃并退出」：浮窗已经收好了，这里只需真正关窗。
        // 置位 _forceClose 让 OnFormClosing 不再拦一次（否则会再弹一遍确认条）。
        _settingsOverlay.AppQuit += () => { _forceClose = true; Close(); };

        // ---- 布局 ----
        ApplyLayout();

        // ---- 上次的会话（放在布局之后：恢复要按已排好的尺寸摆气泡、滚到底） ----
        RestoreConversations();

        // ---- 图片放大查看（盖在所有内容之上，含窗口边框） ----
        Controls.Add(_viewer);
        _guardTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _guardTimer.Tick += (_, _) => { EnsureAffinity(); WatchSystemTheme(); };
        _guardTimer.Start();

        _pttTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _pttTimer.Tick += (_, _) => PttTick();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _chrome.SetStatus(""); };

        _sideTimer = new System.Windows.Forms.Timer { Interval = SideAnimMs };
        _sideTimer.Tick += (_, _) => SideTick();

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

        // 边缘缩放：无边框窗口没有系统边框可抓，由这个消息过滤器自己接手（见 FilterMouse）。
        Application.AddMessageFilter(this);
    }

    /// <summary>用户已经在确认条上选了「放弃并退出」，这一次 FormClosing 直接放行。</summary>
    private bool _forceClose;

    /// <summary>
    /// 顶栏那条 3 秒提示：说一句，3 秒后自己消失。全应用「临时说一句」的唯一出口。
    ///
    /// 计时器先停再启（而不是只 Start）：连着说两句时，第二句要拿到完整的 3 秒，
    /// 否则第一句的尾巴会把第二句提前收走。
    ///
    /// 写成一个方法而不是就地展开，还有一层原因：<c>_statusTimer</c> 是构造函数后半段才建的，
    /// 订阅 <c>_input.Notice</c> 时它还是 null —— 内联在构造函数的 lambda 里，
    /// 编译器会按「此刻它可能是 null」报一条 CS8602，尽管 lambda 要等到运行时才跑。
    /// 换成方法调用，跨过方法边界后这份流分析就不成立了，警告消失，也不必给它加个假的初值。
    /// </summary>
    private void FlashStatus(string text)
    {
        _chrome.SetStatus(text);
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>
    /// 真的可以关窗了吗。设置浮窗开着且有未保存内容时，这里只负责把确认条弹出来
    /// 并取消本次关闭 —— 用户点「放弃并退出」后走 <c>AppQuit</c>，由它置位
    /// <see cref="_forceClose"/> 再关一次，那时这里直接放行。
    ///
    /// 挂在 FormClosing 而不是关闭按钮上：Alt+F4、任务栏右键关闭走的是同一条路，
    /// 只守按钮会漏掉它们。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceClose && !_settingsOverlay.RequestAppClose())
        {
            e.Cancel = true;      // 确认条已弹出，等用户选
            return;
        }
        base.OnFormClosing(e);
    }


    // ---------------- 设置（固定居中的浮窗） ----------------

    // ---------------- 图片放大查看 ----------------

    /// <summary>
    /// 点开了对话里的某张图。读不出原图（临时文件被清掉了）就什么都不做 ——
    /// 不许弹一个空白蒙版把用户关在里面。
    /// </summary>
    private void OpenImage(Attachment a)
    {
        string? path = a.Path;
        if (path == null) return;
        if (!_viewer.Open(path, a.Name ?? "")) FlashStatus("这张图打不开了：源文件已不在原位");
    }

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
        FlashStatus("设置已保存");
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

        // Debug + BANGGANG_DUMP=1 时，把对话区离屏画成 PNG。
        //
        // 走的是 DrawToBitmap（WM_PRINT），**不经过屏幕抓图** —— 气泡不是控件，
        // 截图是唯一能看到它内部排版的常规手段，而截图依赖「桌面还能画」这件事。
        // 桌面挂了（驱动崩了、远程会话断了）的时候，这条路是唯一还能看见排版的入口，
        // 而断掉的样子与「排版坏了」一模一样。和 SettingsOverlay 里那两个 dump 同一套。
        Trace.DumpAfter(_chatView, "dbg-chat", 1600);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);   // 别让缩放过滤器比窗口活得久
        _guardTimer.Stop();
        _pttTimer.Stop();
        _statusTimer.Stop();
        _sideTimer.Stop();
        SideClockEnd();   // 动画途中关窗也要把 timeBeginPeriod 还回去
        CancelStream();
        UnregisterHotkeys();
        _dictation?.Abort();
        base.OnFormClosed(e);
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


}
