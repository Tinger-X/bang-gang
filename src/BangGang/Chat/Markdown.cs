
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
/// 不做的事：链接不可点（只上色加下划线 —— 点击要拉默认浏览器，是另一件事）；
/// 不解析 HTML；代码块超宽时硬折行而不是横向滚动。
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

        public float Advance => SpaceBefore + Pitch;
    }

    /// <summary>一段同色同字体的文字。折行的最小单位是「原子」，落到行上就是它。</summary>
    internal sealed class Run
    {
        public string Text = "";
        public Font Font = SF.Get(BodySize);
        public Ink Ink = Ink.Text;
        public float Width;              // 文字本身的宽
        public float PadX;               // 左右各垫多少（行内代码的圆角底）
        public bool Underline, Strike;

        /// <summary>≥0 时**绝对**定位（列表的项目符号、表格单元格），不推进行内的光标。</summary>
        public float X = -1;

        /// <summary>这一小段占的横向距离。</summary>
        public float Advance => Width + PadX * 2;
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
    private const float CodeLangH = 17f;     // 代码块右上角语言标签占的那一行
    private const float TablePadX = 9f;
    private const float TablePadY = 6f;
    private const float TableMinColW = 54f;
    private const float InlineCodePadX = 3.5f;

    // ---------------- 测量 ----------------

    // 量文字一律走这一对 flag，且**量画同一套**。NoPadding 让量出来的宽就是画的宽；
    // NoPrefix 让 `&` 老老实实是个 `&`（代码里到处是这个字符，不设的话它会开始吃后面的字）。
    private const TextFormatFlags TFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private static readonly Size NoLimit = new(100000, 100000);

    private static float RunWidth(string s, Font f) =>
        s.Length == 0 ? 0f : TextRenderer.MeasureText(s, f, NoLimit, TFlags).Width;

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
    /// 条数上限而不是字节上限：一条消息的排版结果和它自己的长度成正比，
    /// 而「最近 6 条」在任何真实会话里都远小于一张缩略图。留着不封顶的话，
    /// 一晚上流式下来的每一段中间状态都会攒在里面。
    /// </summary>
    private const int CacheMax = 6;
    private static readonly Dictionary<(string text, int cap), Layout> Cache = new();
    private static readonly Queue<(string text, int cap)> CacheOrder = new();

    public static Layout Measure(string md, float capWidth)
    {
        string key = md ?? "";
        int cap = Math.Max(40, (int)Math.Round(capWidth));
        if (Cache.TryGetValue((key, cap), out var hit)) return hit;

        var lay = Build(key, cap);
        Cache[(key, cap)] = lay;
        CacheOrder.Enqueue((key, cap));
        while (CacheOrder.Count > CacheMax) Cache.Remove(CacheOrder.Dequeue());
        return lay;
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

    private const int FBold = 1, FItalic = 2, FStrike = 4, FCode = 8, FLink = 16;

    private sealed class Inline
    {
        public string Text = "";
        public int Flags;

        /// <summary>这里必须换一行（行尾两空格 / 行尾反斜杠）。</summary>
        public bool HardBreak;
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
        if (outp.Count > 0)
        {
            var last = outp[^1];
            if (!last.HardBreak && last.Flags == flags) { last.Text += text; return; }
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
            //
            // 地址**只用来确认这确实是个链接**，解析完就丢掉 —— 本期链接不可点（见类注释）。
            if (c == '[')
            {
                int rb = s.IndexOf(']', i + 1);
                if (rb > i && rb + 1 < s.Length && s[rb + 1] == '(')
                {
                    int rp = s.IndexOf(')', rb + 2);
                    if (rp > rb)
                    {
                        Flush();
                        ScanInline(outp, s[(i + 1)..rb], flags | FLink, depth + 1);
                        i = rp + 1;
                        continue;
                    }
                }
                lit.Append(c);
                i++;
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

        /// <summary>每一项是**一串块**，不是一段文字：列表项里可以放引用、代码块、嵌套列表。</summary>
        public required List<List<Block>> Items;
    }

    private sealed class BTable : Block
    {
        public required List<List<List<Inline>>> Rows;   // 行 → 列 → 行内
        public required List<int> Aligns;                // -1 左 / 0 中 / 1 右
    }

    private sealed class BRule : Block;

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

        public Parser(string[] lines) { _l = lines; }

        public List<Block> Blocks()
        {
            var outp = new List<Block>();
            var para = new List<string>();

            void FlushPara()
            {
                if (para.Count == 0) return;
                outp.Add(new BPara { Inlines = ParseInline(string.Join("\n", para)) });
                para.Clear();
            }

            while (_i < _l.Length)
            {
                string line = _l[_i];
                string t = line.TrimStart();

                if (t.Length == 0) { FlushPara(); _i++; continue; }

                var fm = ReFence.Match(line);
                if (fm.Success) { FlushPara(); outp.Add(ReadFence(fm.Groups[2].Value.Trim())); continue; }

                var hm = ReHead.Match(t);
                if (hm.Success)
                {
                    FlushPara();
                    _i++;
                    outp.Add(new BHead { Level = hm.Groups[1].Value.Length, Inlines = ParseInline(hm.Groups[2].Value) });
                    continue;
                }

                if (ReRule.IsMatch(t)) { FlushPara(); _i++; outp.Add(new BRule()); continue; }

                if (t.StartsWith('>')) { FlushPara(); outp.Add(ReadQuote()); continue; }

                if (line.Contains('|') && _i + 1 < _l.Length && ReTableSep.IsMatch(_l[_i + 1]))
                {
                    FlushPara();
                    outp.Add(ReadTable());
                    continue;
                }

                var im = ReItem.Match(line);
                if (im.Success) { FlushPara(); outp.Add(ReadList(im)); continue; }

                if (IndentOf(line) >= 4) { FlushPara(); outp.Add(ReadIndented()); continue; }

                para.Add(t.TrimEnd());
                _i++;
            }
            FlushPara();
            return outp;
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
            var items = new List<List<Block>>();

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
            }
            return new BList { Ordered = ordered, Items = items };
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
        public float PadX;
        public bool Space;
        public bool BreakBefore;
        public bool ForceBreak;
        public float Width;

        public readonly float Advance => Width + PadX * 2;
    }

    private sealed class LineBuf
    {
        public readonly List<Run> Runs = new();
        public float W, H, Pitch;
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
    private static List<Atom> AtomsOf(List<Inline> inlines, float size, FontStyle baseStyle, Ink ink)
    {
        var atoms = new List<Atom>();

        foreach (var inl in inlines)
        {
            if (inl.HardBreak)
            {
                atoms.Add(new Atom { Text = "", Width = 0, ForceBreak = true });
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
                PadX = code ? InlineCodePadX : 0,
            };

            string s = inl.Text;
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
                    cp.Width = RunWidth(cp.Text, f);
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
                piece.Width = RunWidth(piece.Text, a.Font);
                piece.PadX = 0;
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
            for (int k = f0; k < end; k++)
            {
                var a = atoms[k];
                if (a.Text.Length == 0) continue;
                lb.Runs.Add(new Run
                {
                    Text = a.Text,
                    Font = a.Font,
                    Ink = a.Ink,
                    Width = a.Width,
                    PadX = a.PadX,
                    Underline = a.Underline,
                    Strike = a.Strike,
                });
                lb.W += a.Advance;
                lb.H = Math.Max(lb.H, a.Font.Height);
            }
            if (lb.H <= 0) { lb.Runs.Add(new Run { Text = " ", Font = blank, Width = 0 }); lb.H = blank.Height; }
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

        /// <summary>
        /// 在容器 / 整段的开头时不加段前距。**必须在排版这一块之前先取好**。
        /// 顺带把上一块欠的下内边距**消费掉**：每个块只在开头问这一次，
        /// 谁问谁拿走，免得一路漏到后面不该躲的块头上。
        /// </summary>
        public float Sp(float space)
        {
            float below = PadBelow;
            PadBelow = 0f;
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

    private static void EmitLine(Ctx c, List<Run> runs, float indent, float pitch, float lineH)
    {
        var pl = new PhysLine { Indent = indent, Pitch = pitch, LineHeight = lineH };
        pl.Runs.AddRange(runs);
        c.Lines.Add(pl);
        c.Y += pl.Pitch;
    }

    private static void EmitLines(Ctx c, List<LineBuf> bufs, float indent, float spaceBefore)
    {
        int i0 = c.Lines.Count;
        foreach (var lb in bufs) EmitLine(c, lb.Runs, indent, lb.Pitch, lb.H);
        Pad(c, i0, spaceBefore);
    }

    /// <summary>把一段行内内容折成若干物理行（不带任何块级装饰）。</summary>
    private static List<LineBuf> Flow(List<Inline> inlines, float size, FontStyle style, Ink ink,
                                      float avail, float pitchK, Ctx c)
    {
        var bufs = WrapAtoms(AtomsOf(inlines, size, style, ink), avail, pitchK, out bool wrapped);
        if (wrapped) c.Wrapped = true;
        return bufs;
    }

    private static void EmitBlocks(Ctx c, List<Block> blocks, float left, float avail)
    {
        foreach (var b in blocks)
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
            }
        }

        // 容器到底了：里面最后一块欠的下内边距不外泄给容器的下一个兄弟 ——
        // 那圈内边距是「容器内部和外部的分隔」，已经由容器自己的边框 / 间距表达了。
        c.PadBelow = 0f;
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
    /// 代码块：圆角底 + 右上角语言标签 + 四类语法着色。
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

        if (bc.Lang.Length > 0)
        {
            var lf = SF.Get(9f);
            float lw = RunWidth(bc.Lang, lf);
            var ll = new PhysLine
            {
                Indent = Math.Max(left + CodePadX, left + avail - CodePadX - lw),
                Pitch = CodeLangH,
                LineHeight = CodeLangH,
            };
            ll.Runs.Add(new Run { Text = bc.Lang, Font = lf, Ink = Ink.Muted, Width = lw });
            local.Add(ll);
        }

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

        if (local.Count == 0)
        {
            var pl = new PhysLine { Indent = left + CodePadX, Pitch = pitch, LineHeight = f.Height };
            pl.Runs.Add(new Run { Text = " ", Font = f, Width = 0 });
            local.Add(pl);
        }

        float inner = 0;
        foreach (var pl in local) inner += pl.Pitch;

        // 整块底画在**第一行**上，高度盖住后面的所有行。行是顺序画的，后面几行只画文字，
        // 不会把这层底盖掉；反过来把底挂在每一行上，行与行之间就会露出一条条缝。
        // 矩形往回退 CodePadY：那圈内边距在第一行的**上面**，不属于任何一行。
        local[0].Decors.Insert(0, new Decor
        {
            Rect = new RectangleF(left, -CodePadY, avail, inner + CodePadY * 2),
            Fill = Ink.CodePanel,
            Radius = 8,
        });

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

    /// <summary>
    /// 列表。项目符号在**第一行**上绝对定位，正文整体悬挂缩进 ——
    /// 第二行对齐的是文字的左缘而不是符号，否则折行之后缩进会一截一截往里跑。
    /// </summary>
    private static void EmitList(Ctx c, BList l, float left, float avail)
    {
        var f = SF.Get(BodySize);
        string bullet = BulletGlyph(c.ListDepth);

        // 先量一遍所有符号，取最宽的那个当悬挂宽度：`1.` 和 `10.` 宽度不同，
        // 逐个算的话同一张列表里每一行的文字起点都不一样。
        float markW = 0;
        for (int n = 0; n < l.Items.Count; n++)
            markW = Math.Max(markW, RunWidth(l.Ordered ? (n + 1) + "." : bullet, f));
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

            string mk = l.Ordered ? (n + 1) + "." : bullet;
            c.Lines[before].Runs.Insert(0, new Run
            {
                Text = mk,
                Font = f,
                Ink = l.Ordered ? Ink.Muted : Ink.Accent,
                Width = RunWidth(mk, f),
                X = left,
            });
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
        int outerBase = c.Base;
        c.Base = i0;
        EmitBlocks(c, q.Blocks, innerLeft, innerAvail);
        c.Base = outerBase;
        if (c.Lines.Count == i0) return;
        float contentH = c.Y - yStart;

        // 内外边距都落在行身上（类注释第三条）。注意先取段前距再落内边距：
        // Sp 判的是「本容器里前面有没有块」，顺序反了会让收缩块永远拿不到段前距。
        // 段前距里要连自己的 QuotePadY 一起要 —— 面板是从首行内容原点往上退 QuotePadY
        // 画的，那 7px 会吃掉段前距，只写 SpaceQuote 的话两块面板会贴在一起。
        float sp = c.Sp(SpaceQuote + QuotePadY);
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
                foreach (var a in AtomsOf(t.Rows[r][col], BodySize, r == 0 ? FontStyle.Bold : FontStyle.Regular, Ink.Text))
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
                    AtomsOf(t.Rows[r][col], BodySize, head ? FontStyle.Bold : FontStyle.Regular, Ink.Text),
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
                var pl = new PhysLine { Pitch = pitch, LineHeight = f.Height };
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
                ly += pitch;
            }
        }

        // 网格。装饰矩形的原点在第一行的**顶**，而表格的第一行上面还压着 TablePadY 的
        // 单元格内边距，所以整体上移一格。
        float dy = -TablePadY;
        var first = local[0];
        first.Decors.Add(new Decor
        {
            Rect = new RectangleF(left, dy, tableW, rowH[0]),
            Fill = Ink.TableHead,
            Radius = 6,
        });
        for (int r = 1; r < t.Rows.Count; r++)
            first.Decors.Add(new Decor
            {
                Rect = new RectangleF(left, rowTop[r] + dy, tableW, 1f),
                Fill = Ink.TableLine,
            });

        // 竖线落在单元格的内边距里，不会穿过文字 —— 这正是 TablePadX 的用处。
        float bx = left;
        for (int col = 0; col < n - 1; col++)
        {
            bx += colW[col];
            first.Decors.Add(new Decor { Rect = new RectangleF(bx, dy + 1f, 1f, ly - 2f), Fill = Ink.TableLine });
            bx += 1f;
        }

        int i0 = c.Lines.Count;
        float sp = c.Sp(SpaceTable);
        foreach (var pl in local) { c.Lines.Add(pl); c.Y += pl.Pitch; }
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

    private static Layout Build(string md, int cap)
    {
        var c = new Ctx();
        EmitBlocks(c, new Parser(SplitLines(md)).Blocks(), 0, cap);

        var lay = new Layout();
        lay.Lines.AddRange(c.Lines);
        lay.Height = c.Y;
        lay.Wrapped = c.Wrapped || c.FullWidth;

        if (c.FullWidth) lay.Width = cap;
        else
        {
            float mx = 0;
            foreach (var pl in c.Lines) mx = Math.Max(mx, ExtentOf(pl));
            lay.Width = Math.Min(cap, Math.Max(40f, mx));
        }
        return lay;
    }

    // ---------------- 绘制 ----------------

    /// <summary>
    /// 画一段排版好的内容，返回**画完之后光标的绝对 y**（调用方拿它接着画下面的东西）。
    ///
    /// 这里返回绝对值而不是高度：旧版返回的是高度，而唯一的调用方
    /// （<c>MessageBubble.OnPaint</c>）是把它当绝对 y 用的 —— 于是那条「回复被截断」
    /// 的说明被画在了气泡顶端附近，而不是正文下面。同样地，这边也不许自己另算一遍高度：
    /// 每一步都走 <see cref="PhysLine.Advance"/>。
    /// </summary>
    /// <param name="bubble">画在哪个气泡上。装饰色要相对它来混，见 <see cref="Palette"/>。</param>
    public static float Draw(Graphics g, Layout lay, float x, float y, Color bubble)
    {
        // 分两遍画，因为这两遍**对 Graphics 的用法互斥**（见下面的 HeldDc）。
        //
        // 先把每行的 y 算出来：两遍都要用它，而它是纯累加，算两遍只会给「两遍算得不一样」
        // 留一条缝。行数不多，一个 float[] 的事。
        var tops = new float[lay.Lines.Count];
        float cursor = y;
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            cursor += lay.Lines[i].SpaceBefore;
            tops[i] = cursor;
            cursor += lay.Lines[i].Pitch;
        }

        // ---- 第一遍：所有铺在文字**下面**的 GDI+ 东西（装饰块、行内代码底）----
        //
        // 必须整遍跑完再动文字：第二遍要把 HDC 借出来攥着不放，而那个 Graphics
        // 在此期间是**不能用**的。
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            var pl = lay.Lines[i];
            float top = tops[i];

            foreach (var d in pl.Decors)
            {
                if (d.Fill == Ink.None) continue;
                var rc = new RectangleF(x + d.Rect.X, top + d.Rect.Y, d.Rect.Width, d.Rect.Height);
                if (rc.Width <= 0 || rc.Height <= 0) continue;
                using var br = new SolidBrush(Palette.Of(d.Fill, bubble));
                if (d.Radius > 0)
                {
                    using var path = Rounded(rc, d.Radius);
                    g.FillPath(br, path);
                }
                else g.FillRectangle(br, rc);
            }

            float runX = x + pl.Indent;
            foreach (var r in pl.Runs)
            {
                if (r.Text.Length == 0) continue;
                float at = r.X >= 0 ? x + r.X : runX;

                if (r.PadX > 0)
                {
                    var bg = new RectangleF(at, top + 1.5f, r.Advance, Math.Max(4f, pl.LineHeight - 3f));
                    using var br = new SolidBrush(Palette.Of(Ink.InlineCodeBg, bubble));
                    using var path = Rounded(bg, 4f);
                    g.FillPath(br, path);
                }

                if (r.X < 0) runX += r.Advance;
            }
        }

        // ---- 第二遍：所有文字，全程共用**一个** HDC ----
        //
        // 为什么非得这样：TextRenderer.DrawText 每调一次就 GetHdc + ReleaseHdc 一次，
        // 而 ReleaseHdc 会把 GDI+ 的状态判成失效、下一次 GetHdc 再从头建立一遍 ——
        // 实测一次往返约 1ms，比绘制本身贵两三个数量级。
        //
        // 一行正文在这里是**逐词**一个 Run，于是这个开销不是"每帧几十次"而是"每帧几千次"：
        // 一条 4200 字的回复排成 3075 个 Run，打开这条会话的第一帧整整卡 3 秒
        // （tools/zoom-wheel.ps1 卡在这一帧上，量出来的"图片没画出来"就是这么来的）。
        // 摊成一次 GetHdc 之后，同一份内容从 2952ms 降到 139ms。
        //
        // 剩下的 139ms 是每次调用自己的固定开销（选中字体、ExtTextOut），**不是** DC 往返；
        // 想再快就得让排版把一行里同样式、位置又首尾相接的相邻 Run 并成一个
        // —— 那要动 WrapAtoms 的原子模型，不是这里能顺手改的。
        //
        // 不能改用 Graphics.DrawString 绕开：排版是按 TextRenderer.MeasureText 量的，
        // GDI+ 量、GDI 画（或反过来）会差出一两行 —— 盒子高度是对的、字却溢出去，
        // 这条在 DrawReason 的注释里已经写过一遍。
        using (var dc = new HeldDc(g))
        {
            for (int i = 0; i < lay.Lines.Count; i++)
            {
                var pl = lay.Lines[i];
                float top = tops[i];
                float runX = x + pl.Indent;

                foreach (var r in pl.Runs)
                {
                    if (r.Text.Length == 0) continue;
                    float at = r.X >= 0 ? x + r.X : runX;

                    TextRenderer.DrawText(dc, r.Text, r.Font,
                        new Point((int)MathF.Round(at + r.PadX), (int)MathF.Round(top)),
                        Palette.Of(r.Ink, bubble), TFlags);

                    if (r.X < 0) runX += r.Advance;
                }
            }
        }

        // ---- 第三遍：两道线画在文字**上面** ----
        //
        // 删除线要横穿字形才叫删除线，压到字底下就等于没有（下划线差得没这么明显，
        // 但它在旧版里也是画在字上面，没有理由在这里改）。所以它不能并进第一遍。
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            var pl = lay.Lines[i];
            float top = tops[i];
            float runX = x + pl.Indent;

            foreach (var r in pl.Runs)
            {
                if (r.Text.Length == 0) continue;
                float at = r.X >= 0 ? x + r.X : runX;

                if (r.Underline)
                {
                    using var pen = new Pen(Palette.Of(r.Ink, bubble), 1f);
                    float uy = top + pl.LineHeight - 2f;
                    g.DrawLine(pen, at + r.PadX, uy, at + r.PadX + r.Width, uy);
                }
                if (r.Strike)
                {
                    using var pen = new Pen(Palette.Of(Ink.Muted, bubble), 1f);
                    float sy = top + pl.LineHeight * 0.55f;
                    g.DrawLine(pen, at + r.PadX, sy, at + r.PadX + r.Width, sy);
                }

                if (r.X < 0) runX += r.Advance;
            }
        }

        return cursor;
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
