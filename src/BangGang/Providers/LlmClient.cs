using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>请求失败（接口返回了可读的错误说明）。</summary>
internal sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}

/// <summary>
/// 一次对话请求需要的全部参数，从 <see cref="AppSettings"/> 解析而来。
///
/// 解析单独放在这里、而不是让调用方自己翻 <c>ChatProfiles</c>，是因为「还差什么」
/// 必须只有一处判断：界面上要能一句话说清缺的是地址还是模型名，而不是发出去等接口报错。
/// </summary>
internal sealed class LlmConfig
{
    public string Url = "";
    public string ApiKey = "";
    public string Model = "";
    public bool Vision = true;
    public double Temperature = 0.7;

    /// <summary>单次回复的 token 上限；<b>0 = 不限</b>（请求里干脆不带 max_tokens，由模型自己决定）。</summary>
    public int MaxTokens = 2048;
    public string SystemPrompt = "";
    public string Reinforce = "";

    /// <summary>
    /// 上下文压缩出来的摘要（「压缩」策略）。非空时会被缀在 system 提示词**后面**。
    ///
    /// **不新插一条 system / user 消息**：<see cref="LlmClient.BuildMessages"/> 里那段注释
    /// 讲过了，不少兼容接口只认**开头那一条** system，后面再插一条要么被忽略、
    /// 要么直接报错。缀在原来那条的尾巴上，两边都不得罪。
    /// </summary>
    public string CtxSummary = "";

    /// <summary>
    /// 滑窗策略下「省略了 N 条更早的消息」这句说明。同样缀在 system 提示词后面 ——
    /// 不告诉模型的话，它会以为用户的第一句话就是刚才那句，对着没头没尾的上下文发呆。
    /// </summary>
    public string CtxNote = "";

    /// <summary>
    /// 要提供给模型的工具（见 <see cref="ToolRegistry.SchemaFor"/>）。null 或空 = 这次请求不带工具。
    ///
    /// <b>注意</b>：给会话起名那条路（<c>MainForm.TitleConfig</c>）是逐字段拷贝出来的，
    /// 不会自动带上这一项 —— 那正是要的，起名不该被卷进工具循环。
    /// </summary>
    public JsonArray? Tools;

    /// <summary>把服务商档案读成配置。读不到某项时**不抛异常**，只留空串，由 <see cref="Problem"/> 说缺什么。</summary>
    public static LlmConfig From(AppSettings s)
    {
        var cfg = new LlmConfig
        {
            Vision = s.ChatVision,
            Temperature = Math.Clamp(s.ChatTemperature, 0, 2),
            MaxTokens = Math.Clamp(s.ChatMaxTokens, 0, 1_000_000),
            SystemPrompt = s.ChatSystemPrompt ?? "",
            Reinforce = s.ChatReinforce ?? "",
        };
        // 只读，不调 ProfileOf —— 那个 getter 会顺手往字典里塞一个空档位，
        // 发一次消息就改动设置对象（还会被保存进 settings.json）。
        // 这里原来还有一段「旧版扁平字段兜底」（ChatApiUrl / ChatApiKey / ChatModel）。
        // 随持久化换到 SQLite 一起删了：那三个字段唯一的用途是接住 0.8.1 的 settings.json，
        // 而这次不做旧数据迁移。它也是全仓最后一处**明文存 API Key** 的路径。
        if (s.ChatProfiles != null && s.ChatProfiles.TryGetValue(s.ChatProvider ?? "", out var p) && p != null)
        {
            cfg.Url = Get(p, "url");
            cfg.ApiKey = Get(p, "key");
            cfg.Model = Get(p, "model");
        }
        return cfg;
    }

    private static string Get(Dictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) && v != null ? v.Trim() : "";

    /// <summary>还差什么才能发请求；齐了返回 null。这句话会直接显示给用户。</summary>
    public string? Problem
    {
        get
        {
            if (Url.Length == 0) return "尚未填写接口地址：打开「设置 → 模型接入」补上。";
            if (Model.Length == 0) return "尚未填写模型名：打开「设置 → 模型接入」补上。";
            return null;
        }
    }

