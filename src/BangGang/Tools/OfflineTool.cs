#if DEBUG
using System.Text;
using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>
/// 离屏跑**单个**工具并把结果打到 stdout，不开窗口、不联网以外的依赖。
///
/// 为什么要有这个（和 <see cref="OfflineRender"/> 是同一个理由）：工具的正确性
/// —— 表达式算得对不对、docx 解得对不对、网页正文剥得干不干净 ——
/// 跟「窗口在不在」「桌面画不画得出来」一点关系都没有，但原来只有一条验证路：
/// 起程序 → 配好模型 → 打字 → 等流式 → 看气泡。反馈慢到没人愿意多测一轮，
/// 而桌面一出问题整条链就一起断。
///
/// 触发方式（都是环境变量，只认 Debug 构建）：
/// <list type="bullet">
///   <item><c>BANGGANG_TOOL</c>：<c>工具名</c> 或 <c>工具名:主参数</c>，如 <c>calc:1+1</c>、
///         <c>read_file:D:\a.txt</c>、<c>now</c>。设了才生效。</item>
///   <item><c>BANGGANG_TOOL_ARGS</c>：完整参数 JSON，覆盖上面那个简写（多参数工具用）。</item>
///   <item><c>BANGGANG_TOOL_ATTACH</c>：给假会话挂一个附件路径，用来验「只给文件名也能找到文件」。</item>
///   <item><c>BANGGANG_TOOL_LIST=1</c>：只把工具清单与 schema 打出来，不跑任何工具。</item>
///   <item><c>BANGGANG_HTML</c>：拿一个本地 .html 文件跑一遍 <see cref="WebFetchTool.HtmlToText"/>，
///         把剥出来的正文打出来。<b>不联网</b> —— 剥得对不对是个纯函数问题，
///         而线上页面随时会变、还可能抓不到，拿它当测试输入等于把两件事混在一起。</item>
/// </list>
///
/// 它在 <c>Program.Main</c> 的最开头、**单实例互斥体之前**返回，所以已经开着一个帮帮
/// 也照样能跑。
/// </summary>
internal static class OfflineTool
{
    public static bool TryRun()
    {
        string? spec = Environment.GetEnvironmentVariable("BANGGANG_TOOL");
        string? html = Environment.GetEnvironmentVariable("BANGGANG_HTML");
        bool list = Environment.GetEnvironmentVariable("BANGGANG_TOOL_LIST") == "1";
        if (string.IsNullOrWhiteSpace(spec) && string.IsNullOrWhiteSpace(html) && !list) return false;

        // 显式把 stdout 包成 UTF-8 的 StreamWriter，而**不是**设 Console.OutputEncoding：
        // 后者在输出被重定向到文件时会抛（那时没有控制台句柄），而重定向到文件恰恰是最常用的
        // 用法 —— WinExe 没有自己的控制台，直接跑的话 Console.WriteLine 写到一个空句柄上，
        // 什么都看不到。不显式指定的话写出来的是系统 ANSI（中文全变乱码）。
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 包不上就按默认走，至少不会因此崩掉 */ }

        if (list)
        {
            var s = new AppSettings();
            foreach (var d in ToolRegistry.All)
                Console.WriteLine($"{d.Name,-12} 默认={(ToolRegistry.Enabled(s, d.Name) ? "开" : "关")}  " +
                                  $"主参数={d.Primary,-8} {d.Desc.Split('\n')[0]}");
            Console.WriteLine("\n--- 发给模型的 tools 数组 ---");
            Console.WriteLine(ToolRegistry.SchemaFor(s).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return true;
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            RunHtml(html!);
            return true;
        }

        try { RunOnce(spec!); }
        catch (Exception ex) { Console.WriteLine("tool probe failed: " + ex); }
        return true;   // 已经是工具模式了，别再把窗口开起来
    }

    /// <summary>本地 HTML 过一遍剥离逻辑。见类注释里为什么这件事不该联网测。</summary>
    private static void RunHtml(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string src = TextDecode.Html(bytes, bytes.Length, null);
        string text = WebFetchTool.HtmlToText(src);

        Console.WriteLine($"input  {bytes.Length} bytes / {src.Length} chars");
        Console.WriteLine($"output {text.Length} chars");
        Console.WriteLine("---");
        Console.WriteLine(text.Length > 4000 ? text[..4000] + "\n…（截断显示）" : text);
    }

    private static void RunOnce(string spec)
    {
        int colon = spec.IndexOf(':');
        string name = colon < 0 ? spec : spec[..colon];
        string? inline = colon < 0 ? null : spec[(colon + 1)..];

        var def = ToolRegistry.Find(name.Trim());
        if (def == null)
        {
            Console.WriteLine("没有名为「" + name + "」的工具。用 BANGGANG_TOOL_LIST=1 看清单。");
            return;
        }

        string argsJson = Environment.GetEnvironmentVariable("BANGGANG_TOOL_ARGS") ?? "";
        if (argsJson.Length == 0 && inline != null)
        {
            if (def.Primary.Length == 0)
            {
                Console.WriteLine("「" + def.Name + "」没有主参数，请用 BANGGANG_TOOL_ARGS 给完整 JSON。");
                return;
            }
            // 走 JsonObject 而不是手拼字符串：路径里的反斜杠、引号都得转义，
            // 而在 Windows 上测路径恰恰是最常见的用法。
            argsJson = new JsonObject { [def.Primary] = inline }.ToJsonString();
        }
        if (argsJson.Length == 0) argsJson = "{}";

        var conv = new Conversation();
        string attach = Environment.GetEnvironmentVariable("BANGGANG_TOOL_ATTACH") ?? "";
        if (attach.Length > 0)
            conv.Messages.Add(new ChatMessage
            {
                Role = "user",
                Text = "（离屏探针的假消息）",
                Attachments = { Attachment.ForFile(Path.GetFileName(attach), attach) },
            });

        var call = new ToolCall { Id = "probe", Name = def.Name, Args = argsJson };
        var ctx = new ToolContext { Conv = conv, UiHost = null };
        var settings = new AppSettings();

        Console.WriteLine($"tool {def.Name}  args={argsJson}");
        var done = ToolRunner.RunAsync(call, ctx, settings, CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine($"ok={done.Ok} ms={done.Ms} brief={done.Brief}");
        Console.WriteLine("---");
        Console.WriteLine(done.Result);
    }
}
#endif
