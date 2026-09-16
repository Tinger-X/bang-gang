using System.Drawing.Drawing2D;

namespace BangGang;

internal class RoundPanel : Panel, IThemed
{
    public int Radius { get; set; } = 10;
    public bool DrawBorder { get; set; } = true;

    /// <summary>圆角块底下真正显示的颜色（默认取父级背景色；浮层可显式指定）。</summary>
    public Color? Backdrop { get; set; }

    /// <summary>圆角以外的像素来源：底层界面快照（浮层卡片用它做到圆角处也显示真实底层）。</summary>
    public Bitmap? BackdropBitmap { get; set; }

    /// <summary>本控件左上角在 <see cref="BackdropBitmap"/> 中的坐标。</summary>
    public Point BackdropOffset { get; set; }

    protected virtual Rectangle BodyRect => new(0, 0, Width, Height);
    protected virtual Rectangle BorderRect => new(0, 0, Width, Height);

    public RoundPanel()
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.CardBg; Invalidate(true); }

    /// <summary>背景不透明地画成圆角：先铺底层（快照或底色），再抗锯齿填充圆角本体。</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var body = BodyRect;
        var full = new Rectangle(0, 0, Width, Height);

        if (Radius <= 0)
        {
            // 直角：整块铺满本体色即可（快照只在圆角缺口处才看得见，直角下无缺口）
            using var b0 = new SolidBrush(BackColor);
            g.FillRectangle(b0, full);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var snap = BackdropBitmap;
        var src = new Rectangle(BackdropOffset.X, BackdropOffset.Y, Width, Height);
        if (snap != null && src.X >= 0 && src.Y >= 0 && src.Right <= snap.Width && src.Bottom <= snap.Height)
        {
            g.DrawImage(snap, full, src, GraphicsUnit.Pixel);
        }
        else
        {
            using var bb = new SolidBrush(Backdrop ?? RP.BackdropOf(this, SC.CardBg));
            g.FillRectangle(bb, full);
        }
        RP.Fill(g, body, Radius, BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 边框画在卡片最外缘：描边以边界为中心、抗锯齿混合。
        // 不要往内缩 1px —— 那样外缘会留出一条本体色的窄环，看起来像“边框外还有一圈边框”。
        if (DrawBorder)
            RP.Stroke(g, BorderRect, Radius, SC.CardBorder, 1.6f);
        base.OnPaint(e);
    }
}

/// <summary>
/// 悬浮卡片（设置浮窗本体 / “未保存改动”确认）：只保留填充与 1px 边框，不画任何投影。
/// </summary>
internal sealed class FloatingCard : RoundPanel
{
    /// <summary>四周留白（默认 0：不画阴影，圆角直接贴到控件边缘）。</summary>
    public int Shadow { get; set; } = 0;

    protected override Rectangle BorderRect => InnerRect();
    protected override Rectangle BodyRect => InnerRect();

    public Rectangle InnerRect() =>
        new(Shadow, Shadow, Math.Max(1, Width - Shadow * 2), Math.Max(1, Height - Shadow * 2));
}


/// <summary>浮窗右上角的圆形关闭按钮。</summary>
