using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BangGang;

/// <summary>
/// 输入卡片里的附件列表区：一行小卡片。图片是一块 44×44 的方图块（只有图，不写文件名 ——
/// 缩略图比名字好认），别的文件是「类别色块 + 扩展名 + 文件名 + PDF · 625KB」。
/// 鼠标悬浮在哪一张上，就在**那一张的右上角**浮出一个删除按钮。
///
/// 为什么是一整块自绘的条，而不是「FlowLayoutPanel + 每张一个控件」：
///
/// 1. 「右上角」意味着删除按钮有一半画在卡片**外面**。控件画不出自己的矩形之外（Windows 会裁），
///    要让每张卡片带着这个圆钮，就得把控件做得比卡片大一圈，那条多出来的边距再反过来
///    参与 FlowLayoutPanel 的排版 —— 卡片之间的视觉间距于是时大时小，调不出整齐的一行。
///    整条自己画，谁在哪、按钮压在哪，都是一次算出来的。
/// 2. 悬浮是**整条**的状态（同一时刻只有一张卡片有按钮），不是每张卡片各自的状态。
///    分散到子控件里，鼠标从卡片移到按钮上时会先经过「离开卡片」那一瞬，
///    按钮于是闪一下 —— 命中测试放在一处就不会。
///
/// 卡片宽度会随张数收缩：一行放不下时先把每张压窄，压到 <see cref="ChipMinW"/> 为止，
/// 再多的就不画了，在右端给一个「+N」的计数 —— 悄悄丢掉用户加的附件是最坏的做法。
/// </summary>
internal sealed class DraftStrip : Control
{
    // ---------- 几何。动任何一个都要重跑 tools/draft-strip.ps1 看一眼 ----------

    /// <summary>本区域自身的高度 = 卡片 + 上边给删除按钮让出的那半个。<c>InputPanel</c> 按它让位。</summary>
    public const int RowH = TopPad + ChipH;

    /// <summary>
    /// 卡片高度 = 图片格子的边长。**这一族几何常量只有这一处定义**：对话区气泡里的附件
    /// （<see cref="MessageBubble"/>）画的是同一套卡片，它按这里的数排版，不自己再存一份 ——
    /// 存两份的下场是改了一处、另一处静默地差几像素。
    /// </summary>
    internal const int ChipH = 44;

    /// <summary>图片卡片的宽度 = 它自己的高度：一个正方形格子，整格就是那张图。</summary>
    internal const int ChipImgW = ChipH;

    internal const int ChipGap = 8;

    /// <summary>文件卡片的自然宽度。文字区 = 118 − 7 − 28 − 7 − 8 = 68px，够放「notes.txt」。</summary>
    internal const int ChipNatW = 118;

    /// <summary>挤到这个宽度就不再缩，再多就交给「+N」（此时文字已经放不下，只剩图标）。</summary>
    private const int ChipMinW = 66;

    internal const int IconSize = 28;
    internal const int IconPad = 7;      // 图标离卡片左缘
    internal const int TextGap = 7;      // 图标与文字之间
    internal const int TextRight = 8;    // 文字离卡片右缘

    /// <summary>
    /// 卡片离条带上缘的距离 = 半个删除按钮 + 2px 余量。**删除按钮有一半探在卡片上面**
    /// （圆心正好压在卡片的右上角上），所以卡片必须往下让开半个按钮，否则它会被条带的上边缘裁掉一半。
    /// 它和 <see cref="ChipH"/> 一起凑成 <see cref="RowH"/>，改一个就得连着核另一个。
    /// </summary>
    private const int TopPad = 10;

    /// <summary>同上，最后一张卡片右边让开半个按钮。</summary>
    private const int RightPad = 10;

    private const int XSize = 17;        // 删除按钮的直径

    /// <summary>图片缩略图四角的半径。图片格是 44px 的方块，8 和别的卡片是同一档。</summary>
    internal const int ThumbRadius = 8;

    private readonly List<Attachment> _items = new();
    private readonly List<Image?> _thumbs = new();
    private int _hover = -1;             // 悬浮的是第几张卡片，-1 = 没有
    private bool _overX;                 // 悬浮在删除按钮上（按钮要画得更实）
    private int _fit;                    // 这一帧实际画得下的张数

