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
///   <item><c>BANGGANG_HTML</c>：拿一个本地 .html 文件跑一遍 <see cref="WebPage.Parse"/>，
///         把剥出来的正文**和它引用到的地址**都打出来。<b>不联网</b> —— 剥得对不对、
///         引用收得全不全都是纯函数问题，而线上页面随时会变、还可能抓不到，
///         拿它当测试输入等于把两件事混在一起。</item>
///   <item><c>BANGGANG_DEPTH=1</c>：把 <see cref="WebDepth"/> 按用户给的例子跑一遍
///         （目标 → 第一层外链 → 第二层外链 → 第三层外链该被拒），把每次判定打出来。
///         纯逻辑、确定性、不联网 —— 层次限制这种「错了也照样能跑」的东西，
///         靠联网碰是碰不出来的（真实的链路很少有 4 层深）。</item>
///   <item><c>BANGGANG_UPDATE=1</c>：拿一个假的老版本号去问官网（真的发请求，但只是 GET
///         <c>/api/stats</c>，不计入下载次数），把检查结果打出来；再加
///         <c>BANGGANG_UPDATE_DOWNLOAD=1</c> 就连下载与校验一起走完。
///         校验那段是「下完要运行」的前一道闸门，值得真跑一遍而不是只看代码。</item>
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
        bool depth = Environment.GetEnvironmentVariable("BANGGANG_DEPTH") == "1";
        bool update = Environment.GetEnvironmentVariable("BANGGANG_UPDATE") == "1";
        // 加新模式时**必须**也加进这一句：漏了的话那个模式永远进不去，
        // 而现象是「跑了，什么都没输出」—— 看着和「模式内部判错了」一模一样。
        // （这一条已经漏过两次了，两次的症状都一样。）
        if (string.IsNullOrWhiteSpace(spec) && string.IsNullOrWhiteSpace(html) && !list && !depth && !update)
            return false;

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

        if (Environment.GetEnvironmentVariable("BANGGANG_UPDATE") == "1")
        {
            RunUpdate();
            return true;
        }

        if (Environment.GetEnvironmentVariable("BANGGANG_DEPTH") == "1")
        {
            RunDepth();
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

    /// <summary>
    /// 确定性验一遍 <see cref="Updater.Verify"/> —— 它是「要不要运行这个刚下下来的 exe」
    /// 的唯一判据，四条分支都得走一遍：对得上、哈希不符、大小不符、服务端没给哈希。
    /// 不联网，用的是自己造的临时文件。
    /// </summary>
    private static void VerifyChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BangGang-Update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "verify-probe.bin");
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("this stands in for an installer");
        string realHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));

        int bad = 0;
        void Check(string what, string? hash, long size, bool expectOk)
        {
            File.WriteAllBytes(path, payload);
            var info = new UpdateInfo("v9.9.9", "without-runtime", "verify-probe.bin", size, hash);
            bool ok;
            string detail;
            try
            {
                Updater.Verify(path, info, realHash, payload.Length);
                ok = true;
                detail = "通过";
            }
            catch (Exception ex)
            {
                ok = false;
                detail = ex.Message.Split('\n')[0];
            }
            bool pass = ok == expectOk;
            bool deleted = !File.Exists(path);
            if (!pass) bad++;
            // 校验不过必须把文件删掉：留着的话用户还能自己去双击它。
            if (!expectOk && !deleted) bad++;

            Console.WriteLine($"  {(pass ? "OK  " : "FAIL")} {what,-22} 通过={ok,-5} 已删除={deleted}");
            Console.WriteLine($"       └ {detail}");
        }

        Console.WriteLine("校验闸门的四条分支（临时文件，不联网）");
        Check("哈希对得上", realHash, payload.Length, true);
        Check("哈希不符", new string('A', 64), payload.Length, false);
        Check("大小不符", realHash, payload.Length + 1, false);
        Check("服务端没给哈希", null, payload.Length, true);
        try { File.Delete(path); } catch { }
        Console.WriteLine(bad == 0 ? "  全部通过" : $"  {bad} 条不符");
    }

    /// <summary>
    /// 走一遍真实的检查更新。<c>BANGGANG_UPDATE=1</c> 只发一个 GET（不计下载次数）；
    /// 再加 <c>BANGGANG_UPDATE_DOWNLOAD=1</c> 才真的把包装下来验一遍。
    /// </summary>
    private static void RunUpdate()
    {
        VerifyChecks();

        Console.WriteLine("\n用假的老版本 v0.0.1 去问官网（本机真实版本是 " + MainForm.AppVersion + "）");
        var (info, message) = Updater.CheckAsync("v0.0.1", CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("  说明：" + message);
        if (info == null) return;

        Console.WriteLine($"  版本={info.Version} 档位={info.Variant}");
        Console.WriteLine($"  文件={info.FileName}");
        Console.WriteLine($"  大小={info.Size} ({AttachTypes.SizeText(info.Size)})");
        Console.WriteLine($"  sha256={info.Sha256 ?? "<服务端未提供>"}");

        // 再问一次「已经是最新」那条路：拿官网报的版本号当本机版本，应当判定无需更新。
        var (same, msg2) = Updater.CheckAsync(info.Version, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"\n本机已是 {info.Version} 时：{(same == null ? "正确判为无需更新" : "有误，仍报新版本")} —— {msg2}");

        // 再试一个比它旧一号的版本，确认是比较版本号而不是比字符串。
        var (older, msg3) = Updater.CheckAsync("v0.9.2", CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"本机是 v0.9.2 时：{(older == null ? "有误，没报更新" : "正确报出 " + older.Version)} —— {msg3}");

        if (Environment.GetEnvironmentVariable("BANGGANG_UPDATE_DOWNLOAD") != "1")
        {
            Console.WriteLine("\n（加 BANGGANG_UPDATE_DOWNLOAD=1 才会真的下载并校验）");
            return;
        }

        Console.WriteLine("\n开始下载（会计入官网的下载次数，这是真实下载）…");
        long last = 0;
        string path = Updater.DownloadAsync(info, p =>
        {
            long now = Environment.TickCount64;
            if (now - last < 500 && p < 1.0) return;
            last = now;
            Console.WriteLine($"  {(int)Math.Round(p * 100)}%");
        }, CancellationToken.None).GetAwaiter().GetResult();

        var fi = new FileInfo(path);
        Console.WriteLine($"下载并校验完成：{path}");
        Console.WriteLine($"  实际 {fi.Length} 字节，官网说 {info.Size} 字节，一致={fi.Length == info.Size}");
    }

    /// <summary>
    /// 按用户给的那条链路跑一遍层次判定，顺带把「数量」那一维也试一下。
    /// 全程只碰 <see cref="WebDepth"/>，不发任何请求。
    /// </summary>
    private static void RunDepth()
    {
        var d = new WebDepth();
        int bad = 0;

        void Check(string what, string url, bool expect)
        {
            bool got = d.Allow(url, out int depth, out string? why);
            bool ok = got == expect;
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {what,-34} 允许={got,-5} 层={depth}");
            if (why != null) Console.WriteLine($"       └ {why.Split('\n')[0]}");
        }

        Console.WriteLine("用户目标（第 1 层）");
        Check("抓用户给的页面", "https://a.test/", true);

        // 它引用 30 个外链，全登记成第 2 层
        var many = Enumerable.Range(1, 30)
            .Select(i => new WebRef(RefKind.Page, $"https://a.test/p{i}")).ToList();
        d.Discover(many, 1);
        Console.WriteLine($"第 1 层发现 {many.Count} 条引用");

        Console.WriteLine("\n第 2 层：数量不限制，抓哪条都行");
        Check("第 1 条外链", "https://a.test/p1", true);
        Check("第 30 条外链（数量不限）", "https://a.test/p30", true);

        d.Discover(new[] { new WebRef(RefKind.Style, "https://a.test/deep.css") }, 2);
        Console.WriteLine("\n第 3 层");
        Check("第 2 层引用的样式表", "https://a.test/deep.css", true);

        d.Discover(new[] { new WebRef(RefKind.Image, "https://a.test/deepest.png") }, 3);
        Console.WriteLine("\n第 4 层：该被拒");
        Check("第 3 层引用的图片", "https://a.test/deepest.png", false);

        Console.WriteLine("\n重复抓取与回跳");
        Check("重抓用户目标（仍是第 1 层）", "https://a.test/", true);
        Check("重抓第 2 层那条", "https://a.test/p1", true);
        Check("自己编的网址（当新起点）", "https://b.test/whatever", true);

        // 一个被浅层引用的地址，不该因为深层也引用了就被记深
        d.Discover(new[] { new WebRef(RefKind.Page, "https://a.test/p1") }, 3);
        Console.WriteLine("\n浅层地址不被深层引用拉深");
        Check("p1 仍应是第 2 层", "https://a.test/p1", true);

        Console.WriteLine(bad == 0 ? "\n全部通过" : $"\n{bad} 条不符");
    }

    /// <summary>本地 HTML 过一遍剥离逻辑。见类注释里为什么这件事不该联网测。</summary>
    private static void RunHtml(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string src = TextDecode.Html(bytes, bytes.Length, null);
        var page = WebPage.Parse(src, "https://example.com/dir/page.html");

        Console.WriteLine($"input  {bytes.Length} bytes / {src.Length} chars");
        Console.WriteLine($"output {page.Text.Length} chars / {page.Refs.Count} 个引用");
        Console.WriteLine("--- 正文");
        Console.WriteLine(page.Text.Length > 2000 ? page.Text[..2000] + "\n…（截断显示）" : page.Text);
        Console.WriteLine("--- 引用");
        foreach (var r in page.Refs) Console.WriteLine($"  [{r.Kind}] {r.Url}");
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
