using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace BangGang;

/// <summary>
/// 读 .xlsx 的单元格内容，按工作表输出成「一行一行的文字」。
///
/// 三件不做就会读错的事：
///   1. 字符串不在表里 —— 单元格存的是 <c>sharedStrings.xml</c> 里的**下标**，要换回来；
///   2. 空格要补出来 —— 单元格引用是 <c>B2</c> 这种，跳过空列会把整张表左移一格；
///   3. 日期不是日期 —— Excel 里日期就是个数字（序列号），要按单元格的格式号判断
///      它到底是不是日期，否则用户看到的是「45123」这种。
/// </summary>
internal static class XlsxReader
{
    /// <summary>每张表最多读多少行。表格动辄几万行，全读进来不但没用，还会把上下文占满。</summary>
    private const int MaxRowsPerSheet = 300;

    public static string Read(ZipArchive zip, int maxChars)
    {
        var sheets = Ooxml.Entries(zip, "xl/worksheets/", ".xml");
        if (sheets.Count == 0)
            return "（这个 .xlsx 里没有工作表，可能不是有效的 Excel 文件，或者文件损坏了）";

        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);
        bool date1904 = Uses1904(zip);
        var names = ReadSheetNames(zip);

        var sb = new StringBuilder();
        for (int i = 0; i < sheets.Count; i++)
        {
            if (sb.Length > maxChars) { sb.Append("\n\n…（表格很多，只读了前几张工作表）"); break; }

            // 工作表名字：workbook.xml 里按顺序列着，而 sheet1.xml / sheet2.xml 也基本
            // 按同样的顺序生成（Excel / WPS / LibreOffice 都是）。
            // 这是**位置对应**的猜测而不是查关系表（真查要解 xl/_rels/workbook.xml.rels
            // 再对上 r:id）；对不上的时候退回「工作表 N」，至少不会把名字安错到别的表上。
            string name = i < names.Count ? names[i] : "工作表 " + (i + 1);
            sb.Append("【").Append(name).Append("】\n");

            var doc = Ooxml.Load(zip, sheets[i]);
            if (doc == null) { sb.Append("（读不出来）\n\n"); continue; }
            ReadSheet(doc, shared, dateStyles, date1904, sb, maxChars);
            sb.Append('\n');
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length == 0 ? "（这个表格里没有内容）" : text;
    }

    private static void ReadSheet(XDocument doc, List<string> shared, bool[] dateStyles,
                                  bool date1904, StringBuilder sb, int maxChars)
    {
        var rows = Ooxml.ByLocal(doc, "row").ToList();
        int shown = 0;
        foreach (var row in rows)
        {
            if (++shown > MaxRowsPerSheet)
            {
                sb.Append("…（这张表还有 ").Append(rows.Count - MaxRowsPerSheet).Append(" 行未显示）\n");
                break;
            }
            if (sb.Length > maxChars) { sb.Append("…（内容过长，已截断）\n"); break; }

            // 先按列号摊平再拼：空列要留出位置，否则 B 列的值会被当成 A 列。
            var cells = new SortedDictionary<int, string>();
            foreach (var c in Ooxml.ByLocal(row, "c"))
            {
                int col = Ooxml.ColOf(Ooxml.Attr(c, "r"));
                if (col < 0) col = cells.Count;                 // 没有 r 属性的（少见）按顺序摆
                cells[col] = ValueOf(c, shared, dateStyles, date1904);
            }
            if (cells.Count == 0) { sb.Append('\n'); continue; }   // 整行空着，保留一个空行

            int last = cells.Keys.Max();
            var parts = new string[last + 1];
            for (int i = 0; i <= last; i++) parts[i] = "";
            foreach (var kv in cells) parts[kv.Key] = kv.Value;
            sb.Append(string.Join(" | ", parts)).Append('\n');
        }
    }

