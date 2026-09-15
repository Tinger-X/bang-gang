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
public class MainForm : Form, IMessageFilter
{
    public const string WindowTitle = "帮帮";
    public const string AppVersion = "v0.7.25";

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

    /// <summary>每帧消掉剩余距离的比例。0.28 大约 14 帧（≈250ms）走完，是一条缓出曲线。</summary>
    private const double SideAnimEase = 0.28;

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
    private readonly ChatView _chatView;
    private readonly InputPanel _input;
    private readonly SettingsOverlay _settingsOverlay;
    private readonly WindowFrame _frame = new();

    // 定时器
    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly System.Windows.Forms.Timer _pttTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _sideTimer;

    private AudioMixRecorder? _recorder;
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

        _chatUI = new Panel { BackColor = Theme.ChatBg };
        _convTitle = new Label { TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Font = Theme.UI(13f, FontStyle.Bold), ForeColor = Theme.TextMain, BackColor = Theme.PanelBg };
        _chatView = new ChatView();
        _input = new InputPanel();
        _input.SendRequested += SendFromInput;
        _chatUI.Controls.Add(_convTitle);
        _chatUI.Controls.Add(_chatView);
        _chatUI.Controls.Add(_input);

        // 顶栏左侧的「收起 / 展开左侧栏」。标题居中靠的是 Label 两侧对称的内边距，
        // 所以这个按钮的宽度必须和内边距对得上（见 ApplyLayout 里的 ConvTitlePadX）。
        _btnSideToggle = new IconButton(IconButton.Kind.Collapse, Theme.PanelBg) { Location = new Point(10, 10) };
        _btnSideToggle.Click += (_, _) => ToggleSidebar();
        _chatUI.Controls.Add(_btnSideToggle);
        _btnSideToggle.BringToFront();      // 标题横跨整条，别把它压住

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

    // ---------------- 显式布局 ----------------

    /// <summary>
    /// 顶栏标题两侧对称的内边距：等于「按钮左间距 + 按钮宽 + 一点余量」，
    /// 左右一样宽，于是 <c>MiddleCenter</c> 出来的标题正好落在整条的几何中线上，
    /// 又不会被左边的按钮压住。
    /// </summary>
    private const int ConvTitlePadX = 44;

