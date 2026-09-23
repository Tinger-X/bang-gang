#if DEBUG
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>
/// 把一份会话文件里的消息**离屏**渲染成 PNG，不建窗口、不碰桌面。
///
/// 为什么要有这个（和 <see cref="OfflineRender"/>、<see cref="OfflineTool"/> 是同一个理由）：
/// 气泡不是控件（见 <c>ChatView</c> 的类注释），所以「气泡画出来是什么样」只有两条能看到的路
/// —— 截图，或者 <c>Trace.DumpAfter</c> 的 DrawToBitmap。而这两条都要先有一个**开着会话的窗口**，
/// 而启动一律停在欢迎页、切换会话要点侧栏 —— 也就说**要验证一次排版得先有能用的桌面**。
/// 桌面挂了（驱动崩了、远程会话断了、蓝屏）的时候整条路会一起断，而断掉的样子和
/// 「排版写坏了」一模一样。
///
/// 它走的是**同一条** <see cref="MessageBubble"/> 绘制路径（DrawBack / DrawText / DrawOver），
/// 只是把 Graphics 从窗口换成了内存位图。它证明不了「ChatView 把它摆在了正确的坐标上」，
/// 但那本来就是 <c>ui-rows.json</c> 的事；它证明的是「这一条消息排出来是什么样」。
///
/// 触发方式（只认 Debug 构建）：
/// <list type="bullet">
///   <item><c>BANGGANG_BUBBLE</c>：一份会话 JSON（<c>chats\*.json</c> 那种格式）。设了才生效。</item>
/// </list>
///
/// 输出固定是同目录下的 <c>out-bubble-light.png</c> 与 <c>out-bubble-dark.png</c> —— **一次出两版**，
/// 而不是靠环境变量挑一个：明暗是两套完全不同的调色板，改一处很容易只对了一套，
/// 而「只在暗色下不对」恰恰是最容易漏掉的那一种错。
/// </summary>
internal static class OfflineBubble
{
    /// <summary>每条消息左右各留的边距，以及消息之间的间距。</summary>
    private const int Margin = 20;
    private const int Gap = 14;

    /// <summary>正文内宽。取一个接近对话区实际宽度的值，排版结果才有参考意义。</summary>
    private const int Inner = 620;

    /// <summary>草稿条的宽度。取一个接近输入卡片实际宽度的值。</summary>
    private const int DraftW = 720;

    private static readonly JsonSerializerOptions ReadOpt = new() { PropertyNameCaseInsensitive = true };

    public static bool TryRun()
    {
        string? src = Environment.GetEnvironmentVariable("BANGGANG_BUBBLE");
        if (string.IsNullOrWhiteSpace(src)) return false;

        try
        {
            Conversation? conv;
            string dir;

            // 以 .json 结尾 → 按**一份会话 JSON** 读（手工造的 fixture 仍然有用，
            // 不必为了试一段排版先去建一条真会话）。
            // 否则 → 当成**会话 id**，从库里取（BANGGANG_BUBBLE_OUT 可指定输出目录）。
            if (src.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string full = Path.GetFullPath(src);
                dir = Path.GetDirectoryName(full) ?? ".";
                string json = File.ReadAllText(full, Encoding.UTF8);
                conv = JsonNode.Parse(json)?["Conversation"]?.Deserialize<Conversation>(ReadOpt);
            }
            else
            {
                string outDir = Environment.GetEnvironmentVariable("BANGGANG_BUBBLE_OUT") ?? "";
                dir = outDir.Length > 0 ? outDir : Shoots.Dir();

                var all = ChatStore.Load();
                conv = all.FirstOrDefault(c => c.Id == src);
                if (conv is null)
                {
                    string ids = string.Join(", ", all.Select(c => c.Id));
                    Console.WriteLine($"库里没有会话 {src}。现有：{(ids.Length > 0 ? ids : "(空)")}");
                    return true;
                }
            }

            if (conv is null)
            {
                Console.WriteLine("这份 JSON 里没有 Conversation（顶层要有 Conversation 字段）");
                return true;
            }

            foreach (bool dark in new[] { false, true })
                Render(conv, dir, dark);
        }
        catch (Exception ex) { Console.WriteLine("bubble render failed: " + ex); }
        return true;    // 已经是离屏模式了，别再把窗口开起来
    }

