using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace BangGang;

/// <summary>
/// 读 .docx 的正文文字。
///
/// 只取文字，不碰样式（字号、颜色、加粗一律丢掉）—— 模型要的是「文件里写了什么」，
/// 把样式也塞进上下文既占地方又没什么用。
/// </summary>
internal static class DocxReader
{
    public static string Read(ZipArchive zip, int maxChars)
    {
        var doc = Ooxml.Load(zip, "word/document.xml");
        if (doc == null)
            return "（这个 .docx 里没有 word/document.xml，可能不是有效的 Word 文档，或者文件损坏了）";

        var sb = new StringBuilder();
        bool cut = false;

        // 按文档顺序取所有段落。表格单元格里的段落也在其中，所以表格内容会自然
        // 一行一行地出来 —— 单元格之间没有分隔符，但拿到的至少是原顺序的文字。
        foreach (var p in Ooxml.ByLocal(doc, "p"))
        {
            AppendParagraph(p, sb);
            sb.Append('\n');
            if (sb.Length > maxChars) { cut = true; break; }
        }

        if (!cut && sb.Length == 0)
            return "（这个 Word 文档里没有文字内容）";

        string text = sb.ToString().TrimEnd('\n');
        if (cut) text += "\n\n…（文档很长，只读了前一部分）";
        return text;
    }

    /// <summary>
    /// 把一个 <c>w:p</c> 里的文字拼起来。
    ///
    /// 只看 <c>t</c>（文本）/ <c>tab</c>（制表符）/ <c>br</c>、<c>cr</c>（换行）这几种，
    /// 其余（书签、批注引用、域代码的指令部分）都要跳过：<c>instrText</c> 里是
    /// <c>MERGEFIELD …</c> 这种域代码，把它当正文读出来就成了正文里混进一行乱码。
    /// </summary>
    private static void AppendParagraph(XElement p, StringBuilder sb)
    {
        foreach (var n in p.Descendants())
        {
            switch (n.Name.LocalName)
            {
                case "t": sb.Append(n.Value); break;
                case "tab": sb.Append('\t'); break;
                case "br":
                case "cr": sb.Append('\n'); break;
            }
        }
    }
}
