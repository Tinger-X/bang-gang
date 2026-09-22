using System.IO.Compression;
using System.Text;

namespace BangGang;

/// <summary>
/// 读 .pptx 里每张幻灯片的文字。
///
/// 只读 <c>ppt/slides/</c>，不读母版与备注页 —— 母版里是「单击此处添加标题」那类占位符，
/// 每张片子都重复一遍，读出来只是噪声；备注页用户多半不想要。
/// </summary>
internal static class PptxReader
{
    public static string Read(ZipArchive zip, int maxChars)
    {
        var slides = Ooxml.Entries(zip, "ppt/slides/", ".xml");
        if (slides.Count == 0)
            return "（这个 .pptx 里没有幻灯片，可能不是有效的 PowerPoint 文件，或者文件损坏了）";

        var sb = new StringBuilder();
        int n = 0;
        foreach (var s in slides)
        {
            n++;
            if (sb.Length > maxChars) { sb.Append("\n…（后面的幻灯片未读）"); break; }

            var doc = Ooxml.Load(zip, s);
            if (doc == null) continue;

            var body = new StringBuilder();
            // 按段落取：一个 a:p 是一段，段内可能有多个 a:t（富文本分段），拼起来。
            foreach (var p in Ooxml.ByLocal(doc, "p"))
            {
                string line = string.Concat(Ooxml.ByLocal(p, "t").Select(t => t.Value)).Trim();
                if (line.Length > 0) body.Append(line).Append('\n');
            }

            if (body.Length == 0) continue;   // 纯图片的片子没有文字，不必占一行
            sb.Append("【第 ").Append(n).Append(" 张幻灯片】\n").Append(body).Append('\n');
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length == 0 ? "（这些幻灯片里没有文字内容，可能都是图片）" : text;
    }
}
