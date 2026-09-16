using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>左侧栏顶部品牌块：LOGO / 名称 / 作者版权 / 版本。</summary>
internal sealed class BrandBlock : Panel
{
    public BrandBlock()
    {
        BackColor = Theme.SideBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var tile = new Rectangle(18, 24, 52, 52);
        using (var bg = new SolidBrush(Theme.Accent))
            g.FillEllipse(bg, tile);
        using (var f = new Font("Microsoft YaHei UI", 24f, FontStyle.Bold))
        using (var path = new GraphicsPath())
        {
            // 用字形墨迹（GraphicsPath）而不是 MeasureString 来居中：
            // MeasureString 量到的行框含有上下留白，按它居中会把文字顶偏（下方空隙更大）。
            float em = f.Size * g.DpiY / 72f;
            path.AddString("帮", f.FontFamily, (int)f.Style, em, new PointF(0, 0), StringFormat.GenericTypographic);
            var ink = path.GetBounds();
            var m = new Matrix();
            m.Translate(tile.X + tile.Width / 2f - (ink.X + ink.Width / 2f),
                        tile.Y + tile.Height / 2f - (ink.Y + ink.Height / 2f));
            path.Transform(m);
            m.Dispose();
            using var b = new SolidBrush(Color.White);
            g.FillPath(b, path);
        }
        using (var name = new SolidBrush(Theme.TextMain))
        using (var sub = new SolidBrush(Theme.TextMuted))
        {
            g.DrawString("帮帮", Theme.UI(15f, FontStyle.Bold), name, 84, 30);
            g.DrawString("© 2026 Tinger  ·  " + MainForm.AppVersion, Theme.UI(9.5f), sub, 84, 56);
        }
        using var line = new Pen(Theme.Border);
        g.DrawLine(line, 12, Height - 1, Width - 12, Height - 1);
        base.OnPaint(e);
    }
}
