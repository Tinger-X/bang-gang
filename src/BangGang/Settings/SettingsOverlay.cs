using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 应用内设置浮窗：点击设置按钮后弹出，固定尺寸（比主窗口小）、上下左右居中，
/// 左侧为纵向设置菜单，右侧为对应菜单项的详情页，详情页底部各有保存按钮
/// （仅在内容变化后可点击），右上角为关闭按钮。
///
/// 整个浮窗由主窗口内的子控件构成（不是独立顶层窗口），因此主窗口上的
/// WDA_EXCLUDEFROMCAPTURE 自动覆盖它：对录屏 / 截屏完全不可见。
/// </summary>
internal sealed partial class SettingsOverlay : Panel, IPopupHost, IMessageFilter
{
    public event Action<AppSettings>? Applied;
    public event Action<string>? Status;

    /// <summary>
    /// 用户在「放弃未保存的修改」确认条上选了「放弃并退出」时触发 —— 主窗口据此真正关掉应用。
    /// 关闭的**时机**必须由浮窗说了算，所以这里只发通知，不让主窗口自己再判一次。
    /// </summary>
    public event Action? AppQuit;

    private const int RailW = 208;

    /// <summary>卡片的设计尺寸。</summary>
    public const int CardW = 880;
    public const int CardH = 640;

    /// <summary>
    /// 卡片四周留白之和（左右各一半）。窗口小于「卡片 + 留白」时卡片就跟着缩水
    /// （见 <see cref="LayoutOverlay"/>），所以主窗口拿这两个数当自己的尺寸下限，
    /// 保证设置界面任何时候都能整张摆出来（见 <c>MainForm.MinWindow</c>）。
    /// </summary>
    public const int CardMargin = 96;

    private readonly AppSettings _applied = new();

    private readonly RoundPanel _card = new();
    private readonly Panel _rail = new();
    private readonly Panel _divider = new();
    private readonly Panel _host = new();
    private readonly Label _brandMark = new();
    private readonly Label _railTitle = new();
    private readonly Label _version = new();
    private readonly CloseButton _close = new();

    private readonly List<NavItem> _navs = new();
    private readonly List<SettingsPage> _pages = new();

    private readonly FloatingCard _confirm = new();
    private readonly Label _confirmTitle = new();
    private readonly Label _confirmDesc = new();
    private readonly PillButton _confirmStay = new("继续编辑", PillButton.Look.Ghost, 104, 34);
    private readonly PillButton _confirmQuit = new("放弃并关闭", PillButton.Look.Danger, 116, 34);

    /// <summary>确认条这次是为「关设置」还是「退应用」弹的 —— 决定确认按钮的文案与后续动作。</summary>
    private enum QuitScope { Settings, App }

    private QuitScope _scope = QuitScope.Settings;

    private Point _cardPos;
    private int _sel;

    public SettingsOverlay()
    {
        BackColor = SC.Scrim;
        Dock = DockStyle.Fill;
        // 不能开 OptimizedDoubleBuffer：浮窗大部分区域是“透传”的，双缓冲会把没画的区域涂黑。
        SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);

        // ---- 浮窗主体（固定居中，不可拖动；背后浅色遮罩由主窗口负责） ----
        _card.Radius = 0;   // 直角边框（需求：设置弹窗不用圆角）
        _card.Resize += (_, _) => LayoutCard();
        Controls.Add(_card);

        // ---- 左侧菜单栏 ----
        _rail.BackColor = SC.RailBg;
        _card.Controls.Add(_rail);

        _brandMark.Text = "帮";
        _brandMark.Font = Theme.UI(11f, FontStyle.Bold);
        _brandMark.ForeColor = Color.White;
        _brandMark.BackColor = Theme.Accent;
        _brandMark.TextAlign = ContentAlignment.MiddleCenter;
        _rail.Controls.Add(_brandMark);

        _railTitle.Text = "设置";
        _railTitle.Font = Theme.UI(13f, FontStyle.Bold);
        _railTitle.ForeColor = SC.Ink;
        _railTitle.BackColor = SC.RailBg;
        _railTitle.TextAlign = ContentAlignment.MiddleLeft;
        _rail.Controls.Add(_railTitle);