    /// <summary>
    /// 补全成 chat/completions 那个端点。用户既可能只填到 <c>/v1</c>（预设里就是这样），
    /// 也可能直接把整条端点粘进来 —— 后者再拼一次就成了 <c>…/chat/completions/chat/completions</c>。
    /// </summary>
    public string Endpoint
    {
        get
        {
            string u = Url;
            if (u.Length == 0) return "";
            if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return u;
            return u.TrimEnd('/') + "/chat/completions";
        }
    }
}

/// <summary>
/// 一轮流式回复读完之后剩下的那点信息。
///
/// <see cref="Finish"/> 必须带出来：推理模型把**思考也算进 max_tokens**，
/// 上限小的时候思考能把整个预算吃光，正文只出来半句（甚至一个字都没有），
/// 而流本身是「正常」结束的 —— 不看这个字段就分不清「模型就这么短」和
/// 「被自己的设置截断了」。
/// </summary>
internal sealed class LlmStreamResult
{
    /// <summary>"stop" | "length" | "content_filter" | "tool_calls" …（服务端没给就是空串）。</summary>
    public string Finish = "";

    /// <summary>这一轮思考用了多少 token（服务端没报就是 0）。</summary>
    public int ReasoningTokens;

    /// <summary>
    /// 这一轮**发进去**的 token 数（服务端没报就是 0）。
    /// 它是上下文仪表唯一的权威数据，也是把本地估算「钉」在真实值上的锚点 ——
    /// 估算器只擅长算增量，有了它，误差才不会随对话变长而越滚越大。
    /// </summary>
    public int PromptTokens;

    /// <summary>这一轮**生成**的 token 数（含思考，服务端没报就是 0）。生成速度的分母用它。</summary>
    public int CompletionTokens;

    /// <summary>两者之和（服务端没报就是 0）。</summary>
    public int TotalTokens;

    /// <summary>
    /// 这一轮模型要求调用的工具，按序列里出现的位置排好、参数已拼完整。
    /// 空 = 模型正常回答完了，没有要用工具。
    /// </summary>
    public List<ToolCall> ToolCalls = new();
}

/// <summary>
/// OpenAI 兼容的流式对话客户端。只用 BCL（<see cref="HttpClient"/> + System.Text.Json）——
/// 本仓库不引 NuGet，SSE 也就自己按行拆。
///
/// 两个回调都在**后台线程**上被调用，调用方自己负责切回 UI 线程。
/// </summary>
internal static class LlmClient
{
    /// <summary>
    /// 单张图内联进 JSON 的大小上限。base64 会再涨三分之一，一张 8MB 的截图编码完就十几 MB，
    /// 发出去之前先在这里收口。
    /// </summary>
    private const long InlineMax = 4 * 1024 * 1024;

    /// <summary>超过这个长边就缩一下再发（截图动辄 3000+ 宽，模型侧也会自己缩）。</summary>
    private const int MaxEdge = 1280;

    /// <summary>
    /// 静态复用。**不要**用默认的 100 秒超时：那是整个响应读完为止的时限，
    /// 一条长回复很容易超过，表现成「聊到一半自己断了」。收口交给 CancellationToken（暂停按钮）。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 发一次请求，把 <paramref name="history"/> 现拼成消息数组。
    ///
    /// 走这条路的都是「一轮就完事」的调用方（目前是会话起名）。要跑工具循环的调用方
    /// 得自己拿 <see cref="BuildMessages"/> 攒消息数组、再调下面那个重载 ——
    /// 循环里第二轮起的历史是攒出来的，不是从一个 <c>IReadOnlyList</c> 现拼的。
    /// </summary>
    public static Task<LlmStreamResult> StreamAsync(
        LlmConfig cfg, IReadOnlyList<ChatMessage> history,
        Action<string> onDelta, Action<string> onReasoning, CancellationToken ct)
        => StreamAsync(cfg, BuildMessages(cfg, history), onDelta, onReasoning, null, ct);

