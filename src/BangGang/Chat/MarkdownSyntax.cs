
using System.Text.RegularExpressions;

namespace BangGang;

/// <summary>
/// 代码块的简易语法着色：把一行代码切成若干段，每段带一个 <see cref="Kind"/>。
///
/// **刻意不做完整词法分析。** 这里的输入是模型吐出来的、可能被截断的、可能整个语言都标错的
/// 代码片段，一个真正的解析器在半个字符串上会退化成「整段不着色」或者死循环；
/// 而这一块要回答的问题只是「这几行字看上去像不像代码」。四类（关键字 / 字符串 / 注释 / 数字）
/// 已经能把代码块的骨架勾出来，剩下的交给等宽字体和底色。
///
/// 每种语言的差别只有三样：**关键字表**、**注释记号**、**字符串定界符**。
/// 这三样凑成一个 <see cref="Lang"/>，着色器本身与语言无关。
/// </summary>
internal static class MarkdownSyntax
{
    internal enum Kind { Plain, Keyword, Str, Comment, Number }

    internal sealed record Piece(int Start, int Length, Kind Kind);

    /// <summary>
    /// 一种语言的着色参数。
    /// </summary>
    /// <param name="Keywords">整词匹配的关键字 / 类型名。</param>
    /// <param name="LineComment">行注释记号；空表示这门语言没有行注释。</param>
    /// <param name="BlockComment">块注释的起止（如 <c>("/*", "*/")</c>）；null 表示没有。</param>
    /// <param name="Quotes">字符串定界符。长度 1 的是普通引号，长度 3 的按三引号块处理（Python）。</param>
    /// <param name="CaseFold">关键字是否大小写不敏感（SQL / Pascal 系）。</param>
    private sealed record Lang(
        string[] Keywords,
        string LineComment,
        (string Open, string Close)? BlockComment,
        string[] Quotes,
        bool CaseFold);

