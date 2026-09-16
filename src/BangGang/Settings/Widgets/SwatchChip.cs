using System.Drawing.Drawing2D;

namespace BangGang;

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
