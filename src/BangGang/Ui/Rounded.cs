using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>圆角路径工具。</summary>
internal static class RP
{
    public static GraphicsPath Path(Rectangle r, int rad) =>
        PathF(new RectangleF(r.X, r.Y, r.Width, r.Height), rad);

    public static GraphicsPath PathF(RectangleF r, int rad)
    {
        var p = new GraphicsPath();
        if (rad <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
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

    /// <summary>
    /// 画一块抗锯齿的圆角块：先用 <paramref name="backdrop"/>（该圆角块底下真正显示的颜色）
    /// 铺满整个矩形，再用抗锯齿路径填充圆角本体。
    /// 这样圆角边缘是“本体色 ↔ 底色”的混合像素，不会出现 Region 硬裁剪造成的锯齿；
    /// 调用前必须保证 <c>SmoothingMode = AntiAlias</c>。
    /// </summary>
    public static void Box(Graphics g, Rectangle r, int rad, Color body, Color backdrop)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using (var bb = new SolidBrush(backdrop)) g.FillRectangle(bb, r);
        Fill(g, r, rad, body);
    }

    /// <summary>圆角块底下“真正显示的颜色”：沿父链找到第一个不透明的背景色。</summary>
    public static Color BackdropOf(Control? c, Color fallback)
    {
        for (var p = c?.Parent; p != null; p = p.Parent)
        {
            if (!p.BackColor.IsEmpty && p.BackColor.A == 255) return p.BackColor;
        }
        return fallback;
    }

    public static void Stroke(Graphics g, Rectangle r, int rad, Color c, float w = 1f)
    {
        var rc = new RectangleF(r.X + w / 2f, r.Y + w / 2f, r.Width - w, r.Height - w);
        using var p = PathF(rc, rad);
        using var pen = new Pen(c, w);
        g.DrawPath(pen, p);
    }
}
