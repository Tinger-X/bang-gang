using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace BangGang;

/// <summary>一条消息的气泡控件（自绘）。</summary>
internal sealed class MessageBubble : Control
{
    public ChatMessage Msg { get; }
    public bool IsUser { get; }

    /// <summary>点到了其中一张图片。参数是那张图的附件（路径留给放大浮层自己去读原图）。</summary>
    public event Action<Attachment>? ImagePressed;

    private const int InnerCap = 560; // 气泡文本最大内宽
    private const int PadX = 14, PadY = 11;
    private const int ImgRadius = 8;  // 气泡里图片的圆角

    /// <summary>图片槽位：缓存好的位图 + 它在气泡里的矩形 + 来源附件（点击放大要顺着它找回原图）。</summary>
    private sealed class Slot
    {
        public required Attachment Src;
        public required Image Img;
        public Rectangle Rect;
    }

    private readonly List<Slot> _imgs = new();
    private readonly List<(string Name, float W)> _files = new();
    private Markdown.Layout? _md;

    /// <summary>指针停在上面那张图的序号，-1 表示没停在任何一张上。</summary>
    private int _hover = -1;

    public MessageBubble(ChatMessage msg, bool isUser)
    {
        Msg = msg;
        IsUser = isUser;
        BackColor = Theme.ChatBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BuildAttachments();
        Rebuild();
    }

    /// <summary>
    /// 正文变化后重排一次（流式回复用）。
    ///
    /// 特意与附件分开：附件是从磁盘读图 + 缩放出来的，每收一小段就重读一次磁盘上的图，
    /// 一条回复下来能把同一个文件读上百遍。附件在一条消息的生命周期里不会变，读一次就够。
    /// </summary>
    public void RefreshText()
    {
        Rebuild();
        Invalidate();
    }

    /// <summary>依据消息重建内部布局并计算控件尺寸。</summary>
    private void Rebuild()
    {
        // 正文取一次存成局部变量，后面都读它。
        //
        // 两个理由。一是**确实可能是 null**：消息是从磁盘上的 JSON 反序列化回来的，
        // System.Text.Json 会把 `"Text": null` 直接塞进这个声明为不可空的属性里，
        // 声明的非空拦不住它（上面 Attachments 那一手也是同一个原因）。
        // 二是编译器认这个理：`Msg.Text ?? ""` 会让它把 `Msg.Text` 的流状态记成
        // 「可能为空」，之后再解引用同一个属性就报 CS8602 —— 同一件事在方法里
        // 说两遍，两遍的说法还不一样。存成局部变量，「可能为空」只在取名那一行出现。
        string text = Msg.Text ?? "";

        _md = Markdown.Measure(text, InnerCap);

        float attachH = AttachmentHeight();
        float headH = IsUser ? 0 : 18;  // 助手消息顶部显示“帮帮”

        float contentW = Math.Max(60f, _md.Width);
        foreach (var s in _imgs) contentW = Math.Max(contentW, s.Img.Width);
        foreach (var f in _files) contentW = Math.Max(contentW, f.W);
        Width = (int)Math.Min(InnerCap + PadX * 2, contentW + PadX * 2);
        Height = (int)(PadY + headH + attachH + _md.Height + PadY);
        if (text.Length == 0 && _imgs.Count == 0 && _files.Count == 0) Height = 28;

        PlaceAttachments();
        // 重排后悬浮下标可能指到了另一张图上（甚至指到了不存在的下标）
        if (_hover >= _imgs.Count) { _hover = -1; }
    }

    /// <summary>附件区（图片 + 文件条）占的高度。绘制与量尺寸两处必须问同一个函数。</summary>
    private float AttachmentHeight()
    {
        float h = 0;
        foreach (var s in _imgs) h += s.Img.Height + 6;
        if (_imgs.Count > 0) h -= 6;
        foreach (var _ in _files) h += 34;
        if (_files.Count > 0 && _imgs.Count > 0) h += 4;
        return h;
    }

    /// <summary>把每张图片在气泡里的矩形算好。绘制、命中测试都读这里，不各算一遍。</summary>
    private void PlaceAttachments()
    {
        float y = PadY + (IsUser ? 0 : 18);
        foreach (var s in _imgs)
        {
            s.Rect = new Rectangle(PadX, (int)Math.Round(y), s.Img.Width, s.Img.Height);
            y += s.Img.Height + 6;
        }
    }

    private void BuildAttachments()
    {
        _imgs.Clear();
        _files.Clear();

        if (Msg.Attachments == null) return;
        foreach (var a in Msg.Attachments)
        {
            if (a.Kind == "image")
            {
                var img = a.LoadImage(280, 190);
                if (img != null) _imgs.Add(new Slot { Src = a, Img = RoundImage(img) });
            }
            else
            {
                string n = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileName(a.Path ?? "") : a.Name;
                if (n.Length == 0) n = "(文件)";
                if (n.Length > 30) n = n[..30] + "…";
                float w = TextRenderer.MeasureText(n, Theme.UI(10.5f)).Width + 44;
                _files.Add((n, Math.Min(w, InnerCap)));
            }
        }
    }

