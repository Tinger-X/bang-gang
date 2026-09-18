using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class DropdownSelect : Control, IThemed
{
    private bool _hover;
    private DropdownList? _popup;

    public string[] Items { get; }
    public event Action<int>? Chosen;

    public int SelectedIndex { get; private set; }

    public string SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Length ? Items[SelectedIndex] : "";

    public DropdownSelect(string[] items, int width = 180, int? selected = null)
    {
        Items = items;
        Size = new Size(width, InputField.MinHeight);   // 展示高度与同排输入框完全一致
        SelectedIndex = Math.Clamp(selected ?? Math.Max(0, items.Length - 1), 0, Math.Max(0, items.Length - 1));
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    public void Select(int idx, bool raise)
    {
        idx = Math.Clamp(idx, 0, Items.Length - 1);
        if (idx == SelectedIndex) { Invalidate(); return; }
        SelectedIndex = idx;
        Invalidate();
        if (raise) Chosen?.Invoke(idx);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) TogglePopup();
        base.OnMouseDown(e);
    }

    private void TogglePopup()
    {
        if (_popup != null) { ClosePopup(); return; }
        var host = FindHost();
        if (host == null) return;

        _popup = new DropdownList(Items, SelectedIndex, Math.Max(Width, 160));
        _popup.ItemChosen += i => { Select(i, true); ClosePopup(); };
        _popup.Closed += ClosePopup;

        // 必须先挂到宿主上再摆位：PlacePopup 依赖 popup.Parent 做坐标换算，
        // 否则列表会留在宿主左上角（0,0）而不是输入框下方。
        host.Controls.Add(_popup);
        PlacePopup();
        _popup.BringToFront();
        _popup.Focus();
        _openPopup = _popup;                 // 供消息过滤器判断“点到了别处”
        DropdownClickFilter.Install();
        _scroller = FindScroller();
        if (_scroller != null) _scroller.Scrolled += PlacePopup;
        Trace.Log($"dropdown open items={Items.Length} sel={SelectedIndex} popup={Rect(_popup.RectangleToScreen(_popup.ClientRectangle))}");
    }

    private static string Rect(Rectangle r) => $"({r.Left},{r.Top},{r.Width}x{r.Height})";

    /// <summary>把列表摆到输入框下方（下方不够高时翻到上方），并按该方向可用空间裁剪高度。</summary>
    private void PlacePopup()
    {
        var popup = _popup;
        if (popup == null || popup.IsDisposed) return;
        var host = popup.Parent;
        if (host == null) return;

        var bottom = host.PointToClient(PointToScreen(new Point(0, Height)));   // 输入框下沿
        var top = host.PointToClient(PointToScreen(new Point(0, 0)));           // 输入框上沿
        int spaceBelow = host.ClientSize.Height - (bottom.Y + 2);
        int spaceAbove = top.Y - 2;

        bool up = spaceBelow < popup.Height && spaceAbove > spaceBelow;
        popup.FitHeight(Math.Max(0, (up ? spaceAbove : spaceBelow) - DropdownList.ProjectionPad * 2));
        int y = up ? top.Y - popup.Height - 2 : bottom.Y + 2;
        popup.Location = new Point(bottom.X - DropdownList.ProjectionPad, y);
    }

    /// <summary>找到所在页面的滚动容器，滚动时让弹窗跟着走。</summary>
    private ScrollArea? FindScroller()
    {
        for (Control? c = Parent; c != null; c = c.Parent)
            if (c is ScrollArea sa) return sa;
        return null;
    }

    private void ClosePopup()
    {
        var p = _popup;
        _popup = null;
        if (ReferenceEquals(_openPopup, p)) _openPopup = null;
        if (_scroller != null) { _scroller.Scrolled -= PlacePopup; _scroller = null; }
        if (p != null) { p.Parent?.Controls.Remove(p); p.Dispose(); }
        Invalidate();
        if (p != null) Trace.Log("dropdown close");
    }

    private ScrollArea? _scroller;

    // ---- 点击浮窗内任意其它位置都收起（即使是不会获得焦点的控件） ----
    private static DropdownList? _openPopup;

    /// <summary>有没有下拉弹层正开着（设置浮窗的滚轮路由据此让路：弹层握着自己的焦点和滚轮）。</summary>
    internal static bool PopupOpen => _openPopup != null && !_openPopup.IsDisposed;

    /// <summary>全局消息过滤：只要有下拉处于展开状态，点到它以外就收起（并让这次点击继续生效）。</summary>
    private sealed class DropdownClickFilter : IMessageFilter
    {
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private static readonly DropdownClickFilter Instance = new();

        public static void Install() => Application.AddMessageFilter(Instance);

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_LBUTTONDOWN && m.Msg != WM_RBUTTONDOWN && m.Msg != WM_MBUTTONDOWN) return false;
            var popup = _openPopup;
            if (popup == null || popup.IsDisposed) return false;

            // 鼠标消息里的坐标是相对目标窗口的，这里直接用屏幕坐标判断更稳妥
            var screenRect = popup.RectangleToScreen(popup.ClientRectangle);
            bool inside = screenRect.Contains(Cursor.Position);
            Trace.Log($"filter click at {Cursor.Position.X},{Cursor.Position.Y} popup={screenRect.Left},{screenRect.Top},{screenRect.Width}x{screenRect.Height} inside={inside}");
            if (!inside) popup.RequestClose();
            return false;      // 让这次点击继续生效
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) ClosePopup();
    }

    /// <summary>弹层宿主：设置浮窗本体（这样列表不会被页面裁剪）。</summary>
    private Control? FindHost()
    {
        for (Control? c = Parent; c != null; c = c.Parent)
            if (c is IPopupHost) return c;
        return Parent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, SC.FieldBg);
        RP.Stroke(g, rc, 9, _hover || _popup != null ? SC.Accent : SC.FieldBorder, _hover ? 1.3f : 1f);
        // 选项文字居中显示（下拉框与输入框同宽）
        var tr = new Rectangle(34, 0, Math.Max(10, Width - 68), Height);
        TextRenderer.DrawText(g, SelectedItem, SF.Get(10.5f), tr, SC.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // 右侧箭头
        float cx = Width - 18, cy = Height / 2f;
        using var pen = new Pen(SC.InkMuted, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
        g.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
        base.OnPaint(e);
    }
}

/// <summary>下拉弹层宿主标记（由设置浮窗实现）。</summary>
internal interface IPopupHost { }

/// <summary>下拉展开后的选项列表（自绘，圆角 + 极淡投影），点击任意位置或 Esc 收起，滚轮可滚动。</summary>
