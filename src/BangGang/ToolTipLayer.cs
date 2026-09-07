using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 应用内自绘 ToolTip。它是主窗口的子控件（而不是独立的顶层 ToolTip 窗口），
/// 因此会随主窗口一起被 WDA_EXCLUDEFROMCAPTURE 排除——录屏 / 截图工具看不到它，
/// 从根本上避免了对独立原生 ToolTip 窗口做显示亲和性保护的不可靠性。
/// </summary>
internal sealed class ToolTipLayer : Control
{
    private string _text = "";
    private Control? _anchor;
    private readonly System.Windows.Forms.Timer _timer;

    public ToolTipLayer()
    {
        Visible = false;
        Cursor = Cursors.Default;
        Font = Theme.UI(10.5f);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        _timer = new System.Windows.Forms.Timer { Interval = 520 };
        _timer.Tick += (_, _) => { _timer.Stop(); ShowNow(); };
    }

    /// <summary>鼠标进入：启动悬浮延迟计时，到点后显示。</summary>
    public void Arm(Control anchor, string text)
    {
        if (IsDisposed) return;
        _anchor = anchor;
        _text = text;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>鼠标离开：取消计时并立即隐藏。</summary>
    public void Disarm(Control anchor)
    {
        if (IsDisposed || !ReferenceEquals(_anchor, anchor)) return;
        _timer.Stop();
        _anchor = null;
        Visible = false;
    }

    /// <summary>主窗口隐藏 / 关闭时，清除可能残留的提示。</summary>
    public void HideNow()
    {
        if (IsDisposed) return;
        _timer.Stop();
        _anchor = null;
        Visible = false;
    }

    private void ShowNow()
    {
        var anchor = _anchor;
        if (anchor is null || anchor.IsDisposed) return;
        var form = anchor.FindForm();
        if (form is null || form.IsDisposed || !form.Visible) { Visible = false; return; }

        var sz = TextRenderer.MeasureText(_text, Font);
        Size = new Size(sz.Width + 24, sz.Height + 14);

        var p = form.PointToClient(anchor.PointToScreen(Point.Empty));
        int x = p.X + anchor.Width / 2 - Width / 2;
        int y = p.Y + anchor.Height + 6;
        if (y + Height > form.ClientSize.Height - 4)
            y = p.Y - Height - 6; // 下方空间不足时显示在按钮上方

        x = Math.Clamp(x, 4, Math.Max(4, form.ClientSize.Width - Width - 4));
        y = Math.Clamp(y, 4, Math.Max(4, form.ClientSize.Height - Height - 4));

        Location = new Point(x, y);
        Visible = true;
        BringToFront();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(rc, 6);
        using (var bg = new SolidBrush(Color.FromArgb(255, 38, 42, 50)))
            g.FillPath(bg, path);
        using (var border = new Pen(Color.FromArgb(90, 96, 104, 116)))
            g.DrawPath(border, path);

        TextRenderer.DrawText(g, _text, Font,
            new Rectangle(12, 0, Width - 24, Height),
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
