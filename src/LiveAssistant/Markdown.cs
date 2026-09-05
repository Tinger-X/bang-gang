using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;

namespace LiveAssistant;

/// <summary>轻量 Markdown 排版：标题/加粗/斜体/行内代码/代码块/列表/段落。
/// 支持换行测量与绘制，供气泡自绘使用。</summary>
internal static class Markdown
{
    internal sealed class Layout
    {
        public List<PhysLine> Lines { get; } = new();
        public float Width;   // 内容宽（含 padding 之后使用）
        public float Height;
    }

    internal sealed class PhysLine
    {
        public List<Run> Runs { get; } = new();
        public float LineHeight;
        public bool CodeBlock;
        public float SpaceBefore; // 额外段前距
        public float? Indent;     // 非空表示整体缩进（如代码/引用）
    }

    internal sealed class Run
    {
        public string Text = "";
        public Font Font = Theme.UI(12f);
        public Color Color;
        public float Width;
    }

    private sealed class Para
    {
        public string Kind = "p";  // p | code | bullet | number | h1..h6
        public string Text = "";
        public int Num;
    }

    // ---------- 解析成块 ----------

    private static List<Para> Parse(string md)
    {
        var ps = new List<Para>();
        string[] raw = md.Replace("\r\n", "\n").Split('\n');

        bool inFence = false;
        var codeBuf = new List<string>();
        var paraBuf = new List<string>();

        void FlushPara()
        {
            if (paraBuf.Count > 0)
            {
                ps.Add(new Para { Kind = "p", Text = string.Join(" ", paraBuf) });
                paraBuf.Clear();
            }
        }

        foreach (string line0 in raw)
        {
            string line = line0.TrimEnd();
            string tl = line.TrimStart();

            if (inFence)
            {
                if (tl.StartsWith("```"))
                {
                    ps.Add(new Para { Kind = "code", Text = string.Join("\n", codeBuf) });
                    codeBuf.Clear();
                    inFence = false;
                }
                else codeBuf.Add(line);
                continue;
            }

            if (tl.StartsWith("```"))
            {
                FlushPara();
                inFence = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(tl)) { FlushPara(); continue; }

            var hm = Regex.Match(tl, @"^(#{1,6})\s+(.*)$");
            if (hm.Success)
            {
                FlushPara();
                ps.Add(new Para { Kind = "h" + hm.Groups[1].Value.Length, Text = hm.Groups[2].Value });
                continue;
            }

            var bm = Regex.Match(tl, @"^[-*+]\s+(.*)$");
            if (bm.Success) { FlushPara(); ps.Add(new Para { Kind = "bullet", Text = bm.Groups[1].Value }); continue; }

            var nm = Regex.Match(tl, @"^(\d+)[.)]\s+(.*)$");
            if (nm.Success) { FlushPara(); ps.Add(new Para { Kind = "number", Num = int.Parse(nm.Groups[1].Value), Text = nm.Groups[2].Value }); continue; }

            paraBuf.Add(tl); // 合并成一个段落
        }
        FlushPara();
        if (inFence && codeBuf.Count > 0) ps.Add(new Para { Kind = "code", Text = string.Join("\n", codeBuf) });
        return ps;
    }

    // ---------- 排版 ----------

