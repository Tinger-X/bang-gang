using System.Text;

namespace BangGang;

/// <summary>
/// 把一串字节按正确的编码变成文本。文件工具和网页抓取共用。
///
/// 为什么不能直接 <c>Encoding.UTF8.GetString</c>：中文 Windows 上的 .txt / .csv
/// **大量是 GBK 而不是 UTF-8**（记事本「另存为」的默认编码就是 ANSI），
/// 中文网页也有不少仍是 GBK。按 UTF-8 硬读会得到一屏乱码，而模型会拿这屏乱码
/// 当真去分析 —— 它看不出那是编码错了，只会说「这个文件的内容似乎是乱码符号」。
/// </summary>
internal static class TextDecode
{
    /// <summary>
    /// 先看 BOM，再试严格 UTF-8（遇到非法字节会抛，于是非 ASCII 的 GBK 内容会被认出来），
    /// 最后退回 GBK。
    /// </summary>
    public static string Bytes(byte[] b) => Bytes(b, b.Length);

    public static string Bytes(byte[] b, int count)
    {
        if (count <= 0) return "";
        if (count > b.Length) count = b.Length;

        if (count >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return Encoding.UTF8.GetString(b, 3, count - 3);
        if (count >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return Encoding.Unicode.GetString(b, 2, count - 2);
        if (count >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(b, 2, count - 2);

        try
        {
            // 纯 ASCII 也能过这一关，那正好 —— 两种编码对 ASCII 是一样的。
            return new UTF8Encoding(false, true).GetString(b, 0, count);
        }
        catch (DecoderFallbackException)
        {
            return Gbk(b, count);
        }
    }

    /// <summary>
    /// 网页用：先认 HTTP 头 / HTML meta 里声明的 charset，认不出再按 <see cref="Bytes"/> 猜。
    /// 声明优先是因为它能省掉「GBK 内容里有那么几个字节刚好是合法 UTF-8」这种误判。
    /// </summary>
    public static string Html(byte[] b, int count, string? declared)
    {
        count = Math.Min(count, b.Length);
        string name = string.IsNullOrWhiteSpace(declared) ? MetaCharset(b, count) : declared;
        if (name.Length > 0)
        {
            try { return Encoding.GetEncoding(name.Trim().Trim('"', '\'')).GetString(b, 0, count); }
            catch { /* 认不出这个名字，或那个代码页没注册，就往下走通用逻辑 */ }
        }
        return Bytes(b, count);
    }

    /// <summary>
    /// 从 HTML 开头找 <c>&lt;meta charset=…&gt;</c> 或
    /// <c>&lt;meta http-equiv="Content-Type" content="…charset=…"&gt;</c> 里的编码名。
    /// 只在前 2KB 里找 —— 正经网页的 charset 一定在 head 里。
    /// </summary>
    private static string MetaCharset(byte[] b, int count)
    {
        string head = Encoding.ASCII.GetString(b, 0, Math.Min(count, 2048)).ToLowerInvariant();
        int at = head.IndexOf("charset", StringComparison.Ordinal);
        if (at < 0) return "";

        string tail = head[(at + 7)..].TrimStart('=', ' ', '"', '\'');
        int end = tail.IndexOfAny(new[] { '"', '\'', ' ', ';', '>', '\r', '\n' });
        string name = end > 0 ? tail[..end] : tail;
        // 名字短得离谱或长得离谱都不是真的编码名，当没找到
        return name.Length is > 1 and < 24 ? name : "";
    }

    private static string Gbk(byte[] b, int count)
    {
        try
        {
            // .NET Core 起非 Unicode 代码页要显式注册（程序集在共享框架里，不用引 NuGet，
            // 但不注册就取不到 936）。注册是幂等的，重复调用无副作用。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(b, 0, count);
        }
        catch
        {
            // 最后退到「UTF-8 带替换字符」：宁可留几个问号，也不能整个读不出来。
            return Encoding.UTF8.GetString(b, 0, count);
        }
    }
}