        _version.Text = MainForm.WindowTitle + " " + MainForm.AppVersion;
        _version.Font = Theme.UI(8.5f);
        _version.ForeColor = SC.InkFaint;
        _version.BackColor = SC.RailBg;
        _version.TextAlign = ContentAlignment.MiddleCenter;   // 底部版本信息居中
        _rail.Controls.Add(_version);

        _divider.BackColor = SC.Mix(SC.CardBg, Theme.Border, 0.85f);
        _card.Controls.Add(_divider);

        // ---- 右侧详情页 ----
        _host.BackColor = SC.CardBg;
        _card.Controls.Add(_host);

        AddPage(new ShortcutsPage(), "快捷按键", NavIcon.Win);
        AddPage(new LlmPage(), "模型接入", NavIcon.Llm);
        AddPage(new ChatPage(), "对话参数", NavIcon.Param);
        AddPage(new ToolsPage(), "工具调用", NavIcon.Tool);
        AddPage(new UiPage(), "界面外观", NavIcon.Ui);
        AddPage(new AboutPage(), "软件说明", NavIcon.About);

        // ---- 右上角关闭 ----
        _close.Click += (_, _) => RequestClose(QuitScope.Settings);
        _card.Controls.Add(_close);
        _close.BringToFront();

        // ---- 未保存改动的确认条 ----
        BuildConfirm();
        _card.Controls.Add(_confirm);
        _confirm.BringToFront();

        // ---- 浮窗描边：由卡片自己抗锯齿绘制（不再用覆盖整张卡片的描边控件：
        //      那种“只画一圈边框”的控件会整片盖住卡片，且它的窗口一旦没被重画就是一片黑） ----
        _card.DrawBorder = true;