    /// <summary>
    /// 发一次请求并读完整个 SSE 流。**一次请求**，不含任何重发逻辑 ——
    /// 工具循环「拿到 tool_calls → 执行 → 回灌 → 再请求一次」那个循环在上层
    /// （<c>MainForm.StartReply</c>）。分两层的原因：起名那条路也在用这个方法，
    /// 循环要是写在这里，起名会被一起卷进去。
    ///
    /// 两个内容回调都在**后台线程**上被调用，调用方自己负责切回 UI 线程。
    /// <paramref name="onToolStart"/> 在某条调用的函数名拼全的那一刻触发一次。
    /// </summary>
    public static Task<LlmStreamResult> StreamAsync(
        LlmConfig cfg, JsonArray messages,
        Action<string> onDelta, Action<string> onReasoning,
        Action<string>? onToolStart, CancellationToken ct)
        => StreamCore(cfg, messages, onDelta, onReasoning, onToolStart, ct, _withStreamOptions);

    /// <summary>
    /// 这个进程里还带不带 <c>stream_options</c>。第一次被网关明确拒绝之后就置 false，
    /// 之后所有请求都不再带 —— 不然每一轮都要撞一次 400 再重试，白白多花一个来回。
    /// </summary>
    private static bool _withStreamOptions = true;

    private static async Task<LlmStreamResult> StreamCore(
        LlmConfig cfg, JsonArray messages,
        Action<string> onDelta, Action<string> onReasoning,
        Action<string>? onToolStart, CancellationToken ct, bool withUsage)
    {
        var result = new LlmStreamResult();
        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["stream"] = true,
            ["temperature"] = cfg.Temperature,
            ["messages"] = messages.DeepClone(),   // JsonNode 只能挂在一个父节点上，见下
        };
        // 0 = 不限：干脆不带这个字段。带上 0 会被接口读成「一个 token 都不许回」。
        if (cfg.MaxTokens > 0) body["max_tokens"] = cfg.MaxTokens;

        // 流式下 OpenAI 默认**不报** usage，要显式要。我们的上下文仪表靠它拿真实用量，
        // 拿不到就只能全程估算。有些网关见到不认识的字段会直接 400，所以下面有降级重试。
        if (withUsage) body["stream_options"] = new JsonObject { ["include_usage"] = true };

        // DeepClone 是必须的：这个 JsonArray 在工具循环里会被复用好几轮，
        // 而 JsonNode 只能挂在一个父节点下面 —— 直接放进去，第二轮就是
        // "The node already has a parent"，整轮回复断在第二次请求上。
        if (cfg.Tools is { Count: > 0 })
        {
            body["tools"] = cfg.Tools.DeepClone();
            // 不指定的话有些网关会自作主张选 "none"（等于工具白给了）。
            body["tool_choice"] = "auto";
        }

