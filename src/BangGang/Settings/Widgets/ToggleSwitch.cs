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
