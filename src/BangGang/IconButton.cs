using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>小型图标按钮（放大镜 / 齿轮 / 加号 / 发送 / 关闭 / 回形针等），不改变鼠标指针。</summary>
internal sealed class IconButton : Control, IThemed
{
    public enum Kind { Search, Gear, Plus, Send, Record, Close, Paperclip, Maximize, Restore }

    public Kind Icon { get; set; }
    private readonly Color? _backdropHint;
    private bool _hover;

    public IconButton(Kind kind, Color? backdrop = null)
    {
        Icon = kind;
        _backdropHint = backdrop;
        Size = new Size(28, 28);
        BackColor = backdrop ?? Theme.SideBg;   // 不透明：与所在面板同色即可无痕
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>
    /// 主题切换后重新贴合所在面板的底色。
    /// 必须取“当前”父面板底色：构造时传进来的颜色属于旧主题，继续沿用就会在按钮四周留一圈旧色。
    /// </summary>
    public void Restyle()
    {
        BackColor = Parent?.BackColor ?? _backdropHint ?? Theme.SideBg;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 常驻浅底圆钮，让图标清晰可见（随主题亮暗自动变化）
        Color rest = Theme.Mix(Theme.SideBg, Theme.TextMuted, 0.14f);
        Color over = Theme.Mix(Theme.SideBg, Theme.TextMuted, 0.26f);
        using (var bg = new SolidBrush(_hover ? over : rest))
            g.FillEllipse(bg, 1, 1, Width - 2, Width - 2);

        Color ink = _hover ? Theme.Accent : Theme.Mix(Theme.TextMuted, Theme.TextMain, 0.35f);
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
                using (var xb = new SolidBrush(_hover ? Theme.Mix(Theme.SideBg, Theme.Danger, 0.85f) : rest))
                    g.FillEllipse(xb, 1, 1, Width - 2, Width - 2);
                using (var xp = new Pen(_hover ? Color.White : ink, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(xp, c - 4, c - 4, c + 4, c + 4);
                    g.DrawLine(xp, c + 4, c - 4, c - 4, c + 4);
                }
                break;
            case Kind.Maximize:
                g.DrawRectangle(pen, c - 5, c - 5, 10, 10);
                break;
            case Kind.Restore:
                // 两个错开的方框，后面那个只画露在外面的三条边
                g.DrawRectangle(pen, c - 5, c - 3, 8, 8);
                g.DrawLine(pen, c - 3, c - 3, c - 3, c - 5);
                g.DrawLine(pen, c - 3, c - 5, c + 5, c - 5);
                g.DrawLine(pen, c + 5, c - 5, c + 5, c + 3);
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
            using var kw = new SolidBrush(_hover
                ? Theme.Mix(Theme.SideBg, Theme.TextMuted, 0.26f)
                : Theme.Mix(Theme.SideBg, Theme.TextMuted, 0.14f));
            g.FillEllipse(kw, cx + xs[i] - 1.1f, y - 1.1f, 2.2f, 2.2f);
        }
    }
}
