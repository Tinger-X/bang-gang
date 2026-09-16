using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 侧栏“搜索对话”输入框：无图标、带占位文字与内容清空按钮；内部防抖触发搜索。
/// </summary>
internal sealed class SearchField : Control
{
    private const int WM_PRINT = 0x0317;

    /// <summary>文字距输入框左边缘的距离：占位文字与实际输入共用这一个起点。</summary>
    private const int TextPadX = 10;

    /// <summary>
    /// 字号必须是「磅值 × 96 / 72 得到整数像素」的那种。
    ///
    /// 11.5pt → 15.33px，GDI+（给 EDIT 建 HFONT 的那条路）向下取整成 15px，
    /// 而 <see cref="TextRenderer"/>（占位文字那条路）向上取整成 16px —— 同一个字体、
    /// 同一个字符串，占位文字会比实际输入整整大一个 em 像素（实测 76×16 对 71×13）。
    /// 11.25pt → 正好 15.0px，两边没有可分歧的余数，两条路渲染出来的字一样大。
    /// </summary>
    private readonly Font _uiFont = Theme.UI(11.25f);
    private readonly TextBox _tb;
    private readonly HintText _ph;
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
            AutoSize = false,               // 否则高度被锁回 PreferredHeight，见 Ui.EditBoxHeight
            Font = _uiFont,
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextMain,
        };
        _tb.TextChanged += (_, _) => { UpdateUI(); RestartDebounce(); };
        _tb.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _tb.Leave += (_, _) => { _focused = false; Invalidate(); };
        _tb.HandleCreated += (_, _) => Ui.PinEditTextLeft(_tb);
        Controls.Add(_tb);
        Ui.PinEditTextLeft(_tb);

        _ph = new HintText
        {
            Font = _uiFont,                 // 与 TextBox 同字体，两者文字才可能像素对齐
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextMuted,
            Hint = "搜索对话…",
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
        LayoutBox();
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

    /// <summary>
    /// 输入文字所占的矩形（占位 Label 与 TextBox 共用同一套坐标）。
    ///
    /// 单行 TextBox 的高度由字体锁定 —— 传给 SetBounds 的高度会被 WinForms
    /// 改回 PreferredHeight，而且 EDIT 是把文字**顶对齐**在自己客户区里的。
    /// 所以「文字竖直居中」不能靠调 TextBox 高度，只能把这个盒子的上边缘摆到
    /// 控件正中的行高位置上，再向下加余量（详见 <see cref="Ui.EditBox"/>）。
    ///
    /// 横向同理：左边距由 <see cref="TextPadX"/> 统一给定，EDIT 自己的内边距
    /// 由 <see cref="Ui.PinEditTextLeft"/> 清零，占位文字层也从同一个 x 起画，
    /// 所以输入前后文字的起始位置不会有肉眼可见的偏移。
    /// </summary>
    private Rectangle TextBounds()
        => Ui.EditBox(TextPadX, Width - TextPadX - 26, Height, _tb);

    private void UpdateUI()
    {
        bool has = _tb.Text.Length > 0;
        _ph.Visible = !has;
        _ph.Bounds = TextBounds();      // 与 TextBox 同一个盒子，左边缘与竖直中心都对齐
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
        if (_tb == null || _ph == null) return; // 构造期间子控件尚未建立
        LayoutBox();
    }

    private void LayoutBox()
    {
        _tb.Bounds = TextBounds();
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
        DrawContent(e.Graphics);
        base.OnPaint(e);
    }

    /// <summary>
    /// 拦截 WM_PRINT：DrawToBitmap 打印整棵控件树时，默认实现按 <c>Controls</c>
    /// 的顺序（也就是 z 序的反序）打子控件，TextBox 会盖在占位 Label 之上并
    /// 裁掉其文字。这里自己按正确顺序把子控件画出来。
    ///
    /// 要点是「让子控件自己画」而不是父层代画一遍文字：快照里的文字与屏幕上
    /// 出自同一次绘制，不会出现两套坐标导致的上下位移（设置浮窗的底图正是
    /// 用这张快照铺满整窗的，所以位移会直接暴露给用户）。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_PRINT && m.WParam != IntPtr.Zero)
        {
            using var g = Graphics.FromHdc(m.WParam);
            DrawContent(g);
            PrintChild(g, _tb);
            if (_ph.Visible) PrintChild(g, _ph);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>把子控件按它自己的 WM_PRINT 画到指定画布上（与屏幕上同一份像素）。</summary>
    private static void PrintChild(Graphics g, Control c)
    {
        if (!c.Visible || c.Width <= 0 || c.Height <= 0) return;
        using var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        g.DrawImage(bmp, c.Left, c.Top);
    }

    /// <summary>只画底、边框与清空按钮：文字一律由子控件负责。</summary>
    private void DrawContent(Graphics g)
    {
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