    /// <summary>
    /// 各语言的参数表。键是围栏后面那个语言标注，**一律小写**比对。
    ///
    /// 认不出来的标注（包括没写标注的裸围栏）走 <see cref="Plain"/>：代码块照样有底、
    /// 照样是等宽，只是不上色。猜错语言比不着色难看得多 —— 把一段中文散文按 C 的关键字
    /// 涂一遍，读者第一时间会以为是自己看错了。
    /// </summary>
    private static readonly Dictionary<string, Lang> Langs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = CLike(["int", "char", "float", "double", "void", "long", "short", "unsigned", "signed",
                        "struct", "union", "enum", "typedef", "static", "const", "volatile", "extern",
                        "return", "if", "else", "for", "while", "do", "switch", "case", "default",
                        "break", "continue", "goto", "sizeof", "NULL", "include", "define"]),
        ["cpp"] = CLike(["int", "char", "float", "double", "void", "bool", "long", "short", "unsigned",
                          "struct", "class", "union", "enum", "namespace", "template", "typename",
                          "public", "private", "protected", "virtual", "override", "const", "constexpr",
                          "static", "mutable", "inline", "explicit", "operator", "new", "delete",
                          "return", "if", "else", "for", "while", "do", "switch", "case", "default",
                          "break", "continue", "try", "catch", "throw", "using", "auto", "nullptr",
                          "true", "false", "this"]),
        ["cs"] = CLike(["abstract", "as", "async", "await", "base", "bool", "break", "byte", "case",
                         "catch", "char", "class", "const", "continue", "decimal", "default", "delegate",
                         "do", "double", "else", "enum", "event", "explicit", "extern", "false",
                         "finally", "fixed", "float", "for", "foreach", "get", "goto", "if", "implicit",
                         "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
                         "null", "object", "operator", "out", "override", "params", "private",
                         "protected", "public", "readonly", "record", "ref", "return", "sbyte", "sealed",
                         "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
                         "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
                         "ushort", "using", "var", "virtual", "void", "volatile", "when", "where",
                         "while", "yield"]),
        ["java"] = CLike(["abstract", "assert", "boolean", "break", "byte", "case", "catch", "char",
                           "class", "const", "continue", "default", "do", "double", "else", "enum",
                           "extends", "final", "finally", "float", "for", "goto", "if", "implements",
                           "import", "instanceof", "int", "interface", "long", "native", "new",
                           "package", "private", "protected", "public", "return", "short", "static",
                           "strictfp", "super", "switch", "synchronized", "this", "throw", "throws",
                           "transient", "try", "void", "volatile", "while", "var", "record", "sealed",
                           "true", "false", "null"]),
        ["js"] = CLike(["async", "await", "break", "case", "catch", "class", "const", "continue",
                         "debugger", "default", "delete", "do", "else", "export", "extends", "false",
                         "finally", "for", "function", "if", "import", "in", "instanceof", "let", "new",
                         "null", "of", "return", "static", "super", "switch", "this", "throw", "true",
                         "try", "typeof", "undefined", "var", "void", "while", "with", "yield"]),
        ["ts"] = CLike(["abstract", "any", "as", "async", "await", "boolean", "break", "case", "catch",
                         "class", "const", "continue", "declare", "default", "delete", "do", "else",
                         "enum", "export", "extends", "false", "finally", "for", "from", "function",
                         "if", "implements", "import", "in", "instanceof", "interface", "keyof", "let",
                         "namespace", "new", "null", "number", "of", "private", "protected", "public",
                         "readonly", "return", "static", "string", "super", "switch", "this", "throw",
                         "true", "try", "type", "typeof", "undefined", "var", "void", "while", "yield"]),
        ["go"] = CLike(["break", "case", "chan", "const", "continue", "default", "defer", "else",
                         "fallthrough", "for", "func", "go", "goto", "if", "import", "interface", "map",
                         "package", "range", "return", "select", "struct", "switch", "type", "var",
                         "bool", "byte", "error", "float32", "float64", "int", "int32", "int64",
                         "rune", "string", "uint", "uint32", "uint64", "nil", "true", "false"]),
        ["rs"] = CLike(["as", "async", "await", "break", "const", "continue", "crate", "dyn", "else",
                         "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop",
                         "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self",
                         "static", "struct", "super", "trait", "true", "type", "unsafe", "use", "where",
                         "while", "bool", "char", "f32", "f64", "i32", "i64", "str", "String", "u8",
                         "u32", "u64", "usize", "Vec", "Option", "Result", "Some", "None", "Ok", "Err"]),
        ["py"] = new Lang(["and", "as", "assert", "async", "await", "break", "class", "continue",
                            "def", "del", "elif", "else", "except", "False", "finally", "for", "from",
                            "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not",
                            "or", "pass", "raise", "return", "True", "try", "while", "with", "yield",
                            "self", "int", "str", "float", "list", "dict", "set", "tuple", "print",
                            "len", "range", "type"],
                           "#", null, ["\"\"\"", "'''", "\"", "'"], false),
        ["sh"] = new Lang(["if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case",
                            "esac", "function", "return", "in", "local", "export", "source", "echo",
                            "exit", "set", "unset", "read", "cd", "sudo", "apt", "yum", "git", "grep",
                            "awk", "sed", "curl", "wget", "mkdir", "rm", "cp", "mv", "cat", "chmod",
                            "npm", "pip", "python", "docker"],
                           "#", null, ["\"", "'"], false),
        ["ps1"] = new Lang(["begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam",
                             "else", "elseif", "end", "enum", "exit", "filter", "finally", "for",
                             "foreach", "from", "function", "if", "in", "param", "process", "return",
                             "switch", "throw", "trap", "try", "until", "using", "while", "workflow",
                             "Write-Output", "Write-Host", "Get-ChildItem", "Set-Content", "Get-Content",
                             "New-Item", "Remove-Item", "Test-Path", "Start-Process", "Stop-Process"],
                            "#", ("<#", "#>"), ["\"", "'"], true),
        ["sql"] = new Lang(["select", "from", "where", "insert", "into", "values", "update", "set",
                             "delete", "create", "table", "alter", "drop", "index", "view", "join",
                             "left", "right", "inner", "outer", "on", "group", "by", "order", "having",
                             "limit", "offset", "union", "all", "distinct", "as", "and", "or", "not",
                             "null", "is", "in", "between", "like", "exists", "case", "when", "then",
                             "else", "end", "primary", "key", "foreign", "references", "default",
                             "int", "varchar", "text", "date", "timestamp", "boolean", "count", "sum",
                             "avg", "max", "min"],
                            "--", ("/*", "*/"), ["'", "\""], true),
        ["json"] = new Lang(["true", "false", "null"], "", null, ["\"", "'"], true),
        ["yaml"] = new Lang(["true", "false", "null", "yes", "no"], "#", null, ["\"", "'"], true),
        ["ini"] = new Lang(["true", "false"], ";", null, ["\"", "'"], true),
        ["toml"] = new Lang(["true", "false"], "#", null, ["\"\"\"", "'''", "\"", "'"], false),
        ["vb"] = new Lang(["dim", "as", "sub", "function", "end", "if", "then", "else", "elseif",
                            "for", "each", "next", "while", "do", "loop", "select", "case", "try",
                            "catch", "finally", "new", "return", "class", "module", "public", "private",
                            "protected", "friend", "shared", "static", "const", "byval", "byref",
                            "true", "false", "nothing", "integer", "string", "boolean", "double"],
                           "'", null, ["\""], true),
        ["lua"] = new Lang(["and", "break", "do", "else", "elseif", "end", "false", "for", "function",
                             "if", "in", "local", "nil", "not", "or", "repeat", "return", "then",
                             "true", "until", "while", "self"],
                            "--", null, ["\"", "'"], false),
        ["rb"] = new Lang(["alias", "and", "begin", "break", "case", "class", "def", "do", "else",
                            "elsif", "end", "ensure", "false", "for", "if", "in", "module", "next",
                            "nil", "not", "or", "redo", "rescue", "retry", "return", "self", "super",
                            "then", "true", "undef", "unless", "until", "when", "while", "yield",
                            "puts", "require", "attr_accessor"],
                           "#", null, ["\"", "'"], false),
        ["php"] = new Lang(["abstract", "and", "array", "as", "break", "callable", "case", "catch",
                             "class", "clone", "const", "continue", "declare", "default", "do", "echo",
                             "else", "elseif", "empty", "enddeclare", "endfor", "endforeach", "endif",
                             "endswitch", "endwhile", "eval", "exit", "extends", "final", "finally",
                             "fn", "for", "foreach", "function", "global", "goto", "if", "implements",
                             "include", "instanceof", "interface", "isset", "list", "match", "namespace",
                             "new", "or", "print", "private", "protected", "public", "readonly", "require",
                             "return", "static", "switch", "throw", "trait", "try", "unset", "use", "var",
                             "while", "xor", "yield", "true", "false", "null"],
                            "//", ("/*", "*/"), ["\"", "'"], false),
    };

    private static Lang CLike(string[] kw) =>
        new(kw, "//", ("/*", "*/"), ["\"", "'"], false);

    /// <summary>
    /// 一门语言的着色参数。没收录的（以及 <c>html</c> / <c>css</c> 这种按结构而不是按关键词着色的）
    /// 一律返回 null —— 调用方据此走「不上色、只用等宽 + 底色」那条路。
    /// </summary>
    private static Lang? Find(string lang)
    {
        if (lang.Length == 0) return null;
        // html/xml/css：它们的「关键字」是标签名和属性名，词表无限，按关键词涂只会把
        // 一段正常的 HTML 涂得花花绿绿而没有任何信息量。宁可不上色。
        if (lang is "html" or "xml" or "xhtml" or "vue" or "css" or "scss" or "less" or "svg") return null;
        // 常见别名
        lang = lang switch
        {
            "c++" or "cxx" or "h" or "hpp" or "cc" => "cpp",
            "c#" or "dotnet" => "cs",
            "javascript" or "jsx" or "mjs" or "cjs" or "node" => "js",
            "typescript" or "tsx" => "ts",
            "golang" => "go",
            "rust" => "rs",
            "python" or "python3" => "py",
            "bash" or "zsh" or "shell" or "console" or "terminal" => "sh",
            "powershell" or "pwsh" => "ps1",
            "postgres" or "postgresql" or "mysql" or "sqlite" => "sql",
            "yml" => "yaml",
            "text" or "txt" or "plain" or "plaintext" or "console" => "",
            _ => lang,
        };
        return Langs.TryGetValue(lang, out var l) ? l : null;
    }

    /// <summary>
    /// 把一行代码切成分段。返回的段**首尾相接、完整覆盖整行**（Plain 段填空隙），
    /// 调用方直接照顺序拼成 Run 即可 —— 中间留缝的话那段文字会整个消失，
    /// 而「代码块里少了一段」看起来只像是模型写漏了。
    /// </summary>
    public static List<Piece> Highlight(string language, string line)
    {
        var carry = default(Carry);
        return Highlight(language, line, ref carry);
    }

    /// <summary>
    /// 代码块逐行着色用的版本：<paramref name="carry"/> 带着**跨行的块注释状态**。
    ///
    /// 没有它的话，<c>/*</c> 开了头、几行之后才 <c>*/</c> 收尾的那种注释，除第一行以外
    /// 每一行都是「从头开始」——整段被当成代码涂成关键字色，中间几行还各多出一个
    /// 孤零零的 <c>*</c>。这是逐行着色最容易漏掉的一处，而它恰恰在长注释里最常见。
    /// </summary>
    public static List<Piece> Highlight(string language, string line, ref Carry carry)
    {
        var res = new List<Piece>();
        var lang = Find(language);

        // 认不出语言：整行一个 Plain 段。仍然返回一段而不是空表，调用方不必分支。
        if (lang == null)
        {
            carry.InComment = null;
            if (line.Length > 0) res.Add(new Piece(0, line.Length, Kind.Plain));
            return res;
        }

        if (line.Length == 0) return res;

        int i = 0, plainFrom = 0;
        void FlushPlain(int upto)
        {
            if (upto > plainFrom) res.Add(new Piece(plainFrom, upto - plainFrom, Kind.Plain));
        }

        var bc = lang.BlockComment;

        // 上一行留下的块注释还没收尾：本行从头就是注释，直到关止记号
        if (carry.InComment is { } closer)
        {
            int close = line.IndexOf(closer, StringComparison.Ordinal);
            if (close < 0) { res.Add(new Piece(0, line.Length, Kind.Comment)); return res; }
            int end0 = close + closer.Length;
            res.Add(new Piece(0, end0, Kind.Comment));
            i = plainFrom = end0;
        }
        carry.InComment = null;

        while (i < line.Length)
        {
            // ---- 块注释起始 ----
            if (bc is { } b && StartsAt(line, i, b.Open))
            {
                int close = line.IndexOf(b.Close, i + b.Open.Length, StringComparison.Ordinal);
                int end = close < 0 ? line.Length : close + b.Close.Length;
                FlushPlain(i);
                res.Add(new Piece(i, end - i, Kind.Comment));
                if (close < 0) carry.InComment = b.Close;   // 没在本行收尾，交给下一行
                i = end; plainFrom = i; continue;
            }

            // ---- 行注释 ----
            if (lang.LineComment.Length > 0 && StartsAt(line, i, lang.LineComment))
            {
                FlushPlain(i);
                res.Add(new Piece(i, line.Length - i, Kind.Comment));
                i = line.Length; plainFrom = i; continue;
            }

            // ---- 字符串 ----
            if (QuoteAt(line, i, lang.Quotes) is { } q)
            {
                int end = FindQuoteEnd(line, i, q);
                FlushPlain(i);
                res.Add(new Piece(i, end - i, Kind.Str));
                i = end; plainFrom = i; continue;
            }

            // ---- 数字 ----
            if (char.IsAsciiDigit(line[i]) && !IsWordChar(i > 0 ? line[i - 1] : ' '))
            {
                int end = i;
                while (end < line.Length && (char.IsAsciiLetterOrDigit(line[end]) || line[end] == '.')) end++;
                FlushPlain(i);
                res.Add(new Piece(i, end - i, Kind.Number));
                i = end; plainFrom = i; continue;
            }

            // ---- 标识符：整词比对关键字表 ----
            if (IsWordChar(line[i]))
            {
                int end = i;
                while (end < line.Length && IsWordChar(line[end])) end++;
                string word = line[i..end];
                if (Matches(lang, word))
                {
                    FlushPlain(i);
                    res.Add(new Piece(i, end - i, Kind.Keyword));
                    plainFrom = end;
                }
                i = end; continue;
            }

            i++;
        }
        FlushPlain(line.Length);
        return res;
    }

    /// <summary>
    /// 一段多行代码里的**块注释状态**。逐行着色时每行都要重新开始，
    /// 于是 <c>/*</c> 开了头、几行之后才 <c>*/</c> 收尾的那种注释，
    /// 除第一行外整段都会被当成代码涂成关键字色。所以状态要跨行传。
    /// </summary>
    internal struct Carry
    {
        /// <summary>上一行结束时仍在块注释里，且这是它的关止记号。</summary>
        public string? InComment;
    }

    // ---------------- 小工具 ----------------

    private static bool StartsAt(string s, int i, string token) =>
        token.Length > 0 && i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    /// <summary>这个位置是不是某个引号记号的起点。长记号优先（<c>"""</c> 要先于 <c>"</c> 匹配）。</summary>
    private static string? QuoteAt(string s, int i, string[] quotes)
    {
        foreach (var q in quotes)
            if (StartsAt(s, i, q)) return q;
        return null;
    }

    /// <summary>
    /// 找到字符串的收尾位置。转义用「前一个字符是不是反斜杠」判断，不数连续反斜杠的奇偶 ——
    /// 数奇偶是对的，但代码是从左往右走的，走到转义符那里已经把两个字符一起吞掉了，
    /// 反过来再判一次会互相抵消，<c>"a\\"</c> 就永远收不了尾。
    /// </summary>
    private static int FindQuoteEnd(string s, int start, string q)
    {
        int i = start + q.Length;
        while (i < s.Length)
        {
            if (s[i] == '\\') { i += 2; continue; }
            if (StartsAt(s, i, q)) return i + q.Length;
            i++;
        }
        return s.Length;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool Matches(Lang lang, string word)
    {
        foreach (var k in lang.Keywords)
            if (string.Equals(k, word, lang.CaseFold ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;
        return false;
    }
}
