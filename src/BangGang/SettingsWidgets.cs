using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 设置浮窗配色：全部动态读取 <see cref="Theme"/>，主题变化后只需 Restyle 即可刷新，
/// 不需要重建控件树。
/// </summary>
internal static class SC
{
    /// <summary>按权重把 a 混向 b（k=0 取 a，k=1 取 b）。</summary>
    public static Color Mix(Color a, Color b, float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * k),
            (int)Math.Round(a.G + (b.G - a.G) * k),
            (int)Math.Round(a.B + (b.B - a.B) * k));
    }

    public static Color CardBg => Theme.PanelBg;
    /// <summary>分组卡片底色：比卡片略深一点点的浅灰蓝。</summary>
    public static Color GroupBg => Mix(Theme.PanelBg, Theme.SideBg, 0.55f);
    public static Color RailBg => Mix(Theme.PanelBg, Theme.SideBg, 0.85f);
    /// <summary>输入框 / 分段控件底色：直接跟随主题的输入区颜色（亮暗色都对）。</summary>
    public static Color FieldBg => Theme.InputBg;
    public static Color FieldBorder => Mix(Theme.Border, Theme.TextMuted, 0.16f);
    public static Color CardBorder => Mix(Theme.Border, Theme.TextMuted, 0.10f);
    public static Color Ink => Theme.TextMain;
    public static Color InkMuted => Theme.TextMuted;
    public static Color InkFaint => Mix(Theme.TextMuted, Theme.PanelBg, 0.35f);
    public static Color Accent => Theme.Accent;
    public static Color AccentSoft => Mix(RailBg, Theme.Accent, 0.16f);
    public static Color AccentHover => Mix(RailBg, Theme.Accent, 0.08f);
    public static Color Danger => Theme.Danger;
    /// <summary>遮罩色：把主界面压暗，突出居中浮窗。</summary>
    public static Color Scrim => Mix(Mix(Theme.ChatBg, Theme.SideBg, 0.45f), Color.FromArgb(16, 20, 28), 0.30f);
}

/// <summary>
/// 设置浮窗绘制用字体缓存（避免在 OnPaint 里反复 new Font 造成 GDI 句柄泄漏）。
/// 注意：返回的 Font 由缓存持有，调用方**不要** Dispose，也不要赋给控件的 Font 属性，
/// 控件字体请用 <see cref="Theme.UI"/>（每次新建、由控件持有）。
/// </summary>
internal static class SF
{
    private static readonly Dictionary<(float size, FontStyle style), Font> Cache = new();

    public static Font Get(float size, FontStyle style = FontStyle.Regular)
    {
        var key = (size, style);
        if (!Cache.TryGetValue(key, out var f))
        {
            f = new Font("Microsoft YaHei UI", size, style);
            Cache[key] = f;
        }
        return f;
    }
}

/// <summary>主题变化时可自我刷新的控件。</summary>
internal interface IThemed
{
    void Restyle();
}

/// <summary>需要显式排布的控件（WinForms 会跳过不可见控件的自动布局，因此这里手动排布）。</summary>
internal interface IArranged
{
    void Arrange();
}

/// <summary>圆角路径工具。</summary>
internal static class RP
{
    public static GraphicsPath Path(Rectangle r, int rad) =>
        PathF(new RectangleF(r.X, r.Y, r.Width, r.Height), rad);

    public static GraphicsPath PathF(RectangleF r, int rad)
    {
        var p = new GraphicsPath();
        int d = Math.Max(2, Math.Min(rad * 2, Math.Min((int)r.Width, (int)r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, Rectangle r, int rad, Color c)
    {
        using var p = Path(r, rad);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, Rectangle r, int rad, Color c, float w = 1f)
    {
        var rc = new RectangleF(r.X + w / 2f, r.Y + w / 2f, r.Width - w, r.Height - w);
        using var p = PathF(rc, rad);
        using var pen = new Pen(c, w);
        g.DrawPath(pen, p);
    }
}

/// <summary>线框图标（统一 1.6px 圆头线条，风格与主界面一致）。</summary>
internal enum Glyph { Sliders, Spark, Palette, Eye, EyeOff, Close, Reset, Check }

internal static class Gfx
{
    public static void DrawGlyph(Graphics g, Glyph gl, RectangleF r, Color c, float w = 1.6f)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height) * 0.5f;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var br = new SolidBrush(c);
        switch (gl)
        {
            case Glyph.Sliders:
                float[] kx = { -0.34f, 0.40f, -0.10f };
                for (int i = 0; i < 3; i++)
                {
                    float y = cy + (i - 1) * s * 0.62f;
                    g.DrawLine(pen, cx - s * 0.92f, y, cx + s * 0.92f, y);
                    g.FillEllipse(br, cx + kx[i] * s - s * 0.24f, y - s * 0.24f, s * 0.48f, s * 0.48f);
                }
                break;

            case Glyph.Spark:
                g.FillPolygon(br, new[]
                {
                    new PointF(cx, cy - s * 0.95f), new PointF(cx + s * 0.27f, cy - s * 0.27f),
                    new PointF(cx + s * 0.95f, cy), new PointF(cx + s * 0.27f, cy + s * 0.27f),
                    new PointF(cx, cy + s * 0.95f), new PointF(cx - s * 0.27f, cy + s * 0.27f),
                    new PointF(cx - s * 0.95f, cy), new PointF(cx - s * 0.27f, cy - s * 0.27f),
                });
                break;

            case Glyph.Palette:
                g.DrawEllipse(pen, cx - s * 0.9f, cy - s * 0.9f, s * 1.8f, s * 1.8f);
                g.FillEllipse(br, cx - s * 0.46f, cy - s * 0.46f, s * 0.34f, s * 0.34f);
                g.FillEllipse(br, cx + s * 0.12f, cy - s * 0.46f, s * 0.34f, s * 0.34f);
                g.FillEllipse(br, cx - s * 0.17f, cy + s * 0.06f, s * 0.34f, s * 0.34f);
                break;

            case Glyph.Eye:
            case Glyph.EyeOff:
                g.DrawEllipse(pen, cx - s, cy - s * 0.6f, s * 2, s * 1.2f);
                g.FillEllipse(br, cx - s * 0.28f, cy - s * 0.28f, s * 0.56f, s * 0.56f);
                if (gl == Glyph.EyeOff) g.DrawLine(pen, cx - s * 0.95f, cy + s * 0.85f, cx + s * 0.95f, cy - s * 0.85f);
                break;

            case Glyph.Close:
                g.DrawLine(pen, cx - s * 0.58f, cy - s * 0.58f, cx + s * 0.58f, cy + s * 0.58f);
                g.DrawLine(pen, cx + s * 0.58f, cy - s * 0.58f, cx - s * 0.58f, cy + s * 0.58f);
                break;

            case Glyph.Check:
                g.DrawLine(pen, cx - s * 0.7f, cy + s * 0.04f, cx - s * 0.16f, cy + s * 0.58f);
                g.DrawLine(pen, cx - s * 0.16f, cy + s * 0.58f, cx + s * 0.74f, cy - s * 0.54f);
                break;

            case Glyph.Reset:
                g.DrawArc(pen, cx - s * 0.82f, cy - s * 0.82f, s * 1.64f, s * 1.64f, 35, 280);
                g.DrawLine(pen, cx + s * 0.60f, cy - s * 0.98f, cx + s * 0.92f, cy - s * 0.38f);
                g.DrawLine(pen, cx + s * 0.60f, cy - s * 0.98f, cx + s * 0.06f, cy - s * 0.92f);
                break;
        }
        g.SmoothingMode = old;
    }
}

/// <summary>圆角胶囊按钮；<see cref="On"/> 为 false 时呈禁用态且不响应点击（用于“内容变化后才可保存”）。</summary>
internal sealed class PillButton : Control, IThemed
{
    internal enum Look { Primary, Ghost, Danger }