    public static Layout Measure(string md, float capWidth)
    {
        var lay = new Layout { Width = capWidth };
        float y = 0;
        const float lineSpacing = 1.28f;
        float spacePara = 8;

        foreach (Para p in Parse(md))
        {
            if (p.Kind == "code")
            {
                foreach (string codeLine in p.Text.Split('\n'))
                {
                    var pl = MakeCodeLine(codeLine, capWidth);
                    pl.CodeBlock = true;
                    pl.SpaceBefore = y == 0 ? 0 : 4;
                    y += pl.SpaceBefore + pl.LineHeight * 1.18f;
                    lay.Lines.Add(pl);
                }
                y += spacePara - 4;
                continue;
            }

            float space = p.Kind == "p" ? spacePara
                        : p.Kind.StartsWith("h") ? 10
                        : 2;
            if (y > 0) y += space;

            var (runs, font) = p.Kind.StartsWith("h") ? Heading(p) : BodyRuns(p);
            float maxW = 0;
            var wrap = WrapRuns(runs, capWidth);
            foreach (var wl in wrap)
            {
                maxW = Math.Max(maxW, wl.Width);
                var pl = new PhysLine { LineHeight = wl.LineHeight };
                pl.Runs.AddRange(wl.Runs);
                lay.Lines.Add(pl);
            }
            y += wrap.Sum(w => w.LineHeight) * lineSpacing;
        }
        lay.Height = y;
        // 自然内容宽（代码块按满宽计）
        if (lay.Lines.Any(l => l.CodeBlock)) lay.Width = capWidth;
        else lay.Width = Math.Min(capWidth, Math.Max(40f, lay.Lines.Select(l => l.Runs.Sum(r => r.Width)).DefaultIfEmpty(0).Max()));
        return lay;
    }

    private static PhysLine MakeCodeLine(string text, float capWidth)
    {
        var f = Theme.Mono(11.5f);
        var pl = new PhysLine { CodeBlock = true, Indent = 0, LineHeight = f.Height };
        if (text.Length == 0) { pl.Runs.Add(new Run { Text = " ", Font = f, Color = Theme.TextMuted }); return pl; }
        // 超长硬截断换行
        float avail = capWidth - 8;
        string cur = "";
        foreach (char c in text)
        {
            float w = TextRenderer.MeasureText(cur + c, f).Width;
            if (w > avail && cur.Length > 0)
            {
                pl.Runs.Add(new Run { Text = cur, Font = f, Color = Theme.TextMain, Width = TextRenderer.MeasureText(cur, f).Width });
                cur = "";
            }
            cur += c;
        }
        if (cur.Length > 0)
            pl.Runs.Add(new Run { Text = cur, Font = f, Color = Theme.TextMain, Width = TextRenderer.MeasureText(cur, f).Width });
        return pl;
    }

    private static (List<(string t, Font f, bool code, bool mark)> segs, Font font) Heading(Para p)
    {
        int lv = int.Parse(p.Kind[1..]);
        float size = lv switch { 1 => 21f, 2 => 18f, 3 => 16f, _ => 14f };
        var f = Theme.UI(size, lv <= 2 ? FontStyle.Bold : FontStyle.Bold);
        return (ParseInline(p.Text, f), f);
    }

    private static (List<(string t, Font f, bool code, bool mark)> segs, Font font) BodyRuns(Para p)
    {
        var f = Theme.UI(12.5f);
        string prefix = p.Kind switch
        {
            "bullet" => "•  ",
            "number" => p.Num + ". ",
            _ => ""
        };
        return (ParseInline(prefix + p.Text, f), f);
    }

