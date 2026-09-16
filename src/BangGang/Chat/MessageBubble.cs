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

    /// <summary>
    /// 气泡自己长高或变矮了（点开 / 收起「思考过程」）。
    ///
    /// 必须报出去：气泡的尺寸是它自己算的，而位置是 <see cref="ChatView.LayoutRows"/> 排的，
    /// 下面那些气泡不会自己让位 —— 不收这一声，展开之后就会压着下一条消息。
    /// </summary>
    public event Action? HeightChanged;

    private const int InnerCap = 560; // 气泡文本最大内宽
    private const int PadX = 14, PadY = 11;
    private const int ImgRadius = 8;  // 气泡里图片的圆角

    // ---- 思考过程那块（只有助手消息、且模型真的吐了 reasoning_content 时才有） ----
    private const int ReasonHeadH = 20;   // 「思考过程」那一行的行高，也是点击热区的高度
    private const int ReasonPadY = 9;     // 思考正文框的上下内边距
    private const int ReasonRuleW = 3;    // 正文左边那道竖线（连间距）
    private const int ReasonPadX = 10;

    // ---- 截断说明（回复被长度上限截断时贴在正文下面那句） ----
    private const int WarnPad = 9;
    private const int WarnGap = 10;       // 与上方正文之间的间距

    /// <summary>图片槽位：缓存好的位图 + 它在气泡里的矩形 + 来源附件（点击放大要顺着它找回原图）。</summary>
    private sealed class Slot
    {
        public required Attachment Src;
        public required Image Img;
        public Rectangle Rect;
    }

    private readonly List<Slot> _imgs = new();
    private readonly List<(string Name, float W)> _files = new();

    /// <summary>文件条的矩形，和图片一样在 <see cref="PlaceAttachments"/> 里一次算好。</summary>
    private readonly List<Rectangle> _fileRects = new();

    private Markdown.Layout? _md;

    /// <summary>思考块占的总高（含它与下方内容的间距）；0 = 这条消息没有思考块。</summary>
    private float _reasonH;
    private float _reasonBodyH;
    private Rectangle _reasonHeader;

    /// <summary>思考正文展开着没有。用户自己点过之后就不再自动收放，见 <see cref="_reasonTouched"/>。</summary>
    private bool _reasonOpen;

    /// <summary>用户手动点过表头。点过之后流式那一套自动收放就靠边站 —— 他刚说想看着。</summary>
    private bool _reasonTouched;

    private bool _reasonHover;

    /// <summary>截断说明那块的高度（含与上方正文的间距）；0 = 这条消息没有说明。</summary>
    private float _warnH;

    /// <summary>指针停在上面那张图的序号，-1 表示没停在任何一张上。</summary>
    private int _hover = -1;

    public MessageBubble(ChatMessage msg, bool isUser)
    {
        Msg = msg;
        IsUser = isUser;
        BackColor = Theme.ChatBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        // 从历史里读回来的消息一律**收起**：那是几天前的一轮思考，用户现在要读的是回答。
        // 流式那一轮的 assistant 消息正文此刻还是空的，于是从展开开始长（见 Rebuild）。
        _reasonOpen = (msg.Text ?? "").Length == 0;
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
        string reason = Msg.Reasoning ?? "";
        string warn = Msg.Warning ?? "";

        // 一轮回复里，「思考」在前、「正文」在后。正文一开始冒出来就把它收起来：
        // 不然一屏思考把回答顶到看不见的地方，用户还得先滚过去。
        // 用户自己点开过就不再动它 —— 那一刻起这块归他管。
        if (!IsUser && reason.Length > 0 && !_reasonTouched) _reasonOpen = text.Length == 0;

        _md = Markdown.Measure(text, InnerCap);
        MeasureReason(reason);

        float attachH = AttachmentHeight();
        float headH = IsUser ? 0 : 18;  // 助手消息顶部显示“帮帮”
        _warnH = warn.Length > 0 ? WarnBoxH(warn) + WarnGap : 0;

        float contentW = Math.Max(60f, _md.Width);
        foreach (var s in _imgs) contentW = Math.Max(contentW, s.Img.Width);
        foreach (var f in _files) contentW = Math.Max(contentW, f.W);
        // 思考块固定占满整个内宽：它是「模型在干什么」的一条旁白，
        // 跟着正文一起忽宽忽窄，流式的时候整条气泡会一直跳。
        if (_reasonH > 0) contentW = Math.Max(contentW, InnerCap);
        if (_warnH > 0) contentW = Math.Max(contentW, InnerCap);

        Width = (int)Math.Min(InnerCap + PadX * 2, contentW + PadX * 2);
        Height = (int)(PadY + headH + _reasonH + attachH + _md.Height + _warnH + PadY);
        if (text.Length == 0 && reason.Length == 0 && _imgs.Count == 0 && _files.Count == 0) Height = 28;

        PlaceAttachments();
        // 重排后悬浮下标可能指到了另一张图上（甚至指到了不存在的下标）
        if (_hover >= _imgs.Count) { _hover = -1; }
    }

    /// <summary>
    /// 内容区的起点：跳过内边距、助手名字和思考块。绘制、量尺寸、命中测试都走它，
    /// 别各自把「思考块有多高」再算一遍 —— 三处各算一遍的那种错法，
    /// 表现是正文和附件重叠一点点，谁也不会一眼看出来。
    /// </summary>
    private float BodyTop() => PadY + (IsUser ? 0 : 18) + _reasonH;

    /// <summary>思考正文的可用宽度。</summary>
    private static int ReasonTextW() => InnerCap - ReasonPadX * 2 - ReasonRuleW;

    /// <summary>量一次思考块（收起时只量表头那一行，正文一个字都不排）。</summary>
    private void MeasureReason(string reason)
    {
        _reasonH = 0;
        _reasonBodyH = 0;
        _reasonHeader = Rectangle.Empty;
        if (IsUser || reason.Length == 0) return;

        float top = PadY + 18;                 // 表头永远紧跟在「帮帮」下面
        _reasonHeader = new Rectangle(PadX, (int)Math.Round(top), InnerCap, ReasonHeadH);
        _reasonH = ReasonHeadH + 10;           // 收起：只有表头 + 与下方内容的间距
        if (!_reasonOpen) return;

        using var f = Theme.UI(10.5f);
        var sz = TextRenderer.MeasureText(reason, f, new Size(ReasonTextW(), int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        _reasonBodyH = sz.Height + ReasonPadY * 2;
        _reasonH = ReasonHeadH + 6 + _reasonBodyH + 10;
    }

    /// <summary>截断说明那块盒子有多高（文字按内宽折行量出来）。</summary>
    private static float WarnBoxH(string warn)
    {
        using var f = Theme.UI(10.5f);
        var sz = TextRenderer.MeasureText(warn, f, new Size(InnerCap - WarnPad * 2, int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return sz.Height + WarnPad * 2;
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

    /// <summary>
    /// 把每张图、每条文件在气泡里的矩形算好。绘制、命中测试都读这里，不各算一遍。
    ///
    /// 这里量的**起点是 <see cref="BodyTop"/>**（原来写死成「内边距 + 助手名字」，
    /// 多出思考块之后附件会压在上面）。
    /// </summary>
    private void PlaceAttachments()
    {
        float y = BodyTop();
        foreach (var s in _imgs)
        {
            s.Rect = new Rectangle(PadX, (int)Math.Round(y), s.Img.Width, s.Img.Height);
            y += s.Img.Height + 6;
        }
        _fileRects.Clear();
        foreach (var (_, w) in _files)
        {
            _fileRects.Add(new Rectangle(PadX, (int)Math.Round(y + 2), (int)w, 24));
            y += 34;
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

    private bool ReasonHeadAt(Point p) => _reasonH > 0 && _reasonHeader.Contains(p);

    /// <summary>点表头 = 展开 / 收起那一块。整行都是热区，箭头本身太小不好点。</summary>
    private void ToggleReason()
    {
        _reasonOpen = !_reasonOpen;
        _reasonTouched = true;    // 从此不再跟着「正文开始了没有」自动收放
        Rebuild();
        Invalidate();
        HeightChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool overHead = ReasonHeadAt(e.Location);
        if (overHead != _reasonHover)
        {
            // 指针形状不改（用户要求），所以「这一行能点」只能靠悬浮时文字变个颜色来暗示
            _reasonHover = overHead;
            Invalidate();
        }

        // 指针形状不改（用户要求），所以「这里能点」只能靠悬浮时把图压暗一点点来暗示
        int i = ImgAt(e.Location);
        if (i == _hover) return;
        _hover = i;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        bool dirty = _hover >= 0 || _reasonHover;
        _hover = -1;
        _reasonHover = false;
        if (dirty) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (ReasonHeadAt(e.Location)) { ToggleReason(); return; }
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
            DrawReason(g, y + 18);
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
        }
        for (int i = 0; i < _files.Count; i++)
        {
            var (name, _) = _files[i];
            var r = _fileRects[i];
            using (var path = RoundedRect(r.X, r.Y, r.Width, 24, 6))
            using (var bb = new SolidBrush(Blend(bg, Color.White, .6f)))
            {
                g.FillPath(bb, path);
            }
            using (var linePen = new Pen(Color.FromArgb(150, 150, 150), 1.4f))
            {
                float mid = r.Y + 12;
                g.DrawLine(linePen, r.X + 7, mid - 4, r.X + 7, mid + 4);
                g.DrawLine(linePen, r.X + 4, mid, r.X + 10, mid);
            }
            g.DrawString(name, Theme.UI(10.5f), Brushes.Gray, r.X + 15, r.Y + 3);
        }

        // 正文的起点**问布局要**，不要在这里把刚才那几段高度再加一遍：
        // 加漏一段（比如思考块）就会被正文盖住，而且只错几个像素，很难看出来。
        y = BodyTop() + AttachmentHeight();
        if (_md != null) y = Markdown.Draw(g, _md, x, y, InnerCap);
        if (_warnH > 0) DrawWarning(g, y + WarnGap);

        base.OnPaint(e);
    }

    /// <summary>
    /// 画「思考过程」那块：一行表头（箭头 + 文字，整行可点）+ 展开时的正文框。
    ///
    /// 正文用 <see cref="TextRenderer"/> 量、也用 <see cref="TextRenderer"/> 画，
    /// 两边同一套 flag —— 用 GDI+ 量、GDI 画（或者反过来）会差出一两行，
    /// 盒子高度是对的、字却溢出去了。
    /// </summary>
    private void DrawReason(Graphics g, float top)
    {
        if (_reasonH <= 0 || IsUser) return;
        string reason = Msg.Reasoning ?? "";
        if (reason.Length == 0) return;

        var head = _reasonHeader;
        Color ink = _reasonHover ? Theme.Accent : Theme.TextMuted;

        // 箭头：展开时朝下、收起时朝右。用多边形而不是字符 —— 字符在不同字体下
        // 垂直居中的位置不一样，表头会看着忽高忽低。
        float ax = head.X + 3, ay = head.Y + ReasonHeadH / 2f;
        var tri = _reasonOpen
            ? new[] { new PointF(ax, ay - 2.5f), new PointF(ax + 7, ay - 2.5f), new PointF(ax + 3.5f, ay + 2.5f) }
            : new[] { new PointF(ax, ay - 3.5f), new PointF(ax + 5, ay), new PointF(ax, ay + 3.5f) };
        using (var tb = new SolidBrush(ink)) g.FillPolygon(tb, tri);

        bool answering = (Msg.Text ?? "").Length > 0;
        string label = answering
            ? "思考过程" + (Msg.ReasoningMs > 0 ? " · " + Secs(Msg.ReasoningMs) : "")
            : "正在思考…";
        using (var hf = Theme.UI(10f, FontStyle.Bold))
        using (var hb = new SolidBrush(ink))
            g.DrawString(label, hf, hb, head.X + 15, head.Y + 2);

        if (!_reasonOpen) return;

        float boxY = head.Y + ReasonHeadH + 6;
        // 底色取**气泡自己的填充色**（Theme.AsstBubble）而不是控件的 BackColor：
        // 这块画在气泡里面，混错基准就会在气泡上留一块异色的补丁。
        Color bubble = Theme.AsstBubble;
        using (var box = RoundedRect(PadX, boxY, InnerCap - 1, _reasonBodyH - 1, 8))
        using (var b = new SolidBrush(Theme.Mix(bubble, Theme.TextMuted, 0.09f)))
            g.FillPath(b, box);

        // 左边一道竖线：这段是「旁白」，不是回答本身。两行以上时全靠它区分。
        using (var rule = new SolidBrush(Theme.Mix(bubble, Theme.Accent, 0.45f)))
            g.FillRectangle(rule, PadX + ReasonPadX, boxY + ReasonPadY - 2, ReasonRuleW - 1, _reasonBodyH - ReasonPadY * 2 + 4);

        using var f = Theme.UI(10.5f);
        TextRenderer.DrawText(g, Msg.Reasoning ?? "", f,
            new Rectangle((int)(PadX + ReasonPadX + ReasonRuleW), (int)(boxY + ReasonPadY),
                          ReasonTextW(), (int)(_reasonBodyH - ReasonPadY * 2)),
            Theme.TextMuted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// 画「回复被截断」那句。放在正文**下面**而不是塞进正文里：它不是模型说的话，
    /// 混进正文还会被下一轮当上下文发回给模型。
    /// </summary>
    private void DrawWarning(Graphics g, float top)
    {
        string warn = Msg.Warning ?? "";
        if (warn.Length == 0) return;

        float h = WarnBoxH(warn);
        Color bubble = IsUser ? Theme.UserBubble : Theme.AsstBubble;
        using (var box = RoundedRect(PadX, top, InnerCap - 1, h - 1, 8))
        using (var b = new SolidBrush(Theme.Mix(bubble, WarnInk, 0.14f)))
            g.FillPath(b, box);
        using (var pen = new Pen(Theme.Mix(bubble, WarnInk, 0.5f), 1f))
        using (var box2 = RoundedRect(PadX, top, InnerCap - 1, h - 1, 8))
            g.DrawPath(pen, box2);

        using var f = Theme.UI(10.5f);
        TextRenderer.DrawText(g, warn, f,
            new Rectangle(PadX + WarnPad, (int)(top + WarnPad), InnerCap - WarnPad * 2, (int)(h - WarnPad * 2)),
            WarnInk, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    /// <summary>琥珀色，深浅两套主题各一个 —— 亮色下的深琥珀放到暗底上会糊成一团。</summary>
    private static Color WarnInk => Theme.Dark ? Color.FromArgb(232, 168, 82) : Color.FromArgb(176, 106, 12);

    private static string Secs(int ms) => (ms / 1000.0).ToString("0.0") + "s";

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
