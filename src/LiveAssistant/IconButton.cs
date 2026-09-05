using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>小型图标按钮（放大镜 / 齿轮 / 加号 / 发送 / 关闭），自绘、带悬停。</summary>
internal sealed class IconButton : Control
{
    public enum Kind { Search, Gear, Plus, Send, Record, Close, Paperclip }
    public Kind Icon { get; set; }
    private bool _hover;

    public IconButton(Kind kind)
    {
        Icon = kind;
        Size = new Size(28, 28);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hover)
        {
            using var b = new SolidBrush(Color.FromArgb(40, 100, 140, 190));
            g.FillEllipse(b, 1, 1, Width - 2, Height - 2);
        }
        var pen = new Pen(Theme.TextMuted, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        try
        {
            float c = Width / 2f;
            switch (Icon)
            {
                case Kind.Search: // 放大镜
                    g.DrawEllipse(pen, c - 5, c - 5, 9, 9);
                    g.DrawLine(pen, c + 3, c + 3, c + 7, c + 7);
                    break;
                case Kind.Gear:
                    DrawGear(g, c, c, 6.5f);
                    break;
                case Kind.Plus:
                    g.DrawLine(pen, c, c - 6, c, c + 6);
                    g.DrawLine(pen, c - 6, c, c + 6, c);
                    break;
                case Kind.Send: // 纸飞机
                    g.DrawLine(pen, c - 6, c - 2, c + 5, c - 6);
                    g.DrawLine(pen, c - 6, c + 2, c + 6, c + 5);
                    g.DrawLine(pen, c - 6, c - 2, c - 4, c + 2);
                    break;
                case Kind.Record: // 麦克风示意：圆点
                    using (var rb = new SolidBrush(Color.Crimson)) g.FillEllipse(rb, c - 4, c - 4, 8, 8);
                    break;
                case Kind.Close:
                    using (var xb = new SolidBrush(_hover ? Color.FromArgb(225, 224, 60, 54) : Color.Transparent))
                        g.FillEllipse(xb, 1, 1, Width - 2, Height - 2);
                    using (var xp = new Pen(_hover ? Color.White : Theme.TextMuted, 2f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(xp, c - 4, c - 4, c + 4, c + 4);
                        g.DrawLine(xp, c + 4, c - 4, c - 4, c + 4);
                    }
                    break;
                case Kind.Paperclip:
                    using (var pp = new Pen(Theme.TextMuted, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawArc(pp, c - 7, c - 3, 9, 10, 60, 300);
                        g.DrawLine(pp, c - 3, c, c + 6, c + 2);
                    }
                    break;
            }
        }
        finally { pen.Dispose(); }
        base.OnPaint(e);
    }

    private void DrawGear(Graphics g, float cx, float cy, float r)
    {
        using var pen = new Pen(Theme.TextMuted, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        float inner = r - 2f;
        g.DrawEllipse(pen, cx - inner, cy - inner, inner * 2, inner * 2);
        for (int i = 0; i < 8; i++)
        {
            double a = Math.PI / 4 * i;
            float x1 = (float)(cx + Math.Cos(a) * r);
            float y1 = (float)(cy + Math.Sin(a) * r);
            float x2 = (float)(cx + Math.Cos(a) * (r + 3));
            float y2 = (float)(cy + Math.Sin(a) * (r + 3));
            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }
}
