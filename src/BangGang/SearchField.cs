using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 侧栏“搜索对话”输入框：无图标、带占位文字与内容清空按钮；内部防抖触发搜索。
/// </summary>
internal sealed class SearchField : Control
{
    private readonly TextBox _tb;
    private readonly Label _ph;
    private readonly System.Windows.Forms.Timer _debounce;
    private bool _focused;
    private bool _overClear;

    public event Action<string>? Debounced;

    public SearchField()
    {
        Height = 32;
        BackColor = Theme.SideBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        _tb = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.UI(11.5f),
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextMain,
        };
        _tb.TextChanged += (_, _) => { UpdateUI(); RestartDebounce(); };
        _tb.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _tb.Leave += (_, _) => { _focused = false; Invalidate(); };
        Controls.Add(_tb);

        _ph = new Label
        {
            AutoSize = true,
            BackColor = Theme.InputBg,
            Font = Theme.UI(11.5f),
            ForeColor = Theme.TextMuted,
            Text = "搜索对话…",
        };
        _ph.MouseDown += (_, _) => { _tb.Focus(); };
        Controls.Add(_ph);
        _ph.BringToFront();

        _debounce = new System.Windows.Forms.Timer { Interval = 300 };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Debounced?.Invoke(Text); };

        MouseDown += (_, e) =>
        {
            if (!ClearRect().Contains(e.Location)) _tb.Focus();
        };
        UpdateUI();
    }

    public string Text
    {
        get => _tb.Text;
        set { _tb.Text = value ?? ""; UpdateUI(); }
    }

    private Rectangle ClearRect() => new(Width - 26, (Height - 18) / 2, 18, 18);

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void UpdateUI()
    {
        bool has = _tb.Text.Length > 0;
        _ph.Visible = !has;
        if (!has) _ph.Location = new Point(10, (Height - _ph.Height) / 2);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = Text.Length > 0 && ClearRect().Contains(e.Location);
        if (over != _overClear) { _overClear = over; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_overClear) { _overClear = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Text.Length > 0 && ClearRect().Contains(e.Location))
        {
            _tb.Text = "";
            Debounced?.Invoke("");
            _tb.Focus();
        }
        base.OnMouseUp(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_tb == null) return; // 构造期间子控件尚未建立
        LayoutBox();
    }

    private void LayoutBox()
    {
        int h = Math.Max(18, Height - 4);
        _tb.Bounds = new Rectangle(8, 2, Width - 8 - 26, Height - 4);
        _tb.Font = Theme.UI(11.5f);
        UpdateUI();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.SideBg;
        _tb.BackColor = Theme.InputBg;
        _tb.ForeColor = Theme.TextMain;
        _ph.BackColor = Theme.InputBg;
        _ph.ForeColor = Theme.TextMuted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var p = Rounded(rc, Height / 2))
        using (var b = new SolidBrush(Theme.InputBg))
            g.FillPath(b, p);
        using (var pen = new Pen(_focused ? Theme.Accent : Theme.Border, 1f))
        using (var p2 = Rounded(rc, Height / 2))
            g.DrawPath(pen, p2);

        // 清空按钮
        if (_tb.Text.Length > 0)
        {
            var cr = ClearRect();
            using (var cb = new SolidBrush(Theme.Mix(Theme.InputBg, Theme.TextMuted, _overClear ? 0.34f : 0.18f)))
                g.FillEllipse(cb, cr);
            using (var xp = new Pen(Theme.TextMuted, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(xp, cr.Left + 5, cr.Top + 5, cr.Right - 5, cr.Bottom - 5);
                g.DrawLine(xp, cr.Right - 5, cr.Top + 5, cr.Left + 5, cr.Bottom - 5);
            }
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