    /// <summary>行内解析为带样式的片段。</summary>
    private static List<(string t, Font f, bool code, bool mark)> ParseInline(string s, Font baseFont)
    {
        var list = new List<(string, Font, bool, bool)>();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '`')
            {
                int j = s.IndexOf('`', i + 1);
                if (j > i) { list.Add((s[(i + 1)..j], Theme.Mono(baseFont.SizeInPoints - 1f), true, false)); i = j + 1; continue; }
                list.Add(("`", baseFont, false, false)); i++;
            }
            else if (s[i] == '*' || s[i] == '_')
            {
                char ch = s[i];
                int j = i + 1;
                if (j < s.Length && s[j] == ch) // **
                {
                    int k = s.IndexOf(ch.ToString() + ch, j + 1, StringComparison.Ordinal);
                    if (k > j) { list.Add((s[(j + 1)..k], new Font(baseFont.FontFamily, baseFont.SizeInPoints, FontStyle.Bold), false, false)); i = k + 2; continue; }
                    i++;
                }
                else // 单星 斜体
                {
                    int k = s.IndexOf(ch, j + 1);
                    if (k > j)
                    {
                        string inner = s[(j + 1)..k];
                        // 星号可能来自 ** （** 已在上面处理，此处置普通斜体）
                        if (!inner.Contains('*') && !inner.Contains('_'))
                        {
                            list.Add((inner, new Font(baseFont.FontFamily, baseFont.SizeInPoints, FontStyle.Italic), false, false));
                            i = k + 1; continue;
                        }
                    }
                }
                // 不作为标记：作为普通字符输出（避免因 `**` 已消费而产生残留）
                list.Add((ch.ToString(), baseFont, false, false)); i++;
            }
            else
            {
                // 累积普通文本直到下一个标记
                int k = i;
                while (k < s.Length && s[k] != '`' && s[k] != '*' && s[k] != '_') k++;
                if (k > i) list.Add((s[i..k], baseFont, false, false));
                i = k;
            }
        }
        return list;
    }

    /// <summary>把一串片段折行成物理行。</summary>
    private static List<(List<Run> Runs, float Width, float LineHeight)> WrapRuns(
        List<(string t, Font f, bool code, bool mark)> segs, float capWidth)
    {
        var result = new List<(List<Run>, float, float)>();
        var cur = new List<Run>();
        float curW = 0;
        float curH = 0;
        Font? curFont = null;

        void Flush()
        {
            if (cur.Count == 0) return;
            result.Add((cur, curW, curH));
            cur = new List<Run>();
            curW = 0; curH = 0;
        }

        foreach (var (t, f, code, mark) in segs)
        {
            Color cc = code ? Theme.Accent : Theme.TextMain;
            string[] words = t.Split(' ');
            for (int wi = 0; wi < words.Length; wi++)
            {
                string w = words[wi];
                bool last = wi == words.Length - 1;
                string piece = last ? w : w + " ";
                if (piece.Length == 0) continue;
                float ww = TextRenderer.MeasureText(piece, f).Width;
                if (curW + ww > capWidth && cur.Count > 0) Flush();
                // 单词本身超宽：硬切
                if (ww > capWidth)
                {
                    foreach (string sub in HardSplit(piece, f, capWidth))
                    {
                        float sw = TextRenderer.MeasureText(sub, f).Width;
                        if (curW + sw > capWidth) Flush();
                        cur.Add(new Run { Text = sub, Font = f, Color = cc, Width = sw });
                        curW += sw; curH = Math.Max(curH, f.Height);
                    }
                }
                else
                {
                    cur.Add(new Run { Text = piece, Font = f, Color = cc, Width = ww });
                    curW += ww;
                    curH = Math.Max(curH, f.Height);
                }
            }
        }
        Flush();
        return result;
    }

    private static IEnumerable<string> HardSplit(string s, Font f, float capWidth)
    {
        var parts = new List<string>();
        string cur = "";
        foreach (char c in s)
        {
            if (TextRenderer.MeasureText(cur + c, f).Width > capWidth && cur.Length > 0)
            {
                parts.Add(cur); cur = "";
            }
            cur += c;
        }
        if (cur.Length > 0) parts.Add(cur);
        return parts;
    }

    // ---------- 绘制 ----------

    public static float Draw(Graphics g, Layout lay, float x, float y, float capWidth)
    {
        float cursor = y;
        foreach (var pl in lay.Lines)
        {
            cursor += pl.SpaceBefore;
            float baseline = cursor + 1;

            if (pl.CodeBlock)
            {
                var rc = new RectangleF(x, cursor, capWidth, pl.LineHeight);
                using var bg = new SolidBrush(Color.FromArgb(245, 247, 250));
                using var path = Rounded(rc, 6);
                g.FillPath(bg, path);
            }

            float runX = x + (pl.Indent ?? 0) + (pl.CodeBlock ? 7 : 0);
            foreach (var r in pl.Runs)
            {
                using var br = new SolidBrush(r.Color == Color.Empty ? Theme.TextMain : r.Color);
                g.DrawString(r.Text, r.Font, br, runX, baseline);
                runX += r.Width;
            }
            cursor += pl.LineHeight * 1.28f;
        }
        return cursor - y;
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
