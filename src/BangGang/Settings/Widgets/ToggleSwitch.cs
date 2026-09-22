using System.Drawing.Drawing2D;

namespace BangGang;

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

    /// <summary>
    /// 变灰 / 变回可用的那一刻。**必须把悬浮态清掉**：鼠标可能正停在上面，
    /// 而禁用之后 OnMouseLeave 不会再来了（WinForms 不给被禁用的控件派发鼠标消息），
    /// 那个「悬浮中」的亮色会一直挂着，看着像还能点。
    /// </summary>
    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) _hover = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        // 显式判一次 Enabled，不指望「被禁用的控件收不到鼠标消息」这条 WinForms 细节：
        // 它取决于控件有没有被加上 WS_DISABLED 窗口样式，是个实现细节而不是语言保证，
        // 万一不成立，「总开关关掉后下面还能点」会安静地发生（开关照样变、设置照样存），
        // 而那正是这一版要修掉的东西。多这一行，行为在这里就看得见。
        if (!Enabled) { base.OnMouseClick(e); return; }

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

        // 不可改时整块往分组底色上退，但**保留开关本身的形状**（开着还是关着看得出来）——
        // 直接涂成一色的话，「这个工具是关掉的」和「现在不许动」就分不出来了，
        // 而那两件事的含义完全不同。
        Color fill = Dim(_on
            ? (_hover ? SC.Mix(Theme.Accent, Color.White, 0.12f) : Theme.Accent)
            : SC.Mix(SC.FieldBg, SC.FieldBorder, 0.85f));
        RP.Fill(g, track, track.Height / 2, fill);
        if (!_on) RP.Stroke(g, track, track.Height / 2, Dim(SC.FieldBorder));

        int d = track.Height - 6;
        int kx = _on ? track.Right - d - 3 : track.X + 3;
        using (var b = new SolidBrush(Dim(Color.White)))
            g.FillEllipse(b, kx, track.Y + 3, d, d);
        using (var pen = new Pen(Dim(_on ? Theme.Accent : SC.FieldBorder), 1f))
            g.DrawEllipse(pen, kx, track.Y + 3, d, d);
        base.OnPaint(e);
    }

    /// <summary>可改时原样返回；不可改时把颜色往分组底色上拉一把。0.55 是「明显灰了、但仍认得出原来的颜色」。</summary>
    private Color Dim(Color c) => Enabled ? c : SC.Mix(c, SC.GroupBg, 0.55f);
}

/// <summary>
/// 分段选择器（按住/按下、亮色/暗色/跟随系统 这类少量互斥选项）。
/// 宽度按各选项文字自适应；选中态用一块**平滑滑动的背景**表示，且不改变字号/粗细。
/// </summary>