    private void ApplyLayout()
    {
        int W = ClientSize.Width, H = ClientSize.Height;
        _chrome.Bounds = new Rectangle(0, 0, W, ChromeH);

        int bodyH = H - ChromeH;

        // 侧栏宽 = 当前动画值。栏内所有子控件仍按展开时的 SideW 摆，
        // 收窄全靠父面板裁剪 —— 见 SideW 的注释。
        int sw = _sideW;
        _sidebar.Bounds = new Rectangle(0, ChromeH, sw, bodyH);
        _sidebar.Visible = sw > 0;

        // 品牌块 / 对话功能区 / 对话列表三区紧邻，压缩中间空白
        _brand.Bounds = new Rectangle(0, 0, SideW, 100);
        _convHead.Bounds = new Rectangle(0, 100, SideW, 46);
        _search.Size = new Size(SideW - 106, 32);
        _btnSettings.Location = new Point(SideW - 86, 9);
        _btnNew.Location = new Point(SideW - 46, 9);

        int listTop = 146;
        _convList.Bounds = new Rectangle(0, listTop, SideW, bodyH - listTop);

        _mainArea.Bounds = new Rectangle(sw, ChromeH, W - sw, bodyH);
        _welcome.Bounds = new Rectangle(0, 0, W - sw, bodyH);
        _chatUI.Bounds = new Rectangle(0, 0, W - sw, bodyH);

        _settingsOverlay.Bounds = new Rectangle(0, 0, W, H);
        _frame.Bounds = new Rectangle(0, 0, W, H);

        int mw = W - sw;
        _convTitle.Bounds = new Rectangle(0, 0, mw, 48);
        _convTitle.Padding = new Padding(ConvTitlePadX, 0, ConvTitlePadX, 0);
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

    // ---------------- 左侧栏收起 / 展开 ----------------

    private int _sideW = SideW;          // 当前动画宽度，0 = 完全收起
    private int _sideTarget = SideW;     // 动画目标

    /// <summary>
    /// 收起 / 展开左侧栏。宽度不是一下跳过去的：<see cref="_sideTimer"/> 每帧把
    /// <see cref="_sideW"/> 往目标推掉剩余距离的一部分，走一条缓出曲线。
    /// 图标在这一刻就翻转（而不是等动画结束），点下去马上有反馈。
    /// </summary>
    private void ToggleSidebar()
    {
        bool collapse = _sideTarget > 0;             // 当前是展开的 -> 这一次要收起
        _sideTarget = collapse ? 0 : SideW;
        _btnSideToggle.Icon = collapse ? IconButton.Kind.Expand : IconButton.Kind.Collapse;
        _btnSideToggle.Invalidate();
        _sideTimer.Start();                          // 重复点只是换目标，不会叠出第二个动画
    }

    private void SideTick()
    {
        int d = _sideTarget - _sideW;

        // 收尾：snap 到整数目标并停表。不能只判 d == 0 —— 指数逼近永远差一点点。
        if (Math.Abs(d) <= 2)
        {
            _sideW = _sideTarget;
            _sideTimer.Stop();
        }
        else
        {
            _sideW += (int)Math.Round(d * SideAnimEase);
        }

        ApplyLayout();
    }

    /// <summary>
    /// 确保侧栏是展开的。<see cref="ToggleSidebar"/> 的按钮长在「对话栏」顶栏上，而那个
    /// 顶栏只在有会话时可见，所以「侧栏收起 + 没有会话」是个死局：既没有按钮，也没有
    /// 行可以点。
    ///
    /// 这个死局目前进不去 —— 侧栏一收，会话列表就跟着被父面板裁掉（不显示也点不到），
    /// 删不掉最后一个会话。所以这里是一条保险：只要「活跃会话没了」这件事还能从别的
    /// 路径发生，退到欢迎页时就把侧栏一并展开，不留一个回不来的界面。
    /// </summary>
    private void EnsureSidebarOpen()
    {
        if (_sideTarget > 0) return;
        _sideTimer.Stop();
        _sideTarget = SideW;
        _sideW = SideW;
        _btnSideToggle.Icon = IconButton.Kind.Collapse;
        ApplyLayout();
    }

    // ---------------- 拖动 ----------------

    private void BeginWindowDrag()
    {
        Win32.ReleaseCapture();
        _ = Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
    }

    // ---------------- 最大化 / 还原 ----------------

    private bool _maximized;
    private Rectangle _restoreBounds;

    /// <summary>
    /// 最大化 / 还原。窗口没有系统标题栏，<c>WindowState.Maximized</c> 在无边框窗口上
    /// 铺满的是整块屏幕（连任务栏一起盖住），要靠 <c>MaximizedBounds</c> 再掰回来，
    /// 而那个属性对 <see cref="FormBorderStyle.None"/> 是否生效并不确定。
    /// 所以这里自己定义这件事，和拖动 / 缩放一脉相承：记下当前矩形，铺满**当前显示器的
    /// 工作区**（任务栏留着），再点一次回到原来的矩形。
    /// </summary>
    private void ToggleMaximize()
    {
        if (_maximized)
        {
            _maximized = false;
            if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0) Bounds = _restoreBounds;
        }
        else
        {
            _restoreBounds = Bounds;
            _maximized = true;
            Bounds = Screen.FromControl(this).WorkingArea;
        }
        _chrome.SetMaximized(_maximized);
    }

    // ---------------- 缩放 ----------------

    /// <summary>
    /// 边缘抓手宽度（像素）。无边框窗口没有可抓的边框，这一圈就是那条隐形的边框：
    /// 鼠标落在外沿这么多像素以内按下，就算抓住了这一条边。
    /// </summary>
    private const int GripPx = 6;

    /// <summary>
    /// 窗口尺寸下限 = 一整个**原尺寸**的设置卡片 + 四周留白。
    ///
    /// 设置浮窗的卡片是 <c>min(CardW, max(520, Width - 96))</c>，窗口小于「卡片 + 留白」时
    /// 卡片就跟着缩水，里面的输入框被挤窄、行数被裁。下限取卡片设计尺寸加留白，
    /// 设置界面于是任何时候都是完整的一张，不必再缩。
    ///
    /// 屏幕比这个下限还小时（小笔记本）按工作区收一收：宁可卡片缩水，
    /// 也不能让窗口大过屏幕 —— 无边框窗口没有标题栏，一旦超出就再也拖不回来了。
    /// </summary>
    private static readonly Size MinWindow = ComputeMinWindow();

