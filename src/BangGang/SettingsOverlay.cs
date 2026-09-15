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
        // 不能开 OptimizedDoubleBuffer：浮窗大部分区域是“透传”的，双缓冲会把没画的区域涂黑。
        SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);

        // ---- 浮窗主体（固定居中，不可拖动；背后浅色遮罩由主窗口负责） ----
        _card.Radius = 10;
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
        _confirm.Radius = 10;
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
        CaptureBackdrop();          // 必须在浮窗可见之前抓底层界面
        _applied.CopyFrom(s);
        HideConfirm();
        foreach (var p in _pages) p.Rebind(_applied);
        SelectPage(_sel);
        RefreshDots();
    }

    /// <summary>
    /// 抓一张主界面快照：卡片四角是圆角，圆角以外的像素必须显示“真正的底层界面”，
    /// 用快照填充就能得到抗锯齿的圆角（Region 硬裁剪会留下锯齿）。
    ///
    /// 两个坑：
    /// 1) 不能用屏幕抓图 —— 应用本身对截屏不可见（WDA_EXCLUDEFROMCAPTURE），
    ///    屏幕抓图在 Release 下只会拿到桌面；
    /// 2) 不能直接对主窗口 DrawToBitmap，也不能临时隐藏卡片 —— 前者对带 Region 的
    ///    顶层窗口会得到黑图，后者会在浮窗刚可见时把卡片留在“未绘制”状态（整块变黑）。
    /// 因此这里逐个把主窗口里除浮窗以外的兄弟控件画进位图再拼起来。
    /// </summary>
    private void CaptureBackdrop()
    {
        var parent = Parent;
        if (parent == null || Width <= 0 || Height <= 0) return;
        try
        {
            var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(parent.BackColor);
                // Controls[0] 在最上层，所以从后往前拼
                for (int i = parent.Controls.Count - 1; i >= 0; i--)
                {
                    var c = parent.Controls[i];
                    if (ReferenceEquals(c, this) || !c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                    using var cb = new Bitmap(c.Width, c.Height);
                    c.DrawToBitmap(cb, new Rectangle(0, 0, c.Width, c.Height));
                    // DrawToBitmap 会忽略控件的 Region（例如只在窗口最外圈画描边的 WindowFrame），
                    // 因此这里按 Region 裁一下，否则整张快照会被那圈颜色盖住。
                    var old = g.Clip;
                    if (c.Region != null)
                    {
                        using var rr = c.Region.Clone();
                        rr.Translate(c.Left, c.Top);
                        g.SetClip(rr, CombineMode.Intersect);
                    }
                    g.DrawImage(cb, c.Left, c.Top);
                    g.Clip = old;
                }
            }
            _backdrop?.Dispose();
            _backdrop = bmp;
            _backdropFor = _card.Size;
        }
        catch
        {
            _backdrop?.Dispose();
            _backdrop = null;
        }
        _card.BackdropBitmap = _backdrop;
        _card.BackdropOffset = _cardPos;
    }

    private Bitmap? _backdrop;
    private Size _backdropFor;

    /// <summary>
    /// 重新抓一次底层界面快照。主窗口状态栏这类内容在浮窗打开期间被快照盖住，
    /// 保存设置后状态栏会变成“设置已保存”，此时刷新一次底图，用户就能看到提示。
    /// </summary>
    public void RefreshBackdrop()
    {
        if (!Visible) return;
        CaptureBackdrop();
        Invalidate();
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

    // ---------------- 布局 ----------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutOverlay();
    }

    /// <summary>
    /// 浮窗固定尺寸、始终在主窗口内上下左右居中（不可拖动）。
    /// 浮窗铺满整个窗口：卡片以外画底层界面快照，并吃掉所有鼠标消息
    /// （设置打开时主界面只“看得见”，不能再被点击操作）。
    /// </summary>
    private void LayoutOverlay()
    {
        if (Width <= 0 || Height <= 0) return;

        int cw = Math.Min(CardW, Math.Max(520, Width - 96));
        int ch = Math.Min(CardH, Math.Max(380, Height - 96));
        if (_card.Width != cw || _card.Height != ch) _card.Size = new Size(cw, ch);

        _cardPos = new Point((Width - cw) / 2, (Height - ch) / 2);
        _card.Location = _cardPos;

        // 只有在窗口尺寸真的变了（卡片尺寸变了）时才重新抓底图：
        // 打开设置时已经抓过一次，重复抓会白等一次整窗渲染，正是“打开时先闪一下”的原因之一。
        if (Visible && _card.Size != _backdropFor) CaptureBackdrop();
        ApplyRegion();
        _card.Invalidate(true);             // 卡片是子窗口，父级重画不会带着它刷新
        Invalidate();
        CardBoundsChanged?.Invoke();
    }

    /// <summary>
    /// 浮窗覆盖整个窗口：Region 置空，卡片以外的鼠标消息落在浮窗上被忽略，
    /// 因此设置打开期间主界面不再可点（但外观仍与原界面一致，靠底层快照绘制）。
    /// 万一底图没抓到（DrawToBitmap 失败），退回到“只占卡片区域”的圆角 Region，
    /// 让主界面自己绘制，避免出现一片未绘制的黑区。
    /// </summary>
    private void ApplyRegion()
    {
        Region? region;
        if (_backdrop == null)
        {
            var rect = new Rectangle(_cardPos.X - 1, _cardPos.Y - 1,
                                     Math.Max(1, _card.Width) + 2, Math.Max(1, _card.Height) + 2);
            using var path = RP.Path(rect, _card.Radius + 1);
            region = new Region(path);
        }
        else region = null;

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

        // 确认浮层：400x180 居中；不画阴影，边框直接贴在外缘（不留投影边距）
        const int cw2 = 400, ch2 = 180;
        _confirm.Shadow = 0;   // 不画阴影，仅保留边框
        _confirm.SetBounds((w - cw2) / 2, (h - ch2) / 2, cw2, ch2);
        _confirmTitle.SetBounds(24, 30, cw2 - 48, 26);
        _confirmDesc.SetBounds(24, 62, cw2 - 48, 20);
        int ix = (cw2 - 232) / 2;   // 两个按钮整体居中
        _confirmStay.SetBounds(ix, 118, 104, 34);
        _confirmQuit.SetBounds(ix + 104 + 12, 118, 116, 34);
    }

    /// <summary>
    /// 浮窗本体只画卡片；卡片以外的区域直接用底层界面快照铺上，
    /// 这样不会留下“陈旧像素”。不使用 Region：Region 会把圆角硬裁剪出锯齿，
    /// 卡片以外的点击穿透由 <see cref="WndProc"/> 的 WM_NCHITTEST 处理。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var rc = new Rectangle(0, 0, Width, Height);
        var snap = _backdrop;
        if (snap != null)
        {
            e.Graphics.DrawImage(snap, rc, rc, GraphicsUnit.Pixel);
            return;
        }
        // 底图没抓到也必须画点东西：浮窗覆盖整窗，什么都不画就会露
        // “从未绘制”的黑色区域。
        using var b = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(b, rc);
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
            CaptureConfirmBackdrop();      // 确认条四角要显示真实的设置页内容（抗锯齿圆角）
            _confirm.Visible = true;
            _confirm.BringToFront();
            _confirmStay.Focus();
            return;
        }
        CloseNow();
    }

    /// <summary>抓一张设置卡片快照给确认浮层当圆角底图（不含确认条自己）。</summary>
    private void CaptureConfirmBackdrop()
    {
        if (_card.Width <= 0 || _card.Height <= 0) return;
        bool shown = _confirm.Visible;
        _confirm.Visible = false;
        try
        {
            var bmp = new Bitmap(_card.Width, _card.Height);
            _card.DrawToBitmap(bmp, new Rectangle(0, 0, _card.Width, _card.Height));
            _confirmBackdrop?.Dispose();
            _confirmBackdrop = bmp;
        }
        catch
        {
            _confirmBackdrop?.Dispose();
            _confirmBackdrop = null;
        }
        finally
        {
            _confirm.Visible = shown;
        }
        _confirm.BackdropBitmap = _confirmBackdrop;
        _confirm.BackdropOffset = _confirm.Location;
    }

    private Bitmap? _confirmBackdrop;

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
        base.OnVisibleChanged(e);
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
        }
        else
        {
            HideConfirm();
            Ui.HideToolTip();
        }
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
