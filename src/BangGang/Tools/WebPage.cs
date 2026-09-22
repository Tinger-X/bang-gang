using System.Text;

namespace BangGang;

/// <summary>网页引用到的东西的类别。用来在清单里分组，也决定它读不读得出来。</summary>
internal enum RefKind { Page, Style, Script, Image, Other }

/// <summary>页面上引用的一个地址。</summary>
internal sealed record WebRef(RefKind Kind, string Url);

/// <summary>一页解析出来的两样东西：给模型看的正文，以及这页引用到的地址。</summary>
internal sealed record ParsedPage(string Text, List<WebRef> Refs);

/// <summary>
/// 把 HTML 解析成「正文 + 引用清单」。
///
/// 拆出来的原因：正文那一半和引用那一半是**互相冲突**的 —— 正文要把标签全部剥掉、
/// 还要把 <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c> 的内容整块丢弃（否则一屏 JS 会混进正文，
/// 而 JS 里的 <c>&lt;</c> 还会把后面真正的正文一起吃掉），可引用的地址恰恰就长在那些标签上。
/// 早先只做正文时随手剥掉是没问题的，一旦要同时拿到引用，就必须一次性走完、
/// 边走边把要的东西记下来（见 <see cref="Parse"/> 的主循环）。
/// </summary>
internal static class WebPage
{
    /// <summary>清单最多列多少条。**这是「列出来」的上限，不是「能抓多少」的上限** ——
    /// 抓多少由层次和模型的判断决定。列不下时结果里会写明还有多少条没列。</summary>
    public const int MaxListed = 60;

    /// <summary>
    /// 解析。<paramref name="baseUrl"/> 用来把相对地址补成绝对地址（<c>&lt;base href&gt;</c> 优先）。
    /// </summary>
    public static ParsedPage Parse(string html, string baseUrl)
    {
        var sb = new StringBuilder(html.Length);
        var refs = new List<WebRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // <base href> 得先扫一遍：它可能出现在任何位置，而它之前的相对地址怎么办
        // 是个说不清的事。先扫一遍、整页统一按它算，比「按出现顺序一半用旧基址一半用新基址」好懂。
        string origin = BaseHref(html) ?? baseUrl;

        int i = 0;
        int headDepth = 0;      // >0 = 正在 <head> 里，这期间不往正文写

        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0) { if (headDepth == 0) AppendText(sb, html[i..]); break; }
            if (lt > i && headDepth == 0) AppendText(sb, html[i..lt]);

            int gt = html.IndexOf('>', lt);
            if (gt < 0) break;                       // 没闭合的标签，后面全是它的内容，丢掉

            string tag = html[(lt + 1)..gt].Trim();
            i = gt + 1;

            bool closing = tag.Length > 0 && tag[0] == '/';
            string name = TagName(tag);
            if (name.Length == 0) continue;

            // **不要把 <head> 整块跳过去** —— <link rel=stylesheet> 与 <script src> 恰恰全长在
            // 里面。实测 doc.rust-lang.org/book/ 的 14 个 <link> 全在 head 里、你自己的
            // nav.tinger.host 那个 style.css 也是，跳过 head 等于一条样式表都收不到，
            // 而现象只是「引用清单莫名其妙很干净」。
            if (name == "head")
            {
                headDepth = closing ? Math.Max(0, headDepth - 1) : headDepth + 1;
                continue;
            }

            // head 里出现**放不进 head 的元素**，就当 head 到此为止 —— 浏览器就是这么做的
            // （HTML 规范的 "in head" 插入模式）。少了这一条，一个忘了写 </head> 的页面会让
            // headDepth 一直停在 1，把整页正文全吃掉；而那种页面在浏览器里是好好的。
            // 换句话说：正文能不能出来，不该取决于对方有没有写那个闭合标签。
            if (headDepth > 0 && !InHead(name)) headDepth = 0;

            CollectRef(name, tag, origin, refs, seen);

            // 内容既不是正文、也不可能长出引用的那几种：连内容一起跳过。
            // 这里必须跳过内容而不是只跳过标签 —— <script> 里可能出现
            // `document.write('<a href="...">')` 这种字符串，当标签读会收出一个假引用。
            if (!closing && name is "script" or "style" or "noscript" or "template" or "svg" or "iframe")
            {
                int close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (close >= 0)
                {
                    // 内联 <style> 里的 url() / @import 同样指向真实文件，一并收下来。
                    if (name == "style")
                        foreach (var r in CssRefs(html[i..close], origin))
                            if (seen.Add(r.Url)) refs.Add(r);

                    int end = html.IndexOf('>', close);
                    i = end < 0 ? html.Length : end + 1;
                }
                continue;
            }