    /// <summary>
    /// 给图片切出**抗锯齿**的圆角。
    ///
    /// 不能走 <c>SetClip(圆角路径)</c>：GDI+ 的裁剪区是逐像素硬掩码，<c>SmoothingMode</c>
    /// 对它不生效，边缘会是一格一格的台阶（用户报过的「圆角有锯齿」）。
    /// 抗锯齿只发生在画几何图形那条路上，所以先把原图裁成方形的**不透明**位图，
    /// 再用 <see cref="TextureBrush"/> 去 FillPath 一条圆角路径：边缘像素于是拿到部分覆盖度，
    /// alpha 是渐变的，压在气泡底色上就是平滑的圆角。同 <c>DraftStrip.MakeThumb</c>。
    /// </summary>
    private static Image RoundImage(Image src)
    {
        if (src.Width < ImgRadius * 2 + 2 || src.Height < ImgRadius * 2 + 2) return src;

        using var flat = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var gf = Graphics.FromImage(flat))
        {
            gf.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gf.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
        }
        src.Dispose();

        var bmp = new Bitmap(flat.Width, flat.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 贴图时也要 HighQuality：默认的 PixelOffsetMode 会把整块采样到半个像素上，
            // 连刚磨好的圆角一起糊掉
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var path = RoundedRect(0, 0, flat.Width - 1, flat.Height - 1, ImgRadius);
            using var brush = new TextureBrush(flat, WrapMode.Clamp);
            g.FillPath(brush, path);
        }
        return bmp;
    }

    private void DisposeImgs()
    {
        foreach (var s in _imgs) s.Img.Dispose();
        _imgs.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeImgs();
        base.Dispose(disposing);
    }

    // ---------------- 点击放大 ----------------

    /// <summary>命中的图片序号（没有则 -1）。</summary>
    private int ImgAt(Point p)
    {
        for (int i = 0; i < _imgs.Count; i++)
            if (_imgs[i].Rect.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // 指针形状不改（用户要求），所以「这里能点」只能靠悬浮时把图压暗一点点来暗示
        int i = ImgAt(e.Location);
        if (i == _hover) return;
        _hover = i;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int i = ImgAt(e.Location);
        if (i >= 0) ImagePressed?.Invoke(_imgs[i].Src);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        Color bg = IsUser ? Theme.UserBubble : Theme.AsstBubble;
        using (var path = RoundedRect(0, 0, Width - 1, Height - 1, 12))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);
        if (!IsUser)
        {
            using var borderPen = new Pen(Color.FromArgb(150, 226, 230, 236), 1f);
            using var path2 = RoundedRect(0, 0, Width - 1, Height - 1, 12);
            g.DrawPath(borderPen, path2);
        }

        float x = PadX;
        float y = PadY;
        if (!IsUser)
        {
            using (var hf = Theme.UI(10f, FontStyle.Bold))
                g.DrawString("帮帮", hf, new SolidBrush(Theme.Accent), x + 2, y);
            y += 18;
        }
        for (int i = 0; i < _imgs.Count; i++)
        {
            var s = _imgs[i];
            g.DrawImage(s.Img, s.Rect);
            if (i == _hover)
            {
                // 悬浮提示：图变暗一点点。不用指针形状（用户要求），也不能什么都不给 ——
                // 「这张图能点开」在界面上否则无处可寻。
                using var path = RoundedRect(s.Rect.X, s.Rect.Y, s.Rect.Width - 1, s.Rect.Height - 1, ImgRadius);
                using var hl = new SolidBrush(Color.FromArgb(38, 0, 0, 0));
                g.FillPath(hl, path);
            }
            y += s.Img.Height + 6;
        }
        foreach (var (name, w) in _files)
        {
            float h = 24;
            using (var path = RoundedRect(x, y + 2, w, h, 6))
            using (var bb = new SolidBrush(IsUser ? Blend(bg, Color.White, .6f) : Blend(bg, Color.White, .6f)))
            {
                g.FillPath(bb, path);
            }
            using (var linePen = new Pen(Color.FromArgb(150, 150, 150), 1.4f))
            {
                float mid = y + 2 + h / 2;
                g.DrawLine(linePen, x + 7, mid - 4, x + 7, mid + 4);
                g.DrawLine(linePen, x + 4, mid, x + 10, mid);
            }
            g.DrawString(name, Theme.UI(10.5f), Brushes.Gray, x + 15, y + 5);
            y += 34;
        }
        if (_files.Count > 0) y += 4;

        if (_md != null) Markdown.Draw(g, _md, x, y, InnerCap);

        base.OnPaint(e);
    }

    private static Color Blend(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R * k + b.R * (1 - k)), (int)(a.G * k + b.G * (1 - k)), (int)(a.B * k + b.B * (1 - k)));

    internal static GraphicsPath RoundedRect(float x, float y, float w, float h, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