    private static Size ComputeMinWindow()
    {
        int w = SettingsOverlay.CardW + SettingsOverlay.CardMargin;
        int h = SettingsOverlay.CardH + SettingsOverlay.CardMargin;
        var screen = Screen.PrimaryScreen;
        if (screen != null)
        {
            w = Math.Min(w, Math.Max(480, screen.WorkingArea.Width - 40));
            h = Math.Min(h, Math.Max(360, screen.WorkingArea.Height - 40));
        }
        return new Size(w, h);
    }

    [Flags]
    private enum Edge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private Edge _grip = Edge.None;      // 没在缩放时是 None
    private Point _gripFrom;             // 按下时的屏幕坐标
    private Rectangle _gripStart;        // 按下时的窗口矩形

    /// <summary>
    /// 无边框窗口没有系统边框，缩放自然也无从触发。这里在**消息队列这一层**拦一道：
    /// 落在窗口外沿 <see cref="GripPx"/> 像素以内的左键按下，改成拖窗口边界，
    /// 而不是交给边缘底下那个控件。
    ///
    /// 挂消息过滤器而不是逐个控件挂 MouseDown：窗口四边分别被工具条 / 侧栏 / 主区 /
    /// 会话列表等好几个控件压着，逐个挂既要覆盖整棵树、又会漏掉以后新加的自绘控件。
    /// 过滤器只有一个入口，谁压在最上面都一样。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (_grip != Edge.None)
        {
            if (m.Msg == Win32.WM_MOUSEMOVE) { ApplyResize(Cursor.Position); return true; }
            if (m.Msg == Win32.WM_LBUTTONUP || m.Msg == Win32.WM_CAPTURECHANGED)
            {
                _grip = Edge.None;
                Win32.ReleaseCapture();
                return true;
            }
            return false;
        }

        if (m.Msg != Win32.WM_LBUTTONDOWN) return false;
        if (_maximized) return false;     // 四条边都贴着工作区，没有可拖的余地
        if (!BelongsToThisForm(m.HWnd)) return false;

        var edge = EdgeAt(Cursor.Position);
        if (edge == Edge.None) return false;

