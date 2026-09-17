
namespace BangGang;

/// <summary>
/// TeX 公式解析：把 <c>\frac{a}{b}</c> 这样的一串解析成 <see cref="MNode"/> 树。
///
/// **只做常用子集**，判据是「聊天里真的会出现什么」：分数、根号、上下标、求和积分、希腊字母、
/// 关系符、矩阵、分段函数、重音、<c>\text</c>。不做的：自定义宏、<c>\def</c>、字体包、
/// 对齐环境（<c>align</c>）、化学式、以及一切要靠「排两遍」才能定的东西。
///
/// 解析器不认识的东西**原样画出来**（<c>\foo</c> 就显示 <c>\foo</c>），不抛异常、不吞掉 ——
/// 模型吐出来的公式没人保证是合法的，一个报错比一个显示得不太对的公式糟糕得多。
/// </summary>
internal static class MathTex
{
    // 一行的终止条件。用位掩码是因为同一个 <see cref="Row"/> 在四种不同的上下文里被调用：
    // 组里（遇 } 停）、矩阵格子里（遇 &amp; 和 \\ 停）、\left 里（遇 \right 停）、
    // 以及顶层（谁都不遇）。写成四个 bool 参数的话，调用处全是 false,false,true,false。
    private const int StopBrace = 1, StopAmp = 2, StopRowEnd = 4, StopEnd = 8, StopRight = 16, StopBracket = 32;

    private sealed class P
    {
        public readonly string S;
        public int I;
        public P(string s) { S = s; }
        public bool Eof => I >= S.Length;
        public char Cur => S[I];
        public bool At(int k) => I + k < S.Length && S[I + k] == '\\';
    }

    /// <summary>解析。解析不出任何东西（空串）时返回 null，调用方按原文渲染。</summary>
    public static MNode? Parse(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        var p = new P(src);
        var row = Row(p, 0);
        return row.Items.Count == 0 ? null : Unwrap(row);
    }

    /// <summary>位置 <paramref name="i"/>（应当是个反斜杠）处的命令名，不含反斜杠。</summary>
    public static string NameAt(string s, int i)
    {
        if (i >= s.Length || s[i] != '\\') return "";
        var p = new P(s) { I = i };
        return ReadName(p);
    }

    /// <summary>
    /// 这个名字认不认识。**「裸公式」的边界全靠它**（见 <c>Markdown.BareExtent</c>）：
    /// 不认识的 <c>\x</c> 一律当成普通文字，所以 <c>C:\Users\…</c> 这样的路径
    /// 和 <c>\n</c> 这样的转义序列都不会被误认成公式的起点。
    /// </summary>
    public static bool IsKnownCommand(string name)
    {
        if (name.Length == 0) return false;
        if (name.Length == 1)
            return ",:;!> ".IndexOf(name[0]) >= 0 || "{}|%$&#_\\".IndexOf(name[0]) >= 0;
        return SymTable.ContainsKey("\\" + name) || Funcs.Contains(name) || Structural.Contains(name);
    }

