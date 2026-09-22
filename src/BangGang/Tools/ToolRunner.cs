namespace BangGang;

/// <summary>
/// 把一次工具调用跑完：查名字 → 执行 → 计时 → 截断 → 包成 <see cref="ToolCall"/>。
///
/// 这里是「模型说什么」和「本机真去做」之间唯一的关口，所以有三件事只能在这里做：
///   1. 关掉的工具即便被调用也不执行（模型可能是从上一轮的 schema 里记着的）；
///   2. 线程安排（见下）；
///   3. 结果截断 —— 模型要读的文件有多大是它自己说了不算的。
/// </summary>
internal static class ToolRunner
{
    /// <summary>
    /// 单个工具回给模型的结果上限（字符）。读文件那个尤其需要：一个几 MB 的日志
    /// 原样塞回去，这一轮的上下文当场就满了，而且用户看到的只是「回答变得很慢」。
    /// </summary>
    public const int MaxResultChars = 20000;

    /// <summary>被截断时补在末尾的那句话。要说清「你看到的不是全部」，否则模型会断言"文件里没有"。</summary>
    public const string TruncNote = "\n\n…（内容过长已截断，以上不是全部）";

    /// <summary>
    /// 跑一次工具调用。<paramref name="call"/> 的 <c>Result</c> / <c>Brief</c> /
    /// <c>Ok</c> / <c>Ms</c> 由这里填，<c>Id</c> / <c>Name</c> / <c>Args</c> 由调用方填（来自模型）。
    ///
    /// **不抛异常**：工具炸了也包成一条失败结果交给模型，让它自己决定怎么跟用户说 ——
    /// 抛出去的表现是整轮回复断在半路，用户完全不知道发生了什么。
    /// </summary>
    public static async Task<ToolCall> RunAsync(ToolCall call, ToolContext ctx, AppSettings settings, CancellationToken ct)
    {
        long t0 = Environment.TickCount64;
        try
        {
            var def = ToolRegistry.Find(call.Name);
            if (def == null)
            {
                call.Ok = false;
                call.Result = "没有名为 " + call.Name + " 的工具。";
            }
            else if (!ToolRegistry.Enabled(settings, def.Name))
            {
                call.Ok = false;
                call.Result = "用户已在设置里关闭了「" + ToolRegistry.LabelOf(def.Name) + "」工具，本次调用未执行。";
            }
            else
            {
                var args = ToolArgs.Parse(call.Args);
                call.Result = def.NeedsUi
                    ? await RunOnUiAsync(def, args, ctx, ct).ConfigureAwait(true)
                    : await def.RunAsync(args, ctx, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // 暂停键。留一句给用户看的话，但不再往外抛 —— 外层的 catch 会把正文一起收尾。
            call.Ok = false;
            call.Result = "（已暂停）";
        }
        catch (Exception ex)
        {
            call.Ok = false;
            call.Result = "工具执行失败：" + ex.Message;
        }

        call.Ms = (int)(Environment.TickCount64 - t0);
        if (call.Result.Length > MaxResultChars)
            call.Result = call.Result[..MaxResultChars] + TruncNote;
        return call;
    }

    /// <summary>
    /// 在 UI 线程上跑一次工具并把结果带回来。
    ///
    /// 需要这一层的只有剪贴板：<c>Clipboard.GetText()</c> 要 STA，而
    /// <see cref="Task.Run(System.Action)"/> 用的是线程池（MTA），直接调会抛。
    ///
    /// **没有窗口时就直接在当前线程上跑**（离屏自测那条路）：这时没有消息泵，
    /// <c>BeginInvoke</c> 投进去也没人取，等下去就是死等。之所以能这么退，
    /// 是因为离屏入口跑在 <c>Program.Main</c> 的主线程上，而它带着 <c>[STAThread]</c> ——
    /// 剪贴板那类要求照样满足。
    /// </summary>
    private static async Task<string> RunOnUiAsync(ToolDef def, ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var host = ctx.UiHost;
        if (host == null || host.IsDisposed || !host.IsHandleCreated)
            return await def.RunAsync(args, ctx, ct).ConfigureAwait(true);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            host.BeginInvoke(new Action(() =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(def.RunAsync(args, ctx, ct).GetAwaiter().GetResult()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
        }
        catch (Exception ex) { throw new InvalidOperationException("无法切回界面线程：" + ex.Message); }

        return await tcs.Task.ConfigureAwait(true);
    }
}
