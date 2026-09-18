
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;

namespace BangGang;

/// <summary>
/// Markdown 排版与绘制。四层：
/// <list type="number">
/// <item><b>块级解析</b>（<see cref="Parser"/>）—— 标题 / 围栏代码 / 引用 / 列表 / 表格 / 分隔线 / 段落；</item>
/// <item><b>行内解析</b>（<see cref="ScanInline"/>）—— 粗体 / 斜体 / 删除线 / 行内代码 / 链接 / 转义 / 硬换行；</item>
/// <item><b>折行</b>（<see cref="WrapAtoms"/>）—— 把行内片段切成不可再分的原子再排成物理行；</item>
/// <item><b>绘制</b>（<see cref="Draw"/>）—— 逐物理行画装饰块与文字。</item>
/// </list>
///
/// 三条贯穿全篇的约定，改任何一层之前先看清楚：
///
/// <b>一、颜色不落在排版结果里。</b><see cref="Layout"/> 只在 <see cref="Run"/> /
/// <see cref="Decor"/> 上留一个 <see cref="Ink"/> 记号，真正的颜色到 <see cref="Draw"/>
/// 那一刻才由 <see cref="Palette"/> 按**气泡底色**解出来。所以排版结果可以跨主题、跨气泡缓存
/// —— 把 <see cref="Color"/> 直接烤进 <see cref="Run"/> 的话，切一次主题就得把所有气泡重排一遍，
/// 而且用户消息（蓝底）和助手消息（灰底）不能共用同一份缓存。
///
/// <b>二、量用 <see cref="TextRenderer"/>，画也用 <see cref="TextRenderer"/>，flag 一模一样。</b>
/// 用 GDI+ 量、GDI 画（或者反过来）会差出一两行，盒子高度是对的、字却溢出去了。
/// 顺带一提：GDI 文本**不认 <c>Graphics.Transform</c>**，调用方不能靠
/// <c>TranslateTransform</c> 把气泡挪到别处画 —— 那正是气泡走位图缓存的原因（见 MessageBubble）。
///
/// <b>三、高度只能由行的 <see cref="PhysLine.Advance"/> 累加出来。</b>
/// 段前距、引用块的内边距，全都写进某一行身上，**不另记一个 y 偏移**。
/// 测量和绘制各算一遍「这一块占多高」的话早晚会分叉，而分叉的表现是最后一行
/// 被气泡下缘裁掉几个像素 —— 谁也看不出是渲染器算错了。
///
/// 不做的事：不解析 HTML；代码块超宽时硬折行而不是横向滚动。
/// </summary>
internal static class Markdown
{
    // ---------------- 对外接口 ----------------

    /// <summary>
    /// 排版结果。调用方只读三个东西：<see cref="Width"/> / <see cref="Height"/> / <see cref="Wrapped"/>，
    /// 三个的语义与旧版**一字不变** —— <c>MessageBubble.SetMaxInner</c> 的早退判据完全依赖
    /// <see cref="Wrapped"/>，那条注释写得很长，别动它。
    /// </summary>
    internal sealed class Layout
    {
        public List<PhysLine> Lines { get; } = new();
        public float Width;
        public float Height;

        /// <summary>
        /// 这段内容**被 capWidth 折过行**（或者里面有代码块、表格、分隔线这类本来就该占满宽的东西）。
        ///
        /// <see cref="Width"/> 一个人说不清这件事：折行之后它是「最长的那条物理行有多宽」，
        /// 而那一条总是比 capWidth 窄着一点 —— 贪心折行在放下下一个词之前就换行了，
        /// 差的那一点是那个放不下的词的宽度，从 40 到 200 都有可能。所以
        /// 「Width 到没到 capWidth」根本判不出「这段文字还想更宽」，
        /// 差着半个词的超长正文会被读成「内容就想要这么宽」。
        /// 由折行的那一处**当场**记下来，不留给别人从宽度上反推。
        /// </summary>
        public bool Wrapped;

        /// <summary>
        /// 本文里所有代码块的头部（语言标签在左、复制 / 收展按钮在右的那一行），
        /// 按文档顺序。坐标是 markdown 局部坐标；按钮的图形与命中测试都不归这里管 ——
        /// 排版只负责登记「这块头部在哪、这块代码原文是什么」，
        /// 画图标与响应点击是 <c>MessageBubble</c> 的事。
        /// </summary>
        public List<CodeHead> CodeHeads { get; } = new();
    }

    /// <summary>一个代码块的头部登记。见 <see cref="Layout.CodeHeads"/>。</summary>
    internal sealed class CodeHead
    {
        /// <summary>本文里第几个代码块（从 0 数）。折叠状态按它记（MessageBubble._codeFolded）。</summary>
        public int Ordinal;

        /// <summary>整条头部行的矩形（markdown 局部坐标）。</summary>
        public RectangleF Rect;

        /// <summary>语言标签（可能是空串）。</summary>
        public string Lang = "";

        /// <summary>这块代码的原文（复制按钮用）。</summary>
        public string Code = "";

        /// <summary>这次排版时它是不是收起的（画哪个方向的箭头用）。</summary>
        public bool Folded;
    }

    /// <summary>一条物理行。位置由「前面所有行的 Advance 之和」决定，自己不存 y。</summary>
    internal sealed class PhysLine
    {
        public List<Run> Runs { get; } = new();

        /// <summary>画在文字**下面**的东西（代码块底、引用竖线、表格网格线）。矩形以 (x, 本行顶) 为原点。</summary>
        public List<Decor> Decors { get; } = new();

        /// <summary>左缩进（列表的悬挂对齐、引用 / 代码块的内边距都靠它）。</summary>
        public float Indent;

        /// <summary>本行高（不含 <see cref="SpaceBefore"/>）。用于画行内代码的底、下划线。</summary>
        public float LineHeight;

        /// <summary>本行占的纵向距离。**绘制也只能靠它**，见类注释第三条。</summary>
        public float Pitch;

        /// <summary>段前距 / 容器内边距。整段的第一行才带，后续行是 0。</summary>
        public float SpaceBefore;

        /// <summary>
        /// **基线**在行顶下方多远。0 表示本行没有公式，按老路画（文字顶对齐）。
        ///
        /// 带公式的行必须显式给出它：公式是挂在基线上的（分数线、根号、上下标全相对基线定位），
        /// 而正文的字是**顶对齐**画的。两者要坐在同一条线上，就得知道正文的基线落在行顶下面
        /// 多少 —— 那个数只有量过才知道（见 <c>MathLayout.Ascent</c> 为什么不能用 Font.Height）。
        /// </summary>
        public float Base;

        /// <summary>
        /// 文字要相对行顶**再往下**这么多。没有公式的行是 0。
        ///
        /// 行内公式比正文字体还高时（分数、根式、大运算符），本行会把**整行往下推**
        /// 一点，好让公式的顶不越出行顶去压上一行 —— 推的是整行，包括文字。
        /// 只把公式往下挪是不行的：那样文字和公式各坐一条基线，屏幕上就是
        /// 「行内公式比周围的字矮一截」，而这正是用户第一眼会看出来的毛病。
        /// 所以 <see cref="Base"/> 里含的这段抬升，文字这边必须原样跟一遍。
        /// </summary>
        public float TextShift;

        public float Advance => SpaceBefore + Pitch;
    }

    /// <summary>一段同色同字体的文字。折行的最小单位是「原子」，落到行上就是它。</summary>
    internal sealed class Run
    {
        public string Text = "";
        public Font Font = SF.Get(BodySize);
        public Ink Ink = Ink.Text;
        public float Width;              // 文字本身的宽
        public float PadL, PadR;         // 左 / 右各垫多少（行内代码圆角底的端帽，只在一段代码的两端有）
        public bool Underline, Strike;

        /// <summary>链接的地址（只有链接段有）。排版不读它，命中测试（MessageBubble.LinkAt）读它。</summary>
        public string? Link;

        /// <summary>≥0 时**绝对**定位（列表的项目符号、表格单元格、行间公式），不推进行内的光标。</summary>
        public float X = -1;

        /// <summary>
        /// 公式。非空时 <see cref="Text"/> 是空串，宽度取 <c>Math.Width</c>。
        ///
        /// 公式**不拆成多个 Run**（整个盒子就是一段）：分数线、根号那一撇都是跨字的几何图形，
        /// 拆开之后每一段都要重新知道自己在整体里的位置，而那正是盒子模型存在的意义。
        /// </summary>
        public MBox? Math;

        /// <summary>这一小段占的横向距离。</summary>
        public float Advance => Width + PadL + PadR;
    }

    /// <summary>画在文字下面的一块东西。填 / 描边各自可选。</summary>
    internal sealed class Decor
    {
        public RectangleF Rect;
        public Ink Fill = Ink.None;
        public Ink Stroke = Ink.None;
        public float StrokeW = 1f;
        public float Radius;
    }

    /// <summary>
    /// 颜色记号。**不是颜色** —— 见类注释第一条。真正解出 <see cref="Color"/> 的是
    /// <see cref="Palette"/>，而它需要知道画在哪个气泡上。
    /// </summary>
    internal enum Ink
    {
        None, Text, Muted, Accent,
        InlineCode, InlineCodeBg,
        CodePanel, QuotePanel, QuoteBar,
        TableHead, TableLine, Rule,
        SynKeyword, SynString, SynComment, SynNumber,
    }

    // ---------------- 尺寸常量 ----------------

    private const float BodySize = 12.5f;
    private const float CodeSize = 11.5f;

    /// <summary>行距系数。正文松一点好读，标题要紧凑，代码要更紧凑（行数多）。</summary>
    private const float PitchBody = 1.24f, PitchHead = 1.18f, PitchCode = 1.20f, PitchTable = 1.20f;

    private const float SpacePara = 8f;      // 段落之间
    private const float SpaceItem = 3f;      // 列表项之间
    // 带底色的块（代码 / 引用 / 表格）上下留的是**可见的空隙**，不是「块顶到块底」的
    // 距离 —— 自己的内边距由 Ctx.PadBelow / 自己的段前距另外补。所以这几个值要明显
    // 大于段距，否则面板挨着面板，两块看起来是一整条灰带子。
    private const float SpaceCode = 16f;
    private const float SpaceQuote = 12f;
    private const float SpaceTable = 12f;
    private const float SpaceRule = 13f;

    private const float HeadUnderGap = 4f;   // 标题到它下面那条细线
    private const float HeadUnderTail = 9f;  // 细线下面留的呼吸
    private const float QuoteBarW = 3f;      // 引用左边那道竖线
    private const float QuoteGap = 11f;      // 竖线到引用内容
    private const float QuotePadY = 7f;      // 引用块内的上下留白
    private const float CodePadX = 12f;
    private const float CodePadY = 9f;
    private const float CodeHeadH = 24f;     // 代码块头部那一行：左语言标签、右复制 / 收展按钮
    private const float TablePadX = 9f;
    private const float TablePadY = 6f;
    private const float TableMinColW = 54f;
    private const float InlineCodePadX = 3.5f;

    /// <summary>行间公式的上下留白。公式比正文高得多，贴着上下文会显得挤成一团。</summary>
    private const float SpaceMath = 12f;

    // ---------------- 测量 ----------------

    // 量文字一律走这一对 flag，且**量画同一套**。NoPadding 让量出来的宽就是画的宽；
    // NoPrefix 让 `&` 老老实实是个 `&`（代码里到处是这个字符，不设的话它会开始吃后面的字）。
    private const TextFormatFlags TFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private static readonly Size NoLimit = new(100000, 100000);

    private static float RunWidth(string s, Font f) =>
        s.Length == 0 ? 0f : TextRenderer.MeasureText(s, f, NoLimit, TFlags).Width;

    /// <summary>同 <see cref="RunWidth"/>，但给 <c>MathLayout</c> 用 ——
    /// 量公式里的字必须和正文用**同一套 flag 和同一个测量 API**，否则盒子算出来的宽
    /// 和真画出来的宽对不上，公式会溢到气泡外面。</summary>
    internal static float TextWidth(string s, Font f) => RunWidth(s, f);

    /// <summary>
    /// 单字宽度缓存。中文折行按**字**累加宽度，不走「量一整串再取宽」那条路 ——
    /// 后者是 O(n²)：每加一个字就把整行重量一遍，2000 字的一段就是两百万次字符测量，
    /// 流式回复每 40ms 重排一次，用户看到的是界面一卡一卡。
    ///
    /// 超过上限就整表清掉：真到几千个不同的字说明字号 / 字族被反复换过，
    /// 那是别的地方出了问题，这里不该跟着无限涨内存。
    /// </summary>
    private static readonly Dictionary<(char ch, Font f), float> CharW = new();

    private static float CharWidth(char c, Font f)
    {
        if (CharW.TryGetValue((c, f), out var w)) return w;
        if (CharW.Count > 4096) CharW.Clear();
        w = RunWidth(c.ToString(), f);
        CharW[(c, f)] = w;
        return w;
    }

    // ---------------- 排版缓存 ----------------

    /// <summary>
    /// 排版结果缓存。键是**原文 + 可用宽**：两者都没变时结果必然一样，而这两样在真实使用里
    /// 反复命中 —— 切走会话再切回来、切主题（键里不含颜色，见类注释第一条）、
    /// 同一帧里被问两次、以及重排时那些宽度没动过的气泡。
    ///
    /// <summary>条数上限而不是字节上限：一条消息的排版结果和它自己的长度成正比。
    /// 24 的来历：侧栏动画期间 <see cref="ChatView.ReflowVisible"/> 把宽度量化到 16px
    /// 步长，可见带里的几行（4~8 行）× 每行同时存活的几个步进宽度，再加上流式期间
    /// 同一文本的中间状态，6 条会把这个工作集挤出去、每帧全 miss；24 条在真实会话里
    /// 仍远小于一张缩略图。不封顶的话，一晚上流式下来的每一段中间状态都会攒在里面。
    /// </summary>
    private const int CacheMax = 24;
    private static readonly Dictionary<(string text, int cap, string fold), Layout> Cache = new();
    private static readonly Queue<(string text, int cap, string fold)> CacheOrder = new();

    /// <param name="codeFolded">收起来的代码块的序号集合（见 <see cref="CodeHead.Ordinal"/>）。
    /// 它进缓存键：同一段文字「收起第 1 块」和「全展开」是两份不同的排版。</param>
    public static Layout Measure(string md, float capWidth, IReadOnlySet<int>? codeFolded = null)
    {
        string key = md ?? "";
        int cap = Math.Max(40, (int)Math.Round(capWidth));
        string fold = FoldSig(codeFolded);
        if (Cache.TryGetValue((key, cap, fold), out var hit)) return hit;

        var lay = Build(key, cap, codeFolded);
        Cache[(key, cap, fold)] = lay;
        CacheOrder.Enqueue((key, cap, fold));
        while (CacheOrder.Count > CacheMax) Cache.Remove(CacheOrder.Dequeue());
        return lay;
    }

