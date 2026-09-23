using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>
/// 网页搜索：把关键词交给 AnySearch，拿回标题 / 链接 / 摘要。
///
/// **为什么可以直接用**：AnySearch 的 <c>/v1/search</c> 在不带 <c>Authorization</c> 时走**匿名额度**
/// （见它的 API：认证模式在「没配 key」时就是 <c>anonymous</c>），所以用户不需要注册、不需要填任何东西。
/// 这是本仓库里唯一一个会把数据发去**第三方服务**的工具 —— 本应用其余部分只跟用户自己配置的接口说话。
///
/// 两件必须对用户说清、也在设置页里写了的事：
///
/// 1. **匿名额度是大家共用的**（AnySearch 文档原话：匿名请求受共享免费额度和更低限流约束），
///    所以它可能被用光、可能被限流。命中这两种情况时这里会明确回一句「稍后再试」，
///    而不是把一句原始报错甩给用户 —— 那不是他能做错的任何事。
/// 2. **搜索词会离开这台机器**。搜索是用户主动发起的一次动作，但它确实不同于「只跟我自己配的模型说话」。
///
/// 版本由 <see cref="MainForm.AppVersion"/> 带在 User-Agent 上：匿名额度背后是一个真实的第三方服务，
/// 让它看得见流量来自哪个程序，比伪装成浏览器诚实。
/// </summary>
internal static class WebSearchTool
{
    private const string Endpoint = "https://api.anysearch.com/v1/search";

    /// <summary>一次最多要几条。默认 5，上限 20 —— 这是给上下文留的余量，不是服务端的限制。</summary>
    private const int MaxResultsCap = 20;

    private const string ParamsJson = """
    {
      "type": "object",
      "properties": {
        "query": {
          "type": "string",
          "description": "搜索关键词。用自然语言描述要找什么即可，和搜索引擎一样。"
        },
        "max_results": {
          "type": "integer",
          "description": "最多返回几条结果，默认 5，上限 20。"
        }
      },
      "required": ["query"]
    }
    """;

    public static readonly ToolDef Def = new()
    {
        Name = "web_search",
        Label = "网页搜索",
        Desc = "在网上搜索关键词，返回最相关若干条的标题、链接与摘要。\n" +
               "凡是需要**你并不知道、也推不出来**的当前信息（最近的新闻、某个人/产品/事件的最新情况、" +
               "某个具体数据），都应该先用它搜一次，而不是凭记忆回答 —— 你的知识有截止日期。\n" +
               "搜到之后可以再用 web_fetch 打开其中某一条读全文。",
        Params = ParamsJson,
        Primary = "query",
        Brief = a => "搜索 " + Cut(a.Str("query")),
        Run = (a, _, ct) => RunAsync(a, ct),
    };

    private static async Task<string> RunAsync(ToolArgs args, CancellationToken ct)
    {
        string query = args.Str("query").Trim();
        if (query.Length == 0)
            return "没有收到搜索词。请在 query 参数里给出要搜的内容。";

        int want = args.Int("max_results", 5, 1, MaxResultsCap);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));

        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("User-Agent", "BangGang/" + MainForm.AppVersion.TrimStart('v'));
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Content = new StringContent(
                new JsonObject { ["query"] = query, ["max_results"] = want }.ToJsonString(),
                Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return "搜索暂时不可用：免费搜索的公共额度正忙（限流）。请过一会儿再试，"
                     + "或者先用 web_fetch 打开你已经知道的网址。";
            if (!resp.IsSuccessStatusCode)
                return "搜索失败：服务返回 " + (int)resp.StatusCode
                     + (Explain(body).Length > 0 ? "（" + Explain(body) + "）" : "。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return "搜索超时了（超过 25 秒）。可以换个说法再试一次。";
        }
        catch (Exception ex)
        {
            return "搜索失败：" + ex.Message;
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
                return "搜索失败：返回的内容看不懂。";

            int code = root["code"]?.GetValue<int>() ?? -1;
            if (code != 0)
            {
                string msg = root["message"]?.GetValue<string>() ?? "";
                return "搜索没有成功：" + (msg.Length > 0 ? msg : "服务返回了错误码 " + code)
                     + "。换个说法再试一次，或者直接抓你已经知道的网址。";
            }

            if (root["data"]?["results"] is not JsonArray results || results.Count == 0)
                return "没有搜到「" + query + "」相关的结果。可以换个关键词再试。";

            var sb = new StringBuilder();
            sb.Append("搜索「").Append(query).Append("」的结果：\n");
            int shown = 0;
            foreach (var item in results)
            {
                if (item is not JsonObject r) continue;
                string title = Clean(r["title"]?.GetValue<string>());
                string url = Clean(r["url"]?.GetValue<string>());
                string snippet = Clean(r["snippet"]?.GetValue<string>());
                if (url.Length == 0) continue;

                shown++;
                sb.Append('\n').Append(shown).Append(". ").Append(title.Length > 0 ? title : url).Append('\n');
                sb.Append("   ").Append(url).Append('\n');
                if (snippet.Length > 0) sb.Append("   ").Append(Cut(snippet, SnippetCap)).Append('\n');
            }
            if (shown == 0) return "没有搜到「" + query + "」相关的结果。可以换个关键词再试。";

            sb.Append("\n需要看哪一条的全文，就用 web_fetch 抓它的链接。");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "搜索失败：" + ex.Message;
        }
    }

    /// <summary>每条摘要留多少字。摘要本来就短，这里主要是防个别结果把整段正文塞进 snippet。</summary>
    private const int SnippetCap = 300;

    private static string Clean(string? s) =>
        (s ?? "").Replace("\r", " ").Replace('\n', ' ').Trim();

    private static string Cut(string s) => Cut(s, 24);

    /// <summary>摘要里的一行别太长；折叠行上那一行更是。</summary>
    private static string Cut(string s, int max)
    {
        s = Clean(s);
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>从错误正文里抠一句人话出来（服务端失败时也会给 <c>message</c>）。</summary>
    private static string Explain(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
                return Clean(o["message"]?.GetValue<string>());
        }
        catch { /* 不是 JSON 就算了 */ }
        return "";
    }

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
}
