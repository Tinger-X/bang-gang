namespace BangGang;

/// <summary>附件的类别。决定图标底色、「PDF · 625KB」里那半句，以及收不收这个文件。</summary>
internal enum AttachCat { None, Image, Text, Doc, Audio }

/// <summary>
/// 附件类型白名单：**只收图片、明文文本、office 文档（含 PDF）、音频**，其余一律拒收。
///
/// 为什么要有白名单而不是照单全收：附件最终是要交给模型读的，模型读不了的东西
/// （可执行文件、压缩包、音视频之外的二进制）收进来只会让用户在「为什么它没看懂」上耗时间。
/// 拒收时给一句明确的理由（见 <see cref="RejectReason"/>），比默默收下强。
///
/// 音频这一类是给**本应用自己的录音功能**留的：Alt+V 录完的那条 wav 走的是同一个
/// <c>InputPanel.AddFile</c>，不认它就把自家功能挡在门外了。
///
/// 白名单写成**扩展名表**而不是 MIME / 文件头嗅探：这里判的是「该不该收」，
/// 用户改个后缀就该被当成新类型重判，没必要动文件内容。真正的解析（图片解码）
/// 由各自的加载路径负责，失败了自己会返回 null。
/// </summary>
internal static class AttachTypes
{
    // 图片：能被 Image.FromFile 解出来、也能塞进多模态消息的类型。
    private static readonly string[] ImageExt =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
    };

    // 明文文本：包括源码 —— 用户点名要的 cpp 就在这一类里。
    private static readonly string[] TextExt =
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv",
        ".html", ".htm", ".xml", ".json", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml",
        ".sql", ".cs", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".java", ".py", ".js", ".ts",
        ".jsx", ".tsx", ".css", ".scss", ".sh", ".bat", ".ps1", ".go", ".rs", ".rb", ".php", ".vue",
    };

    // office 文档 + PDF + 几家国产办公套件的格式。
    private static readonly string[] DocExt =
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".rtf", ".odt", ".ods", ".odp", ".wps", ".et", ".dps",
    };

    // 音频：本应用录音功能的产物（以及用户自己丢进来的录音）。
    private static readonly string[] AudioExt =
    {
        ".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma",
    };

    /// <summary>全部受理的扩展名，给「选择文件」对话框拼过滤器用。</summary>
    public static string[] All()
    {
        var all = new List<string>(ImageExt.Length + TextExt.Length + DocExt.Length + AudioExt.Length);
        all.AddRange(ImageExt);
        all.AddRange(TextExt);
        all.AddRange(DocExt);
        all.AddRange(AudioExt);
        return all.ToArray();
    }

    /// <summary>按扩展名判类别；不在白名单里的一律 <see cref="AttachCat.None"/>。</summary>
    public static AttachCat CatOf(string? path)
    {
        string ext = ExtOf(path);
        if (ext.Length == 0) return AttachCat.None;
        if (ImageExt.Contains(ext)) return AttachCat.Image;
        if (TextExt.Contains(ext)) return AttachCat.Text;
        if (DocExt.Contains(ext)) return AttachCat.Doc;
        if (AudioExt.Contains(ext)) return AttachCat.Audio;
        return AttachCat.None;
    }

    /// <summary>小写扩展名（含点）；没有扩展名时是空串。</summary>
    public static string ExtOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetExtension(path).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>大写扩展名（不含点），如 "PDF"；取不到时是空串。</summary>
    public static string ExtLabel(string? path)
    {
        string e = ExtOf(path);
        return e.Length > 1 ? e[1..].ToUpperInvariant() : "";
    }

    public static string CatName(AttachCat c) => c switch
    {
        AttachCat.Image => "图片",
        AttachCat.Text => "文本",
        AttachCat.Doc => "文档",
        AttachCat.Audio => "音频",
        _ => "文件",
    };

    /// <summary>
    /// 第二行的前半段：**类型**。一般就是扩展名（「PDF」），图片与认不出的给类别名。
    ///
    /// 和 <see cref="MetaTail"/> 拆开是因为它们**颜色不同**：类型按类别上色、
    /// 大小保持灰的（见 <c>DraftStrip.DrawMeta</c>）。合成一串画的话整行只能有一个颜色。
    /// 要整串（比如日志里）就自己拼这两段。
    /// </summary>
    public static string MetaHead(Attachment a)
    {
        var cat = CatOf(a.Path);
        // 图片给的是缩略图，「PNG」那三个字母不如直接说它是张图；其余类型报扩展名。
        return cat == AttachCat.Image || cat == AttachCat.None ? CatName(cat) : ExtLabel(a.Path);
    }

    /// <summary>
    /// 第二行的后半段：大小。**自带前面那个分隔符**（「 · 625KB」），没有大小时是空串 ——
    /// 让分隔符跟着它走，调用方就不必去判断「前一截在不在」。
    /// </summary>
    public static string MetaTail(Attachment a)
    {
        string size = SizeText(a.Size);
        return size.Length == 0 ? "" : " · " + size;
    }

    /// <summary>字节数转「625KB」这种短文案；拿不到大小时返回空串。</summary>
    public static string SizeText(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024) return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1000) return Math.Round(kb) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1000) return mb.ToString("0.#") + " MB";
        return (mb / 1024.0).ToString("0.#") + " GB";
    }

    /// <summary>文件大小；文件不在 / 读不到时返回 0（附件照样收，只是少半句文案）。</summary>
    public static long SizeOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>拒收时给用户看的理由。</summary>
    public static string RejectReason(string path)
    {
        string name = "";
        try { name = Path.GetFileName(path); } catch { }
        string ext = ExtLabel(path);
        string what = ext.Length > 0 ? "「." + ext.ToLowerInvariant() + "」" : "这个";
        return "不支持" + what + "文件：" + name + "（仅支持图片、文本文件、office 文档与音频）";
    }
}