    /// <summary>用户点掉了某一张的删除按钮。</summary>
    public event Action<Attachment>? RemoveClicked;

    public DraftStrip()
    {
        // 自绘控件一律开 ResizeRedraw：窗口缩放后不重画就会留着上一个尺寸的卡片。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.InputBg;
    }

    /// <summary>换一整批附件。缩略图在这里加载一次，之后重画不再碰磁盘。</summary>
    public void SetItems(IReadOnlyList<Attachment> items)
    {
        DisposeThumbs();
        _items.Clear();
        _items.AddRange(items);
        _hover = -1;
        _overX = false;
        foreach (var a in _items)
            _thumbs.Add(a.Kind == "image" ? MakeThumb(a) : null);
        Invalidate();
    }

    public void RefreshTheme()
    {
        BackColor = Theme.InputBg;
        Invalidate();
    }

    private void DisposeThumbs()
    {
        foreach (var t in _thumbs) t?.Dispose();
        _thumbs.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeThumbs();
        base.Dispose(disposing);
    }

    // ---------------- 几何 ----------------

    /// <summary>
    /// 一张卡片有多宽。图片是正方形的图块，文件按文字量取自然宽度 —— 所以**宽度是按张算的**，
    /// 不能像以前那样整行共用一个数。
    ///
    /// 「有没有缩略图」既是这里认图片的依据，也是 <see cref="PaintChip"/> 画图的依据：
    /// 两处必须问同一个问题，否则图没读出来时（<c>LoadImage</c> 返回 null）会按图块的窄宽度
    /// 去排文件名，文字整段被切掉。
    /// </summary>
    private int ChipWOf(int i) => IsImg(i) ? ChipImgW : FileW();

    private bool IsImg(int i) => i < _thumbs.Count && _thumbs[i] != null;

    /// <summary>文件卡片的宽度：一行放得下就保持自然宽度，放不下就一起压窄。</summary>
    private int FileW()
    {
        int files = 0, imgs = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (IsImg(i)) imgs++; else files++;
        }
        if (files == 0) return ChipNatW;