    /// <summary>折叠集合的缓存键形态：有序、逐值列出。空集合与 null 是同一个键（""）。</summary>
    private static string FoldSig(IReadOnlySet<int>? s)
    {
        if (s == null || s.Count == 0) return "";
        var arr = new List<int>(s);
        arr.Sort();
        return string.Join(',', arr);
    }

    /// <summary>
    /// 一行正文占多高（不含段前距），和 <see cref="WrapAtoms"/> 里算 <c>LineBuf.Pitch</c>
    /// 的式子**必须**是同一个 —— 一处分用两个式子，预留出来的那一行就会比真来一行时
    /// 高一点或矮一点，气泡在第一个字到达的瞬间跳一下。
    ///
    /// 用它的地方是「等第一个字」那个占位动画（<see cref="MessageBubble"/>）：
    /// 那会儿正文一个字都没有，气泡按真实内容量只有「帮帮」那半截。
    /// </summary>
    internal static float BodyLinePitch() => Math.Max(1f, MathF.Round(SF.Get(BodySize).Height * PitchBody));

    // ---------------- 行内解析 ----------------

    private const int FBold = 1, FItalic = 2, FStrike = 4, FCode = 8, FLink = 16, FMath = 32;

    private sealed class Inline
    {
        public string Text = "";
        public int Flags;

        /// <summary>这里必须换一行（行尾两空格 / 行尾反斜杠）。</summary>
        public bool HardBreak;

        /// <summary>公式语法树（TeX 或 MathML 解析后的同一棵树）。非空时 <see cref="Text"/> 无意义。</summary>
        public MNode? Math;

        /// <summary>链接的地址。<c>[文字](地址)</c> 和裸写的 URL 都有。</summary>
        public string? Link;
    }

    /// <summary>可以反斜杠转义的字符。只对**标点**生效，<c>\n</c> 这种要原样留着。</summary>
    private static bool IsEscapable(char c) =>
        "\\`*_{}[]()#+-.!>~|\"'$%&,/:;<=?@^".IndexOf(c) >= 0;

    private static List<Inline> ParseInline(string s)
    {
        var outp = new List<Inline>();
        ScanInline(outp, s, 0, 0);
        return outp;
    }

    private static void AddInline(List<Inline> outp, string text, int flags)
    {
        if (text.Length == 0) return;
        // 与上一段同格式就并进去：不并的话一个字一个 Run，量宽度和画文字的次数都翻好几倍。
        // 公式那一段永远不并 —— 它是整棵语法树，并进去就只剩最后一段了。
        if (outp.Count > 0)
        {
            var last = outp[^1];
            if (!last.HardBreak && last.Math == null && last.Flags == flags) { last.Text += text; return; }
        }
        outp.Add(new Inline { Text = text, Flags = flags });
    }

    /// <summary>
    /// 行内扫描。<paramref name="depth"/> 是强调嵌套的深度上限 ——
    /// <c>**a *b* c**</c> 这种要递归，而畸形输入（一串没配对的星号）会让朴素实现无限递归下去。
    /// 到顶之后就当普通文字，宁可少一层斜体也不能把界面卡死。
    /// </summary>
    private static void ScanInline(List<Inline> outp, string s, int flags, int depth)
    {
        if (depth > 8) { AddInline(outp, s, flags); return; }

        var lit = new StringBuilder();
        void Flush() { if (lit.Length > 0) { AddInline(outp, lit.ToString(), flags); lit.Clear(); } }

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            // ---- 公式 ----
            //
            // **必须排在转义之前**：`\(` `\[` 是 LaTeX 的行内 / 行间公式定界符，而 `(` 和 `[`
            // 都在可转义字符表里 —— 排在后面的话 `\(x\)` 会先被转义成普通的 `(x)`，
            // 公式永远走不到这里。`\$`（想写一个真的美元符号）反过来要靠转义分支接住，
            // 而 TryMath 只认 `\(` `\[`，所以两边不打架。
            if (TryMath(s, i, out var mnode, out int mnext))
            {
                Flush();
                outp.Add(new Inline { Math = mnode, Flags = flags | FMath });
                i = mnext;
                continue;
            }

            // ---- 转义 ----
            if (c == '\\' && i + 1 < s.Length && IsEscapable(s[i + 1]))
            {
                lit.Append(s[i + 1]);
                i += 2;
                continue;
            }

            // ---- 硬换行 / 软换行 ----
            //
            // 段落里的换行默认折成空格（软换行）；行尾两个空格或一个反斜杠才是「这里必须断」。
            // 不处理的话，模型按「一行一句话」写出来的东西会被拼成一整段。
            if (c == '\n')
            {
                bool hard = false;
                if (outp.Count > 0 && outp[^1].Flags == flags)
                {
                    var last = outp[^1];
                    if (last.Text.EndsWith("  ")) { last.Text = last.Text.TrimEnd(); hard = true; }
                    else if (last.Text.EndsWith("\\")) { last.Text = last.Text[..^1]; hard = true; }
                }
                Flush();
                if (hard) outp.Add(new Inline { HardBreak = true });
                else if (outp.Count > 0 && !outp[^1].HardBreak && !outp[^1].Text.EndsWith(" "))
                    AddInline(outp, " ", flags);
                i++;
                continue;
            }

            // ---- 行内代码 ----
            if (c == '`')
            {
                int n = 1;
                while (i + n < s.Length && s[i + n] == '`') n++;
                string tick = new string('`', n);
                int close = s.IndexOf(tick, i + n, StringComparison.Ordinal);
                if (close > i)
                {
                    Flush();
                    string code = s[(i + n)..close].Replace('\n', ' ');
                    // 两端各一个空格是定界用的（`` ` `` 要能写出一个反引号），只有内部还有内容时才剥
                    if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                        code = code.Trim();
                    AddInline(outp, code, flags | FCode);
                    i = close + n;
                    continue;
                }
                lit.Append(c);
                i++;
                continue;
            }

            // ---- 链接：[文字](地址) ----
            if (c == '[')
            {
                int rb = s.IndexOf(']', i + 1);
                if (rb > i && rb + 1 < s.Length && s[rb + 1] == '(')
                {
                    int rp = s.IndexOf(')', rb + 2);
                    if (rp > rb)
                    {
                        Flush();
                        string? url = NormalizeUrl(s[(rb + 2)..rp].Trim());
                        int mark = outp.Count;
                        ScanInline(outp, s[(i + 1)..rb], flags | FLink, depth + 1);
                        // 递归产出的每一段都挂上地址 —— 命中测试按段取。
                        // 地址不可点（相对地址、别的协议）就留空：照样上色，但点不动。
                        if (url != null)
                            for (int k = mark; k < outp.Count; k++) outp[k].Link = url;
                        i = rp + 1;
                        continue;
                    }
                }
                lit.Append(c);
                i++;
                continue;
            }

            // ---- 裸 URL：http(s)://… 与 www.… ----
            //
            // 模型经常直接甩一个地址出来，不包 []()。认出来上色、加下划线、可点 ——
            // 不认的话它就是一长串普通文字。前一个字符得是边界（行首 / 空白 / 开口标点），
            // 不然 `id=abc.html` 这种串的尾巴会被吃成链接。
            if ((c == 'h' || c == 'H' || c == 'w' || c == 'W')
                && (i == 0 || IsUrlBoundary(s[i - 1]))
                && TryBareUrl(s, i, out var urlText, out int urlEnd))
            {
                Flush();
                outp.Add(new Inline { Text = urlText, Flags = flags | FLink, Link = NormalizeUrl(urlText) });
                i = urlEnd;
                continue;
            }

            // ---- 删除线 ----
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
            {
                int close = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Flush();
                    ScanInline(outp, s[(i + 2)..close], flags | FStrike, depth + 1);
                    i = close + 2;
                    continue;
                }
                lit.Append(c);
                i++;
                continue;
            }

            // ---- 强调 ----
            if (c == '*' || c == '_')
            {
                int n = 1;
                while (i + n < s.Length && s[i + n] == c) n++;
                int use = Math.Min(n, 3);
                string tok = new string(c, use);
                int close = s.IndexOf(tok, i + use, StringComparison.Ordinal);

                // 单词内部的单个下划线不算强调：snake_case 的变量名满屏都是，
                // 按强调处理的话 `my_var_name` 会把中间那截变成斜体，而用户明明是在说代码。
                bool intraWord = c == '_' && i > 0 && i + use < s.Length
                                 && char.IsLetterOrDigit(s[i - 1]) && char.IsLetterOrDigit(s[i + use]);

                if (close > i + use && !intraWord)
                {
                    Flush();
                    int sub = flags;
                    if (use >= 2) sub |= FBold;
                    if (use != 2) sub |= FItalic;      // *** 是粗斜，** 只是粗
                    ScanInline(outp, s[(i + use)..close], sub, depth + 1);
                    i = close + use;
                    continue;
                }
                lit.Append(s, i, use);
                i += use;
                continue;
            }

