using System.Drawing.Drawing2D;

namespace BangGang;

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