            if (headDepth > 0) continue;             // head 里的字（title / 漏在外面的文本）不进正文

            // 块级标签换成换行：段落、列表项、表格行都是「一行」的边界，
            // 全连成一片的话模型读到的是一整坨没有结构的文字
            if (IsBlock(name)) sb.Append('\n');
            else if (name is "br" or "hr") sb.Append('\n');
            else if (name is "td" or "th") sb.Append(" | ");
        }

        return new ParsedPage(Tidy(sb.ToString()), refs);
    }

    /// <summary>
    /// 从一个标签里取地址。取哪个属性由标签名决定 —— <c>&lt;link&gt;</c> 用 <c>href</c>、
    /// <c>&lt;script&gt;</c>/<c>&lt;img&gt;</c> 用 <c>src</c>，取错了就一个都收不到。
    /// </summary>
    private static void CollectRef(string tag, string raw, string origin,
                                   List<WebRef> refs, HashSet<string> seen)
    {
        string attr = tag switch
        {
            "a" or "link" or "area" => "href",
            "script" or "img" or "source" or "iframe" or "embed" or "video" or "audio" => "src",
            _ => "",
        };
        if (attr.Length == 0) return;

        var kind = tag switch
        {
            "a" or "area" => RefKind.Page,
            "link" => RefKind.Style,
            "script" => RefKind.Script,
            "img" => RefKind.Image,
            _ => RefKind.Other,
        };

        string? url = Absolute(AttrOf(raw, attr), origin);
        if (url == null || !seen.Add(url)) return;
        refs.Add(new WebRef(kind, url));

        // 顺手把 <link> 上标的 rel 读出来，只用来把「图标 / 预加载」这类从样式里摘出去。
        // 不做的话一个页面能列出一堆 favicon 和 preconnect，把清单淹掉。
        if (tag == "link")
        {
            string rel = AttrOf(raw, "rel").ToLowerInvariant();
            if (rel.Contains("icon") || rel.Contains("preconnect") || rel.Contains("dns-prefetch")
                || rel.Contains("manifest") || rel.Contains("alternate"))
            {
                refs.RemoveAt(refs.Count - 1);
                seen.Remove(url);
            }
        }
    }

    /// <summary>取标签里某个属性的值（不带引号）。取不到返回空串。</summary>
    private static string AttrOf(string tag, string name)
    {
        int i = 0;
        while (true)
        {
            int at = tag.IndexOf(name, i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";
            // 必须是独立的属性名：前面是空白或标签开头，后面（跳过空白）是 '='
            bool leftOk = at == 0 || char.IsWhiteSpace(tag[at - 1]);
            int j = at + name.Length;
            while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
            if (leftOk && j < tag.Length && tag[j] == '=')
            {
                j++;
                while (j < tag.Length && char.IsWhiteSpace(tag[j])) j++;
                if (j >= tag.Length) return "";
                char q = tag[j];
                if (q is '"' or '\'')
                {
                    int end = tag.IndexOf(q, j + 1);
                    return end < 0 ? "" : tag[(j + 1)..end];
                }
                int stop = tag.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '>' }, j);
                return stop < 0 ? tag[j..] : tag[j..stop];
            }
            i = at + name.Length;
        }
    }

    private static string? BaseHref(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            html, @"<base\b[^>]*\bhref\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string v = m.Groups[2].Success ? m.Groups[2].Value
                 : m.Groups[3].Success ? m.Groups[3].Value
                 : m.Groups[4].Value;
        return v.Trim().Length == 0 ? null : v.Trim();
    }

    /// <summary>
    /// 把标签里那个地址补成绝对的。补不出来的（<c>javascript:</c>、<c>mailto:</c>、<c>#锚点</c>、
    /// <c>data:</c>）返回 null —— 这些既抓不了，列出来也只是噪声。
    /// </summary>
    private static string? Absolute(string href, string origin)
    {
        href = href.Trim();
        if (href.Length == 0) return null;
        if (href.StartsWith('#')) return null;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var b)) return null;
        if (!Uri.TryCreate(b, href, out var abs)) return null;
        if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return null;
        if (abs.Scheme == b.Scheme && abs.Host == b.Host && abs.AbsolutePath == b.AbsolutePath) return null;  // 自己

        string s = abs.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                                     UriFormat.UriEscaped);
        return s;
    }

    /// <summary>
    /// 从一个样式表里取引用：<c>@import "x"</c> 与 <c>url(x)</c>。
    /// 外部 .css 文件与页面内联的 <c>&lt;style&gt;</c> 走的是同一个函数 —— 两者语法一样，
    /// 而内联那块的 <c>url()</c> 同样指向真实文件，只收外部的会漏掉一批背景图与字体。
    ///
    /// 只做 CSS 不做 JS：<c>url()</c> 和 <c>@import</c> 是明确的语法，而 JS 的模块说明符
    /// 形式太多（裸名、路径别名、打包器重写），猜出来的多半是噪声 —— 模型拿着噪声去抓，
    /// 只是浪费抓取次数。脚本的依赖留给模型自己看内容判断。
    /// </summary>
    public static List<WebRef> CssRefs(string css, string baseUrl)
    {
        var list = new List<WebRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var m = System.Text.RegularExpressions.Regex.Matches(
            css, @"@import\s+(?:url\()?\s*[""']?([^""')\s;]+)|url\(\s*[""']?([^""')\s]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match x in m)
        {
            string v = x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value;
            if (v.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            string? url = Absolute(v.Trim(), baseUrl);
            if (url == null || !seen.Add(url)) continue;

            bool img = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
            list.Add(new WebRef(img ? RefKind.Image : RefKind.Style, url));
        }
        return list;
    }

    /// <summary>
    /// 这个元素能不能出现在 <c>&lt;head&gt;</c> 里（HTML 规范 "in head" 插入模式允许的那几种）。
    /// 用来判断「head 到头了没有」，见 <see cref="Parse"/> 主循环里那条注释。
    /// </summary>
    private static bool InHead(string name) => name is
        "base" or "basefont" or "bgsound" or "link" or "meta" or "noframes"
        or "script" or "style" or "template" or "title";

    /// <summary>标签名（小写）；注释、!DOCTYPE、闭合标签都归到空串。</summary>
    private static string TagName(string tag)
    {
        if (tag.Length == 0 || tag[0] == '!' || tag[0] == '?') return "";
        int i = tag[0] == '/' ? 1 : 0;
        int start = i;
        while (i < tag.Length && char.IsLetterOrDigit(tag[i])) i++;
        return tag[start..i].ToLowerInvariant();
    }

    private static bool IsBlock(string name) => name is
        "p" or "div" or "section" or "article" or "header" or "footer" or "main" or "aside"
        or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "tr" or "table"
        or "ul" or "ol" or "dl" or "dt" or "dd" or "blockquote" or "pre" or "form" or "nav";

    private static void AppendText(StringBuilder sb, string s) => sb.Append(System.Net.WebUtility.HtmlDecode(s));

    /// <summary>压空白：连续空格并成一个、连续空行并成一个，并去掉行首尾空白。</summary>
    private static string Tidy(string s)
    {
        var outp = new StringBuilder(s.Length);
        var line = new StringBuilder();
        bool blank = false;

        void Flush()
        {
            string t = line.ToString().Trim();
            line.Clear();
            if (t.Length == 0)
            {
                if (blank || outp.Length == 0) return;   // 开头的空行不要
                blank = true;
                outp.Append('\n');
                return;
            }
            if (outp.Length > 0) outp.Append('\n');
            outp.Append(t);
            blank = false;
        }

        foreach (char c in s)
        {
            if (c == '\n') { Flush(); continue; }
            // 全角空格（&nbsp; 解出来的）也当分隔符 —— 不处理的话一行里会留一串「 」，
            // 而这种「空白字符」在模型眼里会让整段文字显得支离破碎。
            if (c == '\t' || c == ' ' || c == '　') { line.Append(' '); continue; }
            line.Append(c);
        }
        Flush();

        string[] raw = outp.ToString().Split('\n');
        for (int i = 0; i < raw.Length; i++) raw[i] = Collapse(raw[i]);
        return string.Join('\n', raw).Trim();
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool sp = false;
        foreach (char c in s)
        {
            if (c == ' ')
            {
                if (!sp) sb.Append(' ');
                sp = true;
            }
            else { sb.Append(c); sp = false; }
        }
        return sb.ToString();
    }
}
