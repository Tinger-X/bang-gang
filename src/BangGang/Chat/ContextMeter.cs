using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>输入框底部那行要显示的东西。<see cref="Window"/> 非正数表示「不显示仪表」。</summary>
internal readonly record struct CtxInfo(int Used, int Window, double TokPerSec, bool Live)
{
    /// <summary>已用 / 窗口。窗口未知时是 0。</summary>
    public double Ratio => Window > 0 ? Math.Clamp((double)Used / Window, 0, 1) : 0;

    /// <summary>取整后的百分比（给环和文字共用，免得两处各算一次、显示得不一致）。</summary>
    public int Percent => (int)Math.Round(Ratio * 100);
}

/// <summary>
/// 输入框底部那枚上下文仪表：一个圆环 + 「百分比 · 已用/窗口 · 生成速度」。
///
/// 几个不显然的地方：
///
/// <list type="bullet">
///   <item>**它和原来那行快捷键提示占同一个矩形，同一时刻只有一个可见。**
///     没有把那个 Label 换掉，而是并存 —— Label 承载的「Enter 发送 · Shift+Enter 换行…」
///     是现成且验证过的（<c>AutoEllipsis</c>、<c>MiddleCenter</c> 全省事），
///     而 <see cref="InputPanel.LayoutCard"/> 已经把那个矩形算好了，两个控件共用即可。</item>
///   <item>**宽度必须是常量**（由 <c>InputPanel.MeterWidth()</c> 按最坏情况预留）。
///     这是从原来那个 Label 那儿继承来的硬约束：侧栏展开/收起时控件只平移、不变尺寸，
///     一旦宽度跟着数字变，居中每帧重算，屏幕上就会看到数字在抖。</item>
///   <item>**空闲时没有任何时钟在跑。** 它是个常驻可见的控件，但值只在「有增量到达」
///     （那时本来就有 40ms 的刷新在跑）或「一轮收尾」时变；不变就不重画。
///     那个 15ms 的缓动 Timer 到位即停，与 <c>SegmentedControl</c> 同一套。</item>
/// </list>
/// </summary>
internal sealed class ContextMeter : Control, IThemed
{
    /// <summary>环的外径。底部工具行 38px，两侧圆钮 28px，15 留得下且不喧宾夺主。</summary>
    private const int RingD = 15;

    private const float RingW = 2.4f;

    /// <summary>环与文字之间的间隔。</summary>
    private const int RingGap = 6;

    private CtxInfo _info;

    /// <summary>当前**画出来**的百分比（0–1）。缓动追着 <see cref="_info"/> 走。</summary>
    private float _shown = -1;

    private readonly System.Windows.Forms.Timer _anim;

    public ContextMeter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.InputBg;
        // 不设 Cursor：用户明确要求不改鼠标指针，全仓禁用 Cursors.Hand / Cursors.Cross。
        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += (_, _) => StepAnim();
    }

    /// <summary>换一组数字。值没变就什么都不做 —— 这是「空闲不重画」的落点。</summary>
    public void Set(CtxInfo info)
    {
        if (info == _info) return;
        _info = info;
        if (_shown < 0) _shown = (float)info.Ratio;      // 第一次直接到位，别从 0 涨上来
        if (Math.Abs(_shown - (float)info.Ratio) < 0.004f) { _shown = (float)info.Ratio; _anim.Stop(); }
        else if (!_anim.Enabled) _anim.Start();
        Invalidate();
    }

    /// <summary>缓动一步。**到位即停** —— 常驻控件尤其不能一直转。</summary>
    private void StepAnim()
    {
        float target = (float)_info.Ratio;
        float dx = target - _shown;
        if (Math.Abs(dx) < 0.004f) { _shown = target; _anim.Stop(); }
        else _shown += dx * 0.35f;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }

    public void Restyle()
    {
        BackColor = Theme.InputBg;
        Invalidate();
    }

    // ---------------- 绘制 ----------------

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Theme.InputBg);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        string text = Label(_info);
        var font = SF.Get(9.5f);
        Size textSize = TextRenderer.MeasureText(g, text, font);
        int total = RingD + RingGap + textSize.Width;
        int x = Bounds.Width > total ? (Bounds.Width - total) / 2 : 0;
        int cy = Bounds.Height / 2;

        DrawRing(g, x, cy - RingD / 2);

        var textRect = new Rectangle(x + RingD + RingGap, 0, Math.Max(10, Bounds.Width - x - RingD - RingGap),
                                     Bounds.Height);
        // 空闲时用淡色：表示的「这是上一轮的数字，不是实时在涨的」，而不是隐藏它 ——
        // 突然消失会让那一行看着像坏了。
        Color ink = _info.Live ? Theme.TextMuted : Theme.Mix(Theme.TextMuted, Theme.InputBg, 0.45f);
        TextRenderer.DrawText(g, text, font, textRect, ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private void DrawRing(Graphics g, int x, int y)
    {
        float d = RingD - RingW;
        var rect = new RectangleF(x + RingW / 2, y + RingW / 2, d, d);

        using (var track = new Pen(Theme.Mix(Theme.InputBg, Theme.TextMuted, 0.28f), RingW))
            g.DrawArc(track, rect, 0, 360);

        float sweep = _shown * 360f;
        if (sweep < 0.5f) return;                       // 0% 就只留轨道，不画一小段毛刺

        // 60% 之前纯强调色，95% 之后纯红 —— 中间线性过渡。
        float k = Math.Clamp((_shown - 0.60f) / 0.35f, 0f, 1f);
        using var pen = new Pen(Theme.Mix(Theme.Accent, Theme.Danger, k), RingW)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        // GDI+ 的 0° 在三点钟方向、角度顺时针增大，所以 -90° 才是十二点。
        //
        // 满圈要单独走 DrawEllipse：一条 360° 的弧两端各带一个圆头，会在起点处叠出一个
        // 明显的疙瘩（看着像环上多了个点）。接近满圈时也切过去，不然最后一度里那个疙瘩
        // 会突然出现。
        if (sweep >= 359.5f) g.DrawEllipse(pen, rect);
        else g.DrawArc(pen, rect, -90f, sweep);
    }

    // ---------------- 文案 ----------------

    /// <summary>「48% · 12.4K/32K · 8.4 tok/s」。拿不到任何数字时给一串占位，绝不显示空环。</summary>
    private static string Label(CtxInfo c)
    {
        if (c.Window <= 0) return "";
        string speed = c.TokPerSec > 0.05 ? " · " + c.TokPerSec.ToString("0.0") + " tok/s" : "";
        return c.Percent + "% · " + K(c.Used) + "/" + K(c.Window) + speed;
    }

    /// <summary>按最坏情况量一次宽度用（见 InputPanel.MeterWidth）。</summary>
    public static int MeasureWorstWidth()
    {
        const string worst = "100% · 999.9K/999.9K · 999.9 tok/s";
        return RingD + RingGap + TextRenderer.MeasureText(worst, SF.Get(9.5f)).Width;
    }

    /// <summary>token 数的显示缩写：812 / 8.4K / 128K。</summary>
    private static string K(int n)
    {
        if (n < 1000) return n.ToString();
        if (n < 10000) return (n / 1000.0).ToString("0.0") + "K";
        return (n / 1000).ToString("0") + "K";
    }
}
