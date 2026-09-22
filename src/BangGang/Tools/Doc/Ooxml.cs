using System.IO.Compression;
using System.Xml.Linq;

namespace BangGang;

/// <summary>
/// 读 OOXML（.docx / .xlsx / .pptx）用的一点点公共装备。
///
/// 这三种格式本质上都是「一个 zip，里面按固定路径装着几个 XML」，
/// 所以用 BCL 的 <see cref="ZipFile"/> + <see cref="XDocument"/> 就能读，
/// 不需要引任何 Office 库 —— 本仓库零第三方依赖（见 CLAUDE.md）。
///
/// **一律按元素的 local name 找**（<c>w:p</c> 只认 <c>p</c>），不匹配命名空间 URI：
/// 同一个格式在不同软件（Word / WPS / LibreOffice / 各种在线导出）里产出的
/// 命名空间前缀和 URI 版本并不完全一致，写死 URI 会在某些文件上安静地一个节点都找不到。
/// </summary>
internal static class Ooxml
{
    /// <summary>打开 zip。不是合法的 zip / 文件被占用都返回 null（调用方给一句人话）。</summary>
    public static ZipArchive? Open(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch { return null; }
    }

    /// <summary>
    /// 找某个条目并解析成 XML；条目不在或 XML 坏了返回 null。
    ///
    /// 匹配时把条目名里的 <c>\</c> 换成 <c>/</c> 再比（见 <see cref="Norm"/>），
    /// 且不区分大小写。
    /// </summary>
    public static XDocument? Load(ZipArchive zip, string entry)
    {
        var e = Find(zip, entry);
        if (e == null) return null;
        try
        {
            using var s = e.Open();
            return XDocument.Load(s);
        }
        catch { return null; }
    }

    private static ZipArchiveEntry? Find(ZipArchive zip, string entry)
    {
        foreach (var e in zip.Entries)
            if (Norm(e.FullName).Equals(entry, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>
    /// 把 zip 条目名里的反斜杠换成正斜杠。
    ///
    /// 规范的 OOXML 一律用 <c>/</c>，但**用 Windows 自带工具重打包过的文件是 <c>\</c>**
    /// （<c>ZipFile.CreateFromDirectory</c> 在 .NET Framework 上就写成反斜杠，
    /// 本仓库造测试样本时实测撞上）。不多这一下的话，那种文件会被判成「不是有效的 Word 文档」，
    /// 而用户完全无从判断差别在哪。
    /// </summary>
    public static string Norm(string name) => name.Replace('\\', '/');

    /// <summary>某个 zip 里所有匹配的文件名，按**文件名里的数字**排序（slide2 要排在 slide10 前面）。</summary>
    public static List<string> Entries(ZipArchive zip, string prefix, string suffix)
    {
        var names = new List<string>();
        foreach (var e in zip.Entries)
        {
            string n = Norm(e.FullName);
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                names.Add(e.FullName);          // 存原名，后面还要拿它去取条目
        }
        names.Sort((a, b) => Num(a).CompareTo(Num(b)));
        return names;

        static int Num(string s)
        {
            int i = s.Length - 1;
            while (i >= 0 && char.IsDigit(s[i])) i--;
            return int.TryParse(s[(i + 1)..], out int v) ? v : 0;
        }
    }

    /// <summary>按 local name 取所有后代元素。</summary>
    public static IEnumerable<XElement> ByLocal(XContainer c, string local) =>
        c.Descendants().Where(e => e.Name.LocalName == local);

    /// <summary>取一个元素的属性（按 local name，理由同类型注释）。</summary>
    public static string Attr(XElement e, string local)
    {
        foreach (var a in e.Attributes())
            if (a.Name.LocalName == local) return a.Value;
        return "";
    }

    /// <summary>
    /// 把 Excel 的单元格引用（<c>A1</c> / <c>AB12</c>）换算成 0 起的列号。
    /// 空列必须补出来，否则一张「B 列有值、A 列空着」的表读出来会整体左移一格，
    /// 模型拿到手就会把内容认到错误的列上。
    /// </summary>
    public static int ColOf(string cellRef)
    {
        int col = 0;
        foreach (char c in cellRef)
        {
            if (c >= 'A' && c <= 'Z') col = col * 26 + (c - 'A' + 1);
            else if (c >= 'a' && c <= 'z') col = col * 26 + (c - 'a' + 1);
            else break;
        }
        return col - 1;
    }
}
