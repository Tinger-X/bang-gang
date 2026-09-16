using System.Drawing.Drawing2D;

namespace BangGang;

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
