using System.IO.Compression;

namespace BangGang;

/// <summary>
/// Office 文档的分派入口：按扩展名决定怎么读，读不了的说清楚为什么。
///
/// **为什么有「读不了」这一档而不是尽力而为**：PDF 要解内容流与字体的 CID 映射，
/// 老版 <c>.doc/.xls/.ppt</c> 是二进制复合文档 —— 这两类用纯 BCL 做出来的是
/// 「有时能读、有时乱码」，而**乱码比明说读不了更伤**：用户拿到一段错字会以为
/// 文件本身是这样，而模型会对着乱码一本正经地分析。
/// </summary>
internal static class DocText
{
    /// <summary>能真正读出文字的。</summary>
    private static readonly string[] CanRead = { ".docx", ".xlsx", ".pptx" };

    /// <summary>是文档、但读不出文字的（老二进制格式 + PDF）。</summary>
    private static readonly string[] CannotRead = { ".pdf", ".doc", ".xls", ".ppt", ".rtf", ".odt", ".ods", ".odp", ".wps", ".et", ".dps" };

    public static bool CanReadExt(string ext) => CanRead.Contains(ext);

    public static bool IsKnownDocExt(string ext) => CanRead.Contains(ext) || CannotRead.Contains(ext);

    /// <summary>
    /// 读一个 office 文档。<paramref name="ext"/> 是小写含点扩展名（见 <see cref="AttachTypes.ExtOf"/>）。
    /// 读不出来时返回的也是**给模型看的一句说明**，不抛 —— 让模型转告用户比断在半路好。
    /// </summary>
    public static string Read(string path, string ext, int maxChars)
    {
        if (!CanReadExt(ext)) return WhyNot(ext);

        using var zip = Ooxml.Open(path);
        if (zip == null)
            return "（读不了这个文件：它可能损坏了，或者其实不是 " + ext + " 格式但改了扩展名）";

        return ext switch
        {
            ".docx" => DocxReader.Read(zip, maxChars),
            ".xlsx" => XlsxReader.Read(zip, maxChars),
            ".pptx" => PptxReader.Read(zip, maxChars),
            _ => WhyNot(ext),
        };
    }

    private static string WhyNot(string ext) => ext switch
    {
        ".pdf" => "（这是 PDF，读不了里面的文字。请让用户把需要的部分复制成文本贴出来，"
                + "或者用支持图片输入的模型当图片看。）",
        ".doc" or ".xls" or ".ppt" =>
            "（这是 Office 的老版二进制格式（" + ext + "），读不了。"
            + "请让用户用 Office / WPS 另存为新的 " + ext + "x 格式后再试。）",
        _ => "（暂不支持读 " + ext + " 这种格式。）",
    };
}
