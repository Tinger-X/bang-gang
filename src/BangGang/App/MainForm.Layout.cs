using System.Drawing.Drawing2D;

namespace BangGang;

partial class MainForm
{
    // ---------------- 显式布局 ----------------

    /// <summary>
    /// 顶栏标题两侧对称的内边距：等于「按钮左间距 + 按钮宽 + 一点余量」，
    /// 左右一样宽，于是 <c>MiddleCenter</c> 出来的标题正好落在整条的几何中线上，
    /// 又不会被左边的按钮压住。
    /// </summary>
    private const int ConvTitlePadX = 44;

    /// <param name="liveResize">
    /// 这一次布局是**侧栏收展动画的中间帧**。为真时消息区只挪位置、不重排文字 ——
    /// 重排要对每条消息跑一遍 Markdown 折行，而动画期间可用宽度每帧都在变，逐帧重排等于
    /// 逐帧把全部消息重新折行一遍，那正是「卡顿」的来源之一。动画收尾由
    /// <see cref="ChatView.SettleLayout"/> 补一次真正的重排。
    ///
    /// 只有 <c>SideTick</c> 传 true；其余调用方（窗口缩放、改设置、换会话）保持默认，
    /// 它们本来就要实时重排，行为与改动前一致。
    /// </param>
    private void ApplyLayout(bool liveResize = false)
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

        // 右侧主区整块**钉死在窗口上**，收起 / 展开时一格都不动：左右都铺到窗口边，
        // 「露出来的那一截」靠侧栏盖在它上面（z 序见 ctor 里的 SetChildIndex）让出来。
        //
        // 为什么非得钉死：移动一个父控件，Windows 是把**整棵子树**一次性搬走的，而 WinForms
        // 之后才逐条 SetBounds 把每个后代的显式位置补回来。_send / _attach 钉在输入面板的右缘、
        // 右缘又贴着窗口右缘，于是「主区已经挪了、输入面板还没重摆」的那一小段里，这两个按钮
        // 被连带着一起挪走（实测偏移到 112px，正解是 40px）—— DWM 是异步合成的，恰好合到这一帧
        // 就是用户看见的闪烁和左右抖动。祖先不动之后，还在动的就只剩叶子（标题条、收起按钮、
        // 欢迎页）和「自己动完就把孩子摆好」的面板（输入区、消息区）：后者的 OnResize 是同一次
        // SetBounds 里同步回调的，不存在「先挪、后补」的空档。
        _mainArea.Bounds = new Rectangle(0, ChromeH, W, bodyH);
        _chatUI.Bounds = new Rectangle(0, 0, W, bodyH);

        _settingsOverlay.Bounds = new Rectangle(0, 0, W, H);
        _viewer.Bounds = new Rectangle(0, 0, W, H);
        _frame.Bounds = new Rectangle(0, 0, W, H);

        // 这几条才是真正露在外面的：左缘跟着侧栏当前宽度走，宽度是 W - sw。
        int mw = W - sw;
        _welcome.Bounds = new Rectangle(sw, 0, mw, bodyH);
        _convTitle.Bounds = new Rectangle(sw, 0, mw, 48);
        _convTitle.Padding = new Padding(ConvTitlePadX, 0, ConvTitlePadX, 0);
        _btnSideToggle.Location = new Point(sw + 10, 10);
        // 输入面板也铺满整窗、不随动画移动，内容靠 ContentInset 让开侧栏：
        // 它一移动，右下角那两个按钮就得跟着重摆一次，那正是上面要消掉的东西。
        //
        // 高度取 _input.PreferredHeight 而不是常量：加了附件就往上长一行（见 InputPanel），
        // 消息区的下缘跟着让位。两个数必须是同一个来源 —— 差一行的话要么卡片被窗口下缘切掉，
        // 要么消息区底下空出一条。
        int inputH = _input.PreferredHeight;
        _input.Bounds = new Rectangle(0, bodyH - inputH, W, inputH);
        _input.ContentInset = sw;
        // 开关必须在 Bounds **之前**设：消息区是在 SetBounds 里同步回调 OnResize 的，
        // 反过来的话那一帧已经按着旧开关的规矩走完了，这一帧的意图就丢了。
        _chatView.LiveResize = liveResize;
        _chatView.Bounds = new Rectangle(sw, 48, mw, bodyH - 48 - inputH);
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