    /// <summary>没有对应符号、但会改变排法（或只是要吃掉）的命令。</summary>
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "frac", "dfrac", "tfrac", "sqrt", "text", "textrm", "mbox",
        "mathrm", "mathbf", "mathit", "mathbb", "mathcal", "mathfrak", "mathsf",
        "operatorname", "bm", "boldsymbol",
        "overline", "bar", "underline", "hat", "widehat", "vec", "dot", "ddot", "tilde", "widetilde",
        "left", "right", "begin", "end",
        "limits", "nolimits", "displaystyle", "textstyle", "scriptstyle", "scriptscriptstyle",
        "quad", "qquad", "nonumber", "notag", "label", "tag",
        "ldots", "cdots", "dots", "vdots", "ddots",
    };

    // ---------------- 行 ----------------

    private static MRow Row(P p, int stop)
    {
        var row = new MRow();
        int guard = 0;
        while (!p.Eof)
        {
            // 防死循环：任何一条分支只要没往前挪，就硬吃掉一个字符。
            // 这是为畸形输入准备的 —— 模型吐一个孤零零的 `}` 不该让界面卡死。
            int before = p.I;

            if (p.Cur == ' ') { p.I++; continue; }
            if (p.Cur == '}' && (stop & StopBrace) != 0) break;
            if (p.Cur == '&' && (stop & StopAmp) != 0) break;
            if (p.Cur == ']' && (stop & StopBracket) != 0) break;
            if (p.Cur == '\\' && p.I + 1 < p.S.Length)
            {
                char n = p.S[p.I + 1];
                if (n == '\\' && (stop & StopRowEnd) != 0) break;
                if (n == ')' || n == ']') break;                 // 收尾的 \( \) 由调用方处理
                if ((stop & StopRight) != 0 && Match(p, "\\right")) break;
                if ((stop & StopEnd) != 0 && Match(p, "\\end")) break;
            }

            var node = Atom(p);
            if (node == null) { if (p.I == before) p.I++; continue; }

            // 上下标：可以连写（x^2_i），也可以带撇（x'）
            var cur = node;
            while (!p.Eof && (p.Cur == '^' || p.Cur == '_' || p.Cur == '\''))
            {
                if (p.Cur == '\'')
                {
                    p.I++;
                    var pr = new MLeaf { Text = "′" };
                    cur = new MScript { Base = cur, Sup = pr };
                    continue;
                }
                char w = p.Cur;
                p.I++;
                var arg = Arg(p);
                if (arg == null) break;
                var ms = cur as MScript ?? new MScript { Base = cur };
                if (w == '^') ms.Sup = arg; else ms.Sub = arg;
                cur = ms;
            }

            row.Items.Add(cur);

            if (++guard > 20000) break;
        }
        return row;
    }

    private static bool Match(P p, string word) =>
        string.CompareOrdinal(p.S, p.I, word, 0, word.Length) == 0;

    // ---------------- 一个原子 ----------------

    private static MNode? Atom(P p)
    {
        if (p.Eof) return null;
        char c = p.Cur;

        if (c == '{')
        {
            p.I++;
            var g = Row(p, StopBrace);
            if (!p.Eof && p.Cur == '}') p.I++;
            return Unwrap(g);
        }
        if (c == '\\') return Command(p);
        if (c == '~') { p.I++; return new MSpace { Em = 0.35f }; }
        if (c == '}') { p.I++; return null; }        // 落单的 }：吃掉，别让它污染后面

        p.I++;
        if (char.IsLetter(c))
        {
            // 单个拉丁字母一律斜体 —— 数学里的变量就该是斜的。两个字母连写（ab）在 TeX 里
            // 是两个变量相乘，这里照办：拆成两个叶子，各自带自己的斜体和间距。
            return new MLeaf { Text = c.ToString(), Italic = true };
        }
        if (char.IsDigit(c) || c == '.') { p.I--; return Number(p); }

        return Sym(c);
    }

    private static MLeaf Number(P p)
    {
        int i = p.I;
        while (!p.Eof && (char.IsDigit(p.Cur) || p.Cur == '.')) p.I++;
        return new MLeaf { Text = p.S[i..p.I] };
    }

    /// <summary>
    /// 单个符号。类别（<see cref="MKind"/>）在这里定下来 —— 它是**留多少空**的依据，
    /// 所以 <c>+ - = ×</c> 必须标对，标成 Ord 的话 <c>a+b</c> 会挤在一起。
    /// </summary>
    private static MLeaf Sym(char c) => c switch
    {
        '-' => new MLeaf { Text = "−", Kind = MKind.Bin },     // U+2212，比 ASCII 的连字符长且居中
        '+' => new MLeaf { Text = "+", Kind = MKind.Bin },
        '*' => new MLeaf { Text = "∗", Kind = MKind.Bin },
        '/' => new MLeaf { Text = "/", Kind = MKind.Bin },
        '=' => new MLeaf { Text = "=", Kind = MKind.Rel },
        '<' => new MLeaf { Text = "<", Kind = MKind.Rel },
        '>' => new MLeaf { Text = ">", Kind = MKind.Rel },
        ',' => new MLeaf { Text = ",", Kind = MKind.Punct },
        ';' => new MLeaf { Text = ";", Kind = MKind.Punct },
        ':' => new MLeaf { Text = ":", Kind = MKind.Rel },
        '(' or '[' => new MLeaf { Text = c.ToString(), Kind = MKind.Open },
        ')' or ']' => new MLeaf { Text = c.ToString(), Kind = MKind.Close },
        '|' => new MLeaf { Text = "|", Kind = MKind.Ord },
        '!' => new MLeaf { Text = "!", Kind = MKind.Ord },
        _ => new MLeaf { Text = c.ToString() },
    };

    /// <summary>读一个必需的参数：<c>{...}</c>，或者（照 TeX 的规矩）紧跟的一个原子 ——
    /// <c>\frac12</c> 就是 <c>\frac{1}{2}</c>。</summary>
    private static MNode? Arg(P p)
    {
        while (!p.Eof && p.Cur == ' ') p.I++;
        if (p.Eof) return null;
        if (p.Cur == '{')
        {
            p.I++;
            var g = Row(p, StopBrace);
            if (!p.Eof && p.Cur == '}') p.I++;
            return Unwrap(g);
        }
        return Atom(p);
    }

    private static MNode Unwrap(MRow r) => r.Items.Count == 1 ? r.Items[0] : r;

    // ---------------- 命令 ----------------

    private static string ReadName(P p)
    {
        p.I++;                      // 吃掉反斜杠
        // 有些命令就是一个符号：\, \; \{ \} \| \  \% \$ \& \# \_
        if (!p.Eof && !char.IsLetter(p.Cur)) { char c = p.Cur; p.I++; return c.ToString(); }
        int i = p.I;
        while (!p.Eof && char.IsLetter(p.Cur)) p.I++;
        return p.S[i..p.I];
    }

    private static MNode? Command(P p)
    {
        string name = ReadName(p);
        if (name.Length == 0) return null;

        // 空白：TeX 里 \; \, 这些就是「加一点空」
        switch (name)
        {
            case ",": return new MSpace { Em = 0.1667f };
            case ":": case ">": return new MSpace { Em = 0.2222f };
            case ";": return new MSpace { Em = 0.2778f };
            case "!": return new MSpace { Em = -0.1667f };
            case " ": return new MSpace { Em = 0.3333f };
            case "quad": return new MSpace { Em = 1f };
            case "qquad": return new MSpace { Em = 2f };
        }

        switch (name)
        {
            case "frac": case "dfrac": case "tfrac":
                return new MFrac { Num = Arg(p) ?? new MLeaf(), Den = Arg(p) ?? new MLeaf(), Display = name == "dfrac" };

            case "sqrt":
            {
                MNode? idx = null;
                SkipWs(p);
                if (!p.Eof && p.Cur == '[')
                {
                    p.I++;
                    var g = Row(p, StopBracket);
                    if (!p.Eof && p.Cur == ']') p.I++;
                    idx = Unwrap(g);
                }
                return new MSqrt { Rad = Arg(p) ?? new MLeaf(), Index = idx };
            }

            case "text": case "textrm": case "mbox": return Literal(p, false, false);
            case "mathrm": case "operatorname": return Style(p, false, false);
            case "mathbf": case "bm": case "boldsymbol": return Style(p, true, false);
            case "mathit": return Style(p, true, true);
            case "mathbb": case "mathcal": case "mathfrak": case "mathsf": return Style(p, false, false);

            case "overline": case "bar": return new MAccent { Base = Arg(p) ?? new MLeaf(), Bar = true };
            case "underline": return new MAccent { Base = Arg(p) ?? new MLeaf(), Bar = true, Below = true };
            case "hat": case "widehat": return new MAccent { Base = Arg(p) ?? new MLeaf(), Acc = "ˆ" };
            case "tilde": case "widetilde": return new MAccent { Base = Arg(p) ?? new MLeaf(), Acc = "˜" };
            case "vec": return new MAccent { Base = Arg(p) ?? new MLeaf(), Acc = "→" };
            case "dot": return new MAccent { Base = Arg(p) ?? new MLeaf(), Acc = "˙" };
            case "ddot": return new MAccent { Base = Arg(p) ?? new MLeaf(), Acc = "¨" };

            case "left": return LeftRight(p);
            case "right": return null;                     // 落单的 \right：吃掉

            case "begin": return Environment(p);

            // \limits / \nolimits 只影响大运算符的上下限位置，这里本来就按符号种类定死了，
            // 吃掉它即可 —— 不认的话会显示成一个 "\limits" 挂在公式里。
            case "limits": case "nolimits": return null;
            case "displaystyle": case "textstyle": case "scriptstyle": case "scriptscriptstyle": return null;
            case "label": case "tag": Arg(p); return null;
            case "nonumber": case "notag": return null;
        }

        // 就是加空格的命令（\; 那类上面已经处理），剩下的按符号表查
        //
        // 查表必须**带上反斜杠**：表里的键是 `\alpha`，不是 `alpha`（<see cref="IsKnownCommand"/>
        // 就是这么查的）。漏了这一步的表现不是「希腊字母不显示」，而是**整行无限重复** ——
        // 查不到就落进下面那条「不认识的命令」，而它会退回命令开头重来一遍。
        if (SymTable.TryGetValue("\\" + name, out var sym))
            return new MLeaf { Text = sym.Text, Italic = sym.Italic, Kind = sym.Kind, Big = sym.Big, SideLimits = sym.SideLimits, Scale = sym.Scale };

        // 函数名（\sin、\lim）：正体，而且后面要留一点空
        if (Funcs.Contains(name))
        {
            if (name is "lim" or "limsup" or "liminf" or "max" or "min" or "sup" or "inf")
                return new MLeaf { Text = name, Kind = MKind.Op, SideLimits = true };
            return new MLeaf { Text = name, Kind = MKind.Func };
        }

        // 转义的字面量：\{ \} \% \$ \& \# \_ \\ 等
        if (name.Length == 1 && !char.IsLetter(name[0]))
            return new MLeaf { Text = Escaped(name[0]) };

        // 不认识的命令：**原样显示**。模型偶尔会吐出 amsmath 的东西（\begin{align}），
        // 显示成 `\align` 用户一眼就知道是没支持，比无声地吞掉强。
        //
        // 名字已经由 ReadName 吃掉了，**不要退回去**：<see cref="Row"/> 判断「这一轮有没有
        // 往前挪」靠的就是位置，退回起点会让它原地转满 20000 圈、把同一个 `\foo` 摞成
        // 一个两万项的 MRow —— 屏幕上看到的是一整行重复的命令（宽度被 Layout.Width 钳在
        // 版心宽上，所以只露得出二十来个，看着像「渲染器画花了」）。
        return new MLeaf { Text = "\\" + name };
    }

    private static string Escaped(char c) => c switch
    {
        '{' => "{", '}' => "}", '%' => "%", '$' => "$", '&' => "&", '#' => "#", '_' => "_",
        '\\' => "\\", '|' => "‖", ',' => ",", ';' => ";", ' ' => " ", '>' => ">", ':' => ":",
        '!' => "!",
        _ => c.ToString(),
    };

    private static void SkipWs(P p) { while (!p.Eof && p.Cur == ' ') p.I++; }

    /// <summary><c>\text{...}</c>：内容**不再按数学解析**，原样当正体文字 ——
    /// 里面出现 <c>_</c> 或 <c>^</c> 都是普通字符，这是它存在的意义。</summary>
    private static MNode Literal(P p, bool bold, bool italic)
    {
        SkipWs(p);
        if (p.Eof || p.Cur != '{') return new MLeaf();
        int depth = 0, i = p.I;
        var sb = new System.Text.StringBuilder();
        for (; i < p.S.Length; i++)
        {
            char c = p.S[i];
            if (c == '\\' && i + 1 < p.S.Length && (p.S[i + 1] == '{' || p.S[i + 1] == '}'))
            {
                sb.Append(p.S[i + 1]); i++; continue;
            }
            if (c == '{') { depth++; if (depth == 1) continue; }
            if (c == '}') { depth--; if (depth == 0) break; }
            sb.Append(c);
        }
        p.I = Math.Min(p.S.Length, i + 1);
        return new MLeaf { Text = sb.ToString().TrimEnd(), Bold = bold, Italic = italic };
    }

    /// <summary><c>\mathbf{...}</c> 这一族：内容仍然按数学解析，只是换字体。</summary>
    private static MNode Style(P p, bool bold, bool italic)
    {
        var n = Arg(p);
        if (n == null) return new MLeaf();
        Restyle(n, bold, italic);
        return n;
    }

    private static void Restyle(MNode n, bool bold, bool italic)
    {
        switch (n)
        {
            case MLeaf l: l.Bold = bold; l.Italic = italic; break;
            case MRow r: foreach (var it in r.Items) Restyle(it, bold, italic); break;
        }
    }

    // ---------------- \left ... \right ----------------

    private static MNode LeftRight(P p)
    {
        string open = Delim(p);
        var inner = Row(p, StopRight);
        string close = ".";
        if (!p.Eof && Match(p, "\\right"))
        {
            p.I += "\\right".Length;
            close = Delim(p);
        }
        return new MDelim { Inner = Unwrap(inner), Open = open, Close = close };
    }

    /// <summary>读一个定界符。TeX 用 <c>.</c> 表示「这边不画」，这里把它变成空串。</summary>
    private static string Delim(P p)
    {
        SkipWs(p);
        if (p.Eof) return "";
        if (p.Cur == '\\')
        {
            string n = ReadName(p);
            if (n == ".") return "";
            if (SymTable.TryGetValue(n, out var s)) return s.Text;
            return Escaped(n.Length == 1 ? n[0] : ' ');
        }
        char c = p.Cur;
        p.I++;
        return c == '.' ? "" : c.ToString();
    }

    // ---------------- 环境（矩阵 / 分段函数） ----------------

    private static MNode Environment(P p)
    {
        SkipWs(p);
        string env = Brace(p);
        var m = new MMatrix();
        (m.Open, m.Close) = env switch
        {
            "pmatrix" => ("(", ")"),
            "bmatrix" => ("[", "]"),
            "Bmatrix" => ("{", "}"),
            "vmatrix" => ("|", "|"),
            "Vmatrix" => ("‖", "‖"),
            "cases" => ("{", ""),
            _ => ("", ""),                 // matrix / array / aligned / gathered / align*
        };

        // array 与 aligned 后面跟一段列格式 {lcr}，吃掉 —— 列对齐这里统一按居中排，
        // 为它单独实现一套列格式的收益太小（聊天里几乎只有 pmatrix 和 cases）。
        if (env is "array" or "aligned" or "align" or "align*") { SkipWs(p); if (!p.Eof && p.Cur == '{') Brace(p); }

        var row = new List<MNode>();
        int guard = 0;
        while (!p.Eof && ++guard < 500)
        {
            if (Match(p, "\\end")) break;
            var cell = Row(p, StopAmp | StopRowEnd | StopEnd);
            row.Add(Unwrap(cell));

            if (!p.Eof && p.Cur == '&') { p.I++; continue; }
            if (!p.Eof && p.Cur == '\\' && p.I + 1 < p.S.Length && p.S[p.I + 1] == '\\')
            {
                p.I += 2;
                m.Rows.Add(row);
                row = new List<MNode>();
                continue;
            }
            break;                          // 既不是 & 也不是 \\，交给 \end
        }
        if (row.Count > 0) m.Rows.Add(row);

        if (Match(p, "\\end")) { p.I += "\\end".Length; SkipWs(p); if (!p.Eof && p.Cur == '{') Brace(p); }
        return m.Rows.Count == 0 ? (MNode)new MLeaf() : m;
    }

    private static string Brace(P p)
    {
        SkipWs(p);
        if (p.Eof || p.Cur != '{') return "";
        int i = ++p.I;
        while (!p.Eof && p.Cur != '}') p.I++;
        string s = p.S[i..p.I];
        if (!p.Eof) p.I++;
        return s;
    }

    // ---------------- 符号表 ----------------

    private readonly record struct SymDef(string Text, MKind Kind = MKind.Ord, bool Italic = false, bool Big = false, bool SideLimits = false, float Scale = 1f);

    private static readonly HashSet<string> Funcs = new()
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh",
        "log", "ln", "lg", "exp", "lim", "limsup", "liminf", "max", "min", "sup", "inf",
        "det", "dim", "ker", "deg", "gcd", "arg", "hom", "mod",
    };

    private static readonly Dictionary<string, SymDef> SymTable = Build();

    private static Dictionary<string, SymDef> Build()
    {
        var d = new Dictionary<string, SymDef>(StringComparer.Ordinal);
        void A(string k, string v, MKind kind = MKind.Ord, bool italic = false, bool big = false, bool side = false, float scale = 1f)
            => d[k] = new SymDef(v, kind, italic, big, side, scale);

        // 希腊字母（小写斜体 —— TeX 里就是这样，也正好和变量的斜体一致）
        string[] lower = { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota",
                           "kappa", "lambda", "mu", "nu", "xi", "pi", "rho", "sigma", "tau", "upsilon",
                           "phi", "chi", "psi", "omega" };
        string[] glyph = { "α", "β", "γ", "δ", "ε", "ζ", "η", "θ", "ι", "κ", "λ", "μ", "ν", "ξ", "π", "ρ",
                           "σ", "τ", "υ", "φ", "χ", "ψ", "ω" };
        for (int i = 0; i < lower.Length; i++) A("\\" + lower[i], glyph[i], MKind.Ord, italic: true);
        A("\\varepsilon", "ε", MKind.Ord, true); A("\\vartheta", "ϑ", MKind.Ord, true);
        A("\\varphi", "φ", MKind.Ord, true); A("\\varpi", "ϖ", MKind.Ord, true);
        A("\\varrho", "ϱ", MKind.Ord, true); A("\\varsigma", "ς", MKind.Ord, true);

        string[] upper = { "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Upsilon", "Phi", "Psi", "Omega" };
        string[] uglyph = { "Γ", "Δ", "Θ", "Λ", "Ξ", "Π", "Σ", "Υ", "Φ", "Ψ", "Ω" };
        for (int i = 0; i < upper.Length; i++) A("\\" + upper[i], uglyph[i]);

        // 二元运算符
        A("\\times", "×", MKind.Bin); A("\\div", "÷", MKind.Bin); A("\\pm", "±", MKind.Bin);
        A("\\mp", "∓", MKind.Bin); A("\\cdot", "⋅", MKind.Bin); A("\\ast", "∗", MKind.Bin);
        A("\\star", "⋆", MKind.Bin); A("\\circ", "∘", MKind.Bin); A("\\bullet", "∙", MKind.Bin);
        A("\\oplus", "⊕", MKind.Bin); A("\\otimes", "⊗", MKind.Bin); A("\\odot", "⊙", MKind.Bin);
        A("\\cup", "∪", MKind.Bin); A("\\cap", "∩", MKind.Bin); A("\\setminus", "∖", MKind.Bin);
        A("\\wedge", "∧", MKind.Bin); A("\\vee", "∨", MKind.Bin); A("\\land", "∧", MKind.Bin);
        A("\\lor", "∨", MKind.Bin); A("\\neg", "¬", MKind.Ord); A("\\lnot", "¬");

        // 关系符
        A("\\le", "≤", MKind.Rel); A("\\leq", "≤", MKind.Rel); A("\\ge", "≥", MKind.Rel);
        A("\\geq", "≥", MKind.Rel); A("\\ne", "≠", MKind.Rel); A("\\neq", "≠", MKind.Rel);
        A("\\approx", "≈", MKind.Rel); A("\\equiv", "≡", MKind.Rel); A("\\sim", "∼", MKind.Rel);
        A("\\simeq", "≃", MKind.Rel); A("\\cong", "≅", MKind.Rel); A("\\propto", "∝", MKind.Rel);
        A("\\ll", "≪", MKind.Rel); A("\\gg", "≫", MKind.Rel); A("\\doteq", "≐", MKind.Rel);
        A("\\subset", "⊂", MKind.Rel); A("\\subseteq", "⊆", MKind.Rel);
        A("\\supset", "⊃", MKind.Rel); A("\\supseteq", "⊇", MKind.Rel);
        A("\\in", "∈", MKind.Rel); A("\\notin", "∉", MKind.Rel); A("\\ni", "∋", MKind.Rel);
        A("\\perp", "⊥", MKind.Rel); A("\\parallel", "∥", MKind.Rel); A("\\mid", "∣", MKind.Rel);
        A("\\to", "→", MKind.Rel); A("\\rightarrow", "→", MKind.Rel); A("\\leftarrow", "←", MKind.Rel);
        A("\\Rightarrow", "⇒", MKind.Rel); A("\\Leftarrow", "⇐", MKind.Rel);
        A("\\leftrightarrow", "↔", MKind.Rel); A("\\Leftrightarrow", "⇔", MKind.Rel);
        A("\\mapsto", "↦", MKind.Rel); A("\\uparrow", "↑", MKind.Rel); A("\\downarrow", "↓", MKind.Rel);
        A("\\implies", "⟹", MKind.Rel); A("\\iff", "⟺", MKind.Rel);
        A("\\because", "∵", MKind.Rel); A("\\therefore", "∴", MKind.Rel);

        // 大运算符：上下限在正上下方（Op），唯独积分号仍挂在角上（SideLimits）
        A("\\sum", "∑", MKind.Op, big: true, scale: 1.45f);
        A("\\prod", "∏", MKind.Op, big: true, scale: 1.45f);
        A("\\coprod", "∐", MKind.Op, big: true, scale: 1.45f);
        A("\\bigcup", "⋃", MKind.Op, big: true, scale: 1.45f);
        A("\\bigcap", "⋂", MKind.Op, big: true, scale: 1.45f);
        A("\\bigoplus", "⨁", MKind.Op, big: true, scale: 1.45f);
        A("\\bigotimes", "⨂", MKind.Op, big: true, scale: 1.45f);
        A("\\bigvee", "⋁", MKind.Op, big: true, side: true, scale: 1.45f);
        A("\\bigwedge", "⋀", MKind.Op, big: true, side: true, scale: 1.45f);
        A("\\int", "∫", MKind.Op, big: true, side: true, scale: 1.7f);
        A("\\iint", "∬", MKind.Op, big: true, side: true, scale: 1.7f);
        A("\\iiint", "∭", MKind.Op, big: true, side: true, scale: 1.7f);
        A("\\oint", "∮", MKind.Op, big: true, side: true, scale: 1.7f);

        // 定界符
        A("\\langle", "⟨", MKind.Open); A("\\rangle", "⟩", MKind.Close);
        A("\\lfloor", "⌊", MKind.Open); A("\\rfloor", "⌋", MKind.Close);
        A("\\lceil", "⌈", MKind.Open); A("\\rceil", "⌉", MKind.Close);
        A("\\lvert", "|"); A("\\rvert", "|"); A("\\lVert", "‖"); A("\\rVert", "‖");
        A("\\vert", "|"); A("\\Vert", "‖"); A("\\|", "‖"); A("\\backslash", "\\");

        // 杂项
        A("\\infty", "∞"); A("\\partial", "∂", MKind.Ord, true); A("\\nabla", "∇");
        A("\\forall", "∀"); A("\\exists", "∃"); A("\\nexists", "∄");
        A("\\emptyset", "∅"); A("\\varnothing", "∅"); A("\\angle", "∠");
        A("\\degree", "°"); A("\\circ", "∘", MKind.Bin);
        A("\\prime", "′"); A("\\ldots", "…"); A("\\cdots", "⋯"); A("\\dots", "…");
        A("\\vdots", "⋮"); A("\\ddots", "⋱");
        A("\\hbar", "ℏ"); A("\\ell", "ℓ", MKind.Ord, true);
        A("\\Re", "ℜ"); A("\\Im", "ℑ"); A("\\aleph", "ℵ");
        A("\\triangle", "△"); A("\\square", "□"); A("\\checkmark", "✓");
        A("\\surd", "√"); A("\\top", "⊤"); A("\\bot", "⊥");
        A("\\dagger", "†"); A("\\ddagger", "‡"); A("\\S", "§"); A("\\P", "¶");
        return d;
    }
}