    private bool _hover, _pressed, _on = true;

    public Look Kind { get; set; }

    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; Invalidate(); }
    }

    public PillButton(string text, Look kind = Look.Primary, int w = 104, int h = 38)
    {
        Kind = kind;
        Size = new Size(w, h);
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value ?? ""; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    /// <summary>禁用态下吞掉点击事件。</summary>
    protected override void OnClick(EventArgs e)
    {
        if (!_on) return;
        base.OnClick(e);
    }

    /// <summary>键盘（空格 / 回车）等效于点击，便于用键盘完成保存。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Focused && _on && (keyData == Keys.Space || keyData == Keys.Enter))
        {
            OnClick(EventArgs.Empty);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void Restyle() { BackColor = SC.CardBg; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        int rad = Math.Min(Height / 2, 12);

        Color fill, ink;
        switch (Kind)
        {
            case Look.Danger:
                fill = _hover ? SC.Mix(SC.Danger, Color.White, 0.10f) : SC.Danger;
                ink = Color.White;
                break;
            case Look.Ghost:
                fill = _hover ? SC.Mix(SC.CardBg, Theme.Border, 0.45f) : SC.CardBg;
                ink = _on ? SC.Ink : SC.InkFaint;
                break;
            default:
                if (_on)
                {
                    fill = _hover ? SC.Mix(Theme.Accent, Color.White, 0.12f) : Theme.Accent;
                    ink = Color.White;
                }
                else
                {
                    // 禁用态：底色与文字都保留可读的对比度，一眼能看出“不可点击”
                    fill = SC.Mix(SC.CardBg, Theme.Border, 0.85f);
                    ink = SC.Mix(SC.InkMuted, SC.CardBg, 0.18f);
                }
                break;
        }
        if (_pressed && _on && Kind != Look.Ghost) fill = SC.Mix(fill, Color.Black, 0.06f);

        RP.Fill(g, rc, rad, fill);
        if (Kind == Look.Ghost) RP.Stroke(g, rc, rad, _hover ? SC.Accent : SC.FieldBorder);
        TextRenderer.DrawText(g, Text, SF.Get(11f), rc, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        base.OnPaint(e);
    }
}

/// <summary>左侧菜单项：图标 + 文字，可带未保存小圆点。</summary>
internal sealed class NavItem : Control, IThemed
{
    private bool _hover;

    public Glyph Icon { get; }
    public string Label { get; }
    public bool Selected { get; set; }
    public bool Dot { get; set; }

    public NavItem(string label, Glyph icon)
    {
        Label = label;
        Icon = icon;
        Size = new Size(180, 42);
        BackColor = SC.RailBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    public void Restyle() { BackColor = SC.RailBg; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        if (Selected) RP.Fill(g, rc, 10, SC.AccentSoft);
        else if (_hover) RP.Fill(g, rc, 10, SC.AccentHover);

        Color ink = Selected ? SC.Accent : SC.Ink;
        Gfx.DrawGlyph(g, Icon, new RectangleF(14, (Height - 18) / 2f, 18, 18), Selected ? SC.Accent : SC.InkMuted, 1.5f);
        var textRc = new Rectangle(42, 0, Width - 42 - (Dot ? 24 : 12), Height);
        // 选中只改颜色，不改字号/粗细
        TextRenderer.DrawText(g, Label, SF.Get(11.5f), textRc, ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (Dot)
        {
            using var b = new SolidBrush(SC.Accent);
            g.FillEllipse(b, Width - 22, Height / 2f - 3.5f, 7, 7);
        }
        base.OnPaint(e);
    }
}

/// <summary>垂直堆叠容器：按加入顺序自上而下排布，高度由子项自身高度决定。</summary>
internal sealed class StackPanel : Panel, IThemed, IArranged
{
    public int Gap { get; set; } = 16;

    public StackPanel()
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>所有子项高度 + 间距的总高度（供外层滚动容器使用）。</summary>
    public int ContentHeight
    {
        get
        {
            int y = 0;
            foreach (Control c in Controls) y += c.Height + Gap;
            return Math.Max(0, y > 0 ? y - Gap : 0);
        }
    }

    /// <summary>按当前宽度与内容高度重新排布（不依赖 WinForms 的自动布局触发）。</summary>
    public void ArrangeAndResize(int width)
    {
        Width = Math.Max(20, width);
        Height = ContentHeight;
        Arrange();
    }

    public void Arrange()
    {
        int y = 0;
        foreach (Control c in Controls)
        {
            c.SetBounds(0, y, Math.Max(20, Width), c.Height);
            y += c.Height + Gap;
            if (c is IArranged a) a.Arrange();
        }
    }

    public void Restyle()
    {
        BackColor = SC.CardBg;
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }
}

/// <summary>分组卡片：圆角浅底 + 标题 + 可选副标题 + 若干行设置项。</summary>
internal sealed class GroupCard : Panel, IThemed, IArranged
{
    private readonly List<SettingRow> _rows = new();
    public string Title { get; }
    public string Subtitle { get; }
    public int RowH { get; set; } = 52;

    private int RowTop => string.IsNullOrEmpty(Subtitle) ? 48 : 60;

    public GroupCard(string title, string subtitle = "")
    {
        Title = title;
        Subtitle = subtitle;
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Add(SettingRow row)
    {
        _rows.Add(row);
        Controls.Add(row);
    }

    /// <summary>移除一行（切换服务商时重建参数行用）。</summary>
    public void RemoveRow(SettingRow row)
    {
        if (!_rows.Remove(row)) return;
        Controls.Remove(row);
        row.Dispose();
    }

    public int MeasureHeight()
    {
        int n = _rows.Count(r => r.Shown);
        return RowTop + n * RowH + 12;
    }

    public void Arrange()
    {
        int y = RowTop;
        foreach (var r in _rows)
        {
            if (!r.Shown) continue;        // 隐藏的行不占位置（服务商切换时用）
            r.SetBounds(18, y, Math.Max(20, Width - 36), RowH - 2);
            y += RowH;
            if (r is IArranged a) a.Arrange();
        }
    }

    public void Restyle()
    {
        BackColor = SC.GroupBg;
        foreach (var r in _rows) if (r is IThemed t) t.Restyle();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 14, SC.GroupBg);
        RP.Stroke(g, rc, 14, SC.Mix(SC.GroupBg, Theme.Border, 1.0f));
        var tr = new Rectangle(20, string.IsNullOrEmpty(Subtitle) ? 15 : 13, Width - 40, 22);
        TextRenderer.DrawText(g, Title, SF.Get(11.5f, FontStyle.Bold), tr, SC.Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (!string.IsNullOrEmpty(Subtitle))
        {
            var sr = new Rectangle(20, 35, Width - 40, 20);
            TextRenderer.DrawText(g, Subtitle, SF.Get(9f), sr, SC.InkMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        base.OnPaint(e);
    }
}

/// <summary>设置行：左侧标题 + 说明，右侧控件右对齐并垂直居中。</summary>
internal sealed class SettingRow : Panel, IThemed, IArranged
{
    private readonly Label _title;
    private readonly Label _desc;
    private readonly Control _right;

    public SettingRow(string title, string desc, Control right)
    {
        _title = new Label
        {
            Text = title,
            AutoSize = false,
            AutoEllipsis = true,
            Font = Theme.UI(11f),
            ForeColor = SC.Ink,
            BackColor = SC.GroupBg,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _desc = new Label
        {
            Text = desc,
            AutoSize = false,
            AutoEllipsis = true,
            Font = Theme.UI(8.5f),
            ForeColor = SC.InkMuted,
            BackColor = SC.GroupBg,
            TextAlign = ContentAlignment.TopLeft,
        };
        _right = right;
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(_title);
        Controls.Add(_desc);
        Controls.Add(right);
    }

    public void Restyle()
    {
        BackColor = SC.GroupBg;
        _title.BackColor = SC.GroupBg; _title.ForeColor = SC.Ink;
        _desc.BackColor = SC.GroupBg; _desc.ForeColor = SC.InkMuted;
        _right.BackColor = SC.GroupBg;
        if (_right is IThemed t) t.Restyle();
        Invalidate(true);
    }

    public void Arrange()
    {
        int rw = _right.Width, rh = _right.Height;
        _right.SetBounds(Math.Max(0, Width - rw), Math.Max(0, (Height - rh) / 2), rw, rh);
        if (_right is IArranged ra) ra.Arrange();
        int tw = Math.Max(20, Width - rw - 18);
        bool two = !string.IsNullOrEmpty(_desc.Text);
        _title.SetBounds(0, two ? 5 : Math.Max(0, (Height - 22) / 2), tw, 21);
        _desc.SetBounds(0, 27, tw, 18);
        _desc.Visible = two;
    }

    /// <summary>行右侧的控件（切换服务商时用来定位这一行）。</summary>
    public Control RightControl => _right;

    /// <summary>
    /// 是否参与排布。注意不能用 Control.Visible 判断：父级不可见时它也会返回 false。
    /// </summary>
    public bool Shown { get; set; } = true;

    /// <summary>显示/隐藏这一行（同时同步 Control.Visible）。</summary>
    public void SetShown(bool on)
    {
        Shown = on;
        Visible = on;
    }

    /// <summary>更换标题与说明（切换服务商时用）。</summary>
    public void SetText(string title, string desc)
    {
        _title.Text = title;
        _desc.Text = desc ?? "";
        Arrange();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }
}

/// <summary>
/// 圆角文本输入框（可带占位符与密码模式 + 显示/隐藏切换）。
/// 内部文本框与外部边框同底色（看起来就是“一个框”），并按字体高度自适应，
/// 因此高 DPI 下也不会出现文字被遮挡。
/// </summary>
internal sealed class InputField : Control, IThemed, IArranged
{
    private readonly TextBox _tb;
    private readonly Label _ph;
    private bool _focus, _hover, _revealed;

    public bool Secret { get; set; }
    public string Placeholder { get; private set; } = "";
    public event Action? Changed;

    /// <summary>输入框的最小高度（随字体自动加高，保证文字不被裁切）。</summary>
    public const int MinHeight = 42;

    public InputField(int width = 300, bool secret = false, string placeholder = "")
    {
        Secret = secret;
        Placeholder = placeholder;
        Size = new Size(width, MinHeight);
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _tb = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.UI(10.5f),
            BackColor = SC.FieldBg,       // 与外部边框同色，视觉上只有一个框
            ForeColor = SC.Ink,
            UseSystemPasswordChar = secret,
        };
        _tb.TextChanged += (_, _) => { UpdatePlaceholder(); Changed?.Invoke(); Invalidate(); };
        _tb.GotFocus += (_, _) => { _focus = true; UpdatePlaceholder(); Invalidate(); };
        _tb.LostFocus += (_, _) => { _focus = false; UpdatePlaceholder(); Invalidate(); };
        Controls.Add(_tb);

        _ph = new Label
        {
            AutoSize = false,
            Font = Theme.UI(10.5f),
            ForeColor = SC.InkFaint,
            BackColor = SC.FieldBg,
            Text = placeholder,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _ph.MouseDown += (_, _) => _tb.Focus();
        Controls.Add(_ph);
        _ph.BringToFront();
        Arrange();
        Height = NeededHeight;
    }

    /// <summary>按字体需要的行高自动加高（高 DPI 下也不会遮挡文字）。</summary>
    private int NeededHeight => Math.Max(MinHeight, _tb.PreferredHeight + 20);

    /// <summary>更换占位示例（切换服务商时用）。</summary>
    public void SetPlaceholder(string text)
    {
        Placeholder = text ?? "";
        _ph.Text = Placeholder;
        UpdatePlaceholder();
        Invalidate();
    }

    /// <summary>把内部文本框与占位文字按当前尺寸摆好（构造与尺寸变化时都要调用）。</summary>
    public void Arrange()
    {
        if (_tb == null) return;
        int right = 12 + (ShowEye ? 26 : 0);
        int h = _tb.PreferredHeight;
        int y = Math.Max(0, (Height - h) / 2);
        _tb.SetBounds(12, y, Math.Max(10, Width - 12 - right), h);
        UpdatePlaceholder();
    }

    [AllowNull]
    public override string Text
    {
        get => _tb.Text;
        set { _tb.Text = value ?? ""; UpdatePlaceholder(); Invalidate(); }
    }

    /// <summary>编辑中的 Esc 只退出输入，不再冒泡去关闭整个设置浮窗。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _tb.Focused)
        {
            Parent?.SelectNextControl(this, true, true, true, true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool ShowEye => Secret;
    private Rectangle EyeRect() => new(Width - 32, (Height - 20) / 2, 20, 20);

    private void UpdatePlaceholder()
    {
        bool show = _tb.Text.Length == 0 && !_focus;
        _ph.Visible = show;
        // 占位文字只占文本框那一行的高度：铺满整高会把输入框上下边框盖住
        if (show) _ph.SetBounds(12, Math.Max(2, (Height - 20) / 2), Math.Max(10, Width - 24 - (ShowEye ? 28 : 0)), 20);
    }

    public void Restyle()
    {
        BackColor = SC.CardBg;
        _tb.BackColor = SC.FieldBg; _tb.ForeColor = SC.Ink;
        _ph.BackColor = SC.FieldBg; _ph.ForeColor = SC.InkFaint;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (ShowEye && EyeRect().Contains(e.Location))
        {
            _revealed = !_revealed;
            _tb.UseSystemPasswordChar = !_revealed;
            Invalidate();
        }
        else
        {
            _tb.Focus();
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, SC.FieldBg);
        Color border = _focus ? SC.Accent : (_hover ? SC.Mix(SC.FieldBorder, SC.Accent, 0.35f) : SC.FieldBorder);
        RP.Stroke(g, rc, 9, border, _focus ? 1.4f : 1f);
        if (ShowEye)
            Gfx.DrawGlyph(g, _revealed ? Glyph.Eye : Glyph.EyeOff, EyeRect(), _revealed ? SC.Accent : SC.InkMuted, 1.4f);
        base.OnPaint(e);
    }
}

/// <summary>
/// 快捷键录入框：点击进入录入后，**实时显示当前按下的按键**，
/// 等所有按键都抬起时才算录入完成。
/// </summary>
internal sealed class KeyCapBox : Control, IThemed
{
    private bool _focus;
    private ShortcutSetting _sc = new();
    private readonly ShortcutSetting _pending = new();   // 录入中显示的组合
    private readonly ShortcutSetting _commit = new();    // 全部松开后真正提交的组合
    private readonly HashSet<Keys> _down = new();
    private bool _usable;

    public event Action? Changed;

    public KeyCapBox(int width = 240)
    {
        Size = new Size(width, 34);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    public ShortcutSetting Value => new() { Action = _sc.Action, Ctrl = _sc.Ctrl, Alt = _sc.Alt, Shift = _sc.Shift, Vk = _sc.Vk };

    public void Set(ShortcutSetting s)
    {
        _sc = new ShortcutSetting { Action = s.Action, Ctrl = s.Ctrl, Alt = s.Alt, Shift = s.Shift, Vk = s.Vk };
        Invalidate();
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    private void BeginCapture()
    {
        _focus = true;
        _down.Clear();
        _usable = false;
        _pending.Action = _sc.Action;
        _pending.Ctrl = _pending.Alt = _pending.Shift = false;
        _pending.Vk = 0;
        _commit.Ctrl = _commit.Alt = _commit.Shift = false;
        _commit.Vk = 0;
        Invalidate();
    }

    private void EndCapture(bool commit)
    {
        _focus = false;
        _down.Clear();
        Invalidate();
    }

    /// <summary>当前按住的修饰键 → 录入中显示的组合；提交值来自按下可用键的那一刻。</summary>
    private void RefreshLive()
    {
        _pending.Ctrl = _down.Contains(Keys.ControlKey);
        _pending.Alt = _down.Contains(Keys.Menu);
        _pending.Shift = _down.Contains(Keys.ShiftKey);
        _pending.Vk = _usable ? _commit.Vk : 0;
    }

    protected override void OnEnter(EventArgs e) { if (!_focus) BeginCapture(); base.OnEnter(e); }

    protected override void OnLeave(EventArgs e)
    {
        // 录入途中失去焦点：放弃本次录入，沿用原来的组合键
        if (_focus) EndCapture(false);
        base.OnLeave(e);
    }

    /// <summary>点击即进入录入状态（录入完成后仍保持焦点，再次点击可重新录入）。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        BeginCapture();
        Focus();
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    /// <summary>录入中的 Esc 只取消本次录入，不会关闭设置浮窗。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _focus)
        {
            EndCapture(false);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_focus) return;
        if (e.KeyCode == Keys.Tab) return;      // 交给系统做焦点切换

        _down.Add(e.KeyCode);

        bool mod = e.Control || e.Alt || e.Shift;
        if (ShortcutSetting.IsUsable((int)e.KeyCode, mod))
        {
            // 记下这一刻的修饰键：松开全部按键后用它提交
            _commit.Ctrl = _down.Contains(Keys.ControlKey);
            _commit.Alt = _down.Contains(Keys.Menu);
            _commit.Shift = _down.Contains(Keys.ShiftKey);
            _commit.Vk = (int)e.KeyCode;
            _usable = true;
        }
        RefreshLive();
        e.SuppressKeyPress = true;
        Invalidate();                            // 实时显示按下的键
        Trace.Log($"key down {e.KeyCode} down={_down.Count} live={Parts(_pending)}");
    }

    private static string Parts(ShortcutSetting s) =>
        $"{(s.Ctrl ? "Ctrl+" : "")}{(s.Alt ? "Alt+" : "")}{(s.Shift ? "Shift+" : "")}{(s.Vk != 0 ? ShortcutSetting.VkToName(s.Vk) : "-")}";

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!_focus) return;
        _down.Remove(e.KeyCode);
        RefreshLive();
        e.SuppressKeyPress = true;

        // 所有按键都抬起 -> 录入完成
        if (_down.Count == 0 && _usable && _commit.Vk != 0)
        {
            _sc.Ctrl = _commit.Ctrl;
            _sc.Alt = _commit.Alt;
            _sc.Shift = _commit.Shift;
            _sc.Vk = _commit.Vk;
            EndCapture(true);
            Changed?.Invoke();
            Trace.Log($"key commit {Parts(_sc)}");
            return;
        }
        Trace.Log($"key up {e.KeyCode} down={_down.Count} live={Parts(_pending)}");
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, SC.FieldBg);
        RP.Stroke(g, rc, 9, _focus ? SC.Accent : SC.FieldBorder, _focus ? 1.4f : 1f);

        // 录入中：实时显示当前按下的组合键；一个键都没按就显示提示
        var shown = _focus ? _pending : _sc;
        bool hasAny = _focus && (shown.Ctrl || shown.Alt || shown.Shift || shown.Vk != 0);
        if (_focus && !hasAny)
        {
            TextRenderer.DrawText(g, "请按下新的组合键…", SF.Get(10f), rc, SC.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            base.OnPaint(e);
            return;
        }

        var parts = new List<string>();
        if (shown.Ctrl) parts.Add("Ctrl");
        if (shown.Alt) parts.Add("Alt");
        if (shown.Shift) parts.Add("Shift");
        if (shown.Vk != 0) parts.Add(ShortcutSetting.VkToName(shown.Vk));

        // 量算每个键帽的宽度（缓存字体只读，切勿 Dispose）
        var keyFont = SF.Get(10f, FontStyle.Bold);
        var sizes = new List<float>();
        float total = 0;
        foreach (var p in parts) { float w = g.MeasureString(p, keyFont).Width; sizes.Add(w); total += (int)Math.Round(w) + 16; }
        total += (parts.Count - 1) * 12;

        int x = (int)Math.Round((Width - total) / 2f);   // 键帽整体居中
        int y = (Height - 24) / 2;
        using (var kb = new SolidBrush(SC.Mix(SC.FieldBg, Theme.Border, 0.8f)))
            for (int i = 0; i < parts.Count; i++)
            {
                int cw = (int)Math.Round(sizes[i]) + 16;
                using (var p = RP.Path(new Rectangle(x, y, cw, 24), 6)) g.FillPath(kb, p);
                TextRenderer.DrawText(g, parts[i], SF.Get(10f, FontStyle.Bold), new Rectangle(x, y, cw, 24), SC.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += cw;
                if (i < parts.Count - 1)
                {
                    TextRenderer.DrawText(g, "+", SF.Get(9.5f), new Rectangle(x, y, 12, 24), SC.InkFaint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += 12;
                }
            }
        base.OnPaint(e);
    }
}

/// <summary>不透明度滑杆：左侧轨道 + 右侧百分比。</summary>
internal sealed class SliderBar : Control, IThemed
{
    private int _value = 100;
    private bool _drag, _hover;

    public int Min { get; set; } = 50;
    public int Max { get; set; } = 100;
    public event Action? Changed;

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Min, Max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            Changed?.Invoke();
        }
    }

    public SliderBar(int width = 320)
    {
        Size = new Size(width, 34);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    private int TrackW => Math.Max(40, Width - 66);
    private Rectangle TrackRect() => new(0, Height / 2 - 3, TrackW, 6);
    private int KnobX() => TrackRect().X + (int)Math.Round((TrackW - 16) * (_value - Min) / (float)(Max - Min));

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _drag = true; SetFromX(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); base.OnMouseMove(e); }

    private void SetFromX(int x)
    {
        float k = (x - 8) / (float)Math.Max(1, TrackW - 16);
        Value = Min + (int)Math.Round(Math.Clamp(k, 0f, 1f) * (Max - Min));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var tr = TrackRect();
        RP.Fill(g, tr, 3, SC.Mix(SC.FieldBg, Theme.Border, 0.9f));
        int kx = KnobX();
        if (kx > tr.X) RP.Fill(g, new Rectangle(tr.X, tr.Y, kx - tr.X + 8, tr.Height), 3, Theme.Accent);

        var knob = new Rectangle(kx, Height / 2 - 8, 16, 16);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, knob);
        using (var pen = new Pen(Theme.Accent, _drag || _hover ? 2.6f : 2f)) g.DrawEllipse(pen, knob);

        var vr = new Rectangle(Width - 58, 0, 58, Height);
        TextRenderer.DrawText(g, _value + "%", SF.Get(10.5f, FontStyle.Bold), vr, SC.Ink,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        base.OnPaint(e);
    }
}

/// <summary>颜色样本块：显示色块 + 十六进制值，点击打开取色对话框。</summary>
internal sealed class SwatchChip : Control, IThemed
{
    private Color _color = Color.White;
    private bool _hover;

    public event Action? Changed;

    public Color Value
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    public SwatchChip(int width = 168)
    {
        Size = new Size(width, 32);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        using var dlg = new ColorDialog { Color = _color, FullOpen = true };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _color = dlg.Color;
            Invalidate();
            Changed?.Invoke();
        }
        base.OnMouseClick(e);
    }

    private static Color InkFor(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return lum > 0.62 ? Color.FromArgb(38, 42, 50) : Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, _color);
        RP.Stroke(g, rc, 9, _hover ? SC.Accent : SC.Mix(SC.FieldBorder, SC.InkMuted, 0.15f), _hover ? 1.4f : 1f);
        string hex = $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";
        TextRenderer.DrawText(g, hex, SF.Get(9.5f, FontStyle.Bold), rc, InkFor(_color),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        base.OnPaint(e);
    }
}

/// <summary>开关按钮（用于“窗口边框”等布尔项）。</summary>
internal sealed class ToggleSwitch : Control, IThemed
{
    private bool _hover;
    private bool _on;

    public event Action? Changed;

    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; Invalidate(); }
    }

    public ToggleSwitch()
    {
        Size = new Size(48, 26);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _on = !_on;
            Invalidate();
            Changed?.Invoke();
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 2, Width - 1, Height - 5);
        Color fill = _on
            ? (_hover ? SC.Mix(Theme.Accent, Color.White, 0.12f) : Theme.Accent)
            : SC.Mix(SC.FieldBg, SC.FieldBorder, 0.85f);
        RP.Fill(g, track, track.Height / 2, fill);
        if (!_on) RP.Stroke(g, track, track.Height / 2, SC.FieldBorder);

        int d = track.Height - 6;
        int kx = _on ? track.Right - d - 3 : track.X + 3;
        using (var b = new SolidBrush(Color.White))
            g.FillEllipse(b, kx, track.Y + 3, d, d);
        using (var pen = new Pen(_on ? Theme.Accent : SC.FieldBorder, 1f))
            g.DrawEllipse(pen, kx, track.Y + 3, d, d);
        base.OnPaint(e);
    }
}

/// <summary>
/// 分段选择器（按住/按下、亮色/暗色/跟随系统 这类少量互斥选项）。
/// 宽度按各选项文字自适应；选中态用一块**平滑滑动的背景**表示，且不改变字号/粗细。
/// </summary>
internal sealed class SegmentedControl : Control, IThemed
{
    private const int PadX = 14;        // 每段文字左右留白
    private const int MinSegW = 48;
    private const int OuterPad = 3;

    private int _hover = -1;
    private int[] _widths = Array.Empty<int>();
    private float _indicatorX;
    private readonly System.Windows.Forms.Timer _anim;

    public string[] Items { get; }
    public event Action? Changed;

    public int SelectedIndex { get; private set; }

    public SegmentedControl(string[] items, int height = 34, int unusedWidth = 0)
    {
        Items = items;
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += (_, _) => StepAnimation();
        Measure();
        Size = new Size(TotalWidth(), height);
        _indicatorX = SegX(SelectedIndex);
    }

    /// <summary>按文字量算每一段的宽度（宽度跟着内容走）。</summary>
    private void Measure()
    {
        var widths = new int[Items.Length];
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        for (int i = 0; i < Items.Length; i++)
        {
            var sz = g.MeasureString(Items[i], SF.Get(10.5f));
            widths[i] = Math.Max(MinSegW, (int)Math.Ceiling(sz.Width) + PadX * 2);
        }
        _widths = widths;
    }

    private int TotalWidth()
    {
        int w = OuterPad * 2;
        foreach (var x in _widths) w += x;
        return w;
    }

    private int SegX(int idx)
    {
        int x = OuterPad;
        for (int i = 0; i < idx && i < _widths.Length; i++) x += _widths[i];
        return x;
    }

    public void Select(int idx, bool raise)
    {
        idx = Math.Clamp(idx, 0, Math.Max(0, Items.Length - 1));
        if (idx == SelectedIndex) return;
        SelectedIndex = idx;
        StartAnimation();
        if (raise) Changed?.Invoke();
    }

    /// <summary>选中背景平滑滑动到目标段。</summary>
    private void StartAnimation()
    {
        float target = SegX(SelectedIndex);
        if (Math.Abs(target - _indicatorX) < 0.5f) { _indicatorX = target; Invalidate(); return; }
        _anim.Start();
    }

    private void StepAnimation()
    {
        float target = SegX(SelectedIndex);
        float dx = target - _indicatorX;
        if (Math.Abs(dx) < 1f)
        {
            _indicatorX = target;
            _anim.Stop();
        }
        else
        {
            _indicatorX += dx * 0.45f;      // 简单缓动
        }
        Invalidate();
    }

    public void Restyle()
    {
        Measure();
        Width = TotalWidth();
        _indicatorX = SegX(SelectedIndex);
        BackColor = SC.GroupBg;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }

    private int IndexAt(int x)
    {
        int acc = OuterPad;
        for (int i = 0; i < _widths.Length; i++)
        {
            if (x >= acc && x < acc + _widths[i]) return i;
            acc += _widths[i];
        }
        return x < OuterPad ? 0 : Math.Max(0, _widths.Length - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = IndexAt(e.X);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int i = IndexAt(e.X);
            if (i >= 0) Select(i, true);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, SC.FieldBg);
        RP.Stroke(g, rc, 9, SC.FieldBorder);

        // 选中背景（平滑滑动）
        int sw = _widths.Length > 0 ? _widths[Math.Clamp(SelectedIndex, 0, _widths.Length - 1)] : Width;
        var ind = new Rectangle((int)Math.Round(_indicatorX), OuterPad, sw, Height - OuterPad * 2 - 1);
        RP.Fill(g, ind, 7, SC.CardBg);
        RP.Stroke(g, ind, 7, SC.Mix(SC.FieldBorder, Theme.Accent, 0.45f));

        int x = OuterPad;
        for (int i = 0; i < Items.Length; i++)
        {
            var cell = new Rectangle(x, OuterPad, _widths[i], Height - OuterPad * 2 - 1);
            if (i == _hover && i != SelectedIndex)
                RP.Fill(g, cell, 7, SC.Mix(SC.FieldBg, Theme.Accent, 0.07f));
            Color ink = i == SelectedIndex ? SC.Accent : SC.InkMuted;
            // 选中不改变字号与粗细
            TextRenderer.DrawText(g, Items[i], SF.Get(10.5f), cell, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += _widths[i];
        }
        base.OnPaint(e);
    }
}

/// <summary>
/// 预设主色选择器：一排颜色球 + 一块**平滑滑动**的选中背景。
/// 只有当前颜色正好等于某个预设时才点亮对应颜色；自定义颜色时选中背景隐藏。
/// </summary>
internal sealed class ColorDotPicker : Control, IThemed
{
    private const int DotSize = 22;
    private const int Pitch = 30;

    private readonly int[] _centerX;
    private float _hlX;
    private bool _hlVisible;
    private readonly System.Windows.Forms.Timer _anim;

    public Color[] Presets { get; }
    public int SelectedIndex { get; private set; } = -1;
    public Color Value { get; private set; }
    public event Action? Changed;

    public ColorDotPicker(Color[] presets)
    {
        Presets = presets;
        _centerX = new int[presets.Length];
        for (int i = 0; i < presets.Length; i++) _centerX[i] = Pitch / 2 + i * Pitch;
        Size = new Size(presets.Length * Pitch + 2, 32);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += (_, _) => StepAnimation();
        Value = presets.Length > 0 ? presets[0] : Color.White;
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>设置当前颜色：与某个预设相同则选中它并滑动过去，否则隐藏选中背景。</summary>
    public void SetValue(Color c, bool raise)
    {
        Value = c;
        int idx = Array.FindIndex(Presets, p => p.ToArgb() == c.ToArgb());
        if (idx == SelectedIndex && (idx >= 0) == _hlVisible)
        {
            Invalidate();
            if (raise) Changed?.Invoke();
            return;
        }
        SelectedIndex = idx;
        if (idx >= 0) StartSlideTo(idx);
        else _hlVisible = false;
        if (raise) Changed?.Invoke();
        Invalidate();
    }

    private void StartSlideTo(int idx)
    {
        float target = _centerX[idx];
        if (!_hlVisible) { _hlVisible = true; _hlX = target; Invalidate(); return; }
        if (Math.Abs(target - _hlX) < 0.5f) { _hlX = target; Invalidate(); return; }
        _anim.Start();
    }

    private void StepAnimation()
    {
        float target = _centerX[Math.Clamp(SelectedIndex, 0, _centerX.Length - 1)];
        float dx = target - _hlX;
        if (Math.Abs(dx) < 1f) { _hlX = target; _anim.Stop(); }
        else _hlX += dx * 0.4f;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int idx = e.X / Pitch;
            if (idx >= 0 && idx < Presets.Length)
            {
                if (idx == SelectedIndex) return;
                SelectedIndex = idx;
                Value = Presets[idx];
                StartSlideTo(idx);
                Invalidate();
                Changed?.Invoke();
            }
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 选中背景（平滑滑动到目标颜色球，描边用该颜色本身，未保存时也能看出选中的是哪一个）
        if (_hlVisible && SelectedIndex >= 0)
        {
            var hl = new Rectangle((int)Math.Round(_hlX - 15), 1, 30, 30);
            RP.Fill(g, hl, 15, SC.CardBg);
            RP.Stroke(g, hl, 15, Presets[Math.Clamp(SelectedIndex, 0, Presets.Length - 1)], 1.6f);
        }

        for (int i = 0; i < Presets.Length; i++)
        {
            var dot = new Rectangle(_centerX[i] - DotSize / 2, 16 - DotSize / 2, DotSize, DotSize);
            using (var b = new SolidBrush(Presets[i])) g.FillEllipse(b, dot);
            using var pen = new Pen(SC.Mix(SC.GroupBg, SC.Ink, 0.18f), 1f);
            g.DrawEllipse(pen, dot);
        }
        base.OnPaint(e);
    }
}

/// <summary>圆角卡片容器（设置浮窗主体）。</summary>
internal class RoundPanel : Panel, IThemed
{
    public int Radius { get; set; } = 10;
    public bool DrawBorder { get; set; } = true;

    /// <summary>是否按圆角裁剪自身（浮层卡片需要留出投影边距，故不裁剪）。</summary>
    protected virtual bool RoundedClip => true;
    protected virtual Rectangle BorderRect => new(0, 0, Width, Height);

    public RoundPanel()
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.CardBg; Invalidate(true); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!RoundedClip || Width <= 0 || Height <= 0) return;
        using var p = RP.Path(new Rectangle(0, 0, Width, Height), Radius);
        var old = Region;
        Region = new Region(p);
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 边框画在圆角路径内侧一点点：既不会被自身的圆角裁剪吃掉四个角，
        // 也要求子控件从 3px 处开始摆放（见 SettingsOverlay.LayoutCard），否则会被盖住。
        if (DrawBorder)
        {
            var r = BorderRect;
            var inner = new Rectangle(r.X + 1, r.Y + 1, Math.Max(2, r.Width - 2), Math.Max(2, r.Height - 2));
            RP.Stroke(g, inner, Math.Max(2, Radius - 1), SC.CardBorder, 1.6f);
        }
        base.OnPaint(e);
    }
}

/// <summary>
/// 悬浮卡片（用于“未保存改动”确认）：只保留填充与 1px 边框，不画任何投影。
/// </summary>
internal sealed class FloatingCard : RoundPanel
{
    /// <summary>四周留白（默认 0：不画阴影，直接按圆角裁剪）。</summary>
    public int Shadow { get; set; } = 0;

    protected override Rectangle BorderRect => InnerRect();

    public Rectangle InnerRect() =>
        new(Shadow, Shadow, Math.Max(1, Width - Shadow * 2), Math.Max(1, Height - Shadow * 2));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inner = InnerRect();
        RP.Fill(g, inner, Radius, SC.CardBg);
        base.OnPaint(e);
    }
}

/// <summary>
/// 浮窗自己的描边：只占最外圈约 1.4px 的圆角环形区域，画在所有子控件之上，
/// 这样四个圆角处的边框也不会被菜单栏/内容面板盖住。
/// </summary>
internal sealed class CardBorderRing : Control, IThemed
{
    public int Radius { get; set; } = 10;
    private const float Thickness = 1.4f;

    public CardBorderRing()
    {
        Enabled = false;      // 不拦截鼠标
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() => Invalidate();

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 6 || Height <= 6) return;
        using var outer = RP.Path(new Rectangle(0, 0, Width, Height), Radius);
        var innerRect = new Rectangle(
            (int)Math.Round(Thickness), (int)Math.Round(Thickness),
            Math.Max(2, Width - (int)Math.Round(Thickness) * 2),
            Math.Max(2, Height - (int)Math.Round(Thickness) * 2));
        using var inner = RP.Path(innerRect, Math.Max(2, Radius - (int)Math.Round(Thickness)));
        using var region = new Region(outer);
        region.Exclude(inner);
        var old = Region;
        Region = region.Clone();
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(SC.CardBorder);
        e.Graphics.FillRectangle(b, ClientRectangle);
        base.OnPaint(e);
    }
}
/// <summary>浮窗右上角的圆形关闭按钮。</summary>
internal sealed class CloseButton : Control, IThemed
{
    private bool _hover;

    public CloseButton()
    {
        Size = new Size(28, 28);
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.CardBg; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hover)
            using (var b = new SolidBrush(SC.Mix(SC.CardBg, Theme.Danger, 0.14f)))
                g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        Gfx.DrawGlyph(g, Glyph.Close, new RectangleF(8, 8, Width - 16, Height - 16),
            _hover ? Theme.Danger : SC.InkMuted, 1.7f);
        base.OnPaint(e);
    }
}

/// <summary>
/// 设置页内容区：自绘的细长圆角滚动条（替代系统滚动条，暗色/亮色都好看）。
/// 只负责滚动与画滚动条，内容由外部通过 <see cref="SetContent"/> 设置。
/// </summary>
internal sealed class ScrollArea : Panel, IThemed
{
    private const int BarW = 6;        // 滑块宽度
    private const int BarGap = 5;      // 距右边缘
    private Control? _content;
    private int _offset;
    private bool _dragBar;
    private bool _hoverBar;

    public ScrollArea()
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetContent(Control content)
    {
        _content = content;
        if (!Controls.Contains(content)) Controls.Add(content);
        Apply();
    }

    /// <summary>内容高度变化 / 尺寸变化后重新计算滚动范围。</summary>
    public void Relayout(int width, int contentHeight)
    {
        if (_content == null) return;
        _content.SetBounds(0, -_offset, Math.Max(20, width), Math.Max(1, contentHeight));
        Apply();
    }

    private int MaxOffset => _content == null ? 0 : Math.Max(0, _content.Height - Height);
    private Rectangle BarRect()
    {
        if (_content == null || _content.Height <= Height) return Rectangle.Empty;
        int trackH = Height - 8;
        int h = Math.Max(32, (int)Math.Round(trackH * (Height / (double)_content.Height)));
        int y = 4 + (int)Math.Round((trackH - h) * (_offset / (double)Math.Max(1, MaxOffset)));
        return new Rectangle(Width - BarW - BarGap, y, BarW, h);
    }

    /// <summary>内容滚动后触发（下拉弹窗据此跟随移动）。</summary>
    public event Action? Scrolled;

    private void Apply()
    {
        if (_content == null) return;
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        _content.Top = -_offset;
        Invalidate();
        Scrolled?.Invoke();
    }

    public void ScrollBy(int dy)
    {
        _offset = Math.Clamp(_offset + dy, 0, MaxOffset);
        Apply();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(-e.Delta / 120 * 60);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var bar = BarRect();
        if (e.Button == MouseButtons.Left && !bar.IsEmpty && bar.Contains(e.Location))
        {
            _dragBar = true;
        }
        else if (e.Button == MouseButtons.Left && bar.Width > 0)
        {
            // 点击滚动条轨道：翻页
            ScrollBy(e.Y < BarRect().Y ? -Height + 80 : Height - 80);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = !BarRect().IsEmpty && Rectangle.Inflate(BarRect(), 4, 4).Contains(e.Location);
        if (over != _hoverBar) { _hoverBar = over; Invalidate(); }
        if (_dragBar)
        {
            int trackH = Height - 8;
            int h = BarRect().Height;
            int y = Math.Clamp(e.Y - h / 2 - 4, 0, Math.Max(1, trackH - h));
            _offset = MaxOffset == 0 ? 0 : (int)Math.Round(y / (double)Math.Max(1, trackH - h) * MaxOffset);
            Apply();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragBar = false; base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { if (_hoverBar) { _hoverBar = false; Invalidate(); } base.OnMouseLeave(e); }

    /// <summary>鼠标滚轮交给本控件处理（内容控件不再单独滚动）。</summary>
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        var added = e.Control;
        if (added != null) added.MouseWheel += (_, ev) => OnMouseWheel(ev);
    }

    public void Restyle()
    {
        BackColor = SC.CardBg;
        Invalidate(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bar = BarRect();
        if (bar.IsEmpty) return;
        float k = _hoverBar || _dragBar ? 0.42f : 0.28f;
        RP.Fill(g, bar, BarW / 2, SC.Mix(SC.CardBg, SC.Ink, k));
        base.OnPaint(e);
    }
}

/// <summary>
/// 下拉选择框：左侧显示当前选项，右侧箭头；点开后在本浮窗内弹出选项列表（自绘，圆角 + 细阴影）。
/// </summary>
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
        Size = new Size(width, 34);
        SelectedIndex = Math.Clamp(selected ?? Math.Max(0, items.Length - 1), 0, Math.Max(0, items.Length - 1));
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Select(int idx, bool raise)
    {
        idx = Math.Clamp(idx, 0, Items.Length - 1);
        if (idx == SelectedIndex) { Invalidate(); return; }
        SelectedIndex = idx;
        Invalidate();
        if (raise) Chosen?.Invoke(idx);
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

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
internal sealed class DropdownList : Control, IThemed
{
    private const int RowH = 30;
    private const int Pad = 6;          // 四周留白：仅够画一圈很淡的投影
    /// <summary>投影留白（供下拉框对齐列表本体用）。</summary>
    public const int ProjectionPad = Pad;
    private int _hover = -1;
    private readonly int _selected;
    private int _scroll;                // 列表比可视区高时的滚动偏移（像素）

    public string[] Items { get; }
    public event Action<int>? ItemChosen;
    public event Action? Closed;

    /// <summary>请求收起（供点击过滤器调用）。</summary>
    public void RequestClose() => Closed?.Invoke();

    public DropdownList(string[] items, int selected, int width)
    {
        Items = items;
        _selected = selected;
        Size = new Size(width + Pad * 2, items.Length * RowH + 10 + Pad * 2);
        BackColor = SC.CardBg;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    /// <summary>列表本体区域（去掉投影留白）。</summary>
    public Rectangle BodyRect() =>
        new(Pad, Pad, Math.Max(10, Width - Pad * 2), Math.Max(10, Height - Pad * 2));

    /// <summary>按可用高度裁剪列表高度（不够高时内部滚动）。可反复调用：每次按内容重新计算，不会越缩越小。</summary>
    public void FitHeight(int available)
    {
        int wanted = Items.Length * RowH + 10;                                  // 内容高度
        int body = Math.Min(wanted, Math.Max(RowH + 10, available));            // 至少留一行可滚动
        Size = new Size(Width, body + Pad * 2);
    }

    private int ContentHeight => Items.Length * RowH + 10;
    private int MaxScroll => Math.Max(0, ContentHeight - BodyRect().Height);

    public void Restyle() { BackColor = SC.CardBg; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (MaxScroll > 0)
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 120 * RowH, 0, MaxScroll);
            Invalidate();
        }
        Trace.Log($"list wheel delta={e.Delta} scroll={_scroll} maxScroll={MaxScroll}");
        base.OnMouseWheel(e);
    }

    private int ItemAt(Point p)
    {
        int y = p.Y - Pad - 5 + _scroll;
        if (y < 0) return -1;
        int i = y / RowH;
        return i >= 0 && i < Items.Length ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = ItemAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (_hover != -1) { _hover = -1; Invalidate(); } base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = ItemAt(e.Location);
        Trace.Log($"list mousedown at {e.Location.X},{e.Location.Y} scroll={_scroll} -> item {i}");
        if (e.Button == MouseButtons.Left && i >= 0) ItemChosen?.Invoke(i);
        else Closed?.Invoke();       // 点列表以外的任何位置都收起
        base.OnMouseDown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Closed?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>失焦即收起（点其它地方时行为自然）。</summary>
    protected override void OnLeave(EventArgs e) { Closed?.Invoke(); base.OnLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = BodyRect();

        // 很淡的一圈投影：只用来把菜单和背景轻轻分开，不做厚重阴影
        for (int i = Pad; i >= 1; i--)
        {
            using var sp = RP.Path(Rectangle.Inflate(body, i, i), RowRadius + i);
            using var sb = new SolidBrush(Color.FromArgb(3, 12, 16, 28));
            g.FillPath(sb, sp);
        }

        RP.Fill(g, body, RowRadius, SC.CardBg);
        var oldClip = g.Clip;
        g.SetClip(body);
        for (int i = 0; i < Items.Length; i++)
        {
            var row = new Rectangle(body.X + 5, body.Y + 5 + i * RowH - _scroll, body.Width - 10, RowH);
            if (i == _selected) RP.Fill(g, row, 7, SC.Mix(SC.CardBg, SC.Accent, 0.10f));
            else if (i == _hover) RP.Fill(g, row, 7, SC.AccentSoft);
            var ink = i == _selected ? SC.Accent : SC.Ink;
            // 选中只改颜色，不改字号/粗细
            TextRenderer.DrawText(g, Items[i], SF.Get(10.5f),
                new Rectangle(row.X + 10, row.Y, row.Width - 34, row.Height), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (i == _selected)
                Gfx.DrawGlyph(g, Glyph.Check, new RectangleF(row.Right - 22, row.Y + 8, 13, 13), SC.Accent, 1.6f);
        }
        g.Clip = oldClip;
        RP.Stroke(g, body, RowRadius, SC.FieldBorder);
        base.OnPaint(e);
    }

    private const int RowRadius = 10;
}