    private bool _winDrag;                 // 自管拖动进行中
    private Point _dragFrom;               // 按下时的屏幕坐标
    private Rectangle _dragStart;          // 按下时的窗口矩形

    /// <summary>
    /// 顶栏拖动。**不走系统的 HTCAPTION 模态移动循环**（0.9.6）：主窗口常年挂着
    /// WDA_EXCLUDEFROMCAPTURE，实测这种窗口的模态循环是双态的 —— 顺的时候逐帧跟随，
    /// 卡的时候整条拖动期间窗口一动不动、松手后还在慢慢爬完积压的移动（正是用户报的
    /// 「鼠标已经从 A 到了 B，窗口还在 A→B 的路上」）。
    /// 模态循环跑在 DefWindowProc 内部，消息过滤器和 WndProc 都插不进去，应用侧无解。
    /// 所以换成和边缘缩放同一条路：SetCapture + 在 PreFilterMessage 里按
    /// Cursor.Position 逐条 SetWindowPos。WM_MOUSEMOVE 在队列里自动合并，
    /// 读到的永远是最新位置，不存在可积压的队列。
    /// 代价：失去 Aero Snap（拖到屏幕边缘自动最大化）—— 本程序有自己的最大化按钮，
    /// 且 _maximized 是自管状态（WindowState 恒为 Normal），系统本来也只当普通窗口拖。
    /// </summary>
    private void BeginWindowDrag()
    {
        if (_winDrag || _grip != Edge.None) return;
        _winDrag = true;
        _dragFrom = Cursor.Position;
        _dragStart = Bounds;
        _dragMoves = 0;
        Win32.SetCapture(Handle);
        Trace.Log($"drag begin at {_dragFrom.X},{_dragFrom.Y}");
    }

    /// <summary>自管拖动的逐帧位移：只平移，不改尺寸、不碰 z 序、不抢激活。</summary>
    private void ApplyWindowDrag(Point screen, bool force = false)
    {
        // WDA 窗口的 SetWindowPos 在输入风暴下会反过来堵住消息循环（实测 ~1ms 间隔的
        // SetCursorPos 横扫里 74 次移动只活下来 2~3 条）。
        // 真实鼠标 ≤125Hz 完全不受影响，但 1000Hz 的游戏鼠标正好踩在这个量级上，
        // 所以给一个 5ms 的最低间隔；跳过的一帧由随后的移动或松手时的 force 收尾补齐。
        long now = Environment.TickCount64;
        if (!force && now - _dragLastApply < 5) return;
        _dragLastApply = now;
        _dragMoves++;
        Native.SetWindowPos(Handle, IntPtr.Zero,
            _dragStart.X + screen.X - _dragFrom.X,
            _dragStart.Y + screen.Y - _dragFrom.Y,
            0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private int _dragMoves;
    private long _dragLastApply;

    private void EndWindowDrag(string why)
    {
        ApplyWindowDrag(Cursor.Position, force: true);   // 收尾对齐光标，别停在节流跳过的那一帧
        _winDrag = false;
        Win32.ReleaseCapture();
        Trace.Log($"drag end ({why}) at {Cursor.Position.X},{Cursor.Position.Y} moves={_dragMoves}");
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
    /// 无边框窗口没有系统边框可抓，缩放和**顶栏拖动**都由这道消息过滤器自己接手：
    /// 拖动是 <see cref="_winDrag"/> 那一支（为什么不用系统的 HTCAPTION 模态循环，
    /// 见 <see cref="BeginWindowDrag"/>）；缩放是下面 <see cref="_grip"/> 那一支 —
    /// 落在窗口外沿 <see cref="GripPx"/> 像素以内的左键按下，改成拖窗口边界，
    /// 而不是交给边缘底下那个控件。
    ///
    /// 挂消息过滤器而不是逐个控件挂 MouseDown：窗口四边分别被工具条 / 侧栏 / 主区 /
    /// 会话列表等好几个控件压着，逐个挂既要覆盖整棵树、又会漏掉以后新加的自绘控件。
    /// 过滤器只有一个入口，谁压在最上面都一样。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (_winDrag)
        {
            if (m.Msg == Win32.WM_MOUSEMOVE) { ApplyWindowDrag(Cursor.Position); return true; }
            if (m.Msg == Win32.WM_LBUTTONUP) { EndWindowDrag("up"); return true; }
            if (m.Msg == Win32.WM_CAPTURECHANGED) { EndWindowDrag("capture-lost"); return true; }
            return false;
        }

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

}
