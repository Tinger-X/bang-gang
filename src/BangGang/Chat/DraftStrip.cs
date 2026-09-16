using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 输入卡片里的附件列表区：一行小卡片，每张是「缩略图 / 类型图标 + 文件名 + PDF · 625KB」。
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

    /// <summary>本区域自身的高度。<c>InputPanel</c> 的卡片高度按它让位。</summary>
    public const int RowH = 56;

    private const int ChipH = 44;
    private const int ChipGap = 8;
    private const int ChipNatW = 236;    // 一张卡片的自然宽度（名字长一点就到这里为止）
    private const int ChipMinW = 132;    // 挤到这个宽度就不再缩，再多就交给「+N」
    private const int IconSize = 32;
    private const int IconPad = 6;       // 图标离卡片左缘
    private const int TextGap = 8;       // 图标与文字之间
    private const int TextRight = 8;     // 文字离卡片右缘

    /// <summary>
    /// 卡片离条带上缘的距离。**删除按钮有一半探在卡片上面**（圆心正好压在卡片的右上角上），
    /// 所以卡片必须往下让开半个按钮，否则它会被条带的上边缘裁掉一半。
    /// </summary>
    private const int TopPad = 11;

    /// <summary>同上，最后一张卡片右边让开半个按钮。</summary>
    private const int RightPad = 11;

    private const int XSize = 20;        // 删除按钮的直径

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
            _thumbs.Add(a.Kind == "image" ? a.LoadImage(IconSize * 2, IconSize * 2) : null);
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

    /// <summary>一张卡片的宽度：一行放得下就保持自然宽度，放不下就一起压窄。</summary>
    private int ChipW()
    {
        int n = _items.Count;
        if (n <= 0) return ChipNatW;
        int avail = Math.Max(ChipMinW, Width - RightPad);
        int w = (avail - (n - 1) * ChipGap) / n;
        return Math.Clamp(w, ChipMinW, ChipNatW);
    }

    /// <summary>实际画得下的张数。挤到 <see cref="ChipMinW"/> 还放不下的，交给「+N」。</summary>
    private int FitCount()
    {
        int w = ChipW();
        int avail = Math.Max(w, Width - RightPad);
        return Math.Max(1, Math.Min(_items.Count, (avail + ChipGap) / (w + ChipGap)));
    }

    /// <summary>第 i 张卡片的矩形。删除按钮的圆心就压在它的右上角上。</summary>
    private Rectangle ChipRect(int i)
    {
        int w = ChipW();
        return new Rectangle(i * (w + ChipGap), TopPad, w, ChipH);
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
            int w = ChipW();
            var tail = new Rectangle(_fit * (w + ChipGap), TopPad, Math.Max(28, w / 3), ChipH);
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

    private void PaintChip(Graphics g, int i)
    {
        var a = _items[i];
        var c = ChipRect(i);
        RP.Box(g, c, 8, Theme.AsstBubble, BackColor);

        var icon = new Rectangle(c.Left + IconPad, c.Top + (ChipH - IconSize) / 2, IconSize, IconSize);
        var thumb = i < _thumbs.Count ? _thumbs[i] : null;
        if (thumb != null) PaintThumb(g, thumb, icon);
        else PaintFileIcon(g, a, icon);

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
    private static string DisplayName(Attachment a)
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
    private static string Ellipsize(string s, Font f, int avail)
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
    /// 图片缩略图：等比缩放到图标框里再居中，四角磨圆。
    ///
    /// 直接 <c>DrawImage(img, rect)</c> 会把非正方形的图**拉变形**（头像变宽脸），
    /// 所以先算一个保持长宽比的目标矩形。底下垫一层淡底，图比框小时四周不会露出卡片色差。
    /// </summary>
    private static void PaintThumb(Graphics g, Image img, Rectangle box)
    {
        using (var path = RP.Path(box, 6))
        using (var b = new SolidBrush(Theme.Mix(Theme.InputBg, Theme.TextMuted, 0.10f)))
            g.FillPath(b, path);

        double k = Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height);
        int w = Math.Max(1, (int)Math.Round(img.Width * k));
        int h = Math.Max(1, (int)Math.Round(img.Height * k));
        var dst = new Rectangle(box.Left + (box.Width - w) / 2, box.Top + (box.Height - h) / 2, w, h);

        // 用 Save/Restore 而不是存 g.Clip：Clip 的 getter 每次都吐一个新的 Region 出来，
        // 存下来再赋回去等于每画一张就漏一个 GDI 对象（缩略图是每帧都要重画的）。
        var state = g.Save();
        using (var path = RP.Path(box, 6)) g.SetClip(path, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, dst);
        g.Restore(state);
    }

    /// <summary>
    /// 非图片的文件图标：一块按类别着色的圆角方块 + 里面的扩展名（PDF / TXT / DOCX）。
    ///
    /// 用扩展名当图标，是因为白名单里几十种类型画不出几十个图标，而用户认的正是后缀那几个字母。
    /// 字号按字母个数收一收，四个字母（DOCX）也能塞进 32px 的方块。
    /// </summary>
    private static void PaintFileIcon(Graphics g, Attachment a, Rectangle box)
    {
        var cat = AttachTypes.CatOf(a.Path);
        Color tint = cat switch
        {
            AttachCat.Doc => Theme.Danger,
            AttachCat.Text => Theme.Accent,
            AttachCat.Audio => Theme.Mix(Theme.Accent, Theme.Danger, 0.5f),
            _ => Theme.TextMuted,
        };
        RP.Fill(g, box, 7, Theme.Mix(Theme.InputBg, tint, Theme.Dark ? 0.30f : 0.14f));

        string label = AttachTypes.ExtLabel(a.Path);
        if (label.Length == 0) label = AttachTypes.CatName(cat);
        if (label.Length > 4) label = label[..4];
        float size = label.Length >= 4 ? 7f : label.Length == 3 ? 8f : 8.5f;
        TextRenderer.DrawText(g, label, SF.Get(size, FontStyle.Bold), box, tint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    /// <summary>悬浮时那张卡片右上角的删除按钮。压在最上层画 —— 它会盖住卡片的一个角。</summary>
    private void PaintRemove(Graphics g)
    {
        var xr = XRect();
        if (xr.IsEmpty) return;
        using (var b = new SolidBrush(Color.FromArgb(_overX ? 240 : 190, 40, 44, 52)))
            g.FillEllipse(b, xr);
        Gfx.DrawGlyph(g, Glyph.Close, xr, Color.White, 1.5f);
    }
}
