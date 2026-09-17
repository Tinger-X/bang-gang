
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BangGang;

/// <summary>
/// MathML 解析：把模型吐出来的 <c>&lt;math&gt;…&lt;/math&gt;</c> 转成和 TeX 同一棵
/// <see cref="MNode"/> 树，共用 <see cref="MathLayout"/> 排版。
///
/// 走 <c>System.Xml.Linq</c> 而不是手写一个小 XML 解析器：它随 .NET 一起分发、不用装包，
/// 而手写的那一版一定会漏掉属性里的 <c>&gt;</c>、自闭合标签、以及 CDATA 这三种写法，
/// 漏掉的表现是「某些公式整段变成原始文本」，还很难复现。
///
/// 解析失败（模型少写一个闭合标签是常有的事）就返回 null，由调用方按原始文本渲染 ——
/// 一个显示得不好看的公式，比一个把整条消息吞掉的异常好得多。
/// </summary>
internal static class MathMl
{
    /// <summary>MathML 里的具名实体。XML 只自带那五个（lt gt amp quot apos），
    /// 其余的在没有 DTD 的 <c>XDocument</c> 里是**语法错误**，所以先换成字面字符再解析。</summary>
    private static readonly Dictionary<string, string> Entities = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ",
        ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ", ["chi"] = "χ",
        ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ",
        ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["times"] = "×", ["divide"] = "÷", ["plusmn"] = "±", ["minus"] = "−", ["sdot"] = "⋅",
        ["lowast"] = "∗", ["middot"] = "·", ["oplus"] = "⊕", ["otimes"] = "⊗",
        ["cup"] = "∪", ["cap"] = "∩", ["ne"] = "≠", ["le"] = "≤", ["ge"] = "≥",
        ["equiv"] = "≡", ["asymp"] = "≈", ["prop"] = "∝", ["infin"] = "∞",
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["oint"] = "∮",
        ["radic"] = "√", ["part"] = "∂", ["nabla"] = "∇", ["isin"] = "∈", ["notin"] = "∉",
        ["sub"] = "⊂", ["sube"] = "⊆", ["sup"] = "⊃", ["supe"] = "⊇",
        ["rarr"] = "→", ["larr"] = "←", ["harr"] = "↔", ["rArr"] = "⇒", ["lArr"] = "⇐", ["hArr"] = "⇔",
        ["forall"] = "∀", ["exist"] = "∃", ["empty"] = "∅", ["perp"] = "⊥", ["ang"] = "∠",
        ["prime"] = "′", ["hellip"] = "…", ["cdots"] = "⋯", ["there4"] = "∴", ["cong"] = "≅",
        ["nbsp"] = " ", ["ensp"] = " ", ["emsp"] = " ", ["thinsp"] = " ",
    };

    private static readonly Regex EntityRx = new(@"&([A-Za-z][A-Za-z0-9]*);", RegexOptions.Compiled);

    /// <summary>是不是一段 MathML（块级判据也用它）。</summary>
    public static bool Looks(string s) =>
        s.Length > 5 && s[0] == '<' && s.StartsWith("<math", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析一段 MathML。认不出来返回 null。</summary>
    public static MNode? Parse(string src)
    {
        string xml = EntityRx.Replace(src, m =>
            Entities.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        XElement? root;
        try { root = XDocument.Parse(xml).Root; }
        catch { return null; }
        if (root == null) return null;

        // <math> 里面**只有文字**：模型把 LaTeX 塞进 &lt;math&gt; 里是常见写法，当 TeX 再解析一次。
        if (!root.Elements().Any())
        {
            var t = root.Value.Trim();
            return t.Length == 0 ? null : MathTex.Parse(t);
        }

        // <semantics> 的第一个孩子才是真内容，后面的 <annotation> 是 TeX 原文（排它反而错）
        var el = root.Name.LocalName == "math" || root.Name.LocalName == "semantics"
            ? (root.Elements().FirstOrDefault(e => e.Name.LocalName != "annotation"
                                                && e.Name.LocalName != "annotation-xml") ?? root)
            : root;

        var node = Conv(el);
        return node is MRow { Items.Count: 0 } ? null : node;
    }

    // ---------------- 元素 ----------------

    private static MNode Conv(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "math": case "semantics": case "mstyle": case "mpadded": case "merror":
            case "menclose": case "mphantom":
                return Row(e);

            case "mrow": return Row(e);

            case "mi": return Ident(e);
            case "mn": return new MLeaf { Text = Txt(e), Kind = MKind.Ord };
            case "mtext": case "ms": return new MLeaf { Text = Txt(e) };
            case "mo": return Oper(Txt(e));

            case "mfrac":
            {
                var k = e.Elements().ToList();
                return new MFrac { Num = k.Count > 0 ? Conv(k[0]) : new MLeaf(), Den = k.Count > 1 ? Conv(k[1]) : new MLeaf() };
            }

            case "msup": case "msub": case "msubsup":
            {
                var k = e.Elements().ToList();
                var s = new MScript { Base = k.Count > 0 ? Conv(k[0]) : new MLeaf() };
                if (e.Name.LocalName != "msub" && k.Count > 1) s.Sup = Conv(k[1]);
                if (e.Name.LocalName != "msup" && k.Count > (e.Name.LocalName == "msubsup" ? 2 : 1))
                    s.Sub = Conv(k[^1]);
                return s;
            }

            case "msqrt": return new MSqrt { Rad = Row(e) };

            case "mroot":
            {
                var k = e.Elements().ToList();
                return new MSqrt
                {
                    Rad = k.Count > 0 ? Conv(k[0]) : new MLeaf(),
                    Index = k.Count > 1 ? Conv(k[1]) : null,
                };
            }

            case "mover": case "munder": case "munderover":
            {
                var k = e.Elements().ToList();
                if (k.Count < 2) return k.Count > 0 ? Conv(k[0]) : new MLeaf();
                var b = Conv(k[0]);
                var over = Conv(k[1]);
                bool hat = IsAccent(over);
                if (e.Name.LocalName == "mover" && hat)
                    return new MAccent { Base = b, Acc = Over(over), Bar = over is MLeaf { Text: "¯" or "‾" or "─" } };
                if (e.Name.LocalName == "munder" && hat)
                    return new MAccent { Base = b, Acc = Over(over), Bar = true, Below = true };
                var s = new MScript { Base = b };
                if (e.Name.LocalName != "munder") s.Sup = over;
                if (e.Name.LocalName != "mover" && k.Count > 2) s.Sub = Conv(k[2]);
                else if (e.Name.LocalName == "munder") s.Sub = over;
                return s;
            }

            case "mfenced":
            {
                var open = Attr(e, "open", "(");
                var close = Attr(e, "close", ")");
                return new MDelim { Inner = Row(e), Open = open, Close = close };
            }

            case "mtable": return Table(e);
            case "mtr": case "mlabeledtr": return Row(e);
            case "mtd":
            {
                var n = Row(e);
                return n;
            }

            case "mspace": return new MSpace { Em = 0.25f };
            case "none": return new MLeaf();

            default:
                return Row(e);          // 不认识的标签：把里面的东西**摆出来**，别整段丢掉
        }
    }

    private static MNode Row(XElement e)
    {
        var r = new MRow();
        foreach (var c in e.Elements()) r.Items.Add(Conv(c));
        // 纯文字节点（<math> 里直接写 "x+y"）
        foreach (var t in e.Nodes().OfType<XText>())
        {
            var s = t.Value.Trim();
            if (s.Length > 0) r.Items.Add(new MLeaf { Text = s });
        }
        return r.Items.Count == 1 ? r.Items[0] : r;
    }

    /// <summary>
    /// <c>&lt;mi&gt;</c>：**单字符的拉丁标识符是斜体**，多字符的（<c>&lt;mi&gt;sin&lt;/mi&gt;</c>）
    /// 是正体 —— 这是 MathML 自己的规矩，也是它和 <c>&lt;mn&gt;</c> 唯一的区别所在。
    /// </summary>
    private static MLeaf Ident(XElement e)
    {
        string t = Txt(e);
        var variant = e.Attribute("mathvariant")?.Value;
        bool italic = variant switch
        {
            "normal" or "bold" or "upright" => false,
            "italic" or "bold-italic" => true,
            _ => t.Length == 1 && char.IsLetter(t[0]) && t[0] < 128,
        };
        bool bold = variant is "bold" or "bold-italic";
        return new MLeaf { Text = t, Italic = italic, Bold = bold };
    }

    private static bool IsAccent(MNode n) =>
        n is MLeaf { Text: "^" or "ˆ" or "~" or "˜" or "¯" or "‾" or "→" or "˙" or "¨" or "⃗" or "‾" or "─" or "¯" };

    private static string Over(MNode n) => n is MLeaf l ? Norm(l.Text) : "^";

    private static string Norm(string s) => s switch
    {
        "^" or "ˆ" => "ˆ",
        "~" or "˜" => "˜",
        "¯" or "‾" or "─" or "—" => "¯",
        "→" or "⃗" => "→",
        "˙" or "." => "˙",
        "¨" or ".." => "¨",
        _ => s,
    };

    private static MMatrix Table(XElement e)
    {
        var m = new MMatrix();
        foreach (var tr in e.Elements().Where(x => x.Name.LocalName is "mtr" or "mlabeledtr"))
        {
            var row = new List<MNode>();
            foreach (var td in tr.Elements().Where(x => x.Name.LocalName == "mtd"))
            {
                var n = Conv(td);
                // <mtd> 里常常多一层 <mrow>，包不包都一样，拆掉省一层
                row.Add(n);
            }
            if (row.Count > 0) m.Rows.Add(row);
        }
        return m;
    }

    private static string Attr(XElement e, string name, string dflt) =>
        e.Attribute(name) is { Value.Length: > 0 } a ? (a.Value == "." ? "" : a.Value) : dflt;

    /// <summary>取元素里的文字，顺带把 <c>&lt;mtext&gt;</c> 里包的子元素也拉平
    /// （<c>&lt;mo&gt;&lt;mo&gt;+&lt;/mo&gt;&lt;/mo&gt;</c> 这种多余嵌套确实出现过）。</summary>
    private static string Txt(XElement e) => e.Value.Trim();

    /// <summary>
    /// <c>&lt;mo&gt;</c> 的类别：决定它左右留多少空。MathML 本来要求看 <c>form</c> 属性和
    /// 前后文，这里按**符号本身**定，够用且不会因为属性写错而整行挤在一起。
    /// </summary>
    private static MNode Oper(string t)
    {
        if (t.Length == 0) return new MLeaf();

        // 大运算符：字形本身要放大，上下限挂正上下方
        switch (t)
        {
            case "∑" or "∏" or "∐" or "⋃" or "⋂" or "⨁" or "⨂":
                return new MLeaf { Text = t, Kind = MKind.Op, Big = true, Scale = 1.45f };
            case "∫" or "∬" or "∭" or "∮":
                return new MLeaf { Text = t, Kind = MKind.Op, Big = true, SideLimits = true, Scale = 1.7f };
        }

        var kind = t switch
        {
            "+" or "−" or "-" or "±" or "∓" or "×" or "÷" or "⋅" or "∗" or "∪" or "∩"
                or "⊕" or "⊗" or "⊙" or "∧" or "∨" or "∖" => MKind.Bin,
            "=" or "≠" or "≈" or "<" or ">" or "≤" or "≥" or "≡" or "≅" or "∼" or "∝"
                or "→" or "←" or "↔" or "⇒" or "⇐" or "⇔" or "↦" or "∈" or "∉" or "∋"
                or "⊂" or "⊆" or "⊃" or "⊇" or "⊥" or "∥" or "∣" => MKind.Rel,
            "(" or "[" or "{" or "⟨" or "⌊" or "⌈" or "‖" or "|" => MKind.Open,
            ")" or "]" or "}" or "⟩" or "⌋" or "⌉" => MKind.Close,
            "," or ";" => MKind.Punct,
            "!" or "?" => MKind.Ord,
            _ => t.Length == 1 && "abcdefghijklmnopqrstuvwxyz".Contains(t[0]) ? MKind.Ord : MKind.Ord,
        };

        // \left . \right 的占位符
        if (t == ".") return new MLeaf { Text = "" };
        return new MLeaf { Text = t, Kind = kind };
    }
}
