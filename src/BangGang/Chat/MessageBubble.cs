using System.Drawing.Drawing2D;

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
    ///
    /// **注意 <see cref="SetMaxInner"/> 特意不报这个。** 它由 <c>LayoutRows</c> 自己调用，
    /// 而那之后紧接着就会重读 <c>b.Height</c>；在这里回声一次就成了
    /// <c>LayoutRows</c> → <c>HeightChanged</c> → <c>NotifyRowGrew</c> → <c>LayoutRows</c> 的重入。
    /// </summary>
    public event Action? HeightChanged;

    /// <summary>
    /// 气泡文本的最大内宽（不含左右内边距）。默认值是「对话区还没发话」时的兜底，
    /// 真实值由 <see cref="ChatView"/> 每轮布局推进来 —— 需求是气泡宽度随对话区自适应。
    /// </summary>
    private int _inner = 560;

    /// <summary>
    /// 内容**不受 <see cref="_inner"/> 约束**时想要的宽度。只有 <see cref="SetMaxInner"/>
    /// 的早退判据读它，见那里的注释。
    /// </summary>
    private float _naturalW;

    /// <summary>
    /// 上一轮排版时，内容**想要比 <see cref="_inner"/> 更宽**（正文折了行、附件排到了第二行、
    /// 思考块 / 截断说明强制满宽）。这种气泡的上限一变，宽度就该跟着变 ——
    /// <see cref="SetMaxInner"/> 靠它把自己和「内容撑出来的窄气泡」分开。
    /// 四条判据各自算得准不准，见 <see cref="Rebuild"/> 里那一段。
    /// </summary>
    private bool _capped;

    /// <summary>气泡左右内边距。对话区要按它反推「内宽上限」，所以不能是 private。</summary>
    internal const int PadX = 14;

    private const int PadY = 11;

    // ---- 思考过程那块（只有助手消息、且模型真的吐了 reasoning_content 时才有） ----
    private const int ReasonHeadH = 20;   // 「思考过程」那一行的行高，也是点击热区的高度
    private const int ReasonPadY = 9;     // 思考正文框的上下内边距
    private const int ReasonRuleW = 3;    // 正文左边那道竖线（连间距）
    private const int ReasonPadX = 10;

    // ---- 截断说明（回复被长度上限截断时贴在正文下面那句） ----
    private const int WarnPad = 9;
    private const int WarnGap = 10;       // 与上方正文之间的间距

    // ---- 附件区（图片 + 文件，和输入框上方那套是同一批卡片） ----
    private const int ChipGapY = 8;       // 换行时上一行的下缘与下一行的上缘
    private const int AttachGap = 12;     // 附件区与下方正文之间

    /// <summary>
    /// 一个附件格子：来源附件 + 缩略图（只有图片、且真的读出来了才有）+ 它在气泡里的矩形。
    ///
    /// 位置由 <see cref="LayoutChips"/> 一次算好，画、量高、命中测试都读它 ——
    /// 三处各算一遍的话，一旦算岔，表现是图片和正文重叠几个像素，谁也不会一眼看出来。
    /// </summary>
    private sealed class Chip
    {
        public required Attachment Src;

        /// <summary>消息里标着这是图片。**能不能点开看它**（不看缩略图读没读出来）——
        /// 缩略图读不出来时仍然画一个占位格子，用户至少知道这儿有张图。</summary>
        public required bool IsImage;

        public Image? Thumb;
        public Rectangle Rect;
    }

    private readonly List<Chip> _chips = new();

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

    /// <summary>附件区占的总高（含与下方正文的间距）；0 = 这条消息没有附件。</summary>
    private float _attachH;

    /// <summary>附件区最宽那一行有多宽。气泡的内容宽度要把它算进去。</summary>
    private int _chipW;

    /// <summary>附件是不是被 <see cref="_inner"/> 挤到了第二行。同上，早退判据要读它。</summary>
    private bool _chipWrapped;

    /// <summary>指针停在上面那个图片格子的序号，-1 表示没停在任何一格上。</summary>
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
        BuildChips();
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

    /// <summary>
    /// 对话区给的可用内宽上限。侧栏展开 / 收起时**每一帧**都会调它（对话区宽度在变）。
    ///
    /// 早退那一条是这里唯一的技巧：气泡宽度 = min(内宽上限, 内容自然宽度) + 内边距，
    /// 所以当内容比**新旧两个上限里更小的那个**还窄时，说明宽度本来就是内容撑的、
    /// 没被上限卡住，换一个上限不会改变任何东西 —— 不必重排，也不必重画。
    /// 侧栏动画期间省掉的正是这一整批 <c>Markdown.Measure</c>。
    ///
    /// **<see cref="_capped"/> 那一条不能省。** 折行正文量出来的「自然宽度」是
    /// 「最长那条物理行有多宽」，贪心折行总在放下下一个词之前就换行，于是它比上限窄着
    /// 那一个词的宽度 —— 超长正文量出来 803、上限 849，只比 <c>_naturalW</c> 的话，
    /// 上限变大时 <c>_naturalW &lt;= Math.Min(old, inner)</c> 恒成立：一条超长消息在侧栏收起、
    /// 可用宽度多出 230px 之后会原地不动 —— 气泡永远停在它第一次排版时的宽度上，
    /// 而这件事在几何上完全看不出是「没跟上」还是「就该这么宽」。
    /// 所以判据里必须带上「内容到底想不想要更宽」，由排版那边当场记下来。
    /// </summary>
    public void SetMaxInner(int inner)
    {
        if (inner == _inner) return;
        int old = _inner;
        _inner = inner;
        if (!_capped && _naturalW <= Math.Min(old, inner)) return;
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

        _md = Markdown.Measure(text, _inner);
        MeasureReason(reason);
        _warnH = warn.Length > 0 ? WarnBoxH(warn) + WarnGap : 0;
        // 附件最后排：它的起点压在思考块下面（见 BodyTop），所以上面几段的高度得先定下来。
        _attachH = LayoutChips();

        float headH = IsUser ? 0 : 18;  // 助手消息顶部显示"帮帮"

        float natural = Math.Max(60f, _md.Width);
        if (_chipW > natural) natural = _chipW;
        // 思考块和截断说明固定占满整个内宽：它们是「模型在干什么」的一条旁白，
        // 跟着正文一起忽宽忽窄，流式的时候整条气泡会一直跳。
        // 写成「想要无限宽」而不是「想要 _inner 宽」：早退判据读的就是这个数，
        // 写成后者会让「上限变小了但还够用」被误判成不必重排。
        if (_reasonH > 0 || _warnH > 0) natural = float.MaxValue;
        _naturalW = natural;

        // 这一轮的内容有没有把上限顶满 —— 见 SetMaxInner。
        //
        // 正文那一条**不能**拿宽度去比：折行之后 Markdown 报出来的宽度是「最长那条物理行
        // 有多宽」，而贪心折行总是在放下下一个词**之前**就换行，于是它比上限窄着那一个词的
        // 宽度 —— 15 句话的段落量出来 803、上限 849，差 46px，读成「内容就想要 803」
        // 正好会把「侧栏收起后还停在旧宽度」这件事放过去（0.8.5 就是这么栽的第二次）。
        // 所以 Markdown 折行的那一刻自己记下 Wrapped，这里只读那个标记。
        _capped = _md.Wrapped || _chipWrapped || _reasonH > 0 || _warnH > 0;

        float contentW = Math.Min(_naturalW, _inner);
        Width = (int)Math.Min(_inner + PadX * 2, contentW + PadX * 2);
        Height = (int)(PadY + headH + _reasonH + _attachH + _md.Height + _warnH + PadY);
        if (text.Length == 0 && reason.Length == 0 && _chips.Count == 0) Height = 28;

        // 重排后悬浮下标可能指到了另一格上（甚至指到了不存在的下标）
        if (_hover >= _chips.Count) _hover = -1;
    }

    /// <summary>
    /// 内容区的起点：跳过内边距、助手名字和思考块。绘制、量尺寸、命中测试都走它，
    /// 别各自把「思考块有多高」再算一遍 —— 三处各算一遍的那种错法，
    /// 表现是正文和附件重叠一点点，谁也不会一眼看出来。
    /// </summary>
    private float BodyTop() => PadY + (IsUser ? 0 : 18) + _reasonH;

    /// <summary>思考正文的可用宽度。</summary>
    private int ReasonTextW() => _inner - ReasonPadX * 2 - ReasonRuleW;

    /// <summary>量一次思考块（收起时只量表头那一行，正文一个字都不排）。</summary>
    private void MeasureReason(string reason)
    {
        _reasonH = 0;
        _reasonBodyH = 0;
        _reasonHeader = Rectangle.Empty;
        if (IsUser || reason.Length == 0) return;

        float top = PadY + 18;                 // 表头永远紧跟在「帮帮」下面
        _reasonHeader = new Rectangle(PadX, (int)Math.Round(top), _inner, ReasonHeadH);
        _reasonH = ReasonHeadH + 10;           // 收起：只有表头 + 与下方内容的间距
        if (!_reasonOpen) return;

        using var f = Theme.UI(10.5f);
        var sz = TextRenderer.MeasureText(reason, f, new Size(ReasonTextW(), int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        _reasonBodyH = sz.Height + ReasonPadY * 2;
        _reasonH = ReasonHeadH + 6 + _reasonBodyH + 10;
    }

    /// <summary>截断说明那块盒子有多高（文字按内宽折行量出来）。</summary>
    private float WarnBoxH(string warn)
    {
        using var f = Theme.UI(10.5f);
        var sz = TextRenderer.MeasureText(warn, f, new Size(_inner - WarnPad * 2, int.MaxValue),
                                          TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return sz.Height + WarnPad * 2;
    }

    /// <summary>
    /// 排附件格子，返回整块（含与下方正文的间距）的高度。
    ///
    /// 卡片本身就是输入框上方那一套 —— 图片是 44×44 的方图块，文件是「类别色块 + 名字 + 大小」
    /// 的小横条，几何常量只在 <see cref="DraftStrip"/> 一处定义，这里照着用。
    ///
    /// 与输入框那边**故意不同**的一点：那边一行放不下就先压窄、再放不下给「+N」；
    /// 这里换行。输入卡片的高度是定死的（挤掉一行就是挤掉用户正在敲的字），而气泡可以往上长，
    /// 用户自己加进来的附件必须全都看得见。
    /// </summary>
    private float LayoutChips()
    {
        _chipWrapped = false;
        if (_chips.Count == 0) { _chipW = 0; return 0; }

        int top = (int)Math.Round(BodyTop());
        int x = PadX, y = top, widest = 0;
        foreach (var c in _chips)
        {
            int w = c.IsImage ? DraftStrip.ChipImgW : Math.Min(DraftStrip.ChipNatW, _inner);
            if (x > PadX && x + w > PadX + _inner) { x = PadX; y += DraftStrip.ChipH + ChipGapY; _chipWrapped = true; }
            c.Rect = new Rectangle(x, y, w, DraftStrip.ChipH);
            x += w + DraftStrip.ChipGap;
            if (x - DraftStrip.ChipGap > widest) widest = x - DraftStrip.ChipGap;   // 这一行的右缘
        }
        _chipW = widest - PadX;
        return y + DraftStrip.ChipH + AttachGap - top;
    }

    /// <summary>
    /// 把消息里的附件读成格子。只在构造时跑一次 —— 缩略图是从磁盘读图再缩放出来的，
    /// 而流式回复每收一小段就要 <see cref="RefreshText"/> 一次，跟着重读的话
    /// 一条回复下来能把同一个文件读上百遍。
    /// </summary>
    private void BuildChips()
    {
        _chips.Clear();
        if (Msg.Attachments == null) return;
        foreach (var a in Msg.Attachments)
        {
            bool isImg = a.Kind == "image";
            _chips.Add(new Chip
            {
                Src = a,
                IsImage = isImg,
                // 缩略图走 DraftStrip 那一套：cover 裁切 + **抗锯齿**圆角。
                // 原来是自己读一张 280×190 的大图再磨角，格子缩到 44px 之后
                // 那份开销（和解码内存）纯属白给。
                Thumb = isImg ? DraftStrip.MakeThumb(a) : null,
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var c in _chips) c.Thumb?.Dispose();
        base.Dispose(disposing);
    }

    // ---------------- 点击放大 ----------------

    /// <summary>命中的图片格序号（没有则 -1）。**文件格子点不动**（用户明确要求），
    /// 所以它不参与命中测试 —— 不这么写的话它会先「悬浮变一下」再什么都不发生。</summary>
    private int ChipAt(Point p)
    {
        for (int i = 0; i < _chips.Count; i++)
            if (_chips[i].IsImage && _chips[i].Rect.Contains(p)) return i;
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
        int i = ChipAt(e.Location);
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
        int i = ChipAt(e.Location);
        if (i >= 0) ImagePressed?.Invoke(_chips[i].Src);
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
        for (int i = 0; i < _chips.Count; i++) DrawChip(g, bg, _chips[i], i == _hover);

        // 正文的起点**问布局要**，不要在这里把刚才那几段高度再加一遍：
        // 加漏一段（比如思考块）就会被正文盖住，而且只错几个像素，很难看出来。
        y = BodyTop() + _attachH;
        if (_md != null) y = Markdown.Draw(g, _md, x, y, _inner);
        if (_warnH > 0) DrawWarning(g, y + WarnGap);

        base.OnPaint(e);
    }

    /// <summary>
    /// 画一个附件格子。图片就是那张缩略图本身（44×44 的方图块，不写文件名 ——
    /// 缩略图比名字好认，「pic.png」和「pic-2.png」看不出区别，两张缩略图一眼就分得开）；
    /// 别的文件是「底色 + 类别图标 + 名字 + 大小」的小横条，和输入框上方那套完全一样。
    ///
    /// 缩略图读不出来的图片格画成文件样式（用扩展名当图标）：**不能悄悄跳过它** ——
    /// 用户明明加了张图，气泡里却什么都没有，那比画一个「打不开」的格子费解得多。
    /// </summary>
    private void DrawChip(Graphics g, Color bubble, Chip c, bool hover)
    {
        if (c.Thumb != null)
        {
            // 1:1 贴上去。PixelOffsetMode 必须校正：不校正的话整块图会被采样到半个像素上，
            // 四角的抗锯齿连同一整张图一起糊掉，看着就像「磨圆了但很脏」。
            var off = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(c.Thumb, c.Rect);
            g.PixelOffsetMode = off;

            // 悬浮提示：图变暗一点点。不用指针形状（用户要求），也不能什么都不给 ——
            // 「这张图能点开」在界面上否则无处可寻。
            if (hover)
            {
                using var path = RoundedRect(c.Rect.X, c.Rect.Y, c.Rect.Width - 1, c.Rect.Height - 1, DraftStrip.ThumbRadius);
                using var hl = new SolidBrush(Color.FromArgb(38, 0, 0, 0));
                g.FillPath(hl, path);
            }
            return;
        }

        RP.Box(g, c.Rect, 8, ChipFill(bubble), bubble);
        var icon = new Rectangle(c.Rect.Left + DraftStrip.IconPad,
                                 c.Rect.Top + (DraftStrip.ChipH - DraftStrip.IconSize) / 2,
                                 DraftStrip.IconSize, DraftStrip.IconSize);
        DraftStrip.PaintFileIcon(g, c.Src, icon, bubble);

        // 文字区。宽度按卡片算出来，量多少画多少（见 DraftStrip.Ellipsize 的注释）。
        int tx = icon.Right + DraftStrip.TextGap;
        int tw = c.Rect.Right - DraftStrip.TextRight - tx;
        if (tw < 20) return;

        string name = DraftStrip.Ellipsize(DraftStrip.DisplayName(c.Src), SF.Get(9.5f), tw);
        TextRenderer.DrawText(g, name, SF.Get(9.5f), new Rectangle(tx, c.Rect.Top + 6, tw, 18),
            Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        string meta = DraftStrip.Ellipsize(AttachTypes.MetaOf(c.Src), SF.Get(8f), tw);
        if (meta.Length > 0)
            TextRenderer.DrawText(g, meta, SF.Get(8f), new Rectangle(tx, c.Rect.Top + 23, tw, 16),
                Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// 附件卡片的底色。跟着主题走，而且**必须和它身下的东西拉开距离**。
    ///
    /// 这张卡片画在**气泡**上，所以「看得见」取决于它与气泡底色的差，不是它与
    /// <see cref="Theme.InputBg"/> 的差 —— <c>DraftStrip.ChipFill</c> 按输入卡片算，
    /// 那一套照搬过来的话，混出的色和气泡底色几乎一样，一行附件等于没画。
    /// 与气泡底色差 17~20 级，和输入框那边的对比度相当，两处看着才是一套东西。
    /// </summary>
    private static Color ChipFill(Color bubble) => Theme.Mix(bubble, Theme.TextMain, 0.08f);

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
        using (var box = RoundedRect(PadX, boxY, _inner - 1, _reasonBodyH - 1, 8))
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
        using (var box = RoundedRect(PadX, top, _inner - 1, h - 1, 8))
        using (var b = new SolidBrush(Theme.Mix(bubble, WarnInk, 0.14f)))
            g.FillPath(b, box);
        using (var pen = new Pen(Theme.Mix(bubble, WarnInk, 0.5f), 1f))
        using (var box2 = RoundedRect(PadX, top, _inner - 1, h - 1, 8))
            g.DrawPath(pen, box2);

        using var f = Theme.UI(10.5f);
        TextRenderer.DrawText(g, warn, f,
            new Rectangle(PadX + WarnPad, (int)(top + WarnPad), _inner - WarnPad * 2, (int)(h - WarnPad * 2)),
            WarnInk, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    /// <summary>琥珀色，深浅两套主题各一个 —— 亮色下的深琥珀放到暗底上会糊成一团。</summary>
    private static Color WarnInk => Theme.Dark ? Color.FromArgb(232, 168, 82) : Color.FromArgb(176, 106, 12);

    private static string Secs(int ms) => (ms / 1000.0).ToString("0.0") + "s";

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