    private static void Render(Conversation conv, string dir, bool dark)
    {
        if (conv.Messages == null || conv.Messages.Count == 0)
        {
            Console.WriteLine("这份会话里没有消息");
            return;
        }

        // 走设置那一份真正的调色板，别在这儿另抄一套颜色 —— 抄的那套一旦和界面分家，
        // 这里看着没问题的那张图就再也证明不了界面上是什么样。
        var settings = new AppSettings();
        settings.ThemeMode = dark ? "dark" : "light";
        settings.ApplyTheme();

        // 先都排一遍拿到尺寸，再开位图 —— 位图尺寸在画之前就得定下来。
        var bubbles = new List<MessageBubble>();
        foreach (var m in conv.Messages)
        {
            if (m == null) continue;
            var b = new MessageBubble(m, m.Role == "user");
            b.SetMaxInner(Inner);
            bubbles.Add(b);
        }
        if (bubbles.Count == 0) { Console.WriteLine("没有可渲染的消息"); return; }

        int w = bubbles.Max(b => b.Width) + Margin * 2;
        int h = Margin * 2 + bubbles.Sum(b => b.Height) + Gap * (bubbles.Count - 1);
        w = Math.Max(40, Math.Min(w, 4000));
        h = Math.Max(20, Math.Min(h, 20000));

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.ChatBg);

            var at = new List<Point>();
            int yy = Margin;
            foreach (var b in bubbles) { at.Add(new Point(Margin, yy)); yy += b.Height + Gap; }

            // 三遍分开走，**不要**每条消息把三遍连在一起：中间那一遍要借走 Graphics 的 HDC
            // （Markdown.HoldTextDc），而 HDC 被借走期间再调任何 GDI+（DrawOver 里的
            // Graphics.Save）会抛 "Object is currently in use elsewhere"。
            // ChatView.OnPaint 也是这么分的，这里照抄。
            for (int i = 0; i < bubbles.Count; i++)
                bubbles[i].DrawBack(g, at[i], at[i].Y, at[i].Y + bubbles[i].Height);

            using (var dc = Markdown.HoldTextDc(g))
                for (int i = 0; i < bubbles.Count; i++)
                    bubbles[i].DrawText(dc, at[i], at[i].Y, at[i].Y + bubbles[i].Height);

            for (int i = 0; i < bubbles.Count; i++)
                bubbles[i].DrawOver(g, at[i], at[i].Y, at[i].Y + bubbles[i].Height);
        }

        foreach (var b in bubbles)
        {
            Console.WriteLine($"  [{(dark ? "dark" : "light")}] {b.Width}x{b.Height}"
                              + $" tools={(b.Msg.ToolCalls?.Count ?? 0)}"
                              + $" reason={(b.Msg.Reasoning ?? "").Length} text={(b.Msg.Text ?? "").Length}");
            b.Dispose();
        }

        string outPath = Path.Combine(dir, dark ? "out-bubble-dark.png" : "out-bubble-light.png");
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"rendered {w}x{h} -> {outPath}");

        RenderDraft(conv, dir, dark);
    }

    /// <summary>
    /// 输入框上方那条附件草稿条。
    ///
    /// 它和气泡里的附件格**共用** <c>DraftStrip.PaintChip</c> 那张卡片，但画在**没有变换**的
    /// Graphics 上（气泡那边是在 TranslateTransform 里画的）。同一个卡片体、两套坐标前提，
    /// 所以「气泡里对了」证明不了「草稿条里也对」—— 而草稿条恰恰是用户附完文件第一眼看到的东西。
    /// 它又是个普通的 <see cref="Control"/>，能直接离屏画，不必去截那个半透明的窗口。
    /// </summary>
    private static void RenderDraft(Conversation conv, string dir, bool dark)
    {
        var withFiles = conv.Messages?.FirstOrDefault(m => m?.Attachments is { Count: > 0 });
        if (withFiles?.Attachments == null)
        {
            Console.WriteLine("  （没有带附件的消息，跳过草稿条）");
            return;
        }

        using var strip = new DraftStrip();
        strip.SetItems(withFiles.Attachments);
        strip.Size = new Size(DraftW, DraftStrip.RowH);

        using var bmp = new Bitmap(strip.Width, strip.Height);
        strip.DrawToBitmap(bmp, new Rectangle(0, 0, strip.Width, strip.Height));

        string outPath = Path.Combine(dir, dark ? "out-draft-dark.png" : "out-draft-light.png");
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"  [{(dark ? "dark" : "light")}] draft {strip.Width}x{strip.Height}"
                          + $" items={withFiles.Attachments.Count} -> {outPath}");
    }
}
#endif