    private static string ValueOf(XElement c, List<string> shared, bool[] dateStyles, bool date1904)
    {
        string type = Ooxml.Attr(c, "t");
        string v = Ooxml.ByLocal(c, "v").FirstOrDefault()?.Value ?? "";

        switch (type)
        {
            case "s":
                return int.TryParse(v, out int idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
            case "inlineStr":
                // 内联字符串：文字直接写在 <is> 里（多个 <t> 是富文本分段，拼起来）
                return string.Concat(Ooxml.ByLocal(c, "t").Select(t => t.Value));
            case "str":
                return v;                       // 公式算出来的字符串结果
            case "b":
                return v == "1" ? "TRUE" : "FALSE";
            case "e":
                return v;                       // 错误值（#DIV/0! 之类）原样给出，本来就有意义
        }

        // 到这里是数字。判断它是不是被套了日期格式 —— 是的话把序列号换算回日期，
        // 不然一张考勤表读出来满屏都是 45123 这种数。
        if (v.Length == 0) return "";
        int styleIdx = 0;
        _ = int.TryParse(Ooxml.Attr(c, "s"), out styleIdx);
        if (styleIdx >= 0 && styleIdx < dateStyles.Length && dateStyles[styleIdx]
            && double.TryParse(v, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double serial))
        {
            try
            {
                // 1904 工作簿的序列号起点比 1900 那套晚 4 年 1 天，两者差 1462 天。
                if (date1904) serial += 1462;
                var d = DateTime.FromOADate(serial);
                // 只有日期没有时间时不要拖一串 00:00:00 出来
                return d.TimeOfDay == TimeSpan.Zero
                    ? d.ToString("yyyy-MM-dd")
                    : d.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { /* 序列号越界（负值等），退回原样显示数字 */ }
        }
        return v;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var doc = Ooxml.Load(zip, "xl/sharedStrings.xml");
        if (doc == null) return list;
        foreach (var si in Ooxml.ByLocal(doc, "si"))
            list.Add(string.Concat(Ooxml.ByLocal(si, "t").Select(t => t.Value)));
        return list;
    }

    private static List<string> ReadSheetNames(ZipArchive zip)
    {
        var names = new List<string>();
        var doc = Ooxml.Load(zip, "xl/workbook.xml");
        if (doc == null) return names;
        var sheets = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheets");
        if (sheets == null) return names;
        foreach (var s in sheets.Elements().Where(e => e.Name.LocalName == "sheet"))
            names.Add(Ooxml.Attr(s, "name"));
        return names;
    }

    private static bool Uses1904(ZipArchive zip)
    {
        var doc = Ooxml.Load(zip, "xl/workbook.xml");
        var pr = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookPr");
        return pr != null && Ooxml.Attr(pr, "date1904") is "1" or "true";
    }

    /// <summary>
    /// 每一个「单元格样式号」是不是日期格式。
    ///
    /// 内置的日期格式号是固定的（14–22 与 45–47）；自定义格式（号 ≥ 164）要看
    /// <c>formatCode</c> 里有没有年月日时分的占位符。判之前先把引号里的字面量
    /// 和转义字符去掉 —— 否则 <c>0.00"m"</c>（一个带 m 单位的数字格式）会被误判成日期。
    /// </summary>
    private static bool[] ReadDateStyles(ZipArchive zip)
    {
        var doc = Ooxml.Load(zip, "xl/styles.xml");
        if (doc == null) return Array.Empty<bool>();

        var custom = new Dictionary<int, string>();
        foreach (var nf in Ooxml.ByLocal(doc, "numFmt"))
        {
            if (int.TryParse(Ooxml.Attr(nf, "numFmtId"), out int id))
                custom[id] = Ooxml.Attr(nf, "formatCode");
        }

        var cellXfs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs");
        if (cellXfs == null) return Array.Empty<bool>();

        var result = new List<bool>();
        foreach (var xf in cellXfs.Elements().Where(e => e.Name.LocalName == "xf"))
        {
            int id = 0;
            _ = int.TryParse(Ooxml.Attr(xf, "numFmtId"), out id);
            bool isDate = (id >= 14 && id <= 22) || (id >= 45 && id <= 47);
            if (!isDate && custom.TryGetValue(id, out var code)) isDate = LooksLikeDate(code);
            result.Add(isDate);
        }
        return result.ToArray();
    }

    private static bool LooksLikeDate(string formatCode)
    {
        var stripped = new StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < formatCode.Length; i++)
        {
            char ch = formatCode[i];
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (ch == '\\' || ch == '_' || ch == '*') { i++; continue; }   // 转义 / 占位跳过下一个
            stripped.Append(char.ToLowerInvariant(ch));
        }
        string s = stripped.ToString();
        return s.Contains('y') || s.Contains('d') || s.Contains("h:m") || s.Contains('s');
    }
}