        int left = Width - RightPad - imgs * ChipImgW - Math.Max(0, _items.Count - 1) * ChipGap;
        return Math.Clamp(left / files, ChipMinW, ChipNatW);
    }

    /// <summary>第 i 张卡片的左边缘：前面每一张的宽度加上间距，累出来。</summary>
    private int ChipX(int i)
    {
        int x = 0;
        for (int k = 0; k < i; k++) x += ChipWOf(k) + ChipGap;
        return x;
    }

    /// <summary>实际画得下的张数。挤到 <see cref="ChipMinW"/> 还放不下的，交给「+N」。</summary>
    private int FitCount()
    {
        int avail = Width - RightPad;
        int x = 0, n = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            int w = ChipWOf(i);
            if (n > 0 && x + w > avail) break;
            x += w + ChipGap;
            n++;
        }
        return Math.Max(1, n);
    }

    /// <summary>第 i 张卡片的矩形。删除按钮的圆心就压在它的右上角上。</summary>
    private Rectangle ChipRect(int i)
    {
        int w = ChipWOf(i);
        return new Rectangle(ChipX(i), TopPad, w, ChipH);
    }

    /// <summary>第 i 张卡片那个删除按钮的矩形：圆心 = 卡片的右上角。</summary>
    private static Rectangle XRectOf(Rectangle chip) =>
        new(chip.Right - XSize / 2, chip.Top - XSize / 2, XSize, XSize);

    /// <summary>当前这一帧按钮画在哪 —— 画与命中必须问同一个方法，否则会「看得见点不着」。</summary>
    private Rectangle XRect() => _hover < 0 || _hover >= _fit ? Rectangle.Empty : XRectOf(ChipRect(_hover));

    /// <summary>一次鼠标移动落在哪张卡片上：卡片本身、或它右上角那个按钮，都算这一张。</summary>
    private void HitTest(Point p, out int index, out bool overX)
    {
        index = -1;
        overX = false;
        for (int i = 0; i < _items.Count; i++)
        {
            var c = ChipRect(i);
            if (i < _fit && XRectOf(c).Contains(p)) { index = i; overX = true; return; }
            if (c.Contains(p)) { index = i; return; }
        }
    }

    // ---------------- 鼠标 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HitTest(e.Location, out int idx, out bool overX);
        if (idx == _hover && overX == _overX) return;
        _hover = idx;
        _overX = overX;
        // 整条一共 56px 高，重画一次比算「哪几个矩形脏了」便宜，也更不容易漏。
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        _overX = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _hover < 0 || _hover >= _fit) return;
        if (!XRectOf(ChipRect(_hover)).Contains(e.Location)) return;
        RemoveClicked?.Invoke(_items[_hover]);
    }

    // ---------------- 绘制 ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);

        _fit = FitCount();
        for (int i = 0; i < _fit; i++) PaintChip(g, i);

        // 「还有几张没画出来」。只报数不做交互：卡片挤到这个份上，用户该做的是先发出去或删几张。
        if (_fit < _items.Count)
        {
            var tail = new Rectangle(ChipX(_fit), TopPad, Math.Max(28, ChipNatW / 3), ChipH);
            if (tail.Right > Width) tail.Width = Math.Max(0, Width - tail.X);
            if (tail.Width > 20)
            {
                RP.Fill(g, tail, 8, Theme.Mix(Theme.InputBg, Theme.TextMuted, 0.12f));
                string n = "+" + (_items.Count - _fit);
                TextRenderer.DrawText(g, n, SF.Get(9.5f), tail, Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix);
            }
        }

        PaintRemove(g);
    }

    /// <summary>
    /// 文件卡片的底色。跟着主题走，而且**必须和它身下的输入卡片拉开距离**。
    ///
    /// 这张卡片画在输入卡片上（= <see cref="Theme.InputBg"/>），所以「看得见」取决于
    /// 卡片色与 <c>InputBg</c> 的差，不是它与 <c>ChatBg</c> 的差。亮色下 <c>AsstBubble</c>
    /// 正好够（12 级）；暗色下它是 (40,44,50)、<c>InputBg</c> 是 (43,47,54) —— 只差 3 级，
    /// 卡片等于没画，一行附件看上去就是一圈图标浮在输入框上。所以暗色下另取一个
    /// 朝 <see cref="Theme.TextMain"/> 走的混色（≈(60,64,71)，差 17 级），
    /// 与亮色下的对比度相当。
    /// </summary>
    private static Color ChipFill() =>
        Theme.Dark ? Theme.Mix(Theme.InputBg, Theme.TextMain, 0.09f) : Theme.AsstBubble;

    private void PaintChip(Graphics g, int i)
    {
        var a = _items[i];
        var c = ChipRect(i);

        // 图片：整张卡片就是那张图，不写名字也不写大小。缩略图本身比文件名好认 ——
        // 「pic.png」和「pic-2.png」看不出区别，两张缩略图一眼就分得开。
        var thumb = i < _thumbs.Count ? _thumbs[i] : null;
        if (thumb != null)
        {
            // 1:1 贴上去。PixelOffsetMode 必须校正：不校正的话整块图会被采样到半个像素上，
            // 四角的抗锯齿连同一整张图一起糊掉，看着就像「磨圆了但很脏」。
            var off = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(thumb, c);
            g.PixelOffsetMode = off;
            return;
        }

        RP.Box(g, c, 8, ChipFill(), BackColor);
        var icon = new Rectangle(c.Left + IconPad, c.Top + (ChipH - IconSize) / 2, IconSize, IconSize);
        PaintFileIcon(g, a, icon, BackColor);

        // 文字区：右边留出删除按钮探进来的那一小块，名字才不会顶到圆钮上。
        int tx = icon.Right + TextGap;
        int tw = c.Right - TextRight - tx;
        if (tw < 20) return;

        string name = Ellipsize(DisplayName(a), SF.Get(9.5f), tw);
        TextRenderer.DrawText(g, name, SF.Get(9.5f), new Rectangle(tx, c.Top + 6, tw, 18),
            Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        string meta = Ellipsize(AttachTypes.MetaOf(a), SF.Get(8f), tw);
        if (meta.Length > 0)
            TextRenderer.DrawText(g, meta, SF.Get(8f), new Rectangle(tx, c.Top + 23, tw, 16),
                Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>卡片上显示的文件名，空的时候给个占位。</summary>
    internal static string DisplayName(Attachment a)
    {
        string n = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileName(a.Path ?? "") : a.Name;
        return n.Length == 0 ? "(文件)" : n;
    }

    /// <summary>量文字用的 flag。**必须和画的 flag 一致**，否则量出来放得下、画出来还是被切。</summary>
    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    /// <summary>
    /// 按像素宽度截断并补一个「…」。
    ///
    /// 不要改用 <c>EndEllipsis</c> 单独兜底：它要等到真正 <c>DrawText</c> 才知道放不放得下，
    /// 那之前整串字已经排过一遍版了。也不要按「每个字平均多宽」去估 —— 中英文混排时
    /// 那个平均值只能按最宽的字（汉字）取，于是纯 ASCII 的名字（widget.cpp）会被砍掉一截，
    /// 明明右边还有大片空白。二分一次量得准，代价是每个卡片多几次 MeasureText，
    /// 而卡片只在悬浮变化时才重画。
    /// </summary>
    internal static string Ellipsize(string s, Font f, int avail)
    {
        if (avail <= 0 || s.Length == 0) return "";
        if (TextW(s, f) <= avail) return s;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (TextW(s[..mid] + "…", f) <= avail) lo = mid; else hi = mid - 1;
        }
        return s[..lo] + "…";
    }

    /// <summary>名字不能叫 <c>Width</c>：那是 <see cref="Control.Width"/>，会把基类的属性藏掉。</summary>
    private static int TextW(string s, Font f) =>
        TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), MeasureFlags).Width;

    /// <summary>
    /// 图片缩略图：铺满整个格子、四角磨圆、**圆角带抗锯齿**。
    ///
    /// 用「铺满」（cover）而不是「装下」（fit）：正方格子里的横图装下会在上下留两条底色边，
    /// 一块 44px 的小格子再被切掉两条边就只剩一条缝了。缩略图要回答的是「这是哪一张」，
    /// 不是「这张图长什么样」，裁掉两端比缩小更划算。所以这里取的是 Max 而不是 Min，
    /// 并把超出的部分居中裁掉 —— 顺手也就不会**拉变形**（头像变宽脸）。
    ///
    /// **圆角不能靠 SetClip 裁。** GDI+ 的裁剪区是逐像素的硬掩码，<c>SmoothingMode</c> 对它
    /// 不生效 —— 裁出来的四角是一格一格的台阶（用户看到的「圆角有锯齿」）。抗锯齿只发生在
    /// 「画几何图形」那条路上，所以这里分两步：先把原图裁成 44×44 的**不透明**方块，
    /// 再用**纹理刷**去填一条圆角路径。边缘那一圈像素于是拿到部分覆盖度，alpha 是渐变的，
    /// 压在卡片底色上就是平滑的圆角。同一个道理，<see cref="RP.Box"/> 也是先铺底色再填路径。
    ///
    /// 成品在这一步就做完了（换一次附件只做一遍），重画时只是原样贴上去；
    /// 贴的时候连 <c>PixelOffsetMode</c> 都要校正，否则整块图会被采样到半个像素上、糊一层。
    /// </summary>
    internal static Image? MakeThumb(Attachment a)
    {
        using var src = a.LoadThumb(ChipImgW * 3);
        if (src == null) return null;

        double k = Math.Max((double)ChipImgW / src.Width, (double)ChipH / src.Height);
        int w = Math.Max(ChipImgW, (int)Math.Round(src.Width * k));
        int h = Math.Max(ChipH, (int)Math.Round(src.Height * k));

        using var flat = new Bitmap(ChipImgW, ChipH, PixelFormat.Format32bppArgb);
        using (var gf = Graphics.FromImage(flat))
        {
            gf.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gf.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gf.DrawImage(src, new Rectangle((ChipImgW - w) / 2, (ChipH - h) / 2, w, h));
        }

        var bmp = new Bitmap(ChipImgW, ChipH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RP.Path(new Rectangle(0, 0, ChipImgW, ChipH), ThumbRadius);
            using var brush = new TextureBrush(flat, WrapMode.Clamp);
            g.FillPath(brush, path);
        }
        return bmp;
    }

    /// <summary>
    /// 非图片的文件图标：一块按类别着色的圆角方块 + 里面的扩展名（PDF / TXT / DOCX）。
    ///
    /// 用扩展名当图标，是因为白名单里几十种类型画不出几十个图标，而用户认的正是后缀那几个字母。
    /// 字号按字母个数收一收，四个字母（DOCX）也能塞进 32px 的方块。
    ///
    /// <paramref name="backdrop"/> 是这块图标**底下真正显示的颜色**。图标本身是半透明的混色，
    /// 底色取错就会在卡片上留一块异色的补丁 —— 输入卡片和对话气泡是两种底色，
    /// 所以这个值必须由调用方给，不能在这里写死成 <see cref="Theme.InputBg"/>。
    /// </summary>
    internal static void PaintFileIcon(Graphics g, Attachment a, Rectangle box, Color backdrop)
    {
        var cat = AttachTypes.CatOf(a.Path);
        Color tint = cat switch
        {
            AttachCat.Doc => Theme.Danger,
            AttachCat.Text => Theme.Accent,
            AttachCat.Audio => Theme.Mix(Theme.Accent, Theme.Danger, 0.5f),
            _ => Theme.TextMuted,
        };
        RP.Fill(g, box, 7, Theme.Mix(backdrop, tint, Theme.Dark ? 0.30f : 0.14f));

        string label = AttachTypes.ExtLabel(a.Path);
        if (label.Length == 0) label = AttachTypes.CatName(cat);
        if (label.Length > 4) label = label[..4];
        float size = label.Length >= 4 ? 7f : label.Length == 3 ? 8f : 8.5f;
        TextRenderer.DrawText(g, label, SF.Get(size, FontStyle.Bold), box, tint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// 悬浮时那张卡片右上角的删除按钮。压在最上层画 —— 它会盖住卡片的一个角。
    ///
    /// 四层叠出来，缺一层都会「糊」：底色光圈把它从卡片（或照片）上切下来；投影给一点厚度，
    /// 否则看着像一张贴纸；圆面**悬浮时转成危险色** —— 删除不可逆，手指搭上去就该变红；
    /// 最后才是那个 ×，内缩 5px、线宽比常规图标粗一档，22px 的圆里才不发虚。
    /// </summary>
    private void PaintRemove(Graphics g)
    {
        var xr = XRect();
        if (xr.IsEmpty) return;

        // 光圈用**条带自己的底色**画，所以它压在照片上也是「挖掉一个圆」的效果，
        // 而不是一圈白边 —— 深浅两套主题下都跟着走。2px 就够：再宽就从照片上咬掉一大口，
        // 看着像画坏了，而不是像一枚浮在上面的按钮。
        using (var halo = new SolidBrush(BackColor))
            g.FillEllipse(halo, Rectangle.Inflate(xr, 2, 2));

        // 圆面在深浅两套主题下**反过来**：亮色主题是灰蓝，暗色主题是浅灰。
        // 固定用一个色的话，暗色主题下它和卡片底色（43,47,54）只差十几级，等于一枚看不见的按钮 ——
        // 「浮起来的按钮」靠的正是它和底色的差，所以这个色必须跟着主题走。
        //
        // 灰蓝而不是近黑：这是个 17px 的小圆，压得很重时比它盖住的那张照片还抢眼，
        // 整个附件区看着就「花」。它要的是看得见、点得着，不是存在感。
        Color face = _overX ? Theme.Danger
                   : Theme.Dark ? Color.FromArgb(255, 214, 220, 228)
                                : Color.FromArgb(255, 112, 119, 132);
        Color ink = (_overX || !Theme.Dark) ? Color.White : Color.FromArgb(255, 32, 36, 42);

        using (var sh = new SolidBrush(Color.FromArgb(Theme.Dark ? 14 : 20, 0, 0, 0)))
            g.FillEllipse(sh, Rectangle.Inflate(new Rectangle(xr.X, xr.Y + 1, xr.Width, xr.Height), 1, 1));

        using (var b = new SolidBrush(face)) g.FillEllipse(b, xr);

        Gfx.DrawGlyph(g, Glyph.Close, Rectangle.Inflate(xr, -3, -3), ink, 1.5f);
    }
}
