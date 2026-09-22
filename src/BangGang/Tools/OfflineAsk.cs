#if DEBUG
using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>
/// 无头跑一整轮「带工具的对话」，把每一轮请求、工具调用、工具结果打到 stdout。
///
/// 为什么要有这个：工具调用这一块最容易错的地方**不在界面**，而在协议本身 ——
/// 请求体里 <c>tools</c> / <c>tool_choice</c> 的写法、SSE 里 <c>delta.tool_calls</c>
/// 那种「名字先来、参数按碎片拼」的到达方式、以及回灌时 <c>role:"tool"</c> 帧
/// 与 <c>tool_call_id</c> 的对应关系。这些都得**对着真的接口**才能验，而界面上验它们的
/// 代价是：配好模型 → 打字 → 等流式 → 从气泡里猜「刚才到底发了什么」。
///
/// 它用的是产品代码里**同一批**构件（<see cref="LlmClient.BuildMessages"/> /
/// <see cref="LlmClient.StreamAsync"/> / <see cref="LlmClient.AssistantToolFrame"/> /
/// <see cref="LlmClient.ToolResultFrame"/> / <see cref="ToolRunner"/>），
/// 循环结构也照 <c>MainForm.RunToolLoop</c> 来。区别只在结果落到字符串而不是气泡上。
///
/// **它会真的发网络请求，用的是 settings.json 里那份配置。** 所以必须显式设变量才跑：
/// <list type="bullet">
///   <item><c>BANGGANG_ASK</c>：要问的问题。设了才生效。</item>
///   <item><c>BANGGANG_ASK_ROUNDS</c>：最多请求几次，默认 4。</item>
/// </list>
/// </summary>
internal static class OfflineAsk
{
    public static bool TryRun()
    {
        string? prompt = Environment.GetEnvironmentVariable("BANGGANG_ASK");
        if (string.IsNullOrWhiteSpace(prompt)) return false;

        // 和 OfflineTool 同一套：显式把 stdout 包成 UTF-8。不包的话，重定向到文件时
        // 写出来的是系统 ANSI，中文全变乱码 —— 而重定向到文件正是最常用的用法。
        try
        {
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 包不上就按默认走 */ }

        try { Run(prompt); }
        catch (Exception ex) { Console.WriteLine("ask failed: " + ex); }
        return true;
    }

    private static void Run(string prompt)
    {
        int maxRounds = 4;
        if (int.TryParse(Environment.GetEnvironmentVariable("BANGGANG_ASK_ROUNDS"), out int r) && r > 0)
            maxRounds = r;

        // 用 exe 同目录那份 settings.json —— 和界面跑的是同一份配置，
        // 这样探针验的才是我发给用户看的那条路。
        var settings = AppSettings.Load();
        var cfg = LlmConfig.From(settings);
        if (cfg.Problem is { } problem) { Console.WriteLine("配置不全：" + problem); return; }
        if (ToolRegistry.AnyEnabled(settings)) cfg.Tools = ToolRegistry.SchemaFor(settings);

        Console.WriteLine($"model={cfg.Model} endpoint={cfg.Endpoint} tools={cfg.Tools?.Count ?? 0}");
        Console.WriteLine($"问：{prompt}");

        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = "user", Text = prompt });
        var ctx = new ToolContext { Conv = conv, UiHost = null };

        var msgs = LlmClient.BuildMessages(cfg, conv.Messages);
        for (int round = 0; round < maxRounds; round++)
        {
            var text = new System.Text.StringBuilder();
            var reason = new System.Text.StringBuilder();
            Console.WriteLine($"\n--- 第 {round + 1} 次请求（messages={msgs.Count}）");

            var res = LlmClient
                .StreamAsync(cfg, msgs, s => text.Append(s), s => reason.Append(s), null, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (reason.Length > 0) Console.WriteLine($"[思考 {reason.Length} 字]");
            if (text.Length > 0) Console.WriteLine("[正文] " + text.ToString().Trim());

            if (res.ToolCalls.Count == 0)
            {
                Console.WriteLine($"\n完成：finish={res.Finish} 正文 {text.Length} 字");
                return;
            }

            Console.WriteLine($"[要求调用 {res.ToolCalls.Count} 个工具]");
            foreach (var c in res.ToolCalls)
                Console.WriteLine($"  · {c.Name}  id={c.Id}  args={c.Args}");

            msgs.Add(LlmClient.AssistantToolFrame(text.ToString(), res.ToolCalls));

            foreach (var c in res.ToolCalls)
            {
                var done = ToolRunner.RunAsync(c, ctx, settings, CancellationToken.None)
                                     .GetAwaiter().GetResult();
                Console.WriteLine($"  → {done.Brief}  ok={done.Ok}  {done.Ms}ms");
                Console.WriteLine("    " + Indent(done.Result));
                msgs.Add(LlmClient.ToolResultFrame(done));
            }
        }
        Console.WriteLine($"\n达到请求上限 {maxRounds} 次，停下。");
    }

    /// <summary>结果打出来时整体缩进两格，好和探针自己的输出分开。</summary>
    private static string Indent(string s)
    {
        string flat = (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (flat.Length > 600) flat = flat[..600] + "…";
        return flat.Replace("\n", "\n    ");
    }
}
#endif