            lit.Append(c);
            i++;
        }
        Flush();
    }

    // ---------------- 裸 URL 识别 ----------------

    /// <summary>
    /// 可点的地址；不可点就返回 null（调用方照样上色，只是点不动）。
    /// <c>www.</c> 开头的补一个协议头。**只放行 http / https / mailto** ——
    /// 放行的范围与 <c>Ui.OpenLink</c> 的白名单（http / https）取交集才真正点得开，
    /// 但别的协议连「看起来像链接」这层暗示都不该给错方向，所以收窄在这里做。
    /// </summary>
    private static string? NormalizeUrl(string url)
    {
        if (url.Length == 0) return null;
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return "http://" + url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        return null;
    }

    /// <summary>裸 URL 的左边界：前一个字符不能是字母数字或地址里会出现的符号。</summary>
    private static bool IsUrlBoundary(char c) =>
        !(char.IsLetterOrDigit(c) || c is '@' or '/' or '.' or '-' or '_' or ':');

    /// <summary>裸 URL 在这里终止：空白、引号、尖括号、反引号，以及中文句读。</summary>
    private static bool IsUrlTerminus(char c) =>
        char.IsWhiteSpace(c) || "\"'<>`|。，、；：？！（）【】「」『』〈〉《》—…".IndexOf(c) >= 0;

    /// <summary>
    /// 从 <paramref name="i"/> 处认一个裸写的 URL。认出来交出显示文字与下一个位置。
    ///
    /// 结尾的句读要往回吐：「见 https://a.b/c。」里的句号是句子的，不是地址的；
    /// 收尾括号同理，但**成对的留着** —— 维基百科那类地址里真有括号。
    /// </summary>
    private static bool TryBareUrl(string s, int i, out string text, out int end)
    {
        text = "";
        end = i;

        int head;
        if (s.AsSpan(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase)) head = 8;
        else if (s.AsSpan(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase)) head = 7;
        else if (s.AsSpan(i).StartsWith("www.", StringComparison.OrdinalIgnoreCase)) head = 4;
        else return false;

        // 协议头后面总得有点真东西，光一个头是认不得的
        if (i + head >= s.Length || !char.IsLetterOrDigit(s[i + head])) return false;

        int j = i + head;
        while (j < s.Length && !IsUrlTerminus(s[j])) j++;
        int e = j;
        while (e > i + head && ".,;:!?".IndexOf(s[e - 1]) >= 0) e--;
        foreach (var (close, open) in new[] { (')', '('), (']', '['), ('}', '{') })
            while (e > i + head && s[e - 1] == close && CountOf(s, i, e, open) < CountOf(s, i, e, close))
                e--;
        if (e <= i + head) return false;
        text = s[i..e];
        end = e;
        return true;
    }

    private static int CountOf(string s, int from, int to, char c)
    {
        int n = 0;
        for (int k = from; k < to; k++) if (s[k] == c) n++;
        return n;
    }

    // ---------------- 公式识别 ----------------

    /// <summary>
    /// 从 <paramref name="i"/> 处认一段公式。认出来就把解析好的树和下一个位置交出去。
    ///
    /// 四种写法都认：
    /// <list type="bullet">
    /// <item><c>$…$</c> 行内（带货币判定，见 <see cref="IsCurrency"/>）</item>
    /// <item><c>$$…$$</c>、<c>\(…\)</c>、<c>\[…\]</c></item>
    /// <item><c>&lt;math&gt;…&lt;/math&gt;</c>（MathML）</item>
    /// <item><b>裸命令</b>：<c>\frac{a}{b}</c>、<c>\alpha</c> 这样直接写、没有定界符的
    ///    —— 模型很爱这么写，而用户看到的是一串反斜杠，这正是这个功能存在的理由</item>
    /// </list>
    /// </summary>
    private static bool TryMath(string s, int i, out MNode? node, out int next)
    {
        node = null;
        next = i;
        char c = s[i];

        // ---- \( … \) / \[ … \] ----
        if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '(' || s[i + 1] == '['))
        {
            string close = s[i + 1] == '(' ? "\\)" : "\\]";
            int e = s.IndexOf(close, i + 2, StringComparison.Ordinal);
            if (e < 0) return false;
            node = ParseMath(s[(i + 2)..e]);
            next = e + 2;
            return node != null;
        }

        // ---- $ … $ / $$ … $$ ----
        if (c == '$')
        {
            bool disp = i + 1 < s.Length && s[i + 1] == '$';
            string tok = disp ? "$$" : "$";
            int body = i + tok.Length;
            int e = FindClose(s, body, tok);
            if (e < 0) return false;

            string tex = s[body..e];
            if (!disp)
            {
                // 行内公式**里面不能有换行**、首尾不能有空格：`$100 和一个 $50 的东西` 这种
                // 句子两个 `$` 之间跨了半句话，认成公式会把中间的正文整段吃掉。
                if (tex.Length == 0 || tex[0] == ' ' || tex[^1] == ' ' || tex.Contains('\n')) return false;
                if (IsCurrency(tex)) return false;
            }
            node = ParseMath(tex);
            if (node == null) return false;
            next = e + tok.Length;
            return true;
        }

        // ---- <math> … </math> ----
        if (c == '<' && i + 5 <= s.Length
            && string.Compare(s, i, "<math", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
        {
            int e = s.IndexOf("</math>", i, StringComparison.OrdinalIgnoreCase);
            if (e < 0) return false;
            node = MathMl.Parse(s[i..(e + 7)]);
            if (node == null) return false;
            next = e + 7;
            return true;
        }

        // ---- 裸命令 ----
        if (c == '\\')
        {
            int e = BareExtent(s, i);
            if (e <= i) return false;
            node = MathTex.Parse(s[i..e]);
            if (node == null) return false;
            next = e;
            return true;
        }

        return false;
    }

    /// <summary>找一个不被转义的定界符。</summary>
    private static int FindClose(string s, int from, string tok)
    {
        int k = from;
        while (k < s.Length)
        {
            int e = s.IndexOf(tok, k, StringComparison.Ordinal);
            if (e < 0) return -1;
            bool esc = e > 0 && s[e - 1] == '\\' && !(e > 1 && s[e - 2] == '\\');
            if (!esc) return e;
            k = e + tok.Length;
        }
        return -1;
    }

    /// <summary>
    /// 是不是钱而不是公式。<c>$100</c>、<c>$1,200.50</c> 里的 `$` 后面只跟数字和千分位 ——
    /// 这种一律不当公式。**不做这一条的话**，一段正常的报价文字里两个 `$` 之间的内容
    /// 会被拿去当公式解析，结果是那半句话整个变了样。
    /// </summary>
    private static bool IsCurrency(string t)
    {
        foreach (char c in t)
            if (!char.IsDigit(c) && c != ',' && c != '.' && c != '\'' && c != ' ') return false;
        return true;
    }

    /// <summary>
    /// 裸命令公式**到哪儿为止**。
    ///
    /// 它没有定界符，所以边界只能靠「这个字符是不是还会出现在公式里」来判断。做法是从
    /// <c>\name</c> 开始一路吃：字母数字、<c>{} ^ _ + - = &lt; &gt; * / | ( ) [ ] , . ; : ! ~ '</c>、
    /// 以及**后面还接着公式字符的空格**。碰到汉字、换行、反引号、另一个 <c>$</c> 就停 ——
    /// 「用 <c>\alpha</c> 表示」这句话里，公式在 <c>\alpha</c> 之后那个空格处就该断了。
    /// </summary>
    private static int BareExtent(string s, int i)
    {
        int k = i, last = i;
        while (k < s.Length)
        {
            char c = s[k];

            if (c == '\\')
            {
                int j = k + 1;
                string nm;
                if (j < s.Length && !char.IsLetter(s[j])) { nm = s[j].ToString(); j++; }
                else { int st = j; while (j < s.Length && char.IsLetter(s[j])) j++; nm = s[st..j]; }
                if (nm.Length == 0 || !MathTex.IsKnownCommand(nm)) break;
                k = j;
                last = k;
                continue;
            }
            if (c == '{' || c == '}' || c == '^' || c == '_') { k++; last = k; continue; }
            if (c < 128 && (char.IsLetterOrDigit(c) || "+-=<>*/|()[],.;:!~'".IndexOf(c) >= 0)) { k++; last = k; continue; }
            if (c == ' ')
            {
                int j = k;
                while (j < s.Length && s[j] == ' ') j++;
                if (j < s.Length && IsMathChar(s, j)) { k = j; continue; }
                break;
            }
            break;
        }
        return last;
    }

    private static bool IsMathChar(string s, int i)
    {
        char c = s[i];
        if (c == '\\') return MathTex.IsKnownCommand(MathTex.NameAt(s, i));
        if (c < 128 && (char.IsLetterOrDigit(c) || "+-=<>*/|()[],.;:!~'{ }^_".IndexOf(c) >= 0)) return true;
        return false;
    }

    /// <summary>一段公式文本：MathML 走 MathML，其余走 TeX。都不认就返回 null，由调用方按普通文字渲染。</summary>
    private static MNode? ParseMath(string src)
    {
        string t = src.Trim();
        if (t.Length == 0) return null;
        if (t.StartsWith("<math", StringComparison.OrdinalIgnoreCase)) return MathMl.Parse(t);
        return MathTex.Parse(t);
    }

    // ---------------- 块级解析 ----------------

    private abstract class Block;

    private sealed class BPara : Block
    {
        public required List<Inline> Inlines;
    }

    private sealed class BHead : Block
    {
        public int Level;
        public required List<Inline> Inlines;
    }

    private sealed class BCode : Block
    {
        public string Lang = "";
        public required List<string> Lines;
    }

    private sealed class BQuote : Block
    {
        public required List<Block> Blocks;
    }

    private sealed class BList : Block
    {
        public bool Ordered;

        /// <summary>有序列表第一项的编号：源文写几就从几数（<c>3.</c> 开头就是 3、4、5…）。</summary>
        public int Start = 1;

        /// <summary>每一项是**一串块**，不是一段文字：列表项里可以放引用、代码块、嵌套列表。</summary>
        public required List<List<Block>> Items;

        /// <summary>
        /// 任务列表项（<c>- [ ]</c> / <c>- [x]</c>），与 <see cref="Items"/> 一一对应：
        /// 0 = 普通项、1 = 未完成、2 = 已完成。整张表一个任务项都没有时是 null。
        /// </summary>
        public List<int>? Tasks;
    }

    private sealed class BTable : Block
    {
        public required List<List<List<Inline>>> Rows;   // 行 → 列 → 行内
        public required List<int> Aligns;                // -1 左 / 0 中 / 1 右
    }

    private sealed class BRule : Block;

    /// <summary>独立成行的公式（<c>$$…$$</c>、<c>\[…\]</c>、行首的 <c>&lt;math&gt;</c>）。</summary>
    private sealed class BMath : Block
    {
        public MNode? Node;

        /// <summary>解析不出来时的原文 —— 宁可显示一串反斜杠，也不能整块变空。</summary>
        public string Raw = "";
    }

    private static readonly Regex ReFence = new(@"^\s{0,3}(`{3,}|~{3,})\s*(\S*)\s*$", RegexOptions.Compiled);
    private static readonly Regex ReHead = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex ReRule = new(@"^\s{0,3}([-*_])[ \t]*(\1[ \t]*){2,}$", RegexOptions.Compiled);
    private static readonly Regex ReItem = new(@"^( *)([-*+]|\d+[.)])( +)(.*)$", RegexOptions.Compiled);
    private static readonly Regex ReTableSep =
        new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

    /// <summary>这一行前面有几个空格（制表符在进来之前已经摊平了，见 <see cref="SplitLines"/>）。</summary>
    private static int IndentOf(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    /// <summary>
    /// 切成行。制表符摊平成 4 空格 —— **围栏代码块里面不摊**（那里的制表符是内容本身，
    /// 摊平会把对齐过的代码画歪）。缩进这套东西难就难在制表符算几列，
    /// 干脆在最外面一次性摊掉，后面所有的缩进计算就都只需要看空格了。
    /// </summary>
    private static string[] SplitLines(string md)
    {
        string[] raw = (md ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inFence = false;
        for (int i = 0; i < raw.Length; i++)
        {
            if (ReFence.IsMatch(raw[i])) { inFence = !inFence; continue; }
            if (!inFence && raw[i].Contains('\t')) raw[i] = raw[i].Replace("\t", "    ");
        }
        return raw;
    }

    private sealed class Parser
    {
        private readonly string[] _l;
        private int _i;

        /// <summary>
        /// 每个顶层块的**源文**，与 <see cref="Blocks"/> 的返回值一一对应。
        /// 块级排版缓存（<see cref="BlockLayoutOf"/>）拿它当键：源文逐字相同、宽度相同的块，
        /// 排版结果必然相同 —— 流式回复每 40ms 重排全文时，前面所有块都满足这一条。
        /// </summary>
        public readonly List<string> Sources = new();

        public Parser(string[] lines) { _l = lines; }

        public List<Block> Blocks()
        {
            var outp = new List<Block>();
            var para = new List<string>();

            void FlushPara()
            {
                if (para.Count == 0) return;
                string src = string.Join("\n", para);
                outp.Add(new BPara { Inlines = ParseInline(src) });
                Sources.Add(src);
                para.Clear();
            }

            // 记一块的源文跨度（从 from 到当前行为止），与块一一对应，见 Sources。
            void AddSpanned(Block b, int from)
            {
                outp.Add(b);
                Sources.Add(string.Join("\n", _l, from, _i - from));
            }

            while (_i < _l.Length)
            {
                string line = _l[_i];
                string t = line.TrimStart();

                if (t.Length == 0) { FlushPara(); _i++; continue; }

                var fm = ReFence.Match(line);
                if (fm.Success) { FlushPara(); int s0 = _i; AddSpanned(ReadFence(fm.Groups[2].Value.Trim()), s0); continue; }

                var hm = ReHead.Match(t);
                if (hm.Success)
                {
                    FlushPara();
                    int s0 = _i;
                    _i++;
                    AddSpanned(new BHead { Level = hm.Groups[1].Value.Length, Inlines = ParseInline(hm.Groups[2].Value) }, s0);
                    continue;
                }

                if (ReRule.IsMatch(t)) { FlushPara(); int s0 = _i; _i++; AddSpanned(new BRule(), s0); continue; }

                // 独立成行的公式。必须在段落之前判 —— 落到段落里就变成行内公式了，
                // 行内公式是**跟着文字走的**，而 `$$…$$` 写法的意思正是「单独占一行」。
                if (t.StartsWith("$$") || t.StartsWith("\\[") || t.StartsWith("<math", StringComparison.OrdinalIgnoreCase))
                {
                    FlushPara();
                    int s0 = _i;
                    AddSpanned(ReadMath(), s0);
                    continue;
                }

                if (t.StartsWith('>')) { FlushPara(); int s0 = _i; AddSpanned(ReadQuote(), s0); continue; }

                if (line.Contains('|') && _i + 1 < _l.Length && ReTableSep.IsMatch(_l[_i + 1]))
                {
                    FlushPara();
                    int s0 = _i;
                    AddSpanned(ReadTable(), s0);
                    continue;
                }

                var im = ReItem.Match(line);
                if (im.Success) { FlushPara(); int s0 = _i; AddSpanned(ReadList(im), s0); continue; }

                if (IndentOf(line) >= 4) { FlushPara(); int s0 = _i; AddSpanned(ReadIndented(), s0); continue; }

                para.Add(t.TrimEnd());
                _i++;
            }
            FlushPara();
            return outp;
        }

        /// <summary>
        /// 读一块独立成行的公式。三种开法各有各的收法，但**都得有一个明确的终点** ——
        /// 收不到就只吃到本行为止（模型偶尔会把 <c>$$</c> 打成单数个，那之后整篇都是正文，
        /// 无限吃下去会把整条消息吞光）。
        /// </summary>
        private BMath ReadMath()
        {
            string t = _l[_i].TrimStart();
            string open, close;
            bool keepClose;
            if (t.StartsWith("$$")) { open = "$$"; close = "$$"; keepClose = false; }
            else if (t.StartsWith("\\[")) { open = "\\["; close = "\\]"; keepClose = false; }
            // MathML 这一支和上面两支是**反的**：`$$` / `\]` 是纯定界符，切掉才是公式本身；
            // 而 `</math>` 是这段 XML 的**收尾标签**，公式本身要连它一起交给 MathMl.Parse
            // （`Looks` / `ParseMath` 都要求开头是 `<math`，`XDocument` 又要求有闭合标签），
            // 切掉就只剩一个开着的标签，解析必定失败、整块原样显示成一串尖括号。
            else { open = ""; close = "</math>"; keepClose = true; }
            int tail = keepClose ? close.Length : 0;

            var body = new List<string>();
            string first = t[open.Length..];
            _i++;

            int one = first.IndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (one >= 0) body.Add(first[..(one + tail)]);   // 单行写完的 $$…$$
            else
            {
                if (first.Trim().Length > 0) body.Add(first);
                while (_i < _l.Length)
                {
                    string cur = _l[_i];
                    int e = cur.IndexOf(close, StringComparison.OrdinalIgnoreCase);
                    if (e >= 0) { body.Add(cur[..(e + tail)]); _i++; break; }
                    body.Add(cur.TrimEnd());
                    _i++;
                }
            }

            string raw = string.Join("\n", body).Trim();
            var node = ParseMath(raw);
            if (node != null) MathLayout.MarkDisplay(node);
            return new BMath { Node = node, Raw = raw };
        }

        private BCode ReadFence(string lang)
        {
            _i++;                                  // 跳过起始围栏
            var body = new List<string>();
            while (_i < _l.Length && !ReFence.IsMatch(_l[_i])) { body.Add(_l[_i].TrimEnd()); _i++; }
            if (_i < _l.Length) _i++;              // 跳过收尾围栏
            return new BCode { Lang = lang, Lines = body };
        }

        /// <summary>四个空格缩进的代码块。到第一个不缩进的行就结束。</summary>
        private BCode ReadIndented()
        {
            var body = new List<string>();
            while (_i < _l.Length)
            {
                if (_l[_i].Trim().Length == 0) { body.Add(""); _i++; continue; }
                if (IndentOf(_l[_i]) < 4) break;
                body.Add(_l[_i][4..].TrimEnd());
                _i++;
            }
            while (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
            return new BCode { Lang = "", Lines = body };
        }

        private BQuote ReadQuote()
        {
            var inner = new List<string>();
            while (_i < _l.Length)
            {
                string t = _l[_i].TrimStart();
                if (!t.StartsWith('>')) break;
                t = t[1..];
                if (t.StartsWith(' ')) t = t[1..];
                inner.Add(t);
                _i++;
            }
            // 不认「下一行不写 > 也算引用」那条（CommonMark 的懒惰续行）：
            // 模型的输出里引用后面紧跟的往往就是正文，接上去反而把正文吞进引用块里。
            return new BQuote { Blocks = new Parser(inner.ToArray()).Blocks() };
        }

        /// <summary>
        /// 列表。**用缩进层级说话**：比记号深出一截的续行属于本项，剥掉缩进之后整项交给
        /// 一个新的解析器 —— 嵌套列表于是不需要单独一套逻辑，它只是本项内容里又一个列表而已。
        /// </summary>
        private BList ReadList(Match first)
        {
            bool ordered = char.IsAsciiDigit(first.Groups[2].Value[0]);

            // 起始编号：源文写几就是几。模型接着上文续写列表时不会从 1 开始，
            // 一律重编成 1 起会把「第 5 步」画成「第 1 步」。
            int start = 1;
            if (ordered)
            {
                string mk = first.Groups[2].Value;
                if (int.TryParse(mk.AsSpan(0, mk.Length - 1), out int n0) && n0 >= 0) start = n0;
            }

            var items = new List<List<Block>>();
            var tasks = new List<int>();
            bool anyTask = false;

            while (_i < _l.Length)
            {
                var m = ReItem.Match(_l[_i]);
                if (!m.Success) break;
                if (char.IsAsciiDigit(m.Groups[2].Value[0]) != ordered) break;

                int markerCol = m.Groups[1].Length;                                    // 记号所在的列
                int contentCol = markerCol + m.Groups[2].Length + m.Groups[3].Length;  // 续行要对齐到这一列
                var body = new List<string> { m.Groups[4].Value };
                _i++;

                while (_i < _l.Length)
                {
                    string nl = _l[_i];

                    if (nl.Trim().Length == 0)
                    {
                        // 空行之后还缩进得够深才算本项的续行，否则本项到此为止 ——
                        // 不判这一下的话，列表后面隔一个空行的**新段落**会被吸进最后一项里。
                        int k = _i + 1;
                        while (k < _l.Length && _l[k].Trim().Length == 0) k++;
                        if (k < _l.Length && IndentOf(_l[k]) >= contentCol) { body.Add(""); _i++; continue; }
                        break;
                    }

                    int ind = IndentOf(nl);
                    if (ind >= contentCol) { body.Add(nl[contentCol..]); _i++; continue; }
                    if (ind > markerCol) { body.Add(nl[Math.Min(ind, contentCol)..]); _i++; continue; }
                    break;
                }

                var sub = new Parser(body.ToArray()).Blocks();
                if (sub.Count == 0) sub.Add(new BPara { Inlines = new List<Inline>() });
                items.Add(sub);

                // 任务列表项：`- [ ] xxx` / `- [x] xxx`。把方框记号从正文里剥掉，
                // 画的时候它是一个真的小方框，不是三个字符（见 EmitList）。
                int task = 0;
                if (!ordered && sub[0] is BPara tp && tp.Inlines.Count > 0)
                {
                    var t0 = tp.Inlines[0];
                    if (t0.Math == null && !t0.HardBreak && t0.Text.Length >= 3
                        && t0.Text[0] == '[' && (t0.Text[1] == ' ' || t0.Text[1] is 'x' or 'X') && t0.Text[2] == ']')
                    {
                        task = t0.Text[1] == ' ' ? 1 : 2;
                        anyTask = true;
                        t0.Text = t0.Text.Length > 3 && t0.Text[3] == ' ' ? t0.Text[4..] : t0.Text[3..];
                    }
                }
                tasks.Add(task);
            }
            return new BList { Ordered = ordered, Start = start, Items = items, Tasks = anyTask ? tasks : null };
        }

        private BTable ReadTable()
        {
            var aligns = new List<int>();
            var rows = new List<List<List<Inline>>>();

            var head = SplitRow(_l[_i]);
            _i++;
            // 分隔行要看的是**原始文本**（`:` 在左还是在右决定对齐），不是解析完的行内片段 ——
            // 那一行里的 `-` 会被行内解析当成普通文本，但 `:` 的位置只有原文里才看得见。
            foreach (string c in SplitCells(_l[_i]))
            {
                bool l = c.StartsWith(':'), r = c.EndsWith(':');
                aligns.Add(l && r ? 0 : r ? 1 : -1);
            }
            _i++;

            rows.Add(head);
            while (_i < _l.Length && _l[_i].Contains('|') && _l[_i].Trim().Length > 0)
            {
                rows.Add(SplitRow(_l[_i]));
                _i++;
            }

            // 各行列数补齐到表头那么宽：少一列时后面几列整个错位，
            // 而错位在屏幕上看着只像是「模型这张表写歪了」。
            int n = Math.Max(aligns.Count, head.Count);
            while (aligns.Count < n) aligns.Add(-1);
            foreach (var r in rows)
                while (r.Count < n) r.Add(new List<Inline>());

            return new BTable { Rows = rows, Aligns = aligns };
        }

        private static List<List<Inline>> SplitRow(string line)
        {
            var cells = new List<List<Inline>>();
            foreach (string c in SplitCells(line)) cells.Add(ParseInline(c));
            return cells;
        }

        /// <summary>
        /// 把一行表格切成**原始文本**单元格。分隔行（<c>:--:</c>）的对齐方式只有在这一层
        /// 才看得出来 —— 到了行内那一层，那些 `:` 已经和别的字符一样只是文字了。
        ///
        /// 转义的竖线不是分隔符。**先占位再切**：反过来的话 `a \| b` 已经被切成两格了，
        /// 再还原只会把竖线拼回某一格里，格数已经错了。占位符取 U+0001 而不是某个可见字符：
        /// 它不可能出现在模型吐出来的文本里，不必再去处理「原文本来就有这个字符」。
        /// </summary>
        private static List<string> SplitCells(string line)
        {
            const char Mark = (char)1;
            string s = line.Trim();
            if (s.StartsWith('|')) s = s[1..];
            if (s.EndsWith('|')) s = s[..^1];
            s = s.Replace(((char)92).ToString() + "|", Mark.ToString());
            var cells = new List<string>();
            foreach (string c in s.Split('|'))
                cells.Add(c.Trim().Replace(Mark, '|'));
            return cells;
        }
    }

    // ---------------- 折行 ----------------

    /// <summary>
    /// 折行的最小单位。切到原子这一层是为了让「哪里能断」成为一个**事先算好**的属性，
    /// 而不是在折行循环里边走边猜 —— 边猜的那个写法在遇到
    /// 「放不下但又不许断」的组合时只能二选一：要么溢出，要么把英文单词劈成两半。
    /// </summary>
    private struct Atom
    {
        public string Text;
        public Font Font;
        public Ink Ink;
        public bool Underline, Strike;
        public float PadL, PadR;
        public bool Space;
        public bool BreakBefore;
        public bool ForceBreak;
        public float Width;

        /// <summary>公式。非空时 <see cref="Text"/> 是空串，<see cref="Width"/> 取盒子宽。</summary>
        public MBox? Math;

        /// <summary>链接地址。一个词折成几个原子都带同一份，落到每个 <see cref="Run"/> 上。</summary>
        public string? Link;

        public readonly float Advance => Width + PadL + PadR;
    }

    private sealed class LineBuf
    {
        public readonly List<Run> Runs = new();
        public float W, H, Pitch;

        /// <summary>本行的基线（行顶到基线）。见 <see cref="PhysLine.Base"/>。</summary>
        public float Base;

        /// <summary>文字往下跟多少。见 <see cref="PhysLine.TextShift"/>。</summary>
        public float TextShift;
    }

    private static bool IsCjk(char c) =>
        (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF);

    /// <summary>行首不能出现的字（避头尾）。断在它前面的话，标点会孤零零地跑到下一行开头。</summary>
    private static bool NoBreakBefore(string s) =>
        s.Length > 0 && "。，、；：？！）〕］｝〉》」』】”’·～%,.!?:;)]}".IndexOf(s[0]) >= 0;

    /// <summary>行尾不能出现的字（避头尾的另一半）：开括号留在行尾同样难看。</summary>
    private static bool NoBreakAfter(string s) =>
        s.Length > 0 && "（〔［｛〈《「『【“‘([{".IndexOf(s[^1]) >= 0;

    /// <summary>
    /// 把行内片段切成原子。切法只有三条：
    /// 空白各自成段、CJK（含全角标点）**一字一段**、其余连续的西文算一个词。
    ///
    /// 中文必须一字一段，否则「没有空格的一整段中文」会被当成一个不可断的巨词，
    /// 只能硬切 —— 硬切是按固定字数均分的，断点会落在词中间。
    /// </summary>
    private static List<Atom> AtomsOf(List<Inline> inlines, float size, FontStyle baseStyle, Ink ink, float avail)
    {
        var atoms = new List<Atom>();

        foreach (var inl in inlines)
        {
            if (inl.HardBreak)
            {
                atoms.Add(new Atom { Text = "", Width = 0, ForceBreak = true });
                continue;
            }

            // 公式：整个盒子就是一个原子 —— 它内部一律不许断（断了分数线就从中间劈开），
            // 所以 Text 留空。空 Text 同时让下面那个「单个原子就超宽就按字拆开」的循环
            // 直接跳过它（那里的判据是 `Text.Length <= 1`），不会把一棵语法树拆散。
            if (inl.Math != null)
            {
                var box = MathLayout.Build(inl.Math, size, avail);
                atoms.Add(new Atom { Text = "", Font = SF.Get(size), Ink = ink, Width = box.Width, Math = box });
                continue;
            }

            bool code = (inl.Flags & FCode) != 0;
            var style = baseStyle;
            if (!code)
            {
                if ((inl.Flags & FBold) != 0) style |= FontStyle.Bold;
                if ((inl.Flags & FItalic) != 0) style |= FontStyle.Italic;
            }
            var f = code ? SF.Mono(size - 1f) : SF.Get(size, style);

            var proto = new Atom
            {
                Font = f,
                Ink = code ? Ink.InlineCode
                     : (inl.Flags & FLink) != 0 ? Ink.Accent
                     : ink,
                Underline = (inl.Flags & FLink) != 0,
                Strike = (inl.Flags & FStrike) != 0,
                Link = inl.Link,
            };

            string s = inl.Text;
            int spanStart = atoms.Count;      // 行内代码这一段从这里开始（给端帽用）
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];

                if (c == ' ' || c == '\t')
                {
                    int j = i;
                    while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
                    var sp = proto;
                    sp.Text = s[i..j];
                    sp.Space = true;
                    sp.Width = RunWidth(sp.Text, f);
                    atoms.Add(sp);
                    i = j;
                    continue;
                }

                // 代理对不能拆开：拆了会变成两个孤立的半字，屏幕上就是两个方块
                int len = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                if (IsCjk(c) || len == 2)
                {
                    var cp = proto;
                    cp.Text = s.Substring(i, len);
                    // 单字走缓存：中文逐字成原子，逐字 MeasureText 一条上万字的回复就是上万次
                    // GDI 调用；缓存命中后这一步只剩一次字典查找。
                    cp.Width = len == 1 ? CharWidth(c, f) : RunWidth(cp.Text, f);
                    atoms.Add(cp);
                    i += len;
                    continue;
                }

                int k = i;
                while (k < s.Length && !IsCjk(s[k]) && s[k] != ' ' && s[k] != '\t') k++;
                var wp = proto;
                wp.Text = s[i..k];
                wp.Width = RunWidth(wp.Text, f);
                atoms.Add(wp);
                i = k;
            }

            // 行内代码的左右垫（圆角底的端帽）只加在**整段的两端**，不是每个原子各垫一份 ——
            // 一个 `` `a = b` `` 会拆成「a / 空格 / = / 空格 / b」五个原子，每个都垫的话
            // 每个空格两侧各多出 7px 的底，行内代码里的空格看上去就是断开的（用户报的
            // 「空格前后不连续」就是这么来的）。只垫两端：中间的原子紧紧挨着，底色连成一整条。
            if (code && atoms.Count > spanStart)
            {
                var first = atoms[spanStart]; first.PadL = InlineCodePadX; atoms[spanStart] = first;
                var lastA = atoms[^1]; lastA.PadR = InlineCodePadX; atoms[^1] = lastA;
            }
        }

        // 断点：空白之后可断；CJK 挨着任何东西都可断；西文词之间无空格则不可断。
        //
        // <see cref="Atom"/> 是 struct，`atoms[n].BreakBefore = x` 会报 CS1612
        // （取值返回的是副本，改副本没有任何意义）—— 必须取出来改完再放回去。
        for (int n = 1; n < atoms.Count; n++)
        {
            var prev = atoms[n - 1];
            var cur = atoms[n];
            if (cur.ForceBreak) continue;
            if (prev.ForceBreak) continue;
            if (cur.Space) continue;

            // 公式的两侧永远可断。它是个**独立物件**（和行内图片一样），
            // 前后连着汉字时那条「挨着 CJK 才可断」的规则本来也成立，但公式后面紧跟西文词
            // （`$x$值`、`$\alpha$coefficient`）时就不成立了 —— 那种情况下整行会被推下去。
            if (cur.Math != null || prev.Math != null) { cur.BreakBefore = true; atoms[n] = cur; continue; }

            // prev / cur 都可能是空串（公式原子、或者被吃掉的分隔），
            // 直接取 Text[0] 会抛 —— 这在旧版是个够不着的分支，加了公式就够得着了。
            if (prev.Text.Length == 0 || cur.Text.Length == 0) continue;

            // prev 是空白、或者两个词之间本来就挨着可断的边界，才算一个断点
            bool brk = prev.Space
                       || (!NoBreakAfter(prev.Text) && !NoBreakBefore(cur.Text)
                           && (IsCjk(prev.Text[0]) || IsCjk(cur.Text[0])));
            cur.BreakBefore = brk;
            atoms[n] = cur;
        }
        return atoms;
    }

    /// <summary>
    /// 折行。用「记住最近一个可断点」的贪心算法，一次扫描：
    /// 走到放不下时退回到最近那个可断点，而不是当场硬切 ——
    /// 当场硬切是「先把词写进去再看超没超」，断点会落在词的中间。
    /// </summary>
    private static List<LineBuf> WrapAtoms(List<Atom> atoms, float avail, float pitchK, out bool wrapped)
    {
        wrapped = false;
        var lines = new List<LineBuf>();
        if (atoms.Count == 0) return lines;

        avail = Math.Max(24f, avail);

        // 单个原子就超宽（长 URL、没有空格的长代码）：先按字拆开。
        // 不拆的话下面那条「放不下就退回上一个可断点」的规则对它完全无效 ——
        // 它前后都没有可断点，只能整行溢出到气泡外面去。
        for (int n = 0; n < atoms.Count; n++)
        {
            var a = atoms[n];
            if (a.Space || a.ForceBreak || a.Text.Length <= 1 || a.Advance <= avail) continue;
            atoms.RemoveAt(n);
            int at = n;
            for (int k = 0; k < a.Text.Length; k++)
            {
                char c = a.Text[k];
                int len = char.IsHighSurrogate(c) && k + 1 < a.Text.Length && char.IsLowSurrogate(a.Text[k + 1]) ? 2 : 1;
                var piece = a;
                piece.Text = a.Text.Substring(k, len);
                piece.Width = len == 1 ? CharWidth(c, a.Font) : RunWidth(piece.Text, a.Font);
                // 端帽跟着端走：劈开之后左垫留在第一片、右垫留到最后一片，中间的片不垫。
                piece.PadL = k == 0 ? a.PadL : 0;
                piece.PadR = k + len >= a.Text.Length ? a.PadR : 0;
                piece.BreakBefore = at > 0;
                atoms.Insert(at++, piece);
                if (len == 2) k++;
            }
            n--;
        }

        var ranges = new List<(int from, int to)>();
        int from = 0, i = 0, lastBreak = -1;
        float w = 0;

        while (i < atoms.Count)
        {
            var a = atoms[i];

            if (a.ForceBreak)
            {
                if (i > from) ranges.Add((from, i));
                from = i + 1;
                lastBreak = -1;
                w = 0;
                i++;
                continue;
            }

            if (w + a.Advance > avail && lastBreak > from)
            {
                ranges.Add((from, lastBreak));
                wrapped = true;
                from = lastBreak;
                while (from < i && atoms[from].Space) from++;     // 行首的空白丢掉
                w = 0;
                for (int k = from; k < i; k++) w += atoms[k].Advance;
                lastBreak = -1;
                for (int k = from + 1; k < i; k++) if (atoms[k].BreakBefore) lastBreak = k;
                continue;                                          // 不推进 i，重新判当前这个原子
            }

            w += a.Advance;
            if (i + 1 < atoms.Count && atoms[i + 1].BreakBefore) lastBreak = i + 1;
            i++;
        }
        if (from < atoms.Count) ranges.Add((from, atoms.Count));

        var blank = SF.Get(BodySize);
        foreach (var (f0, t0) in ranges)
        {
            int end = t0;
            while (end > f0 && atoms[end - 1].Space) end--;        // 行尾的空白也丢掉

            var lb = new LineBuf();
            Font? tall = null;                 // 决定本行高度的那个字体（公式要靠它算基线）
            float lead = 0, mdep = 0;          // 公式伸到基线上方 / 下方的最大值
            for (int k = f0; k < end; k++)
            {
                var a = atoms[k];

                if (a.Math != null)
                {
                    lb.Runs.Add(new Run { Math = a.Math, Width = a.Math.Width, Ink = a.Ink });
                    lb.W += a.Math.Width;
                    lead = Math.Max(lead, a.Math.Height);
                    mdep = Math.Max(mdep, a.Math.Depth);
                    continue;
                }

                if (a.Text.Length == 0) continue;

                // 同行内字体 / 颜色 / 样式完全一致且首尾相接的相邻原子**并成一个 Run**。
                // 中文是一字一原子的，不并的话一行正文就是几十次 DrawText（每次都有不可省的
                // 固定开销：选字体、ExtTextOut）—— 一份 4200 字的回复曾因此排成 3075 个 Run。
                // 并完之后一行只剩「逐样式段」那么多个。宽度照旧按原子累加（lb.W 不变），
                // 只是少建对象、少画几十次。
                // 两端带垫的（行内代码的端帽）不并：并了垫就跑到 Run 中间去了。而**段内**的
                // 相邻原子两端都不带垫，照样并成一个 —— 所以一段行内代码折进一行后仍是一个
                // Run，端帽由最后一个原子的 PadR 在并入时带过来。
                var last = lb.Runs.Count > 0 ? lb.Runs[^1] : null;
                if (last != null && last.Math == null && last.X < 0 && last.PadR == 0 && a.PadL == 0
                    && ReferenceEquals(last.Font, a.Font) && last.Ink == a.Ink
                    && last.Underline == a.Underline && last.Strike == a.Strike && last.Link == a.Link)
                {
                    last.Text += a.Text;
                    last.Width += a.Width;
                    last.PadR = a.PadR;
                }
                else
                {
                    lb.Runs.Add(new Run
                    {
                        Text = a.Text,
                        Font = a.Font,
                        Ink = a.Ink,
                        Width = a.Width,
                        PadL = a.PadL,
                        PadR = a.PadR,
                        Underline = a.Underline,
                        Strike = a.Strike,
                        Link = a.Link,
                    });
                }
                lb.W += a.Advance;
                if (a.Font.Height > lb.H) { lb.H = a.Font.Height; tall = a.Font; }
            }

            // 空行占位。**只有一行字都没有的行才需要** —— 一行里只有公式时 lb.H 还是 0，
            // 但它明明有内容，补一个空格进去会多出一条空白的高度。
            if (lb.H <= 0 && lead <= 0) { lb.Runs.Add(new Run { Text = " ", Font = blank, Width = 0 }); lb.H = blank.Height; }

            // 带公式的行：先定基线，再让公式决定行高。
            //
            //   基线 = 正文字体自己的上缘高度（公式和文字**共用这一条**）
            //   公式比正文还高时，把整行往下推（公式顶伸出去的那部分），而不是把公式往下挪
            // 公式往下伸的部分（分数线、分母）再把行往下撑。全程只用**一个**数（Base）
            // 把两套坐标接起来；文字那边跟着 TextShift 走，没有公式的行两者都是 0。
            if (lead > 0)
            {
                float textH = lb.H;
                float baseA = MathLayout.Ascent(tall ?? blank);
                float lift = Math.Max(0f, lead - baseA);
                lb.Base = baseA + lift;
                lb.TextShift = lift;
                lb.H = Math.Max(textH + lift, lb.Base + mdep);
            }

            lb.Pitch = Math.Max(1f, MathF.Round(lb.H * pitchK));
            lines.Add(lb);
        }
        return lines;
    }

    // ---------------- 排版 ----------------

    private sealed class Ctx
    {
        public readonly List<PhysLine> Lines = new();
        public float Y;

        /// <summary>
        /// 当前容器**开始之前**已经有几行。块与块之间的间距只看「本容器里前面有没有块」，
        /// 不看全局行数 —— 不然引用块 / 列表项里的第一段会莫名其妙地多出一段段前距，
        /// 而那一段是给「和上一个兄弟块分开」用的，容器顶上没有兄弟。
        /// </summary>
        public int Base;

        public bool Wrapped;
        public bool FullWidth;

        /// <summary>
        /// 当前身处第几层列表（最外层是 0，进去一层 +1）。只用来给无序项换符号：
        /// 嵌套层级在块树里没有存，解析器是按递归建的 `BList`，层级只有排到这里才知道。
        /// </summary>
        public int ListDepth;

        /// <summary>
        /// 上一个兄弟块**欠在下面**的留白：带底色的面板（代码块 / 引用块）从首行内容原点
        /// 往回退一圈内边距去画，那圈内边距就压在「本块底 → 下一块顶」这段间距上。
        /// 只补在自己头顶是不够的（那样后面跟的是段落或表格时照样只剩 1px），
        /// 所以由面板把这段记下来，交给**紧挨着的下一个块**去躲。
        /// </summary>
        public float PadBelow;

        /// <summary>本次排的这个代码块是不是收起的（块级缓存一次只排一个块，所以是单个 bool）。</summary>
        public bool CodeFolded;

        /// <summary>排代码块时登记的头部（块局部坐标，装配时加上文档内的 y 再进 <see cref="Layout.CodeHeads"/>）。</summary>
        public CodeHead? Head;

        /// <summary>
        /// 本块的**名义块首距**：排版这一块时第一次「要不要段前距」问到的那个值。
        /// 块级排版缓存（<see cref="BlockLayoutOf"/>）把每个块排成「不含块首距」的行，
        /// 装配时才按「是不是容器里第一块、上一块欠多少下内边距」把这一段加回首行 ——
        /// 那个值就得有地方读。-1 = 这一块从里到外没问过（空块）。
        /// </summary>
        public float LeadSpace = -1f;

        /// <summary>
        /// 在容器 / 整段的开头时不加段前距。**必须在排版这一块之前先取好**。
        /// 顺带把上一块欠的下内边距**消费掉**：每个块只在开头问这一次，
        /// 谁问谁拿走，免得一路漏到后面不该躲的块头上。
        /// </summary>
        public float Sp(float space)
        {
            float below = PadBelow;
            PadBelow = 0f;
            if (LeadSpace < 0f) LeadSpace = space;   // 每个块只在开头问一次（见 Build 的装配）
            return Lines.Count > Base ? space + below : 0f;
        }
    }

    /// <summary>段前距 / 内边距。**必须落在某一行身上**，见类注释第三条。</summary>
    private static void Pad(Ctx c, int i0, float space)
    {
        if (space <= 0 || i0 >= c.Lines.Count) return;
        c.Lines[i0].SpaceBefore += space;
        c.Y += space;
    }

    private static void EmitLine(Ctx c, List<Run> runs, float indent, float pitch, float lineH,
                                 float baseY = 0f, float textShift = 0f)
    {
        var pl = new PhysLine
        {
            Indent = indent,
            Pitch = pitch,
            LineHeight = lineH,
            Base = baseY,
            TextShift = textShift,
        };
        pl.Runs.AddRange(runs);
        c.Lines.Add(pl);
        c.Y += pl.Pitch;
    }

    private static void EmitLines(Ctx c, List<LineBuf> bufs, float indent, float spaceBefore)
    {
        int i0 = c.Lines.Count;
        foreach (var lb in bufs) EmitLine(c, lb.Runs, indent, lb.Pitch, lb.H, lb.Base, lb.TextShift);
        Pad(c, i0, spaceBefore);
    }

    /// <summary>把一段行内内容折成若干物理行（不带任何块级装饰）。</summary>
    private static List<LineBuf> Flow(List<Inline> inlines, float size, FontStyle style, Ink ink,
                                      float avail, float pitchK, Ctx c)
    {
        var bufs = WrapAtoms(AtomsOf(inlines, size, style, ink, avail), avail, pitchK, out bool wrapped);
        if (wrapped) c.Wrapped = true;
        return bufs;
    }

    private static void EmitBlocks(Ctx c, List<Block> blocks, float left, float avail)
    {
        foreach (var b in blocks) EmitBlock(c, b, left, avail);

        // 容器到底了：里面最后一块欠的下内边距不外泄给容器的下一个兄弟 ——
        // 那圈内边距是「容器内部和外部的分隔」，已经由容器自己的边框 / 间距表达了。
        c.PadBelow = 0f;
    }

    /// <summary>
    /// 排一个块。除了 <see cref="EmitBlocks"/> 的逐个直排，块级排版缓存
    /// （<see cref="BlockLayoutOf"/>）一次只排一个块时也从这里进 —— 两条路必须共用
    /// 同一个分派，不然缓存里那份和直排出来的会长得不一样。
    /// </summary>
    private static void EmitBlock(Ctx c, Block b, float left, float avail)
    {
        switch (b)
        {
            case BHead h: EmitHead(c, h, left, avail); break;
            case BPara p:
                EmitLines(c, Flow(p.Inlines, BodySize, FontStyle.Regular, Ink.Text, avail, PitchBody, c),
                          left, c.Sp(SpacePara));
                break;
            case BCode k: EmitCode(c, k, left, avail); break;
            case BList l: EmitList(c, l, left, avail); break;
            case BQuote q: EmitQuote(c, q, left, avail); break;
            case BTable t: EmitTable(c, t, left, avail); break;
            case BRule: EmitRule(c, left, avail); break;
            case BMath mm: EmitMath(c, mm, left, avail); break;
        }
    }

    private static float HeadSize(int lv) => lv switch
    {
        1 => 20f, 2 => 17f, 3 => 15f, 4 => 13.5f, _ => 12.5f,
    };

    private static float HeadSpace(int lv) => lv switch
    {
        1 => 17f, 2 => 15f, 3 => 13f, 4 => 11f, _ => 10f,
    };

    /// <summary>
    /// 标题。一级 / 二级在下面压一条细线 —— 这正是「好看」和「只是更大号的粗体」的分界：
    /// 光靠字号，h1 和 h2 在长文里几乎分不出来；有了一条线，扫一眼就知道这是新的一节。
    /// </summary>
    private static void EmitHead(Ctx c, BHead h, float left, float avail)
    {
        var bufs = Flow(h.Inlines, HeadSize(h.Level), FontStyle.Bold,
                        h.Level >= 6 ? Ink.Muted : Ink.Text, avail, PitchHead, c);
        EmitLines(c, bufs, left, c.Sp(HeadSpace(h.Level)));

        if (h.Level > 2 || c.Lines.Count == 0) return;

        // 线画在行尾下面 4px 处，所以这一行的 Pitch 得先让出「间隙 + 1px 线 + 呼吸」三截，
        // 否则线会压到下一块的第一行字上（类注释第三条：多出来的高度记在行上）。
        var last = c.Lines[^1];
        float basePitch = last.Pitch;
        last.Pitch = basePitch + HeadUnderGap + 1f + HeadUnderTail;
        c.Y += last.Pitch - basePitch;
        last.Decors.Add(new Decor
        {
            Rect = new RectangleF(left, basePitch + HeadUnderGap, avail, 1f),
            Fill = Ink.Rule,
        });
    }

    /// <summary>
    /// 独占一行的公式：**居中**、整行只有它一个 Run。
    ///
    /// 位置走 <c>Run.X ≥ 0</c> 那条绝对定位的路，而不是靠缩进 —— 缩进是「行从哪儿开始」，
    /// 而这里要知道的是「这行总共多宽、公式摆在多宽里的正中」，居中是相对于**可用宽**算的。
    ///
    /// 字号比正文大一档：行间公式就该比正文醒目，这是 TeX 的 display style 唯一的视觉含义。
    /// </summary>
    private static void EmitMath(Ctx c, BMath bm, float left, float avail)
    {
        c.FullWidth = true;

        if (bm.Node == null)
        {
            // 解析不出来：按普通文字原样排出来。**不能整块丢掉** —— 丢掉的后果是用户
            // 完全不知道模型写了什么，而原样显示至少能让他看出「这里有个公式没渲染出来」。
            EmitLines(c, Flow(ParseInline(bm.Raw), BodySize, FontStyle.Regular, Ink.Muted, avail, PitchBody, c),
                      left, c.Sp(SpaceMath));
            return;
        }

        var box = MathLayout.Build(bm.Node, BodySize * 1.18f, Math.Max(40f, avail - 8f));
        float pad = 7f;
        float sp = c.Sp(SpaceMath);                 // 必须在建行之前消费掉（见 Ctx.Sp）

        int i0 = c.Lines.Count;
        var pl = new PhysLine
        {
            Indent = 0,
            Base = box.Height,
            LineHeight = box.Height + box.Depth + pad * 2f,
            Pitch = box.Height + box.Depth + pad * 2f,
        };
        pl.Runs.Add(new Run { Math = box, Width = box.Width, X = Math.Max(0f, (avail - box.Width) / 2f) });
        c.Lines.Add(pl);
        c.Y += pl.Pitch;
        Pad(c, i0, sp);
    }

    /// <summary>分隔线。整条占满可用宽，所以它同时也把气泡撑到满宽。</summary>
    private static void EmitRule(Ctx c, float left, float avail)
    {
        c.FullWidth = true;
        int i0 = c.Lines.Count;
        float sp = c.Sp(SpaceRule);
        EmitLine(c, new List<Run>(), left, 9f, 9f);
        c.Lines[i0].Decors.Add(new Decor { Rect = new RectangleF(left, 4f, avail, 1f), Fill = Ink.Rule });
        Pad(c, i0, sp);
    }

    /// <summary>
    /// 代码块：圆角底 + 头部行（**左**语言标签、**右**复制 / 收展按钮）+ 四类语法着色。
    ///
    /// 头部行**永远在**（没有语言的块也一样）—— 复制 / 收展按钮得有个家。
    /// 按钮的图形与点击不归排版管：这里只把头部矩形登记到 <see cref="Ctx.Head"/>，
    /// 画图标与命中测试是 <c>MessageBubble</c> 的事。
    ///
    /// 超宽的行**硬折行**而不是横向滚动 —— 气泡里塞不下一个横向滚动条，
    /// 而「看不见的那半行」比「折下来的那半行」糟得多。
    /// </summary>
    private static void EmitCode(Ctx c, BCode bc, float left, float avail)
    {
        c.FullWidth = true;      // 代码块本来就该占满宽，不必等折行
        c.Wrapped = true;

        var f = SF.Mono(CodeSize);
        float textAvail = Math.Max(40f, avail - CodePadX * 2);
        float pitch = Math.Max(1f, MathF.Round(f.Height * PitchCode));

        var local = new List<PhysLine>();

        var head = new PhysLine { Indent = left + CodePadX, Pitch = CodeHeadH, LineHeight = CodeHeadH };
        if (bc.Lang.Length > 0)
        {
            var lf = SF.Get(9f);
            float lw = RunWidth(bc.Lang, lf);
            // 9pt 的标签在 24px 的行高里居中：TextShift 把文字往下推半段差值。
            // 面板底色的矩形要把它**减回去**（装饰原点跟着 TextShift 走，见 DrawBack）。
            head.TextShift = Math.Max(0f, (CodeHeadH - lf.Height) / 2f);
            head.Runs.Add(new Run { Text = bc.Lang, Font = lf, Ink = Ink.Muted, Width = lw });
        }
        local.Add(head);
        float headShift = head.TextShift;

        // 收起的块到此为止：头部就是整块内容，面板收成一条。
        if (!c.CodeFolded)
        {
            var carry = default(MarkdownSyntax.Carry);
            foreach (string raw in bc.Lines)
            {
                var runs = new List<Run>();
                foreach (var piece in MarkdownSyntax.Highlight(bc.Lang, raw, ref carry))
                {
                    var ink = piece.Kind switch
                    {
                        MarkdownSyntax.Kind.Keyword => Ink.SynKeyword,
                        MarkdownSyntax.Kind.Str => Ink.SynString,
                        MarkdownSyntax.Kind.Comment => Ink.SynComment,
                        MarkdownSyntax.Kind.Number => Ink.SynNumber,
                        _ => Ink.Text,
                    };
                    string txt = raw.Substring(piece.Start, piece.Length);
                    runs.Add(new Run { Text = txt, Font = f, Ink = ink, Width = RunWidth(txt, f) });
                }

                foreach (var chunk in HardWrap(runs, textAvail))
                {
                    var pl = new PhysLine { Indent = left + CodePadX, Pitch = pitch, LineHeight = f.Height };
                    pl.Runs.AddRange(chunk);
                    local.Add(pl);
                }
            }
        }

        float inner = 0;
        foreach (var pl in local) inner += pl.Pitch;

        // 整块底画在**第一行**（头部行）上，高度盖住后面的所有行。行是顺序画的，
        // 后面几行只画文字，不会把这层底盖掉；反过来把底挂在每一行上，
        // 行与行之间就会露出一条条缝。
        // 矩形往回退 CodePadY：那圈内边距在第一行的**上面**，不属于任何一行。
        // 头部行的 TextShift（标签居中用的那几像素）不参与定位，要减回去。
        local[0].Decors.Insert(0, new Decor
        {
            Rect = new RectangleF(left, -CodePadY - headShift, avail, inner + CodePadY * 2),
            Fill = Ink.CodePanel,
            Radius = 8,
        });

        // 登记头部：块局部坐标（首行顶 = 0），装配时再加上文档内的 y（见 Build）。
        c.Head = new CodeHead
        {
            Rect = new RectangleF(left, 0, avail, CodeHeadH),
            Lang = bc.Lang,
            Code = string.Join("\n", bc.Lines),
            Folded = c.CodeFolded,
        };

        int i0 = c.Lines.Count;
        // 段前距要连**自己的内边距**一起要：面板是从首行内容原点往上退 CodePadY 画的
        // （见上一条注释），那 9px 会吃掉下面这里的段前距 —— 只写 SpaceCode 的话，
        // 两个紧挨着的代码块之间只剩 10-9-9 = **负数**，两块面板直接叠在一起变成一整条灰带子。
        float sp = c.Sp(SpaceCode + CodePadY);
        foreach (var pl in local) { c.Lines.Add(pl); c.Y += pl.Pitch; }
        Pad(c, i0, sp);
        c.PadBelow = CodePadY;      // 面板下缘那圈内边距要由下一块来躲，见 Ctx.PadBelow
    }

    /// <summary>把一串 Run 按可用宽硬切开。等宽字体没有字距调整，逐字累加就是准确宽度。</summary>
    private static List<List<Run>> HardWrap(List<Run> runs, float avail)
    {
        var outp = new List<List<Run>>();
        var cur = new List<Run>();
        float w = 0;

        foreach (var r in runs)
        {
            int i = 0;
            while (i < r.Text.Length)
            {
                int j = i;
                float ww = 0;
                while (j < r.Text.Length)
                {
                    float cw = CharWidth(r.Text[j], r.Font);
                    if (w + ww + cw > avail && (w > 0 || ww > 0)) break;
                    ww += cw;
                    j++;
                }
                if (j == i) { j = i + 1; ww = CharWidth(r.Text[i], r.Font); }   // 一个字都放不下也塞进去

                string sub = r.Text[i..j];
                cur.Add(new Run { Text = sub, Font = r.Font, Ink = r.Ink, Width = RunWidth(sub, r.Font) });
                w += ww;
                i = j;
                if (i < r.Text.Length) { outp.Add(cur); cur = new List<Run>(); w = 0; }
            }
        }
        outp.Add(cur);
        return outp;
    }

    /// <summary>列表项的记号文字（任务项没有记号文字，它的记号是小方框）。</summary>
    private static string MarkerText(BList l, int n, string bullet) =>
        l.Ordered ? (l.Start + n) + "." : bullet;

    /// <summary>任务列表那个小方框的边长。</summary>
    private const float TaskBoxW = 12f;

    /// <summary>
    /// 列表。项目符号在**第一行**上绝对定位，正文整体悬挂缩进 ——
    /// 第二行对齐的是文字的左缘而不是符号，否则折行之后缩进会一截一截往里跑。
    /// </summary>
    private static void EmitList(Ctx c, BList l, float left, float avail)
    {
        var f = SF.Get(BodySize);
        string bullet = BulletGlyph(c.ListDepth);

        // 先量一遍所有符号，取最宽的那个当悬挂宽度：`1.` 和 `10.` 宽度不同，
        // 逐个算的话同一张列表里每一行的文字起点都不一样。任务项的记号是那个小方框。
        float markW = 0;
        for (int n = 0; n < l.Items.Count; n++)
            markW = Math.Max(markW, l.Tasks != null && l.Tasks[n] > 0 ? TaskBoxW : RunWidth(MarkerText(l, n, bullet), f));
        float hang = markW + 8f;

        int i0 = c.Lines.Count;
        float sp = c.Sp(SpacePara);
        int outerBase = c.Base;
        int outerDepth = c.ListDepth;
        c.ListDepth = outerDepth + 1;     // 项里再出现的列表就是下一层，换一个符号

        for (int n = 0; n < l.Items.Count; n++)
        {
            int before = c.Lines.Count;
            c.Base = before;      // 项里的第一块不加段前距（那是「和上一个兄弟分开」用的）
            EmitBlocks(c, l.Items[n], left + hang, Math.Max(40f, avail - hang));
            c.Base = outerBase;

            if (c.Lines.Count == before)     // 空项：至少留一行，否则那个符号无处可挂
                EmitLine(c, new List<Run>(), left + hang, f.Height, f.Height);

            if (l.Tasks != null && l.Tasks[n] > 0)
            {
                // 任务项：记号是一个小方框（完成 = 实心强调色，未完成 = 描边），挂在该项
                // 第一行上、垂直跟文字行居中。用画出来的方框而不是 ☐/☑ 字符 —— 那两个字符
                // 在微软雅黑里的字形和基线都不稳，方框是自己画的，多大、在哪全是定数。
                var ln0 = c.Lines[before];
                float boxY = ln0.LineHeight > 0 ? (ln0.LineHeight - TaskBoxW) / 2f + 1f : 1f;
                bool done = l.Tasks[n] == 2;
                ln0.Decors.Add(new Decor
                {
                    Rect = new RectangleF(left + 1f, boxY, TaskBoxW, TaskBoxW),
                    Fill = done ? Ink.Accent : Ink.None,
                    Stroke = done ? Ink.None : Ink.Muted,
                    StrokeW = 1.2f,
                    Radius = 3f,
                });
            }
            else
            {
                string mk = MarkerText(l, n, bullet);
                c.Lines[before].Runs.Insert(0, new Run
                {
                    Text = mk,
                    Font = f,
                    Ink = l.Ordered ? Ink.Muted : Ink.Accent,
                    Width = RunWidth(mk, f),
                    X = left,
                });
            }
            if (n > 0) Pad(c, before, SpaceItem);
        }
        c.ListDepth = outerDepth;
        Pad(c, i0, sp);
    }

    /// <summary>
    /// 无序列表的符号按**嵌套层级**轮换（实心 / 空心 / 方块），和 GitHub 一样。
    /// 全用同一个圆点的话，缩进之后看不出这一项到底是哪一层的。
    /// </summary>
    private static string BulletGlyph(int depth) => (depth % 3) switch
    {
        0 => "▪",
        2 => "◦",
        _ => "•",
    };

    /// <summary>
    /// 引用块：左侧一道竖线 + 稍暗的底。内容递归解析，所以引用里能放列表、代码块、表格。
    ///
    /// 底色的宽度按**内容实际用了多宽**算，不是照满宽画 —— 满宽的话一句
    /// <c>&gt; 好</c> 会拖出一条横贯整个气泡的灰带子。
    /// </summary>
    private static void EmitQuote(Ctx c, BQuote q, float left, float avail)
    {
        float innerLeft = left + QuoteBarW + QuoteGap;
        float innerAvail = Math.Max(40f, avail - QuoteBarW - QuoteGap);

        int i0 = c.Lines.Count;
        float yStart = c.Y;

        // 段前距要在**进容器之前**取：Sp 判的是「本容器里前面有没有块」，等内容排完再问，
        // 连「它是容器里第一块」都会被刚排出来的内容顶成「前面有块」—— 于是引用块在
        // 容器顶上时也会凭空多出一段（别的块型都不是这样，见 Ctx.Base 的注释）。
        // 段前距里要连自己的 QuotePadY 一起要 —— 面板是从首行内容原点往上退 QuotePadY
        // 画的，那 7px 会吃掉段前距，只写 SpaceQuote 的话两块面板会贴在一起。
        float sp = c.Sp(SpaceQuote + QuotePadY);
        int outerBase = c.Base;
        c.Base = i0;
        EmitBlocks(c, q.Blocks, innerLeft, innerAvail);
        c.Base = outerBase;
        if (c.Lines.Count == i0) return;
        float contentH = c.Y - yStart;

        // 内边距同样落在行身上（类注释第三条）：首行往上加一圈、末行往下加一圈。
        c.Lines[i0].SpaceBefore += QuotePadY;
        c.Y += QuotePadY;
        c.Lines[^1].Pitch += QuotePadY;
        c.Y += QuotePadY;
        Pad(c, i0, sp);

        float contentExtent = 0;
        for (int n = i0; n < c.Lines.Count; n++) contentExtent = Math.Max(contentExtent, ExtentOf(c.Lines[n]));
        float panelW = Math.Max(70f, Math.Min(avail, contentExtent - left));
        float panelH = QuotePadY + contentH + QuotePadY;

        // 面板从「第一行的顶**再往上** QuotePadY」开始 —— 那 7px 是内边距，
        // 已经记在首行的 SpaceBefore 里，所以矩形要往回退。
        var first = c.Lines[i0];
        first.Decors.Insert(0, new Decor
        {
            Rect = new RectangleF(left + QuoteBarW, -QuotePadY, Math.Max(0f, panelW - QuoteBarW), panelH),
            Fill = Ink.QuotePanel,
            Radius = 6,
        });
        first.Decors.Insert(1, new Decor
        {
            Rect = new RectangleF(left, -QuotePadY, QuoteBarW, panelH),
            Fill = Ink.QuoteBar,
            Radius = 1.5f,
        });

        c.PadBelow = QuotePadY;     // 竖线面板下缘那圈内边距要由下一块来躲，见 Ctx.PadBelow
    }

    /// <summary>一行里文字用到的最右缘（相对于内容原点）。</summary>
    private static float ExtentOf(PhysLine pl)
    {
        float mx = 0, runX = pl.Indent;
        foreach (var r in pl.Runs)
        {
            if (r.X >= 0) mx = Math.Max(mx, r.X + r.Advance);            // 绝对定位的不推进光标
            else { mx = Math.Max(mx, runX + r.Advance); runX += r.Advance; }
        }
        return mx;
    }

    /// <summary>
    /// 表格。列宽先按内容自适应，超宽时**按比例压缩而不是溢出** ——
    /// 溢出的表格会把右边几列整个挤出气泡，看上去像内容被切掉了。
    /// </summary>
    private static void EmitTable(Ctx c, BTable t, float left, float avail)
    {
        c.Wrapped = true;        // 表格要重新量宽度，窄了列会被压得没法读

        int n = t.Aligns.Count;
        if (n == 0 || t.Rows.Count == 0) return;

        var f = SF.Get(BodySize);
        float pitch = Math.Max(1f, MathF.Round(f.Height * PitchTable));

        var nat = new float[n];
        for (int col = 0; col < n; col++)
        {
            float m = 22f;
            for (int r = 0; r < t.Rows.Count; r++)
            {
                float w = 0;
                foreach (var a in AtomsOf(t.Rows[r][col], BodySize, r == 0 ? FontStyle.Bold : FontStyle.Regular, Ink.Text, avail))
                    w += a.Advance;
                m = Math.Max(m, w);
            }
            nat[col] = m + TablePadX * 2;
        }
        var colW = AllocCols(nat, n, avail);
        float tableW = (n - 1) * 1f;
        foreach (var w in colW) tableW += w;

        var local = new List<PhysLine>();
        var rowTop = new List<float>();
        var rowH = new List<float>();
        float ly = 0;

        for (int r = 0; r < t.Rows.Count; r++)
        {
            bool head = r == 0;
            var perCell = new List<List<LineBuf>>();
            int maxSub = 1;
            for (int col = 0; col < n; col++)
            {
                var ls = WrapAtoms(
                    AtomsOf(t.Rows[r][col], BodySize, head ? FontStyle.Bold : FontStyle.Regular, Ink.Text,
                            Math.Max(24f, colW[col] - TablePadX * 2)),
                    Math.Max(24f, colW[col] - TablePadX * 2), PitchTable, out bool wrapped);
                if (wrapped) c.Wrapped = true;
                if (ls.Count == 0)
                {
                    var blank = new LineBuf { Pitch = pitch, H = f.Height };
                    blank.Runs.Add(new Run { Text = " ", Font = f, Width = 0 });
                    ls.Add(blank);
                }
                perCell.Add(ls);
                maxSub = Math.Max(maxSub, ls.Count);
            }

            rowTop.Add(ly);
            rowH.Add(maxSub * pitch + TablePadY * 2);

            for (int k = 0; k < maxSub; k++)
            {
                // 单元格的上下内边距是**真的行距**，不是只在装饰矩形的账上记一笔：
                // 行首子行吃 TablePadY 的段前距、行尾子行把 Pitch 加长 TablePadY。
                // 不这么办的话 ly 只累计了文字行距，rowH 里的那 2×TablePadY 根本没被
                // 排版让出来 —— 表头色块于是多盖住下一行 6px、行分隔线压进上一行
                // 文字的降部里（0.9.4 之前那张「错位」的表就是这么来的）。
                var pl = new PhysLine { Pitch = pitch, LineHeight = f.Height };
                if (k == 0) pl.SpaceBefore = TablePadY;
                if (k == maxSub - 1) pl.Pitch = pitch + TablePadY;
                float x = left;
                for (int col = 0; col < n; col++)
                {
                    var ls = perCell[col];
                    if (k < ls.Count)
                    {
                        float cellAvail = colW[col] - TablePadX * 2;
                        float off = t.Aligns[col] switch
                        {
                            0 => Math.Max(0f, (cellAvail - ls[k].W) / 2f),
                            1 => Math.Max(0f, cellAvail - ls[k].W),
                            _ => 0f,
                        };
                        float runX = x + TablePadX + off;
                        foreach (var rn in ls[k].Runs)
                        {
                            rn.X = runX;
                            runX += rn.Advance;
                        }
                        pl.Runs.AddRange(ls[k].Runs);
                    }
                    x += colW[col] + 1f;
                }
                local.Add(pl);
                ly += pl.Advance;
            }
        }

        // 网格。装饰挂在第一行身上，而第一行自己的段前距就是 TablePadY（上内边距），
        // 所以装饰的 y 一律按「行槽坐标 − TablePadY」换算：行槽原点 = 第一行文字顶 − TablePadY。
        // 这样表头色块的下缘**恰好**停在第一条行分隔线上（rowTop[1] = rowH[0]），
        // 每条分隔线上下各有 6px 的真内边距，谁也压不到文字。
        float dy = -TablePadY;
        var first = local[0];
        first.Decors.Add(new Decor
        {
            Rect = new RectangleF(left, dy, tableW, rowH[0]),
            Fill = Ink.TableHead,
            // 直角：表头色块的外缘就是整张表的外框（下面四条外边框线），
            // 圆角色块配直角框线会在四个角上各缺一小块。
            Radius = 0,
        });

        // 外边框：整张表最外面一圈，与内部网格线同一个色。
        // ly 这时是整张表（含内边距）的高；表体横向占 [left, left + tableW)。
        first.Decors.Add(new Decor { Rect = new RectangleF(left, dy, tableW, 1f), Fill = Ink.TableLine });
        first.Decors.Add(new Decor { Rect = new RectangleF(left, dy + ly - 1f, tableW, 1f), Fill = Ink.TableLine });
        first.Decors.Add(new Decor { Rect = new RectangleF(left, dy + 1f, 1f, ly - 2f), Fill = Ink.TableLine });
        first.Decors.Add(new Decor { Rect = new RectangleF(left + tableW - 1f, dy + 1f, 1f, ly - 2f), Fill = Ink.TableLine });
        for (int r = 1; r < t.Rows.Count; r++)
            first.Decors.Add(new Decor
            {
                Rect = new RectangleF(left, rowTop[r] + dy, tableW, 1f),
                Fill = Ink.TableLine,
            });

        // 竖线落在单元格的内边距里，不会穿过文字 —— 这正是 TablePadX 的用处。
        // ly 这时是整张表（含内边距）的高，竖线从色块里 1px 一直画到表底上 1px。
        float bx = left;
        for (int col = 0; col < n - 1; col++)
        {
            bx += colW[col];
            first.Decors.Add(new Decor { Rect = new RectangleF(bx, dy + 1f, 1f, ly - 2f), Fill = Ink.TableLine });
            bx += 1f;
        }

        int i0 = c.Lines.Count;
        float sp = c.Sp(SpaceTable);
        foreach (var pl in local) { c.Lines.Add(pl); c.Y += pl.Advance; }
        Pad(c, i0, sp);
    }

    /// <summary>
    /// 列宽分配：先按内容，超宽时按比例削，**削到下限就不动它**，剩下的从还能削的列里再按比例拿。
    ///
    /// 一步到位地乘一个系数是不行的：一列本来就是窄的（比如「序号」），
    /// 按比例削会把它的内容压成竖排，而旁边那列还有的是余量。
    /// </summary>
    private static float[] AllocCols(float[] nat, int n, float avail)
    {
        var w = (float[])nat.Clone();
        float total = (n - 1) * 1f;
        foreach (var x in w) total += x;
        float need = total - avail;

        for (int pass = 0; pass < 6 && need > 0.5f; pass++)
        {
            float excess = 0;
            for (int i = 0; i < n; i++) excess += Math.Max(0f, w[i] - TableMinColW);
            if (excess <= 0.5f) break;

            float take = Math.Min(need, excess);
            for (int i = 0; i < n; i++)
            {
                float over = Math.Max(0f, w[i] - TableMinColW);
                if (over > 0f) w[i] -= over / excess * take;
            }
            need -= take;
        }
        return w;
    }

    // ---------------- 块级排版缓存 ----------------

    /// <summary>
    /// 一个块**与位置无关**的排版结果：不含块首距。块首距（段前距 + 上一块欠的下内边距）
    /// 取决于块在文档里的位置，装配时才加回首行；缓存回答的只是「这块内容在这个宽度下
    /// 长什么样」。
    /// </summary>
    private sealed class BlockLayout
    {
        public required List<PhysLine> Lines;
        public float Height;                    // 全部行的 Advance 之和
        public float Lead;                      // 名义块首距（Ctx.LeadSpace）
        public float PadBelow;                  // 欠给下一块的下内边距（面板型块）
        public bool Wrapped, FullWidth;

        /// <summary>代码块的头部登记（块局部坐标）。非代码块是 null。装配时复制一份加上文档内 y。</summary>
        public CodeHead? Head;
    }

    /// <summary>
    /// 块级排版缓存，键是（块的**源文**, 可用宽, 是否收起）。
    ///
    /// 流式回复每 40ms 把**全文**重排一次（MessageBubble.RefreshText），而一次流式里
    /// 变的只有**最后一个块** —— 前面所有块源文逐字相同、宽度相同，全部在这里命中。
    /// 省掉的就是「每 40ms 把一条上万字的回复从头解析、量宽、折行一遍」。
    ///
    /// 条目间共享同一个 <see cref="PhysLine"/> 实例：装配只读它们，块首距落在复制出来的
    /// 壳上（见 <see cref="Build"/>），绝不原地改。<see cref="Run"/> 同理。
    /// </summary>
    private const int BlockCacheMax = 64;
    private static readonly Dictionary<(string src, int cap, bool fold), BlockLayout> BlockCache = new();
    private static readonly Queue<(string src, int cap, bool fold)> BlockCacheOrder = new();

    private static BlockLayout BlockLayoutOf(Block b, string src, int cap, bool fold)
    {
        if (BlockCache.TryGetValue((src, cap, fold), out var hit)) return hit;

        var c = new Ctx { CodeFolded = fold };
        EmitBlock(c, b, 0, cap);
        var bl = new BlockLayout
        {
            Lines = c.Lines,
            Height = c.Y,
            Lead = Math.Max(0f, c.LeadSpace),
            PadBelow = c.PadBelow,
            Wrapped = c.Wrapped,
            FullWidth = c.FullWidth,
            Head = c.Head,
        };
        BlockCache[(src, cap, fold)] = bl;
        BlockCacheOrder.Enqueue((src, cap, fold));
        while (BlockCacheOrder.Count > BlockCacheMax) BlockCache.Remove(BlockCacheOrder.Dequeue());
        return bl;
    }

    /// <summary>
    /// 整篇排版 = 逐块排版（走缓存）+ 装配。装配做两件事：把块首距按
    /// 「是不是第一块、上一块欠多少下内边距」加回各块首行；累出总高。
    /// 顺带把代码块的头部登记从块局部坐标换算成文档坐标（<see cref="Layout.CodeHeads"/>）。
    /// </summary>
    private static Layout Build(string md, int cap, IReadOnlySet<int>? folded)
    {
        var parser = new Parser(SplitLines(md));
        var blocks = parser.Blocks();
        var srcs = parser.Sources;

        var lay = new Layout();
        float padBelow = 0f;
        bool first = true, fullWidth = false;
        int codeOrd = 0;         // 代码块的文档序：折叠状态（MessageBubble._codeFolded）按它记

        for (int i = 0; i < blocks.Count; i++)
        {
            int ord = blocks[i] is BCode ? codeOrd++ : -1;
            var bl = BlockLayoutOf(blocks[i], srcs[i], cap,
                                   ord >= 0 && folded != null && folded.Contains(ord));

            // 先消费、再判空（空块也消费）：上一块欠的下内边距属于「本块之前」这段空隙，
            // 空块不占行，这段空隙就跟它一起消掉 —— 与逐块直排时 Sp 的行为逐字一致。
            float lead = first ? 0f : bl.Lead + padBelow;
            padBelow = bl.PadBelow;
            if (bl.Lines.Count == 0) continue;

            // 块首距落在首行身上（类注释第三条）。缓存里的行是跨条目共享的，不能原地改 ——
            // 需要非零的距时复制一个壳，Runs / Decors 照样共享（绘制对它们只读）。
            var l0 = bl.Lines[0];
            if (lead > 0f)
            {
                var head = new PhysLine
                {
                    Indent = l0.Indent,
                    Pitch = l0.Pitch,
                    LineHeight = l0.LineHeight,
                    Base = l0.Base,
                    TextShift = l0.TextShift,
                    SpaceBefore = l0.SpaceBefore + lead,
                };
                head.Runs.AddRange(l0.Runs);
                head.Decors.AddRange(l0.Decors);
                lay.Lines.Add(head);
            }
            else lay.Lines.Add(l0);
            for (int j = 1; j < bl.Lines.Count; j++) lay.Lines.Add(bl.Lines[j]);

            // 头部登记**复制**一份：缓存里的那份是跨条目共享的模板，文档 y 与序号
            // 都是装配时才知道的，写回去就串到别家了。
            if (bl.Head != null)
                lay.CodeHeads.Add(new CodeHead
                {
                    Ordinal = ord,
                    Rect = new RectangleF(bl.Head.Rect.X, lay.Height + lead + bl.Head.Rect.Y,
                                          bl.Head.Rect.Width, bl.Head.Rect.Height),
                    Lang = bl.Head.Lang,
                    Code = bl.Head.Code,
                    Folded = bl.Head.Folded,
                });

            lay.Height += lead + bl.Height;
            lay.Wrapped |= bl.Wrapped;
            fullWidth |= bl.FullWidth;
            first = false;
        }

        lay.Wrapped |= fullWidth;
        if (fullWidth) lay.Width = cap;
        else
        {
            float mx = 0;
            foreach (var pl in lay.Lines) mx = Math.Max(mx, ExtentOf(pl));
            lay.Width = Math.Min(cap, Math.Max(40f, mx));
        }
        return lay;
    }

    // ---------------- 绘制 ----------------

    /// <summary>
    /// 画一段排版好的内容，返回**画完之后光标的绝对 y**（调用方拿它接着画下面的东西）。
    ///
    /// 三遍的完整版，给离屏渲染（OfflineRender）用。界面（ChatView）不走这里：
    /// 它把三遍**拆到所有气泡的外面**（DrawBack / DrawText / DrawOver 各自对每个可见气泡
    /// 调一次），这样整屏的文字共用**一次** HDC 借还，而不是每个气泡一次。
    /// </summary>
    /// <param name="bubble">画在哪个气泡上。装饰色要相对它来混，见 <see cref="Palette"/>。</param>
    public static float Draw(Graphics g, Layout lay, float x, float y, Color bubble)
    {
        DrawBack(g, lay, x, y, bubble, float.MinValue, float.MaxValue);
        using (var dc = HoldTextDc(g))
            DrawText(dc, lay, x, y, bubble, float.MinValue, float.MaxValue);
        DrawOver(g, lay, x, y, bubble, float.MinValue, float.MaxValue);
        return y + lay.Height;
    }

    /// <summary>借一次 HDC 攥住不放（<see cref="HeldDc"/>），给一整屏的文字绘制共用。
    /// 用 <c>using</c> 包住，Dispose 才真正归还。</summary>
    internal static IDeviceContext HoldTextDc(Graphics g) => new HeldDc(g);

    /// <summary>每行的行顶 y（绝对坐标）。三遍各算一次：纯累加，比「传一个数组进去还要
    /// 解释它从哪来」便宜。</summary>
    private static float[] LineTops(Layout lay, float y)
    {
        var tops = new float[lay.Lines.Count];
        float cursor = y;
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            cursor += lay.Lines[i].SpaceBefore;
            tops[i] = cursor;
            cursor += lay.Lines[i].Pitch;
        }
        return tops;
    }

    /// <summary>行裁掉的余量：只作用于**行**判定（文字 / 行内代码底 / 公式线），多放一圈免得
    /// 贴着可见带边缘的行忽隐忽现。装饰不走这条 —— 它们按自己的矩形精确求交（见 DrawBack），
    /// 因为面板型块的整幅装饰挂在首行上、横跨后面所有行。</summary>
    private const float CullMargin = 40f;

    /// <summary>这一行落在可见带外吗。可见带是与 <paramref name="top"/> 同坐标的绝对 y。</summary>
    private static bool CulledOut(float top, float pitch, float visTop, float visBottom) =>
        top + pitch < visTop - CullMargin || top > visBottom + CullMargin;

    /// <summary>
    /// 第一遍：所有铺在文字**下面**的 GDI+ 东西（装饰块、行内代码底、公式里的线）。
    /// 必须整遍跑完再动文字：第二遍要把 HDC 借出来攥着不放，而那个 Graphics
    /// 在此期间是**不能用**的。
    /// </summary>
    public static void DrawBack(Graphics g, Layout lay, float x, float y, Color bubble, float visTop, float visBottom)
    {
        var tops = LineTops(lay, y);
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            var pl = lay.Lines[i];
            float top = tops[i];
            bool lineOut = CulledOut(top, pl.Pitch, visTop, visBottom);
            // 行出带了但装饰可能还伸在带里：面板型块（代码块底 / 表格框线 / 引用块底）把
            // 整幅装饰挂在**首行**、横跨后面所有行，按行裁就是「块还在、底没了」。
            if (lineOut && pl.Decors.Count == 0) continue;
            float ttop = top + pl.TextShift;        // 有行内公式的行，文字跟着整行一起下来

            foreach (var d in pl.Decors)
            {
                var rc = new RectangleF(x + d.Rect.X, ttop + d.Rect.Y, d.Rect.Width, d.Rect.Height);
                if (rc.Width <= 0 || rc.Height <= 0) continue;
                // 装饰按**自己的矩形**与可见带求交（CullMargin 只管行判定）：挂在首行的
                // 整幅面板在首行出带后仍有半截在带里，行裁会把它整个吞掉。
                if (rc.Bottom < visTop || rc.Top > visBottom) continue;
                if (d.Fill != Ink.None)
                {
                    using var br = new SolidBrush(Palette.Of(d.Fill, bubble));
                    if (d.Radius > 0)
                    {
                        using var path = Rounded(rc, d.Radius);
                        g.FillPath(br, path);
                    }
                    else g.FillRectangle(br, rc);
                }
                if (d.Stroke != Ink.None)
                {
                    using var pen = new Pen(Palette.Of(d.Stroke, bubble), d.StrokeW);
                    if (d.Radius > 0)
                    {
                        using var path = Rounded(rc, d.Radius);
                        g.DrawPath(pen, path);
                    }
                    else g.DrawRectangle(pen, rc.X, rc.Y, rc.Width, rc.Height);
                }
            }

            // 文字 / 行内代码底 / 公式线仍按行裁：它们都不跨行。
            if (lineOut) continue;

            float runX = x + pl.Indent;
            for (int ri = 0; ri < pl.Runs.Count; ri++)
            {
                var r = pl.Runs[ri];
                float at = r.X >= 0 ? x + r.X : runX;

                if (r.Math != null)
                {
                    // 公式里的**线**（分数线、根号、括号线、上划线）走这一遍：
                    // 它们要垫在字下面，而且必须是 GDI+ —— 第二遍把 HDC 攥住不放，
                    // 那期间一个 GDI+ 调用都不能有。
                    MathLines(g, r.Math, at, top + pl.Base, bubble);
                    if (r.X < 0) runX += r.Advance;
                    continue;
                }

                if (r.Text.Length == 0) continue;

                if (r.Ink == Ink.InlineCode)
                {
                    // 一段行内代码折进一行后通常已经并成了一个 Run（端帽在两端，见 AtomsOf）。
                    // 这里再兜一层：万一没并上（样式不同的相邻片段），底色也画成**一整条**
                    // 圆角底，而不是每段一个圆角块 —— 块与块之间的圆角缝就是「空格断开」
                    // 的另一种长相。
                    float start = at, end = at + r.Advance;
                    while (ri + 1 < pl.Runs.Count)
                    {
                        var nx = pl.Runs[ri + 1];
                        if (nx.X >= 0 || nx.Math != null || nx.Text.Length == 0 || nx.Ink != Ink.InlineCode) break;
                        end += nx.Advance;
                        ri++;
                    }
                    var bg = new RectangleF(start, ttop + 1.5f, end - start, Math.Max(4f, pl.LineHeight - 3f));
                    using var br = new SolidBrush(Palette.Of(Ink.InlineCodeBg, bubble));
                    using var path = Rounded(bg, 4f);
                    g.FillPath(br, path);
                    if (r.X < 0) runX = end;
                    continue;
                }

                if (r.X < 0) runX += r.Advance;
            }
        }
    }

    /// <summary>
    /// 第二遍：所有文字，全程共用**一个** HDC（<paramref name="dc"/>，来自 <see cref="HoldTextDc"/>）。
    ///
    /// 为什么非得攥着一个 DC 不放：TextRenderer.DrawText 每调一次就 GetHdc + ReleaseHdc 一次，
    /// 而 ReleaseHdc 会把 GDI+ 的状态判成失效、下一次 GetHdc 再从头建立一遍 ——
    /// 实测一次往返约 1ms，比绘制本身贵两三个数量级。
    ///
    /// 不能改用 Graphics.DrawString 绕开：排版是按 TextRenderer.MeasureText 量的，
    /// GDI+ 量、GDI 画（或反过来）会差出一两行 —— 盒子高度是对的、字却溢出去，
    /// 这条在 DrawReason 的注释里已经写过一遍。
    /// </summary>
    public static void DrawText(IDeviceContext dc, Layout lay, float x, float y, Color bubble, float visTop, float visBottom)
    {
        var tops = LineTops(lay, y);
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            var pl = lay.Lines[i];
            float top = tops[i];
            if (CulledOut(top, pl.Pitch, visTop, visBottom)) continue;
            float ttop = top + pl.TextShift;
            float runX = x + pl.Indent;

            foreach (var r in pl.Runs)
            {
                float at = r.X >= 0 ? x + r.X : runX;

                if (r.Math != null)
                {
                    MathGlyphs(dc, r.Math, at, top + pl.Base, bubble);
                    if (r.X < 0) runX += r.Advance;
                    continue;
                }

                if (r.Text.Length == 0) continue;

                TextRenderer.DrawText(dc, r.Text, r.Font,
                    new Point((int)MathF.Round(at + r.PadL), (int)MathF.Round(ttop)),
                    Palette.Of(r.Ink, bubble), TFlags);

                if (r.X < 0) runX += r.Advance;
            }
        }
    }

    /// <summary>
    /// 第三遍：两道线画在文字**上面**。
    ///
    /// 删除线要横穿字形才叫删除线，压到字底下就等于没有（下划线差得没这么明显，
    /// 但它在旧版里也是画在字上面，没有理由在这里改）。所以它不能并进第一遍。
    /// </summary>
    public static void DrawOver(Graphics g, Layout lay, float x, float y, Color bubble, float visTop, float visBottom)
    {
        var tops = LineTops(lay, y);
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            var pl = lay.Lines[i];
            float top = tops[i];
            if (CulledOut(top, pl.Pitch, visTop, visBottom)) continue;
            top += pl.TextShift;
            float runX = x + pl.Indent;

            foreach (var r in pl.Runs)
            {
                if (r.Text.Length == 0) { if (r.X < 0) runX += r.Advance; continue; }
                float at = r.X >= 0 ? x + r.X : runX;

                if (r.Underline)
                {
                    using var pen = new Pen(Palette.Of(r.Ink, bubble), 1f);
                    float uy = top + pl.LineHeight - 2f;
                    g.DrawLine(pen, at + r.PadL, uy, at + r.PadL + r.Width, uy);
                }
                if (r.Strike)
                {
                    using var pen = new Pen(Palette.Of(Ink.Muted, bubble), 1f);
                    float sy = top + pl.LineHeight * 0.55f;
                    g.DrawLine(pen, at + r.PadL, sy, at + r.PadL + r.Width, sy);
                }

                if (r.X < 0) runX += r.Advance;
            }
        }
    }

    /// <summary>
    /// 公式里的线和面（走 GDI+ 那一遍）。<paramref name="bx"/> / <paramref name="by"/> 是
    /// **基线的原点**：盒子里所有纵坐标都是相对基线算的。
    /// </summary>
    private static void MathLines(Graphics g, MBox box, float bx, float by, Color bubble)
    {
        foreach (var p in box.Prims)
        {
            switch (p)
            {
                case MRule r:
                {
                    // 分数线 / 根号横杠只有 1px 高，而它的 y 是**算出来的小数**
                    // （基线 + 轴线的 0.25em 再减去半个线宽）。直接按小数填，
                    // 这一像素会摊到相邻两行上各半个 —— 抗锯齿对矩形一样生效，
                    // 画出来是两条 50% 的浅灰，看着像「线画淡了」。
                    // 先落到整数像素再填：线还是那 1px，颜色才是实笔。
                    float rx = MathF.Round(bx + r.X);
                    float ry = MathF.Round(by + r.Y);
                    float rw = MathF.Max(1f, MathF.Round(r.W));
                    float rh = MathF.Max(1f, MathF.Round(r.H));
                    using var br = new SolidBrush(Palette.Of(r.Ink, bubble));
                    g.FillRectangle(br, rx, ry, rw, rh);
                    break;
                }
                case MPoly y:
                {
                    if (y.Pts.Length < 2) continue;
                    var pts = new PointF[y.Pts.Length];
                    for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(bx + y.Pts[i].X, by + y.Pts[i].Y);
                    using var pen = new Pen(Palette.Of(y.Ink, bubble), y.Th)
                    {
                        LineJoin = LineJoin.Bevel,       // 斜接会在那个尖角上戳出一根毛刺
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                    };
                    g.DrawLines(pen, pts);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 公式里的字（走攥着 HDC 的那一遍）。
    ///
    /// 每个字都是**独立**的一次 DrawText，位置由盒子算好 —— 不按 Run 合并，
    /// 是因为公式里相邻的两个字几乎总是不同字号或不同斜体（上标、分数上下、正体函数名）。
    /// 一次 DrawText 一个字的开销在这条路上是可接受的：真正的代价是 DC 往返，
    /// 而那一次已经由外层摊掉了（见第二遍的注释）。
    /// </summary>
    private static void MathGlyphs(IDeviceContext dc, MBox box, float bx, float by, Color bubble)
    {
        foreach (var p in box.Prims)
        {
            if (p is not MGlyph gl || gl.Text.Length == 0) continue;
            // 字格是**顶对齐**画的，所以基线位置要减去这个字体自己的上缘高度
            // —— 和 <see cref="MathLayout.Ascent"/> 是同一个数，不是近似。
            float asc = MathLayout.Ascent(gl.Font);
            TextRenderer.DrawText(dc, gl.Text, gl.Font,
                new Point((int)MathF.Round(bx + gl.X), (int)MathF.Round(by + gl.Y - asc)),
                Palette.Of(gl.Ink, bubble), TFlags);
        }
    }

    /// <summary>
    /// 借一次 HDC，攥住不放，给成百上千次 <see cref="TextRenderer.DrawText(IDeviceContext, string, Font, Point, Color, TextFormatFlags)"/>
    /// 共用（见 <see cref="Draw"/> 第二遍的注释）。
    ///
    /// <see cref="TextRenderer"/> 拿到 <see cref="IDeviceContext"/> 之后是 GetHdc → 画 → ReleaseHdc，
    /// 所以这里把 <see cref="ReleaseHdc"/> 做成空操作，真正的归还推迟到 <see cref="Dispose"/>。
    ///
    /// **持有期间那个 Graphics 一个 GDI+ 调用都不能有** —— 这正是上面要把绘制拆成两遍的原因，
    /// 不是"顺手分了两个循环"。
    /// </summary>
    private sealed class HeldDc : IDeviceContext
    {
        private readonly Graphics _g;
        private IntPtr _hdc;
        private bool _held;

        public HeldDc(Graphics g) => _g = g;

        public IntPtr GetHdc()
        {
            if (!_held) { _hdc = _g.GetHdc(); _held = true; }
            return _hdc;
        }

        public void ReleaseHdc() { /* 故意不还，见上面的注释 */ }

        public void Dispose()
        {
            if (!_held) return;
            _held = false;
            _g.ReleaseHdc(_hdc);
        }
    }

    /// <summary>
    /// 把 <see cref="Ink"/> 记号解成颜色。排版与绘制之间只流记号（见类注释第一条）；
    /// 这一个出口是给气泡画代码块头部按钮用的：复制图标前一张纸要先拿面板底色填掉，
    /// 那个色必须和面板是**同一个**出处，不能在外头另混一份。
    /// </summary>
    internal static Color InkColor(Ink ink, Color bubble) => Palette.Of(ink, bubble);

    /// <summary>
    /// 记号 → 颜色。<paramref name="bubble"/> 是这张气泡的填充色，
    /// 所有「比底色深一点 / 浅一点」的色都由它混出来 —— 混错基准就会在气泡上留一块异色补丁
    /// （用户气泡是蓝的、助手气泡是灰的，同一份排版结果要能贴在两种底上）。
    /// </summary>
    private static class Palette
    {
        public static Color Of(Ink ink, Color bubble) => ink switch
        {
            Ink.Text => Theme.TextMain,
            Ink.Muted => Theme.TextMuted,
            Ink.Accent => Theme.Accent,
            Ink.InlineCode => Theme.Accent,
            Ink.InlineCodeBg => Theme.Mix(bubble, Theme.TextMain, Theme.Dark ? 0.13f : 0.055f),
            Ink.CodePanel => Theme.Mix(bubble, Theme.TextMain, Theme.Dark ? 0.16f : 0.06f),
            Ink.QuotePanel => Theme.Mix(bubble, Theme.TextMuted, Theme.Dark ? 0.12f : 0.05f),
            Ink.QuoteBar => Theme.Mix(bubble, Theme.Accent, 0.45f),
            Ink.TableHead => Theme.Mix(bubble, Theme.TextMain, Theme.Dark ? 0.12f : 0.05f),
            Ink.TableLine => Theme.Mix(bubble, Theme.TextMuted, 0.24f),
            Ink.Rule => Theme.Mix(bubble, Theme.TextMuted, 0.30f),
            Ink.SynKeyword => Theme.Dark ? Color.FromArgb(122, 176, 255) : Color.FromArgb(41, 88, 196),
            Ink.SynString => Theme.Dark ? Color.FromArgb(214, 158, 106) : Color.FromArgb(160, 82, 20),
            Ink.SynComment => Theme.Mix(Theme.TextMuted, bubble, 0.28f),
            Ink.SynNumber => Theme.Dark ? Color.FromArgb(160, 210, 150) : Color.FromArgb(44, 126, 66),
            _ => Color.Empty,
        };
    }

    internal static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
