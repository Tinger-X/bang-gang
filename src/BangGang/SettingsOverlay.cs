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
internal sealed class SettingsOverlay : Panel, IPopupHost
{
    public event Action<AppSettings>? Applied;
    public event Action<string>? Status;

    private const int RailW = 208;
    private const int CardW = 880;
    private const int CardH = 640;

    private readonly AppSettings _applied = new();

    private readonly RoundPanel _card = new();
    private readonly CardBorderRing _cardBorder = new();
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

    private Point _cardPos;
    private int _sel;

    public SettingsOverlay()
    {
        BackColor = SC.Scrim;
        Dock = DockStyle.Fill;
        // 注意：这里**不能**开 OptimizedDoubleBuffer —— 浮窗只保留卡片区域，
        // 双缓冲会把未绘制的区域画成黑色。
        SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        // ---- 浮窗主体（固定居中，不可拖动；背后浅色遮罩由主窗口负责） ----
        _card.Radius = 16;
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

        AddPage(new ShortcutsPage(), "快捷键", Glyph.Sliders);
        AddPage(new LlmPage(), "模型接入", Glyph.Spark);
        AddPage(new UiPage(), "界面外观", Glyph.Palette);

        // ---- 右上角关闭 ----
        _close.Click += (_, _) => RequestClose();
        Ui.SetToolTip(_close, "关闭设置");
        _card.Controls.Add(_close);
        _close.BringToFront();

        // ---- 未保存改动的确认条 ----
        BuildConfirm();
        _card.Controls.Add(_confirm);
        _confirm.BringToFront();

        // ---- 浮窗描边（画在所有子控件之上，圆角处也不缺边） ----
        _card.DrawBorder = false;
        _card.Controls.Add(_cardBorder);
        _cardBorder.BringToFront();

        SelectPage(0);
    }

    private void AddPage(SettingsPage page, string navLabel, Glyph icon)
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
        _confirm.Radius = 14;
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
        _confirmQuit.Click += (_, _) => CloseNow();
        Ui.SetToolTip(_confirmStay, "返回继续编辑");
        Ui.SetToolTip(_confirmQuit, "放弃修改并关闭设置");