        // 工具调用的参数是**碎片**到达的，按 index 累加（见 ChunkOf）。
        var calls = new SortedDictionary<int, ToolCall>();

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint);
        if (cfg.ApiKey.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                   .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string detail = "";
            try { detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch { /* 读不到正文就用状态码说话 */ }

            // 网关不认 stream_options：去掉它重发一次。
            // **在读到任何正文之前重发是安全的** —— 这里连响应流都还没打开，
            // 不存在「同一段回复收两遍」。所以这个重试可以放心放在这一层，
            // 不必像工具降级那样上升到调用方（调用方根本无从判断是哪个字段被拒了）。
            if (withUsage && LooksLikeStreamOptionsRejection(detail))
            {
                _withStreamOptions = false;
                Trace.Log("llm: gateway rejected stream_options, retrying without usage");
                return await StreamCore(cfg, messages, onDelta, onReasoning, onToolStart, ct, false)
                             .ConfigureAwait(false);
            }
            throw new LlmException(Describe(resp.StatusCode, detail));
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;                     // 服务器关流：就当作结束
            if (line.Length == 0 || line[0] == ':') continue;   // 心跳 / 注释行
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            var chunk = ChunkOf(payload);
            if (chunk == null) continue;
            // 思考在前、正文在后，两路各走各的回调：调用方按「正文有没有开始」决定
            // 思考那块要不要收起来，混进一路就没法区分了。
            if (!string.IsNullOrEmpty(chunk.Value.Reasoning)) onReasoning(chunk.Value.Reasoning);
            if (!string.IsNullOrEmpty(chunk.Value.Content)) onDelta(chunk.Value.Content);
            if (!string.IsNullOrEmpty(chunk.Value.Finish)) result.Finish = chunk.Value.Finish;
            if (chunk.Value.ReasoningTokens > 0) result.ReasoningTokens = chunk.Value.ReasoningTokens;
            // 用量只在最后那一帧报一次（那一帧的 choices 是空的，见 ChunkOf）。
            // 不累加而是直接覆盖：一轮请求只会报一次，累加反而会在网关重复报时翻倍。
            if (chunk.Value.PromptTokens > 0) result.PromptTokens = chunk.Value.PromptTokens;
            if (chunk.Value.CompletionTokens > 0) result.CompletionTokens = chunk.Value.CompletionTokens;
            if (chunk.Value.TotalTokens > 0) result.TotalTokens = chunk.Value.TotalTokens;
            if (chunk.Value.Tools != null) Accumulate(chunk.Value.Tools, calls, onToolStart);
        }

        // 有些网关（尤其是自建的那些）第一帧里根本不带 id。回灌时 tool_call_id 必须
        // 对得上，给一个自己编的也比空串强 —— 真按空串发出去，一部分接口会直接 400，
        // 而报出来的错跟「id 没给」这件事一点关系都看不出来。
        foreach (var kv in calls)
            if (kv.Value.Id.Length == 0) kv.Value.Id = "call_" + kv.Key;

        result.ToolCalls = calls.Values.ToList();
        return result;
    }

    /// <summary>把一条工具调用的碎片并进累加表。名字第一次拼出来时通知界面。</summary>
    private static void Accumulate(List<ToolFrag> frags, SortedDictionary<int, ToolCall> calls,
                                   Action<string>? onToolStart)
    {
        foreach (var f in frags)
        {
            if (!calls.TryGetValue(f.Index, out var tc))
            {
                tc = new ToolCall();
                calls[f.Index] = tc;
            }
            if (!string.IsNullOrEmpty(f.Id)) tc.Id = f.Id;

            if (!string.IsNullOrEmpty(f.Name))
            {
                bool wasEmpty = tc.Name.Length == 0;
                tc.Name += f.Name;
                if (wasEmpty) onToolStart?.Invoke(tc.Name);
            }
            if (!string.IsNullOrEmpty(f.Args)) tc.Args += f.Args;
        }
    }

    // ---------------- 请求体 ----------------

    /// <summary>
    /// 把一段历史拼成请求体的 <c>messages</c>。工具循环的调用方要先拿它铺底，
    /// 再往里追加 assistant / tool 往返帧（见 <see cref="MainForm"/> 的 <c>StartReply</c>）。
    /// </summary>
    internal static JsonArray BuildMessages(LlmConfig cfg, IReadOnlyList<ChatMessage> history)
    {
        var arr = new JsonArray();
        // 上下文那两段（摘要 / 省略说明）**缀在 system 提示词后面**，而不是另起一条 system
        // 或插一条 user：这条路径上的不少兼容接口只认开头那一条 system。
        // 它们本身就是提示词的一部分，缀着反而语义更顺。
        string sys = cfg.SystemPrompt.Trim();
        if (cfg.CtxSummary.Length > 0)
            sys = (sys.Length > 0 ? sys + "\n\n" : "") + "以下是本次对话较早内容的摘要：\n" + cfg.CtxSummary;
        if (cfg.CtxNote.Length > 0)
            sys = (sys.Length > 0 ? sys + "\n\n" : "") + cfg.CtxNote;
        if (sys.Length > 0)
            arr.Add(new JsonObject { ["role"] = "system", ["content"] = sys });

        for (int i = 0; i < history.Count; i++)
        {
            var m = history[i];
            string text = m.Text ?? "";

            // 强化信息附在**最后一条用户消息**之后，而不是新加一条 system：
            // 不少兼容接口只认开头那一条 system，尾巴上再塞一条会被直接拒掉。
            if (i == history.Count - 1 && m.Role == "user" && cfg.Reinforce.Trim().Length > 0)
            {
                string r = cfg.Reinforce.Trim();
                text = text.Length == 0 ? r : text + "\n\n" + r;
            }

            // 之前几轮用过工具的话，只在正文后面缀一行摘要。
            //
            // **不回放那几轮的 tool_calls / tool 往返，也不重发结果全文**：
            // 读一次文件就是几千字，每轮都重发的话上下文几轮就顶到上限，
            // 而用户看到的现象只是「聊久了突然变慢、然后开始忘事」。
            // 一行摘要足够让模型知道「这件事我查过」，也让它的回答不自相矛盾。
            string note = ToolNote(m);
            if (note.Length > 0) text = text.Length == 0 ? note : text + "\n\n" + note;

            var imgs = new List<(string Mime, string B64)>();
            if (cfg.Vision) imgs.AddRange(ImagesOf(m));
            string head = Notes(m, cfg.Vision && imgs.Count > 0);

            if (imgs.Count == 0)
            {
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = head + text });
                continue;
            }

            var parts = new JsonArray();
            if (head.Length > 0 || text.Length > 0)
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = head + text });
            foreach (var (mime, b64) in imgs)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:" + mime + ";base64," + b64 },
                });
            }
            arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = parts });
        }
        return arr;
    }

    /// <summary>
    /// 一条消息用过的工具，压成一行「（本轮使用了工具：读取 报告.docx、计算 1+1）」。
    /// 没有工具调用就返回空串。
    /// </summary>
    internal static string ToolNote(ChatMessage m)
    {
        if (m.ToolCalls == null || m.ToolCalls.Count == 0) return "";
        var parts = new List<string>();
        foreach (var c in m.ToolCalls)
            if (!string.IsNullOrWhiteSpace(c.Brief)) parts.Add(c.Brief);
        if (parts.Count == 0) return "";
        return "（本轮使用了工具：" + string.Join("、", parts) + "）";
    }

    // ---------------- 工具循环用的两种帧 ----------------

    /// <summary>
    /// 「模型刚才要求调用这些工具」那一条 assistant 消息，回灌时用。
    ///
    /// <paramref name="text"/> 只放**本轮新生成的**正文：气泡里的
    /// <c>Msg.Text</c> 是跨轮累加的，整段发回去会让模型以为自己在重复。
    /// </summary>
    internal static JsonObject AssistantToolFrame(string text, IReadOnlyList<ToolCall> calls)
    {
        var arr = new JsonArray();
        foreach (var c in calls)
        {
            arr.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.Name,
                    // 参数一个都没有时给 "{}"：有些网关不接受空串，会当成参数解析失败
                    ["arguments"] = c.Args.Length > 0 ? c.Args : "{}",
                },
            });
        }
        return new JsonObject
        {
            ["role"] = "assistant",
            // 只有工具调用、没有正文时**也要带上这个键**（值是 null）。
            // 直接省掉 content 的话，一部分网关会判这条消息不合法。
            ["content"] = text.Length > 0 ? text : null,
            ["tool_calls"] = arr,
        };
    }

    /// <summary>
    /// 一次工具执行的结果，回灌时用。<c>tool_call_id</c> 必须与请求里的 id 逐字相同，
    /// 对不上的报错各家都不一样，而且都不好懂。
    /// </summary>
    internal static JsonObject ToolResultFrame(ToolCall call) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = call.Id,
        ["content"] = call.Result,
    };

    /// <summary>
    /// 错误信息看起来是不是「这个接口根本不支持 tools」。
    ///
    /// 用来做一次降级重试（去掉 tools 再发一遍）。不是所有 OpenAI 兼容接口都实现了
    /// 工具调用（DeepSeek 支持、Ollama 看模型、不少网关直接 400），
    /// 让用户面对一句原始的接口报错是说不通的 —— 他上次用得好好的，这次只是模型换了一个。
    ///
    /// 判错了也不要紧：重试一次本身无害，真失败的话用户看到的还是那条真错误。
    /// </summary>
    internal static bool LooksLikeToolRejection(string message)
    {
        string m = (message ?? "").ToLowerInvariant();
        if (!m.Contains("tool") && !m.Contains("function")) return false;
        foreach (string k in new[]
        {
            "not support", "unsupported", "does not support", "不支持",
            "unknown", "unrecognized", "no such", "invalid_request",
            "unexpected", "not allowed", "extra fields",
        })
            if (m.Contains(k)) return true;
        return false;
    }

    /// <summary>
    /// 网关是不是因为不认识 <c>stream_options</c> 而拒了这次请求。
    ///
    /// 判据比 <see cref="LooksLikeToolRejection"/> 更严：这里**先要求正文里点名了那个字段**，
    /// 再看有没有「不支持 / 未知字段」这类措辞。不这么严的话，一个恰好提到
    /// "unknown model" 的 400 会被误判成「去掉 stream_options 就好了」，
    /// 于是用户看到的是**同一个错误被重试一遍**，还多花一个来回。
    /// </summary>
    internal static bool LooksLikeStreamOptionsRejection(string message)
    {
        string m = (message ?? "").ToLowerInvariant();
        if (!m.Contains("stream_options") && !m.Contains("include_usage")) return false;
        foreach (string k in new[]
        {
            "not support", "unsupported", "does not support", "不支持",
            "unknown", "unrecognized", "no such", "invalid_request",
            "unexpected", "not allowed", "extra fields", "additional properties",
        })
            if (m.Contains(k)) return true;
        return false;
    }

    /// <summary>
    /// 图片附件编码成 data URL。小图原样发（截图是 PNG，重编码成 JPEG 会把界面文字糊掉），
    /// 大图或非常见格式先缩到 <see cref="MaxEdge"/> 再编成 JPEG。
    /// </summary>
    private static IEnumerable<(string Mime, string B64)> ImagesOf(ChatMessage m)
    {
        if (m.Attachments == null) yield break;
        foreach (var a in m.Attachments)
        {
            if (a.Kind != "image") continue;
            var enc = EncodeImage(a);
            if (enc != null) yield return enc.Value;
        }
    }

    private static (string Mime, string B64)? EncodeImage(Attachment a)
    {
        try
        {
            string? path = a.Path;
            if (path == null || !File.Exists(path)) return null;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            string mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png",
            };

            // 认识的那几种、而且不大：原样发。截图是 PNG，重编码成 JPEG 会把界面上的小字糊掉，
            // 而那恰恰是本程序里最常见的图。
            bool passThrough = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
            if (passThrough && new FileInfo(path).Length <= InlineMax)
                return (mime, Convert.ToBase64String(File.ReadAllBytes(path)));

            // 其余（超过上限的、bmp/tiff 之类不认识的）先缩到 MaxEdge 再编码。
            // PNG 保持 PNG：那些多半是带透明通道的图，转 JPEG 会让透明处变成黑块。
            using var src = Image.FromFile(path);
            int edge = Math.Max(src.Width, src.Height);
            double k = edge <= MaxEdge ? 1.0 : (double)MaxEdge / edge;
            int w = Math.Max(1, (int)Math.Round(src.Width * k));
            int h = Math.Max(1, (int)Math.Round(src.Height * k));
            using var small = new Bitmap(w, h);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            if (mime == "image/png") { small.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ("image/png", Convert.ToBase64String(ms.ToArray())); }
            small.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            return ("image/jpeg", Convert.ToBase64String(ms.ToArray()));
        }
        catch { return null; }   // 读不出来就当这张图没加进来，别让整条消息发不出去
    }

    /// <summary>
    /// 没被当作图片发出去的那些附件，用一行文字告诉模型「有这么个东西」。
    /// 不写的话多模态关掉/不支持时，附件在模型眼里等于不存在，用户却明明贴在消息里。
    /// </summary>
    private static string Notes(ChatMessage m, bool imagesSent)
    {
        if (m.Attachments == null || m.Attachments.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var a in m.Attachments)
        {
            bool img = a.Kind == "image";
            if (img && imagesSent) continue;
            string name = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileName(a.Path ?? "") : a.Name;
            if (name.Length == 0) continue;
            sb.Append(img ? "[图片] " : "[附件] ").Append(name).Append('\n');
        }
        return sb.ToString();
    }

    // ---------------- 响应 ----------------

    /// <summary>一条 SSE 数据里可能带的几样东西：正文增量、思考增量、工具调用碎片、结束原因、用量。</summary>
    private readonly struct Chunk
    {
        public readonly string? Content;
        public readonly string? Reasoning;
        public readonly string? Finish;
        public readonly int ReasoningTokens;

        // 用量。服务端没报就是 0 —— 「不知道」和「用了 0 个」在下游是同一件事（都退回估算）。
        public readonly int PromptTokens;
        public readonly int CompletionTokens;
        public readonly int TotalTokens;

        /// <summary>这一帧携带的工具调用碎片；没有就是 null。</summary>
        public readonly List<ToolFrag>? Tools;

        public Chunk(string? content, string? reasoning, string? finish, int reasoningTokens,
                     List<ToolFrag>? tools, int promptTokens = 0, int completionTokens = 0, int totalTokens = 0)
        {
            Content = content; Reasoning = reasoning; Finish = finish;
            ReasoningTokens = reasoningTokens; Tools = tools;
            PromptTokens = promptTokens; CompletionTokens = completionTokens; TotalTokens = totalTokens;
        }
    }

    /// <summary>
    /// 工具调用的一小块。服务端是**流式吐参数**的 —— 名字和 <c>call_xxx</c> 通常在第一帧里
    /// 就齐了，而 <c>arguments</c> 那段 JSON 会被切成好几帧（有时一个键名都被切开），
    /// 必须按 <see cref="Index"/> 拼回去。
    /// </summary>
    private readonly struct ToolFrag
    {
        public readonly int Index;
        public readonly string? Id;
        public readonly string? Name;
        public readonly string? Args;

        public ToolFrag(int index, string? id, string? name, string? args)
        {
            Index = index; Id = id; Name = name; Args = args;
        }
    }

    /// <summary>
    /// 从一条 SSE 数据里取出这一帧携带的内容；不是内容帧（角色帧、心跳）返回 null。
    ///
    /// 一帧里几样都可能出现，所以**不能**像原来那样「取到正文就返回」：
    /// 推理模型的帧长这样 —— <c>{"delta":{"content":null,"reasoning_content":"We"}}</c>，
    /// 正文那一格里是 JSON 的 null，只有思考那一格有东西；而要求调用工具的那些帧
    /// 正文干脆整格不在，只有 <c>tool_calls</c>。
    /// </summary>
    private static Chunk? ChunkOf(string payload)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(payload); }
        catch (JsonException) { return null; }      // 半条帧 / 非 JSON 心跳，跳过就好

        if (node is not JsonObject o) return null;
        // 有些网关错误是**以 200 + 一条 error 帧**回来的，只看状态码会当成「模型没说话」
        if (o["error"] is JsonObject err) throw new LlmException(ErrText(err) ?? "接口返回了一个错误");

        // **usage 必须在看 choices 之前读。**
        // 开了 stream_options.include_usage 之后，最后那一帧长这样：
        //     {"choices":[],"usage":{...}}
        // —— choices 是**空数组**。原来那句 `is not JsonArray { Count: > 0 }` 会在读到
        // usage 之前就 return null，于是用量**永远收不到**，而现象只是「token 一直是 0」，
        // 安静得像是服务端没报。
        int rt = 0, pt = 0, ct = 0, tt = 0;
        if (o["usage"] is JsonObject u)
        {
            pt = IntOf(u["prompt_tokens"]);
            ct = IntOf(u["completion_tokens"]);
            tt = IntOf(u["total_tokens"]);
            if (u["completion_tokens_details"] is JsonObject det) rt = IntOf(det["reasoning_tokens"]);
        }

        if (o["choices"] is not JsonArray { Count: > 0 } ch)
            // 纯用量帧（上面那种）：有用量就照常带出去，没有就当它不是内容帧
            return pt + ct + tt > 0
                ? new Chunk(null, null, null, rt, null, pt, ct, tt)
                : null;
        if (ch[0] is not JsonObject c0) return null;

        string? content = null, reasoning = null, finish = Str(c0["finish_reason"]);
        List<ToolFrag>? tools = null;
        if (c0["delta"] is JsonObject d)
        {
            content = Str(d["content"]);
            reasoning = Str(d["reasoning_content"]) ?? Str(d["reasoning"]);
            tools = ToolsOf(d["tool_calls"]);
        }
        else if (c0["message"] is JsonObject mm)
        {
            content = Str(mm["content"]);      // 非流式兜底
            reasoning = Str(mm["reasoning_content"]);
            tools = ToolsOf(mm["tool_calls"]);
        }

        if (content == null && reasoning == null && finish == null && rt == 0
            && (tools == null || tools.Count == 0)) return null;
        return new Chunk(content, reasoning, finish, rt, tools, pt, ct, tt);
    }

    /// <summary>取一个可能不存在的整数。取不到返回 0，不抛。</summary>
    private static int IntOf(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<int>(out int i) ? i : 0;

    /// <summary>
    /// 取这一帧里的工具调用碎片。取不出一条合法的就返回 null（而不是空表）——
    /// 空表和「这一帧压根没有工具内容」在调用方那里是同一件事。
    /// </summary>
    private static List<ToolFrag>? ToolsOf(JsonNode? n)
    {
        if (n is not JsonArray { Count: > 0 } arr) return null;

        List<ToolFrag>? list = null;
        foreach (var item in arr)
        {
            if (item is not JsonObject t) continue;

            // index 缺了按 0 算：只有一条调用时不少网关干脆不给这个字段，
            // 而按 0 收正好就是「就这一条」。
            int index = 0;
            if (t["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx)) index = idx;

            string? name = null, args = null;
            if (t["function"] is JsonObject fn)
            {
                name = Str(fn["name"]);
                args = Str(fn["arguments"]);
            }

            (list ??= new()).Add(new ToolFrag(index, Str(t["id"]), name, args));
        }
        return list;
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? ErrText(JsonObject err) =>
        Str(err["message"]) ?? Str(err["error"]) ?? Str(err["code"]);

    /// <summary>把「HTTP 401 + 一大坨 JSON」压成一句人话。</summary>
    private static string Describe(System.Net.HttpStatusCode status, string body)
    {
        string msg = "";
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
                msg = (o["error"] is JsonObject e ? ErrText(e) : null)
                   ?? Str(o["message"]) ?? Str(o["error"]) ?? "";
        }
        catch { /* 不是 JSON 就按原文截 */ }

        if (msg.Length == 0)
        {
            msg = (body ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            if (msg.Length > 200) msg = msg[..200] + "…";
        }
        int code = (int)status;
        string hint = code switch
        {
            401 or 403 => "（API Key 不对，或没有这个模型的权限）",
            404 => "（接口地址或模型名不对）",
            429 => "（请求太频繁或额度用尽）",
            _ => "",
        };
        return "请求失败 " + code + " " + status + hint + (msg.Length > 0 ? "：" + msg : "");
    }
}
