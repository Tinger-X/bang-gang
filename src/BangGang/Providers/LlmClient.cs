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
    public int MaxTokens = 2048;
    public string SystemPrompt = "";
    public string Reinforce = "";

    /// <summary>把服务商档案读成配置。读不到某项时**不抛异常**，只留空串，由 <see cref="Problem"/> 说缺什么。</summary>
    public static LlmConfig From(AppSettings s)
    {
        var cfg = new LlmConfig
        {
            Vision = s.ChatVision,
            Temperature = Math.Clamp(s.ChatTemperature, 0, 2),
            MaxTokens = Math.Clamp(s.ChatMaxTokens, 1, 1_000_000),
            SystemPrompt = s.ChatSystemPrompt ?? "",
            Reinforce = s.ChatReinforce ?? "",
        };
        // 只读，不调 ProfileOf —— 那个 getter 会顺手往字典里塞一个空档位，
        // 发一次消息就改动设置对象（还会被保存进 settings.json）。
        if (s.ChatProfiles != null && s.ChatProfiles.TryGetValue(s.ChatProvider ?? "", out var p) && p != null)
        {
            cfg.Url = Get(p, "url");
            cfg.ApiKey = Get(p, "key");
            cfg.Model = Get(p, "model");
        }
        // 旧版扁平字段兜底：老 settings.json 迁移过，但手改过文件的人可能只剩这几个
        if (cfg.Url.Length == 0) cfg.Url = s.ChatApiUrl ?? "";
        if (cfg.ApiKey.Length == 0) cfg.ApiKey = s.ChatApiKey ?? "";
        if (cfg.Model.Length == 0) cfg.Model = s.ChatModel ?? "";
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
/// OpenAI 兼容的流式对话客户端。只用 BCL（<see cref="HttpClient"/> + System.Text.Json）——
/// 本仓库不引 NuGet，SSE 也就自己按行拆。
///
/// 回调 <c>onDelta</c> 在**后台线程**上被调用，调用方自己负责切回 UI 线程。
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

    public static async Task StreamAsync(
        LlmConfig cfg, IReadOnlyList<ChatMessage> history, Action<string> onDelta, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["stream"] = true,
            ["temperature"] = cfg.Temperature,
            ["max_tokens"] = cfg.MaxTokens,
            ["messages"] = BuildMessages(cfg, history),
        };

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

            string? delta = DeltaOf(payload);
            if (!string.IsNullOrEmpty(delta)) onDelta(delta);
        }
    }

    // ---------------- 请求体 ----------------

    private static JsonArray BuildMessages(LlmConfig cfg, IReadOnlyList<ChatMessage> history)
    {
        var arr = new JsonArray();
        if (cfg.SystemPrompt.Trim().Length > 0)
            arr.Add(new JsonObject { ["role"] = "system", ["content"] = cfg.SystemPrompt.Trim() });

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

    /// <summary>从一条 SSE 数据里取出增量正文；不是正文（角色帧、结束帧）返回 null。</summary>
    private static string? DeltaOf(string payload)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(payload); }
        catch (JsonException) { return null; }      // 半条帧 / 非 JSON 心跳，跳过就好

        if (node is not JsonObject o) return null;
        // 有些网关错误是**以 200 + 一条 error 帧**回来的，只看状态码会当成「模型没说话」
        if (o["error"] is JsonObject err) throw new LlmException(ErrText(err) ?? "接口返回了一个错误");

        if (o["choices"] is not JsonArray { Count: > 0 } ch) return null;
        if (ch[0] is not JsonObject c0) return null;
        if (c0["delta"] is JsonObject d && Str(d["content"]) is { } s) return s;
        if (c0["message"] is JsonObject mm && Str(mm["content"]) is { } s2) return s2;   // 非流式兜底
        return null;
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