        _grip = edge;
        _gripFrom = Cursor.Position;
        _gripStart = Bounds;
        Win32.SetCapture(Handle);
        return true;      // 这一下是抓边框，别再让底下的按钮也响应一次
    }

    /// <summary>鼠标（屏幕坐标）压在外沿的哪一条边上；角上会同时命中两条。</summary>
    private Edge EdgeAt(Point screen)
    {
        var r = RectangleToScreen(ClientRectangle);
        var e = Edge.None;
        if (screen.X < r.Left + GripPx) e |= Edge.Left;
        else if (screen.X >= r.Right - GripPx) e |= Edge.Right;
        if (screen.Y < r.Top + GripPx) e |= Edge.Top;
        else if (screen.Y >= r.Bottom - GripPx) e |= Edge.Bottom;
        return e;
    }

    /// <summary>
    /// 按鼠标位移重算窗口矩形。撞到 <see cref="MinWindow"/> 时让**被拖的那条边**停住、
    /// 对面那条边不动 —— 少了这一步，继续拖会让窗口一边缩一边朝反方向跑。
    /// </summary>
    private void ApplyResize(Point screen)
    {
        int dx = screen.X - _gripFrom.X, dy = screen.Y - _gripFrom.Y;
        var s = _gripStart;
        int l = s.Left, t = s.Top, w = s.Width, h = s.Height;

        if ((_grip & Edge.Left) != 0) { l += dx; w -= dx; }
        if ((_grip & Edge.Right) != 0) w += dx;
        if ((_grip & Edge.Top) != 0) { t += dy; h -= dy; }
        if ((_grip & Edge.Bottom) != 0) h += dy;

        if (w < MinWindow.Width)
        {
            if ((_grip & Edge.Left) != 0) l = s.Right - MinWindow.Width;
            w = MinWindow.Width;
        }
        if (h < MinWindow.Height)
        {
            if ((_grip & Edge.Top) != 0) t = s.Bottom - MinWindow.Height;
            h = MinWindow.Height;
        }

        Bounds = new Rectangle(l, t, w, h);
    }

    /// <summary>
    /// 这个 HWND 是主窗口自己还是它的后代。截图浮窗之类的**另外的**顶层窗口走的是同一个
    /// 消息队列，不筛一下会把它们上面的点击也当成抓边框吃掉。
    /// </summary>
    private bool BelongsToThisForm(IntPtr h)
    {
        for (var c = Control.FromHandle(h); c != null; c = c.Parent)
            if (ReferenceEquals(c, this)) return true;
        return false;
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
            EnsureSidebarOpen();     // 顶栏（连同收起按钮）没了，侧栏就得自己回来
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
        Application.RemoveMessageFilter(this);   // 别让缩放过滤器比窗口活得久
        _guardTimer.Stop();
        _pttTimer.Stop();
        _statusTimer.Stop();
        _sideTimer.Stop();
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

/// <summary>顶部工具条：品牌 + 状态 + 最大化 + 关闭；整条可拖动。</summary>
internal sealed class ChromeBar : Panel, IThemed
{
    public event Action? DragRequested;
    public event Action? CloseRequested;
    public event Action? MaximizeRequested;
    public string StatusText { get; private set; } = "";
    private readonly Label _status;
    private readonly Label _brand;
    private readonly IconButton _max;
    private readonly IconButton _close;

    private const int BtnSize = 28;
    private const int BtnTop = 5;
    private const int CloseRight = 12;    // 关闭按钮右边缘到工具条右端的空档
    private const int BtnGap = 6;         // 关闭与最大化之间的空档

    /// <summary>
    /// 「设置打开期间也要能点」的那些按钮（最大化 / 关闭）相对本工具条的矩形。
    /// 工具条贴在客户区左上角，所以这份坐标直接就是主窗口客户区坐标 ——
    /// 设置浮窗据此在自己的 Region 上给这些按钮开洞（<c>SettingsOverlay.AppChromeHoles</c>）。
    /// </summary>
    public Rectangle[] ChromeHoleBounds => new[] { _max.Bounds, _close.Bounds };

    /// <summary>按钮被重新摆放（工具条改宽）时触发，订阅者据此同步自己缓存的矩形。</summary>
    public event Action? ChromeButtonsMoved;

    public ChromeBar()
    {
        Height = 38;
        BackColor = Theme.PanelBg;
        // 本文件里自绘的控件一律开 ResizeRedraw（见 WindowFrame / WelcomeView）。
        // 这一条本身不是必须的 —— 底边线画在 y=Height-1、横跨 0..Width，而尺寸变化时 Windows
        // 补画的恰好就是它要延伸的那条新增区域，所以它不会画旧。纯粹是补齐一致性，
        // 别给下一个往这条上画东西的人留坑（缺 CS_HREDRAW/CS_VREDRAW 时的症状见 WelcomeView）。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

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

        _max = new IconButton(IconButton.Kind.Maximize, Theme.PanelBg);
        _max.Click += (_, _) => MaximizeRequested?.Invoke();
        Controls.Add(_max);

        _close = new IconButton(IconButton.Kind.Close, Theme.PanelBg);
        _close.Click += (_, _) => CloseRequested?.Invoke();
        Controls.Add(_close);

        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        _brand.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        Resize += (_, _) => PlaceButtons();
        PlaceButtons();
    }

    /// <summary>两个按钮一起靠右端排开：关闭在最右，最大化紧挨着它左边。</summary>
    private void PlaceButtons()
    {
        _close.Location = new Point(Width - CloseRight - BtnSize, BtnTop);
        _max.Location = new Point(_close.Left - BtnGap - BtnSize, BtnTop);
        ChromeButtonsMoved?.Invoke();
    }

    /// <summary>最大化状态变了：按钮在「最大化 / 还原」两个字形之间切换。</summary>
    public void SetMaximized(bool on)
    {
        var want = on ? IconButton.Kind.Restore : IconButton.Kind.Maximize;
        if (_max.Icon == want) return;
        _max.Icon = want;
        _max.Invalidate();
    }

    /// <summary>主题切换后重新着色（顶栏也要跟随暗色）。</summary>
    public void Restyle()
    {
        BackColor = Theme.PanelBg;
        _brand.BackColor = Theme.PanelBg;
        _brand.ForeColor = Theme.Accent;
        _status.BackColor = Theme.PanelBg;
        SetStatus(StatusText);
        _max.Restyle();
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
        // ResizeRedraw 必须开：整块内容是照着 Width / Height 现场摆的（居中、上下留白），
        // 而不带 CS_HREDRAW/CS_VREDRAW 的窗口在尺寸变化时只有 Windows 补画的那一条新增区域
        // 会重画，中间的原像素原样留着 —— 表现成「窗口拉大了，欢迎页还停在旧宽度居中」。
        // 这是实测到的那个 bug 本身（tools/resize-lag.ps1 少了这一行就 FAIL），不是预防性写法。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
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