        SelectPage(0);
    }

    /// <summary>
    /// 整棵浮窗用 WS_EX_COMPOSITED 一次性合成后再上屏。
    /// 否则卡片的十几个子窗口会各自 WM_PAINT，先看到“空卡片/半截内容”（像骨架屏），
    /// 过一两帧才补全，也就是“打开设置先闪一下”的原因。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
            return cp;
        }
    }

    private void AddPage(SettingsPage page, string navLabel, NavIcon icon)
    {
        var nav = new NavItem(navLabel, icon);
        nav.Click += (_, _) => SelectPage(_navs.IndexOf(nav));
        _navs.Add(nav);
        _rail.Controls.Add(nav);

        page.SaveRequested += OnPageSave;
        page.DirtyChanged += RefreshDots;
        page.Visible = false;
        _pages.Add(page);
        _host.Controls.Add(page);
    }

    private void BuildConfirm()
    {
        _confirm.Radius = 0;   // 直角边框（需求：确认弹窗不用圆角）
        _confirm.Visible = false;

        _confirmTitle.Text = "放弃未保存的修改？";
        _confirmTitle.Font = Theme.UI(12.5f, FontStyle.Bold);
        _confirmTitle.ForeColor = SC.Ink;
        _confirmTitle.BackColor = SC.CardBg;
        _confirmTitle.TextAlign = ContentAlignment.MiddleCenter;

        _confirmDesc.Font = Theme.UI(9.5f);
        _confirmDesc.ForeColor = SC.InkMuted;
        _confirmDesc.BackColor = SC.CardBg;
        _confirmDesc.TextAlign = ContentAlignment.MiddleCenter;

        _confirmStay.Click += (_, _) => HideConfirm();
        _confirmQuit.Click += (_, _) =>
        {
            // scope 在 CloseNow 之前读出来：CloseNow 会把它复位成 Settings。
            var scope = _scope;
            CloseNow();
            // 真退出交给主窗口 —— 浮窗只管「问清楚了没有」。
            if (scope == QuitScope.App) AppQuit?.Invoke();
        };

        _confirm.Controls.Add(_confirmTitle);
        _confirm.Controls.Add(_confirmDesc);
        _confirm.Controls.Add(_confirmStay);
        _confirm.Controls.Add(_confirmQuit);
    }

    // ---------------- 对外接口 ----------------

    /// <summary>打开设置前载入当前设置，并清空所有未保存状态。</summary>
    public void ReloadFrom(AppSettings s)
    {
        CaptureBackdrop();          // 必须在浮窗可见之前抓底层界面
        _backdropStale = false;     // 上面这一张就是当前尺寸的，别再重抓一次
        _applied.CopyFrom(s);
        HideConfirm();
        foreach (var p in _pages) p.Rebind(_applied);
        SelectPage(_sel);
        RefreshDots();
    }


    public void ApplyTheme()    {
        BackColor = SC.Scrim;
        _divider.BackColor = SC.Mix(SC.CardBg, Theme.Border, 0.85f);
        _brandMark.BackColor = Theme.Accent;
        _rail.BackColor = SC.RailBg;
        _railTitle.ForeColor = SC.Ink;
        _railTitle.BackColor = SC.RailBg;
        _version.ForeColor = SC.InkFaint;
        _version.BackColor = SC.RailBg;
        _host.BackColor = SC.CardBg;
        _confirmTitle.ForeColor = SC.Ink;
        _confirmTitle.BackColor = SC.CardBg;
        _confirmDesc.ForeColor = SC.InkMuted;
        _confirmDesc.BackColor = SC.CardBg;
        RestyleTree(this);
        Invalidate(true);
    }

    private static void RestyleTree(Control root)
    {
        if (root is IThemed t) t.Restyle();
        root.Invalidate();
        foreach (Control c in root.Controls) RestyleTree(c);
    }


    // ---------------- 菜单 / 保存 ----------------

    private void SelectPage(int idx)
    {
        if (_pages.Count == 0) return;
        _sel = Math.Clamp(idx, 0, _pages.Count - 1);
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visible = i == _sel;
            _navs[i].Selected = i == _sel;
            _navs[i].Invalidate();
        }
        HideConfirm();
    }

    private void RefreshDots()
    {
        for (int i = 0; i < _pages.Count && i < _navs.Count; i++)
        {
            _navs[i].Dot = _pages[i].IsDirty;
            _navs[i].Invalidate();
        }
    }

    private void OnPageSave(SettingsPage page)
    {
        var merged = new AppSettings();
        merged.CopyFrom(_applied);
        page.ApplyTo(merged);

        Applied?.Invoke(merged);        // 主窗口负责落盘、刷新主题与热键
        _applied.CopyFrom(merged);

        // 保存后必须重设基线：否则“改回某个值”会被误判为“未修改”，
        // 导致保存按钮该亮的时候反而不可点。
        page.Rebind(_applied);
        page.MarkSaved();
        RefreshDots();
        Status?.Invoke("设置已保存");
    }

    // ---------------- 关闭 ----------------

    /// <summary>
    /// 关设置（或退应用）前的守门人。<paramref name="scope"/> 只影响确认按钮的文案和
    /// 确认之后干什么：<see cref="QuitScope.Settings"/> 只收浮窗，
    /// <see cref="QuitScope.App"/> 由 <see cref="BuildConfirm"/> 里的处理再发 <see cref="AppQuit"/>。
    /// </summary>
    /// <returns>true = 已经真的关了；false = 确认条弹出来了，等用户选。</returns>
    private bool RequestClose(QuitScope scope)
    {
        int dirty = _pages.Count(p => p.IsDirty);
        if (dirty == 0) { CloseNow(); return true; }
        _scope = scope;
        _confirmDesc.Text = dirty == 1 ? "当前页面还有未保存的修改。" : $"有 {dirty} 个页面存在未保存的修改。";
        _confirmQuit.Text = scope == QuitScope.App ? "放弃并退出" : "放弃并关闭";
        CaptureConfirmBackdrop();      // 确认条四角要显示真实的设置页内容（抗锯齿圆角）
        _confirm.Visible = true;
        _confirm.BringToFront();
        _confirmStay.Focus();
        return false;
    }

    /// <summary>
    /// 供主窗口在 FormClosing 里调用：设置浮窗开着且有未保存内容时，先把确认条弹出来。
    /// </summary>
    /// <returns>true = 可以关应用了；false = 这次关闭要取消，等用户在确认条上选。</returns>
    public bool RequestAppClose()
    {
        if (!Visible) return true;                 // 没开设置就没有要守的东西
        if (_confirm.Visible)
        {
            // 确认条已经因为「关设置」弹着了，而用户现在要关的是整个应用：
            // 升级 scope，按钮文案跟着变成退出，不重新抓底图（内容没变）。
            _scope = QuitScope.App;
            _confirmQuit.Text = "放弃并退出";
            return false;
        }
        return RequestClose(QuitScope.App);
    }


    private void CloseNow()
    {
        _scope = QuitScope.Settings;   // 复位：下一次弹确认条默认是「关设置」
        _confirm.Visible = false;
        Visible = false;
        Status?.Invoke("已关闭设置（未保存的修改已放弃）");
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        AnyOpen = Visible;
        if (Visible)
        {
            LayoutOverlay();               // 先摆好位置（含圆角底图与 Region）
            // base 之后窗口才真正带上 WS_VISIBLE：此时重画才会生效，
            // 否则卡片/菜单栏会停在“从未绘制”的状态（整块黑）。
            _card.Invalidate(true);
            Invalidate(true);
            Update();
            Trace.DumpAfter(_card, "dbg-card", 900);
            Trace.DumpAfter(this, "dbg-overlay", 1000);
            if (_navs.Count > 0) _navs[_sel].Focus();
            Application.AddMessageFilter(this);
        }
        else
        {
            HideConfirm();
            Application.RemoveMessageFilter(this);
        }
    }

    /// <summary>
    /// 设置浮窗是否开着。给<b>别的</b>消息过滤器看的（<see cref="ChatView"/> 与
    /// <c>InputPanel</c> 各有一个在吃 <c>WM_MOUSEWHEEL</c>）：浮窗是对话区的兄弟、
    /// 盖在它上面，对话区的 <c>Visible</c> 照样是 true —— 不让路的话，光标压在浮窗
    /// 卡片上时滚轮会被底下的对话区先吃掉（0.9.6 修的「设置页滚不动」）。
    /// 过滤器的注册顺序不可依赖，所以用显式标志让路，同 <see cref="ImageViewer.AnyOpen"/>。
    /// </summary>
    internal static bool AnyOpen { get; private set; }

    private const int WM_MOUSEWHEEL = 0x020A;

    /// <summary>
    /// 浮窗打开期间的滚轮路由：WM_MOUSEWHEEL 投递给**焦点**控件而不是光标下的控件，
    /// 而打开时焦点在左侧菜单上，光标压在页面正文上滚轮就哪儿也到不了。这里按光标位置
    /// 把滚轮直接喂给当前页的滚动区（光标在滚动区之外就让路，走默认的焦点路由）。
    /// 下拉弹层开着时让路：弹层自己握着焦点、自己处理滚轮。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL || !Visible) return false;
        if (DropdownSelect.PopupOpen) return false;
        if (_pages.Count == 0) return false;

        var area = _pages[_sel].Body;
        if (!area.IsHandleCreated || !area.Visible) return false;

        long lp = m.LParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        if (!area.ClientRectangle.Contains(area.PointToClient(screen))) return false;

        int notches = (short)((long)m.WParam >> 16) / 120;
        if (notches == 0) return false;
        area.ScrollBy(-notches * 60);   // 与 ScrollArea.OnMouseWheel 同一档步长
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Visible && keyData == Keys.Escape)
        {
            if (_confirm.Visible) HideConfirm();
            else RequestClose(QuitScope.Settings);
            return true;
        }
        // 设置打开时 Tab 只在浮窗内部循环，避免焦点跑到被遮罩盖住的主界面上
        if (Visible && (keyData == Keys.Tab || keyData == (Keys.Tab | Keys.Shift)))
        {
            CycleFocus(keyData == (Keys.Tab | Keys.Shift));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>在浮窗内部切换焦点（按可见、可用、可选中的顺序）。</summary>
    private void CycleFocus(bool backwards)
    {
        var list = new List<Control>();
        CollectFocusable(_card, list);
        if (list.Count == 0) return;
        int idx = list.FindIndex(c => c.Focused || c.ContainsFocus);
        int next = idx < 0 ? 0 : (idx + (backwards ? -1 : 1) + list.Count) % list.Count;
        list[next].Focus();
    }

    private static void CollectFocusable(Control root, List<Control> into)
    {
        foreach (Control c in root.Controls)
        {
            if (!c.Visible || !c.Enabled) continue;
            if (c.TabStop && c.CanSelect) into.Add(c);
            CollectFocusable(c, into);
        }
    }

    /// <summary>供主窗口调用：无论焦点在不在浮窗里，Esc 都能收起设置。</summary>
    public void CloseByEscape()
    {
        if (!Visible) return;
        if (_confirm.Visible) HideConfirm();
        else RequestClose(QuitScope.Settings);
    }
}
