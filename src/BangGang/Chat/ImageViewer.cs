using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BangGang;

/// <summary>
/// 图片放大查看浮层：点对话里的图片时铺满整窗显示。
/// 滚轮缩放（以指针为锚点）、按住拖动平移、点图片以外的地方或 Esc 关闭。
///
/// 它是**主窗口的子控件**而不是独立窗口 —— 独立顶层窗口要另外挂防录屏 affinity，
/// 漏一个就成了一扇能把整段对话截图带出去的窗（见 CaptureProtector 的注释）。
/// 铺满整窗还有个好处：底下的界面不用重画，一层半透明蒙版盖过去就行。
///
/// 蒙版是**半透明**的（用户要求），而子控件画不出真透明 —— 它只会透出父控件的纯色底，
/// 不会透出兄弟控件。所以走 <c>SettingsOverlay.CaptureBackdrop</c> 那条已验证的路子：
/// 打开时把兄弟控件逐个 <c>DrawToBitmap</c> 拼一张底图，再依次画 底图 → 蒙版 → 图片。
/// 底下的界面于是看得见、但明显压暗，图片仍然是画面的主角。
///
/// 指针形状一律不动（用户明确要求过）：能不能点靠蒙版上的提示文字说，不靠手型指针。
/// </summary>
internal sealed class ImageViewer : Control, IThemed
{
    /// <summary>图片与窗口边缘至少留出的空白，同时也是「点在图片外 = 关闭」的判定基准。</summary>
    private const int EdgePad = 28;

    private const float MaxZoom = 8f;

    /// <summary>
    /// 蒙版：压在底图上的一层深色。**要能看见底下的界面**（用户明确要求不要纯黑），
    /// 所以 alpha 取 150（约 59%）而不是原来的 226（约 89%，看着就是一块黑）。
    /// </summary>
    private static readonly Color Scrim = Color.FromArgb(150, 12, 14, 18);

    private Image? _img;
    private string _name = "";

    /// <summary>底下界面的快照。打不开时是 null，那时退回到纯色底（见 OnPaintBackground）。</summary>
    private Bitmap? _backdrop;
    private bool _backdropStale;

    /// <summary>放大倍率。&lt;= 0 表示「适应窗口」——窗口一变它就该跟着变，所以不能存成具体数值。</summary>
    private float _zoom;
    private float _panX, _panY;

    private bool _drag;
    private Point _dragFrom;
    private float _dragPanX, _dragPanY;

    public ImageViewer()
    {
        // 抓不出底图时的兜底色。**不能是纯黑**：那样一失败就又回到「周围全黑」。
        BackColor = Color.FromArgb(16, 18, 22);
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
        _backdropStale = true;    // 底图等到绘制那一刻再抓，见 OnPaintBackground
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
        // 底图是一整窗的位图，关掉就该松手 —— 一直攥着等于给主窗口常驻一份全屏大小的内存。
        _backdrop?.Dispose();
        _backdrop = null;
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
        ClampPan();               // 缩放后「适应窗口」的比例变了，旧的平移量要跟着收一收
        _backdropStale = true;    // 底图是按当时的窗口尺寸抓的，尺寸变了必须重抓
    }

    // ---------------- 底图 ----------------

    /// <summary>
    /// 抓一张**主界面快照**：半透明蒙版底下要透出真正的界面，而子控件透不出兄弟控件，
    /// 只能自己把它们画进位图再拼起来。算法与 <c>SettingsOverlay.CaptureBackdrop</c> 同一套
    /// （逐个 <c>DrawToBitmap</c>、从 z-order 后往前拼、按 Region 裁），那边踩过的两个坑这里同样适用：
    /// 不能屏幕抓图（Release 下本程序对截屏不可见，只会拿到桌面），
    /// 也不能对主窗口整体 <c>DrawToBitmap</c>（带 Region 的顶层窗口会得到黑图）。
    ///
    /// 不变量：抓不到位图**不是失败**，只是回到纯色底（<see cref="Scrim"/> 压在上面仍然能看）。
    /// 所以整段包在 try 里，宁可难看一点也不能让看图这件事整个崩掉。
    /// </summary>
    private void CaptureBackdrop()
    {
        var parent = Parent;
        _backdrop?.Dispose();
        _backdrop = null;
        if (parent == null || Width <= 0 || Height <= 0) return;
        try
        {
            var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(parent.BackColor);
                // Controls[0] 在最上层，所以从后往前拼
                for (int i = parent.Controls.Count - 1; i >= 0; i--)
                {
                    var c = parent.Controls[i];
                    if (ReferenceEquals(c, this) || !c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                    using var cb = new Bitmap(c.Width, c.Height);
                    c.DrawToBitmap(cb, new Rectangle(0, 0, c.Width, c.Height));
                    // DrawToBitmap 会忽略控件的 Region（窗口那圈描边就是靠 Region 只画最外圈的），
                    // 不裁的话整张快照会被那一圈颜色盖住。
                    var old = g.Clip;
                    if (c.Region != null)
                    {
                        using var rr = c.Region.Clone();
                        rr.Translate(c.Left, c.Top);
                        g.SetClip(rr, CombineMode.Intersect);
                    }
                    g.DrawImage(cb, c.Left, c.Top);
                    g.Clip = old;
                }
            }
            _backdrop = bmp;
        }
        catch
        {
            _backdrop?.Dispose();
            _backdrop = null;
        }
    }

    /// <summary>
    /// 只画底图 / 兜底色，蒙版和图片留给 <see cref="OnPaint"/>。
    ///
    /// （重）抓放在这里而不是 <see cref="Open"/> 里：那一刻兄弟控件的布局未必已经落定
    /// （主窗口同一帧里还可能再重排一次），到绘制这一刻才一定对。同 <c>SettingsOverlay</c> 的做法。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_backdropStale)
        {
            _backdropStale = false;
            CaptureBackdrop();
        }

        var rc = new Rectangle(0, 0, Width, Height);
        if (_backdrop != null)
        {
            e.Graphics.DrawImage(_backdrop, rc, rc, GraphicsUnit.Pixel);
            return;
        }
        // 底图抓不到也必须画点东西：浮层铺满整窗，什么都不画就是「从未绘制」的黑块。
        using var b = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(b, rc);
    }

    // ---------------- 绘制 ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // 半透明蒙版压在底图上：底下的界面看得见、但明显变暗，中间那张图才是主角。
        using (var scrim = new SolidBrush(Scrim))
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
