using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 可选的主题色窗口边框：只保留窗口最外圈 2px 的直角环形区域，
/// 因此既画在全部内容之上，又不会遮挡任何界面。
/// </summary>
internal sealed class WindowFrame : Control
{
    public WindowFrame()
    {
        Enabled = false;          // 不拦截鼠标
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 4 || Height <= 4) return;
        const int t = 2;
        using var outer = new GraphicsPath();
        outer.AddRectangle(new Rectangle(0, 0, Width, Height));
        using var inner = new GraphicsPath();
        inner.AddRectangle(new Rectangle(t, t, Width - t * 2, Height - t * 2));
        using var region = new Region(outer);
        region.Exclude(inner);
        var old = Region;
        Region = region.Clone();
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(Theme.Mix(Theme.Accent, Theme.PanelBg, 0.3f));
        e.Graphics.FillRectangle(b, ClientRectangle);
        base.OnPaint(e);
    }
}
