using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>小型图标按钮（放大镜 / 齿轮 / 加号 / 发送 / 关闭 / 回形针等），不改变鼠标指针。</summary>
internal sealed class IconButton : Control
{
    public enum Kind { Search, Gear, Plus, Send, Record, Close, Paperclip }

    public Kind Icon { get; set; }
    private bool _hover;

    public IconButton(Kind kind, Color? backdrop = null)
    {
        Icon = kind;
        Size = new Size(28, 28);
        BackColor = backdrop ?? Theme.SideBg;   // 不透明：与所在面板同色即可无痕
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 常驻浅底圆钮，让图标清晰可见
        using (var bg = new SolidBrush(_hover ? Color.FromArgb(226, 230, 238) : Color.FromArgb(239, 242, 248)))
            g.FillEllipse(bg, 1, 1, Width - 2, Width - 2);

        Color ink = _hover ? Theme.Accent : Color.FromArgb(96, 105, 120);
        using var pen = new Pen(ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float c = Width / 2f;
        switch (Icon)
        {
            case Kind.Search:
                g.DrawEllipse(pen, c - 5, c - 5, 9, 9);
                g.DrawLine(pen, c + 3, c + 3, c + 7, c + 7);
                break;
            case Kind.Gear:
                DrawGear(g, c, c, 6f, ink);
                break;
            case Kind.Plus:
                g.DrawLine(pen, c, c - 6, c, c + 6);
                g.DrawLine(pen, c - 6, c, c + 6, c);
                break;
            case Kind.Send:
                g.DrawLine(pen, c - 6, c - 2, c + 5, c - 6);
                g.DrawLine(pen, c - 6, c + 2, c + 6, c + 5);
                g.DrawLine(pen, c - 6, c - 2, c - 4, c + 2);
                break;
            case Kind.Record:
                using (var rb = new SolidBrush(Color.Crimson)) g.FillEllipse(rb, c - 4, c - 4, 8, 8);
                break;
            case Kind.Close:
                using (var xb = new SolidBrush(_hover ? Color.FromArgb(230, 214, 60, 54) : Color.FromArgb(239, 242, 248)))
                    g.FillEllipse(xb, 1, 1, Width - 2, Width - 2);
                using (var xp = new Pen(_hover ? Color.White : ink, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(xp, c - 4, c - 4, c + 4, c + 4);
                    g.DrawLine(xp, c + 4, c - 4, c - 4, c + 4);
                }
                break;
            case Kind.Paperclip:
                g.DrawArc(pen, c - 7, c - 3, 9, 10, 60, 300);
                g.DrawLine(pen, c - 3, c, c + 6, c + 2);
                break;
        }
        base.OnPaint(e);
    }

    private void DrawGear(Graphics g, float cx, float cy, float r, Color ink)
    {
        // 设置：三条水平调节滑杆（比齿轮更清晰，避免被误认作太阳）
        using var pen = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float[] xs = { -3f, 3f, -3f }; // 旋钮水平偏移
        for (int i = 0; i < 3; i++)
        {
            float y = cy + (i - 1) * 5;
            g.DrawLine(pen, cx - 7, y, cx + 7, y);
            using var kb = new SolidBrush(ink);
            g.FillEllipse(kb, cx + xs[i] - 2.5f, y - 2.5f, 5, 5);
            using var kw = new SolidBrush(BackColor);
            g.FillEllipse(kw, cx + xs[i] - 1.1f, y - 1.1f, 2.2f, 2.2f);
        }
    }
}
