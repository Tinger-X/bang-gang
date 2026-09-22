namespace BangGang;

/// <summary>
/// 读本机文件。**文本文件与 Office 文档合成了一个工具**（不是两个）：
/// 模型不该被迫先判断「这个文件该用哪个工具」，而两者的处理器本来就重叠 ——
/// 分开只会凭空多一个它选错的场合。
///
/// 受理范围直接复用附件的白名单 <see cref="AttachTypes"/>，不另造一套：
/// 让「能拖进输入框的文件」和「模型能读的文件」是同一批，用户不用记两套规则。
/// </summary>
internal static class FileTool
{
    /// <summary>文本文件一次最多读多少字节。整读一个大日志会白占内存，读进来也只是被截断。</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private const string ParamsJson = """
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "文件路径。可以是完整路径，也可以只给文件名（会在本次对话的附件、桌面、文档、下载目录里找）。支持文本/源码文件，以及 .docx / .xlsx / .pptx。"
        },
        "max_chars": {
          "type": "integer",
          "description": "最多返回多少个字符，默认 8000。"
        }
      },
      "required": ["path"]
    }
    """;

    public static readonly ToolDef Def = new()
    {
        Name = "read_file",
        Label = "文件读取",
        Desc = "读取本机上某个文件的内容，支持文本与源码文件（txt/md/csv/json/log/各种代码），" +
               "以及 Word（.docx）、Excel（.xlsx）、PowerPoint（.pptx）。" +
               "当用户说「读一下这个文件」「刚才那个附件里写了什么」时用它。" +
               "读不了 PDF 与老版 .doc/.xls/.ppt，那些格式请让用户提供文本或另存为新格式。",
        Params = ParamsJson,
        Primary = "path",
        Brief = a => "读取 " + Name(a.Str("path")),
        Run = (a, ctx, _) => Task.FromResult(Run1(a, ctx)),
    };

    private static string Run1(ToolArgs args, ToolContext ctx)
    {
        string raw = args.Str("path").Trim();
        if (raw.Length == 0)
            return "没有收到文件路径。请在 path 参数里给出要读的文件，例如 {\"path\":\"报告.docx\"}。";

        int cap = args.Int("max_chars", 8000, 200, ToolRunner.MaxResultChars);

        string? path = Resolve(raw, ctx);
        if (path == null)
            return "找不到文件「" + raw + "」。可以把完整路径再试一次；" +
                   "如果是本次对话里拖进来的附件，直接用附件的文件名也行。";

        try
        {
            if (Directory.Exists(path))
                return "「" + path + "」是一个文件夹，不是文件。请给出具体文件的路径。";

            var fi = new FileInfo(path);
            if (!fi.Exists) return "文件不存在：" + path;

            string ext = AttachTypes.ExtOf(path);
            switch (AttachTypes.CatOf(path))
            {
                case AttachCat.Text:
                    return Head(path, fi.Length) + ReadText(path, cap);

                case AttachCat.Doc:
                    {
                        // 文档类整体交给 DocText：读不了的格式（PDF / 老 .doc）它会
                        // 回一句能转述给用户的话，而不是一个异常。
                        string body = DocText.Read(path, ext, cap);
                        return Head(path, fi.Length) + body;
                    }

                case AttachCat.Image:
                    return "「" + Path.GetFileName(path) + "」是一张图片，我读不了图里的内容。" +
                           "请让用户把这张图**拖进输入框**发给你 —— 那样才能作为图片被看到。";

                case AttachCat.Audio:
                    return "「" + Path.GetFileName(path) + "」是音频，我读不了。" +
                           "如果里面有话要说，请让用户用本程序的语音转写把它变成文字。";

                default:
                    return AttachTypes.RejectReason(path);
            }
        }
        catch (Exception ex)
        {
            return "读这个文件的时候出错了：" + ex.Message;
        }
    }

    private static string Head(string path, long size) =>
        "文件：" + path + "（" + AttachTypes.SizeText(size) + "）\n---\n";

    // ---------------- 路径解析 ----------------

    /// <summary>
    /// 把模型给的字符串变成一个真实存在的路径。
    ///
    /// 顺序是有讲究的：**会话附件排在常见目录前面**。用户说「读一下我刚拖进来的报告」时
    /// 模型只会拿到一个文件名，而桌面上很可能躺着一个同名的旧版本 —— 先认附件才对得上
    /// 用户此刻的所指。
    /// </summary>
    private static string? Resolve(string raw, ToolContext ctx)
    {
        try
        {
            if (Path.IsPathRooted(raw) && File.Exists(raw)) return Path.GetFullPath(raw);
        }
        catch { /* 路径里有非法字符，继续往下试 */ }

        // 本次对话的附件
        foreach (var m in ctx.Conv.Messages)
        {
            if (m.Attachments == null) continue;
            foreach (var a in m.Attachments)
            {
                string? p = a.Path;
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                if (string.Equals(p, raw, StringComparison.OrdinalIgnoreCase)) return p;
                if (string.Equals(a.Name, raw, StringComparison.OrdinalIgnoreCase)) return p;
                try
                {
                    if (string.Equals(Path.GetFileName(p), raw, StringComparison.OrdinalIgnoreCase)) return p;
                }
                catch { /* 取不出文件名就跳过这一条 */ }
            }
        }

        // 常见目录
        foreach (string dir in CommonDirs())
        {
            try
            {
                string p = Path.Combine(dir, raw);
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
            catch { /* 这个目录拼不出来就试下一个 */ }
        }

        return null;
    }

    private static IEnumerable<string> CommonDirs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (home.Length > 0) yield return Path.Combine(home, "Downloads");
    }

    /// <summary>文件名，用于一行的摘要。取不出来就退回原文（截短）。</summary>
    private static string Name(string path)
    {
        try
        {
            string n = Path.GetFileName(path);
            if (n.Length > 0) return n.Length <= 28 ? n : n[..28] + "…";
        }
        catch { }
        return path.Length <= 28 ? path : path[..28] + "…";
    }

    // ---------------- 文本解码 ----------------

    /// <summary>
    /// 读文本文件并转成字符串。编码判断在 <see cref="TextDecode"/> 里 ——
    /// 中文 Windows 上的 .txt / .csv 大量是 GBK（记事本另存为默认就是 ANSI），
    /// 按 UTF-8 硬读会得到一屏乱码，而模型会拿这屏乱码当真去分析。
    /// </summary>
    private static string ReadText(string path, int cap)
    {
        using var fs = File.OpenRead(path);
        int len = (int)Math.Min(fs.Length, MaxBytes);
        var buf = new byte[len];
        int got = fs.Read(buf, 0, len);
        if (got < len) Array.Resize(ref buf, got);

        bool truncated = fs.Length > MaxBytes;
        string text = TextDecode.Bytes(buf);
        if (truncated) text += "\n\n…（文件很大，只读了开头 " + (MaxBytes / 1024 / 1024) + "MB）";
        if (text.Length > cap) text = text[..cap] + "\n\n…（内容过长已截断）";
        return text;
    }
}
