using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BangGang;

/// <summary>
/// 图片放大查看浮层：点对话里的图片时铺满整窗显示。
/// 滚轮缩放（以指针为锚点）、按住拖动平移、点图片以外的地方或 Esc 关闭。
///
/// 它是**主窗口的子控件**而不是独立窗口 —— 独立顶层窗口要另外挂防录屏 affinity，
/// 漏一个就成了一扇能把整段对话截图带出去的窗（见 CaptureProtector 的注释）。
/// 铺满整窗还有个好处：底下的界面不用重画，直接一层暗色蒙版盖过去。
///
/// 指针形状一律不动（用户明确要求过）：能不能点靠蒙版上的提示文字说，不靠手型指针。
/// </summary>
internal sealed class ImageViewer : Control, IThemed
{
    /// <summary>图片与窗口边缘至少留出的空白，同时也是「点在图片外 = 关闭」的判定基准。</summary>
    private const int EdgePad = 28;

    private const float MaxZoom = 8f;

    private Image? _img;
    private string _name = "";

    /// <summary>放大倍率。&lt;= 0 表示「适应窗口」——窗口一变它就该跟着变，所以不能存成具体数值。</summary>
    private float _zoom;
    private float _panX, _panY;

    private bool _drag;
    private Point _dragFrom;
    private float _dragPanX, _dragPanY;

    public ImageViewer()
    {
        BackColor = Color.Black;
        Dock = DockStyle.None;
        Visible = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
               | ControlStyles.Selectable, true);
        TabStop = false;
    }

    public bool IsOpen => Visible;

    /// <summary>打开某张图。读不出来返回 false（调用方据此不弹浮层）。</summary>
    public bool Open(string path, string name)
    {
        var img = LoadCapped(path);
        if (img == null) return false;

        _img?.Dispose();
        _img = img;
        _name = name.Length > 0 ? name : Path.GetFileName(path);
        _zoom = 0;
        _panX = _panY = 0;
        _drag = false;

        Visible = true;
        BringToFront();
        Focus();
        Invalidate();
        return true;
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        _drag = false;
        _img?.Dispose();
        _img = null;
        Invalidate();
    }

    /// <summary>
    /// 读一张用于放大查看的图。超过 <see cref="MaxEdge"/> 的长边会先缩下来：8K 截图整张装进内存
    /// 要几百 MB，而屏幕上根本显示不了那么多像素。文件读完立刻松手（复用一份位图），
    /// 否则 <c>Image.FromFile</c> 会把用户的原文件一直锁着。
    /// </summary>
    private const int MaxEdge = 4096;

    private static Image? LoadCapped(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var src = Image.FromStream(fs);
            int edge = Math.Max(src.Width, src.Height);
            if (edge <= MaxEdge) return new Bitmap(src);
            double k = (double)MaxEdge / edge;
            var bmp = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
            }
            return bmp;
        }
        catch { return null; }
    }

    // ---------------- 几何 ----------------

    /// <summary>「适应窗口」时的倍率。原图比窗口小就不放大 —— 那是糊，不是「看得更清」。</summary>
    private float FitScale
    {
        get
        {
            if (_img == null || _img.Width <= 0 || _img.Height <= 0) return 1f;
            float k = Math.Min((Width - EdgePad * 2f) / _img.Width, (Height - EdgePad * 2f) / _img.Height);
            return k <= 0 ? 0.01f : Math.Min(1f, k);
        }
    }

    private float ViewScale => _img == null ? 1f : (_zoom <= 0 ? FitScale : _zoom);

    private RectangleF ImageRect()
    {
        if (_img == null) return RectangleF.Empty;
        float s = ViewScale;
        float w = _img.Width * s, h = _img.Height * s;
        float cx = Width / 2f + _panX, cy = Height / 2f + _panY;
        return new RectangleF(cx - w / 2, cy - h / 2, w, h);
    }

    /// <summary>图比窗口大的那个方向才允许拖出去；比窗口小的居中摆着（拖了也没意义）。</summary>
    private void ClampPan()
    {
        if (_img == null) { _panX = _panY = 0; return; }
        float s = ViewScale;
        float maxX = Math.Max(0f, (_img.Width * s - (Width - EdgePad * 2f)) / 2f);
        float maxY = Math.Max(0f, (_img.Height * s - (Height - EdgePad * 2f)) / 2f);
        _panX = Math.Clamp(_panX, -maxX, maxX);
        _panY = Math.Clamp(_panY, -maxY, maxY);
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_img == null) return;

        // 以指针为锚点：指针底下那个像素在缩放前后不动，否则放大一圈图就跑到窗口外面去了
        float sOld = ViewScale;
        float sNew = Math.Clamp(sOld * (e.Delta > 0 ? 1.15f : 1f / 1.15f), FitScale * 0.25f, MaxZoom);
        if (Math.Abs(sNew - sOld) < 1e-4f) return;

        var r = ImageRect();
        float ix = (e.X - r.Left) / sOld;
        float iy = (e.Y - r.Top) / sOld;
        _zoom = sNew;
        _panX = (e.X - ix * sNew + _img.Width * sNew / 2f) - Width / 2f;
        _panY = (e.Y - iy * sNew + _img.Height * sNew / 2f) - Height / 2f;
        ClampPan();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _img == null) return;
        if (!ImageRect().Contains(e.Location)) { Close(); return; }   // 点蒙版 = 关闭

        _drag = true;
        _dragFrom = e.Location;
        _dragPanX = _panX;
        _dragPanY = _panY;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drag) return;
        _panX = _dragPanX + (e.X - _dragFrom.X);
        _panY = _dragPanY + (e.Y - _dragFrom.Y);
        ClampPan();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
    }

    /// <summary>Esc 关闭。焦点在本控件上时才会走到这里，所以 <see cref="Open"/> 里要 Focus()。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && Visible) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void Restyle() => Invalidate();

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampPan();       // 缩放后「适应窗口」的比例变了，旧的平移量要跟着收一收
    }

    // ---------------- 绘制 ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var scrim = new SolidBrush(Color.FromArgb(226, 12, 14, 18)))
            g.FillRectangle(scrim, ClientRectangle);

        if (_img == null) return;

        var r = ImageRect();
        float s = ViewScale;
        // 放大时用最近邻：截图放大就该看见像素方块，插值只会把要看的字糊成一团。
        g.InterpolationMode = s >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(_img, r);
        // 一圈浅描边：白底截图压在同色蒙版上，不留这条边就看不出图到哪儿为止
        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255)))
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);

        DrawCaption(g);
        base.OnPaint(e);
    }

    private void DrawCaption(Graphics g)
    {
        string info = _name + "   " + _img!.Width + " x " + _img.Height
                    + "   " + (int)Math.Round(ViewScale * 100) + "%";
        const string hint = "滚轮缩放 · 拖动平移 · 点空白处或按 Esc 关闭";

        var f = SF.Get(10.5f);
        var fh = SF.Get(9.5f);
        var si = g.MeasureString(info, f);
        var sh = g.MeasureString(hint, fh);

        // 说明固定贴着窗口下缘：图放得再大也压不到它，图片再小也不会把文字推到脚下去
        float y = Height - 46;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var white = new SolidBrush(Color.FromArgb(232, 236, 242));
        using var dim = new SolidBrush(Color.FromArgb(150, 158, 168));
        g.DrawString(info, f, white, (Width - si.Width) / 2f, y);
        g.DrawString(hint, fh, dim, (Width - sh.Width) / 2f, y + 20);
    }
}
