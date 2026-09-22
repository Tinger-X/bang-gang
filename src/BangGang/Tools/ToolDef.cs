using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>
/// 一个可供模型调用的工具：名字 + 描述 + 参数 schema + 干活的函数。
///
/// <see cref="Desc"/> 是**唯一**告诉模型「这工具干什么、什么时候该用」的东西，
/// 值得当用户可见的文案来写 —— 写含糊了模型就不会用（或乱用），
/// 而模型看不见这个仓库里的任何注释。
///
/// 用对象初始化器而不是一长串构造参数：一个工具要填的东西有七八样，
/// 按位置传的话在调用处根本看不出哪个是哪个。
/// </summary>
internal sealed class ToolDef
{
    /// <summary>给模型看的名字（英文小写下划线，接口对它有字符要求）。</summary>
    public required string Name { get; init; }

    /// <summary>给用户看的中文名。折叠行、设置页的开关标题用它。</summary>
    public required string Label { get; init; }

    /// <summary>给模型看的说明。必须写清「什么时候该用它」，不能只写它是什么。</summary>
    public required string Desc { get; init; }

    /// <summary>干活的函数。返回值是**给模型看的**文字，不是给用户看的。</summary>
    public required Func<ToolArgs, ToolContext, CancellationToken, Task<string>> Run { get; init; }

    /// <summary>参数 schema 的 JSON 原文；没有参数的工具留空串。</summary>
    public string Params { get; init; } = "";

    /// <summary>
    /// 必须在 UI 线程上执行。目前只有剪贴板需要 —— <c>Clipboard.GetText()</c> 要求 STA，
    /// 而工具默认跑在线程池（MTA）上。见 <see cref="ToolRunner"/>。
    /// </summary>
    public bool NeedsUi { get; init; }

    /// <summary>
    /// 最主要那个参数的名字（如 <c>expr</c> / <c>path</c> / <c>url</c>）；没有参数的工具留空串。
    ///
    /// 只给离屏自测用（见 <see cref="OfflineTool"/>）：有了它就能写
    /// <c>BANGGANG_TOOL=calc:1+1</c> 而不是每次手打一坨 JSON。
    /// 它不参与发给模型的 schema —— 那边的一切都以 <see cref="Params"/> 为准。
    /// </summary>
    public string Primary { get; init; } = "";

    /// <summary>
    /// 一行摘要（「计算 (1234*5678)/2」「读取 报告.docx」），用于折叠行括号里和**回灌给
    /// 模型的历史**。不回灌结果全文是这套设计里最要紧的一条：读一次文件几千字，
    /// 每轮都重发的话上下文几轮就满了。不给就退回 <see cref="Label"/>。
    /// </summary>
    public Func<ToolArgs, string>? Brief { get; init; }

    public Task<string> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct) => Run(args, ctx, ct);

    /// <summary>取这一行摘要。摘要函数自己炸了不该连累整次调用，所以兜一层。</summary>
    public string BriefFor(ToolArgs args)
    {
        if (Brief == null) return Label;
        try
        {
            string s = Brief(args) ?? "";
            return s.Trim().Length == 0 ? Label : s.Trim();
        }
        catch { return Label; }
    }

    /// <summary>拼成请求体 <c>tools</c> 数组里的一项（OpenAI 的 function 形态）。</summary>
    public JsonObject Schema()
    {
        // 每次现解一遍而不是缓存 JsonNode：JsonNode 只能挂在一个父节点上，
        // 缓存一份会被第二次 Schema() 调用炸掉（"node already has a parent"）。
        JsonNode? ps = null;
        if (Params.Length > 0)
        {
            try { ps = JsonNode.Parse(Params); }
            catch { ps = null; }        // 写错的 schema 不该让整次请求发不出去
        }
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = Name,
                ["description"] = Desc,
                ["parameters"] = ps ?? new JsonObject { ["type"] = "object" },
            },
        };
    }
}

/// <summary>
/// 模型给的工具参数。**必须按「什么都可能不对」来读**：参数可能是半截 JSON、
/// 可能多给几个没用的键、也可能该给字符串的地方给了数字。
/// 读不出来就退回默认值，让工具自己用默认值去跑或者回一句「参数不对」，
/// 而不是在这里抛 —— 抛出去的那句话会原样进模型的上下文，它看不懂。
/// </summary>
internal sealed class ToolArgs
{
    private readonly JsonObject _o;

    private ToolArgs(JsonObject o) { _o = o; }

    public static ToolArgs Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ToolArgs(new JsonObject());
        try { return JsonNode.Parse(json) is JsonObject o ? new ToolArgs(o) : new ToolArgs(new JsonObject()); }
        catch { return new ToolArgs(new JsonObject()); }
    }

    /// <summary>取字符串参数；缺失、为 null、类型不对一律给 <paramref name="def"/>。</summary>
    public string Str(string key, string def = "")
    {
        var n = _o[key];
        if (n is JsonValue v && v.TryGetValue<string>(out var s) && s != null) return s;
        return def;
    }

    /// <summary>取整数参数，并夹到 <paramref name="min"/>..<paramref name="max"/>（模型经常给离谱的数字）。</summary>
    public int Int(string key, int def, int min, int max)
    {
        var n = _o[key];
        int got = def;
        if (n is JsonValue v)
        {
            if (!v.TryGetValue<int>(out got) && v.TryGetValue<double>(out var d)) got = (int)d;
        }
        return Math.Clamp(got, min, max);
    }

    public bool Has(string key) => _o.ContainsKey(key);

    /// <summary>原文，用于「把模型给的参数原样显示出来」。</summary>
    public override string ToString() => _o.ToJsonString();
}

/// <summary>
/// 一次工具执行需要的外界信息。
///
/// 做成一个对象往下传而不是给每个处理函数加一串参数：将来多一个工具要用到别的东西
/// （窗口、设置项、当前会话）时，只改这里，不用动所有处理函数的签名。
/// </summary>
internal sealed class ToolContext
{
    /// <summary>当前会话。文件工具要靠它翻附件 —— 用户说「读一下我刚拖进来的报告」时，
    /// 模型给的多半只是个文件名，真实路径只有附件里才有。</summary>
    public required Conversation Conv { get; init; }

    /// <summary>
    /// UI 线程的宿主控件，用于 <see cref="ToolDef.NeedsUi"/> 那几个工具的封送。
    ///
    /// **可以是 null**（离屏自测那条路就没有窗口），此时需要 UI 线程的工具会直接在
    /// 当前线程上跑 —— 见 <see cref="ToolRunner"/> 里对此的说明。
    /// </summary>
    public Control? UiHost { get; init; }

    /// <summary>
    /// 这一轮里网页抓取已经到过哪些网址、各在第几层（见 <c>WebFetchTool</c>）。
    ///
    /// **必须是每轮一份**，所以挂在这里（<c>RunToolLoop</c> 每轮新建一个 context），
    /// 而不是做成 WebFetchTool 的静态字段：静态的话上一轮的深度会带到下一轮，
    /// 用户换个话题随手指个链接，就会被上一轮遗留的「超出层次限制」挡下来 ——
    /// 而那个现象看起来完全像是抓取坏了。
    /// </summary>
    public WebDepth Web { get; } = new();
}
