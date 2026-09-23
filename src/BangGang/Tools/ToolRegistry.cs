using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>
/// 所有工具的登记处：清单、开关、发给模型的 schema。
///
/// **加一个工具要动的地方就三处**：写一个类（照 <see cref="NowTool"/> 那种形状）、
/// 在下面的 <see cref="All"/> 里加一行、在 <see cref="Enabled"/> 里加一个 case
/// 并去 <see cref="AppSettings"/> 添一个开关字段（别忘了同时加进 <c>CopyFrom</c>）。
/// 将来要自己实现网页搜索时，走的也是这条路，不用动别的。
/// </summary>
internal static class ToolRegistry
{
    /// <summary>发给模型的顺序。没有讲究，按重要程度排一下便于以后看日志。</summary>
    public static readonly ToolDef[] All =
    {
        NowTool.Def,
        CalcTool.Def,
        ClipboardTool.Def,
        FileTool.Def,
        WebSearchTool.Def,
        WebFetchTool.Def,
        SysInfoTool.Def,
    };

    public static ToolDef? Find(string name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));

    /// <summary>工具的中文名，用于界面上那句「已关闭「文件读取」工具」。</summary>
    public static string LabelOf(string name) => Find(name)?.Label ?? name;

    /// <summary>这个工具现在开没开。总开关关掉时一律为 false。</summary>
    public static bool Enabled(AppSettings s, string name)
    {
        if (!s.ToolsEnabled) return false;
        return name switch
        {
            "now" => s.ToolNow,
            "calc" => s.ToolCalc,
            "clipboard" => s.ToolClipboard,
            "read_file" => s.ToolFile,
            "web_search" => s.ToolWebSearch,
            "web_fetch" => s.ToolWebFetch,
            "sys_info" => s.ToolSysInfo,
            // 认不出来的名字一律不放行：这一条是给「设置里删了个工具但模型还记着它」兜底的
            _ => false,
        };
    }

    /// <summary>当前该开哪些工具、有没有开。</summary>
    public static bool AnyEnabled(AppSettings s) => All.Any(d => Enabled(s, d.Name));

    /// <summary>
    /// 拼出请求体里的 <c>tools</c> 数组。一个都没开时返回空数组，
    /// 调用方据此**干脆不带 tools 字段**（带一个空数组有些接口会报错）。
    /// </summary>
    public static JsonArray SchemaFor(AppSettings s)
    {
        var arr = new JsonArray();
        foreach (var d in All)
            if (Enabled(s, d.Name)) arr.Add(d.Schema());
        return arr;
    }
}
