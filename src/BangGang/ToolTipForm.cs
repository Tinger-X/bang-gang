using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BangGang;

/// <summary>
/// 应用内悬浮提示：一个带每像素 Alpha 的置顶无边框窗口（WS_EX_LAYERED + UpdateLayeredWindow）。
/// 圆角与文字均经抗锯齿渲染，边缘平滑、四角真正透明（无锯齿、无底色）；
/// 同时应用 WDA_EXCLUDEFROMCAPTURE，与主窗口一样对录屏 / 截图不可见。
/// </summary>
internal sealed class ToolTipForm : Form
{
    private const int Radius = 5;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private string _text = "";
    private Control? _anchor;
    private readonly System.Windows.Forms.Timer _timer;

    public ToolTipForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Cursor = Cursors.Default;
        Font = Theme.UI(9f);
        _timer = new System.Windows.Forms.Timer { Interval = 520 };
        _timer.Tick += (_, _) => { _timer.Stop(); ShowNow(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    /// <summary>显示时不抢占焦点（悬浮提示不能打断输入等操作）。</summary>
    protected override bool ShowWithoutActivation => true;

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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    private void ShowNow()
    {
        var anchor = _anchor;
        if (anchor is null || anchor.IsDisposed) return;
        var owner = anchor.FindForm();
        if (owner is null || owner.IsDisposed || !owner.Visible) { Visible = false; return; }

        var textSize = MeasureText(_text, Font);
        Size = new Size((int)Math.Ceiling(textSize.Width) + 12, (int)Math.Ceiling(textSize.Height) + 6);

        var screen = Screen.FromControl(anchor);
        var wa = screen.WorkingArea;
        var p = anchor.PointToScreen(Point.Empty);
        int x = p.X + anchor.Width / 2 - Width / 2;
        int y = p.Y + anchor.Height + 6;
        if (y + Height > wa.Bottom)
            y = p.Y - Height - 6; // 下方空间不足时显示在按钮上方

        x = Math.Clamp(x, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - Width - 4));
        y = Math.Clamp(y, wa.Top + 4, Math.Max(wa.Top + 4, wa.Bottom - Height - 4));

        Location = new Point(x, y);
        if (!Visible) Show();

        Render();

        // 每次显示后重建“排除”表面（与主窗口 Alt+X 后的处理一致），
        // 防止显示后 WDA_EXCLUDEFROMCAPTURE 的排除表面失效。
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_NONE);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    private void Render()
    {
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var rc = new Rectangle(0, 0, w - 1, h - 1);
            using var path = Rounded(rc, Radius);
            using (var bg = new SolidBrush(Color.FromArgb(255, 62, 66, 74)))
                g.FillPath(bg, path);
            using (var border = new Pen(Color.FromArgb(60, 120, 128, 136)))
                g.DrawPath(border, path);

            DrawCenteredText(g, _text, Font, Color.FromArgb(224, 228, 233), new RectangleF(0, 0, w, h));
        }
        UpdateLayered(bmp);
    }

    private void UpdateLayered(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var ptSrc = new POINT { X = 0, Y = 0 };
            var ptDst = new POINT { X = Left, Y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0 /*AC_SRC_OVER*/, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            _ = UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>紧凑测量格式（与 GenericTypographic 一致：无额外行距/内边距、不省略、不裁剪）。</summary>
    private static StringFormat TightFormat() => new StringFormat(
        StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit | StringFormatFlags.NoClip)
    {
        Trimming = StringTrimming.None,
    };

    private static SizeF MeasureText(string text, Font font)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var fmt = TightFormat();
        return g.MeasureString(text, font, int.MaxValue, fmt);
    }

    /// <summary>按紧凑尺寸手动居中绘制文本，保证水平、垂直都真正视觉居中。</summary>
    private static void DrawCenteredText(Graphics g, string text, Font font, Color color, RectangleF rect)
    {
        using var brush = new SolidBrush(color);
        using var fmt = TightFormat();
        var size = g.MeasureString(text, font, int.MaxValue, fmt);
        float x = rect.X + (rect.Width - size.Width) / 2f;
        // 背景框相对文字上移 1 个单位（等价于文字在框内下移 1px）
        float y = rect.Y + (rect.Height - size.Height) / 2f + 1f;
        g.DrawString(text, font, brush, x, y, fmt);
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

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