        _confirm.Controls.Add(_confirmTitle);
        _confirm.Controls.Add(_confirmDesc);
        _confirm.Controls.Add(_confirmStay);
        _confirm.Controls.Add(_confirmQuit);
    }

    // ---------------- 对外接口 ----------------

    /// <summary>打开设置前载入当前设置，并清空所有未保存状态。</summary>
    public void ReloadFrom(AppSettings s)
    {
        _applied.CopyFrom(s);
        HideConfirm();
        foreach (var p in _pages) p.Rebind(_applied);
        SelectPage(_sel);
        RefreshDots();
    }

    public void ApplyTheme()
    {
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

    // ---------------- 布局 ----------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutOverlay();
    }

    /// <summary>
    /// 浮窗固定尺寸、始终在主窗口内上下左右居中（不可拖动）。
    /// 本控件的 Region 只保留卡片区域，卡片以外的“变暗与拦截点击”由主窗口的浅色遮罩窗口负责。
    /// </summary>
    private void LayoutOverlay()
    {
        if (Width <= 0 || Height <= 0) return;

        int cw = Math.Min(CardW, Math.Max(520, Width - 96));
        int ch = Math.Min(CardH, Math.Max(380, Height - 96));
        if (_card.Width != cw || _card.Height != ch) _card.Size = new Size(cw, ch);

        _cardPos = new Point((Width - cw) / 2, (Height - ch) / 2);
        _card.Location = _cardPos;

        ApplyRegion();
        Invalidate();
        CardBoundsChanged?.Invoke();
    }

    /// <summary>
    /// 只保留浮窗本体所在的区域（圆角卡片），区域外不绘制、也不接收鼠标，
    /// 因此不会在原界面上留下“陈旧像素”；同时避免把卡片的圆角切掉。
    /// </summary>
    private void ApplyRegion()
    {
        var rect = new Rectangle(_cardPos.X, _cardPos.Y, Math.Max(1, _card.Width), Math.Max(1, _card.Height));
        using var path = RP.Path(rect, 16);
        var region = new Region(path);
        var old = Region;
        Region = region;
        old?.Dispose();
    }

    /// <summary>卡片位置或尺寸变化（窗口缩放）时通知主窗口。</summary>
    public event Action? CardBoundsChanged;

    private void LayoutCard()
    {
        int w = _card.Width, h = _card.Height;
        if (w <= 0 || h <= 0) return;

        // 子控件从 3px 处开始：给浮窗自己的 1px 边框留出位置，否则边框会被盖住
        const int inset = 3;
        _rail.SetBounds(inset, inset, RailW - 1, h - inset * 2);
        _divider.SetBounds(RailW + 1, inset, 1, h - inset * 2);
        _host.SetBounds(RailW + 2, inset, Math.Max(10, w - RailW - inset - 2), h - inset * 2);
        _close.SetBounds(w - 44, 12, 28, 28);     // 尽量贴近右上角
        _cardBorder.SetBounds(0, 0, w, h);
        _cardBorder.BringToFront();

        _brandMark.SetBounds(18, 24, 28, 28);
        _railTitle.SetBounds(54, 24, RailW - 70, 28);

        int y = 96;
        foreach (var n in _navs)
        {
            n.SetBounds(14, y, RailW - 28, 42);
            y += 48;
        }
        // 底部版本信息整行居中
        _version.SetBounds(14, Math.Max(y + 10, h - 38), RailW - 28, 20);
        _version.TextAlign = ContentAlignment.MiddleCenter;

        foreach (var p in _pages) p.SetBounds(0, 0, _host.ClientSize.Width, _host.ClientSize.Height);

        // 确认浮层：420x180 居中，四周 12px 用于绘制投影
        const int cw2 = 420, ch2 = 180, pad = 12;
        _confirm.Shadow = pad;
        _confirm.SetBounds((w - cw2) / 2, (h - ch2) / 2, cw2, ch2);
        int ix = pad + (cw2 - pad * 2 - 232) / 2;   // 两个按钮整体居中
        _confirmTitle.SetBounds(pad + 20, pad + 20, cw2 - pad * 2 - 40, 24);
        _confirmDesc.SetBounds(pad + 20, pad + 48, cw2 - pad * 2 - 40, 20);
        _confirmStay.SetBounds(ix, pad + 100, 104, 34);
        _confirmQuit.SetBounds(ix + 104 + 12, pad + 100, 116, 34);
    }

    /// <summary>
    /// 浮窗之外不绘制任何东西：本控件只保留卡片所在的区域，
    /// 该区域外的像素仍属于原界面，从而做到“不遮挡、不影响原有 UI”。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 故意留空：不填充背景，避免盖住底下原有的界面内容
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

    private void RequestClose()
    {
        int dirty = _pages.Count(p => p.IsDirty);
        if (dirty > 0)
        {
            _confirmDesc.Text = dirty == 1 ? "当前页面还有未保存的修改。" : $"有 {dirty} 个页面存在未保存的修改。";
            _confirm.Visible = true;
            _confirm.BringToFront();
            _confirmStay.Focus();
            return;
        }
        CloseNow();
    }

    private void HideConfirm()
    {
        if (_confirm.Visible) _confirm.Visible = false;
    }

    private void CloseNow()
    {
        _confirm.Visible = false;
        Visible = false;
        Status?.Invoke("已关闭设置（未保存的修改已放弃）");
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (Visible)
        {
            LayoutOverlay();               // 先摆好位置，主窗口随后才能按卡片位置挖遮罩的洞
            if (_navs.Count > 0) _navs[_sel].Focus();
        }
        else
        {
            HideConfirm();
            Ui.HideToolTip();
        }
        base.OnVisibleChanged(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Visible && keyData == Keys.Escape)
        {
            if (_confirm.Visible) HideConfirm();
            else RequestClose();
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
        else RequestClose();
    }
}
