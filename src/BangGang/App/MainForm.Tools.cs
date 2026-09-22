using System.Text.Json.Nodes;

namespace BangGang;

/// <summary>
/// 工具调用这一轮的驱动：模型要调工具 → 本机执行 → 结果回灌 → 再问一次，直到它不再要工具。
///
/// 单独一个 partial 而不是塞进 <c>MainForm.Chat.cs</c>：那边管的是「怎么把一轮回复画出来」，
/// 这边管的是「一轮里可能要来回好几次」，两者的状态和约束都不一样
/// （见下面四条不许破坏的前提）。
/// </summary>
partial class MainForm
{
    /// <summary>
    /// 一轮里最多问模型几次。<b>是请求次数而不是工具次数</b>：5 次请求、最多 4 轮工具。
    ///
    /// 要有上限是因为这是个循环而模型可以永远要工具（尤其在工具老失败的时候）——
    /// 没有上限的表现是「问一句，转很久，然后没结果」，用户既不知道发生了什么，
    /// 也没法让它停。
    /// </summary>
    private const int MaxToolRounds = 5;

    /// <summary>
    /// 跑完一整轮（含工具往返）。返回**最后一次**请求的结果，供调用方判
    /// 「是不是被截断了」（<c>finish_reason</c> 与思考 token 都在里面）。
    ///
    /// 四条不许破坏的前提（都是现有代码约定好的，破坏的后果写在各自后面）：
    ///   1. <paramref name="history"/> 是发起这一轮时拍的**快照**，循环里往后攒的是
    ///      <c>msgs</c>。继续用 history 会丢掉工具往返，模型会反复调同一个工具。
    ///   2. 一轮**仍然只有一条** <c>st.Msg</c>。工具记录挂在它身上 ——
    ///      <c>_stream</c> / <c>BubbleFor</c> / <c>_input.Busy</c> 的收尾全建立在
    ///      「一轮 = 一个气泡」上，多开气泡要连带改一串东西。
    ///   3. 异常往外抛，由 <c>StartReply</c> 那个**包住整个循环**的 try 接住。
    ///      每轮各包一个 try 的话，第二轮回调失败会把第一轮已经收到的正文一起丢掉。
    ///   4. 每轮记下 <c>Text</c> 的起始偏移。它是跨轮累加的，回灌时只该带本轮新增的那段。
    /// </summary>
    private async Task<LlmStreamResult> RunToolLoop(LlmConfig cfg, Conversation conv, StreamState st,
                                                    IReadOnlyList<ChatMessage> history)
    {
        var msgs = LlmClient.BuildMessages(cfg, history);
        var ctx = new ToolContext { Conv = conv, UiHost = this };
        LlmStreamResult res;

        for (int round = 0; ; round++)
        {
            int textAt = st.Msg.Text.Length;

            res = await AskModelAsync(cfg, msgs, st, allowToolFallback: round == 0);
            if (res.ToolCalls.Count == 0) break;      // 模型正常答完了

            if (round >= MaxToolRounds - 1)
            {
                // 次数用完了还在要工具：把话说明白。不说的话用户看到的只是「回答没写完」，
                // 而模型那边也完全不知道自己是为什么停的。
                st.Msg.Warning = "（已达工具调用上限 " + MaxToolRounds + " 次，这一轮先停在这里）";
                break;
            }

            // 把「刚才要求调用这些工具」这条 assistant 消息补进对话，
            // 正文只带**本轮新生成的**那一段（见前提 4）。
            msgs.Add(LlmClient.AssistantToolFrame(st.Msg.Text[textAt..], res.ToolCalls));

            foreach (var call in res.ToolCalls)
            {
                if (st.Cts.IsCancellationRequested) break;

                FlashStatus("正在使用工具：" + ToolRegistry.LabelOf(call.Name) + "…");
                var done = await ToolRunner.RunAsync(call, ctx, _settings, st.Cts.Token);

                st.Msg.ToolCalls.Add(done);
                Flush(st);          // 先把可能压着的正文增量落到消息上
                PaintTools(st);     // 再把工具那一行画出来（不能指望 Flush 顺手画，见那里）
                Trace.Log($"tool call name={done.Name} ok={done.Ok} ms={done.Ms}");

                msgs.Add(LlmClient.ToolResultFrame(done));
            }

            if (st.Cts.IsCancellationRequested) break;
        }

        return res;
    }

    /// <summary>
    /// 工具记录变了之后把气泡刷出来。
    ///
    /// **不能指望 <see cref="Flush"/> 顺手画**：它只在「正文或思考有新增」时才重画，
    /// 而这里变的是 <c>ToolCalls</c>。漏了这一下的表现是「工具明明跑完了，
    /// 气泡上什么都没有」，一直要等到模型下一个字符到达才突然冒出来 ——
    /// 而调用工具的那几秒正是用户最需要看见反馈的时候。
    /// </summary>
    private void PaintTools(StreamState st)
    {
        if (!ReferenceEquals(_active, st.Conv)) return;
        var b = _chatView.BubbleFor(st.Msg);
        if (b == null) return;
        // 已经在调工具了，那三个点的占位动画该让位（见 MessageBubble 的 _showWait）
        _chatView.SetWaiting(st.Msg, false);
        b.RefreshText();
        _chatView.NotifyRowGrew();
    }

    /// <summary>
    /// 发一次请求并读完流。回调都是把增量塞进 <see cref="StreamState"/> 的缓冲，
    /// 由那个 40ms 的表落到界面上（和没有工具时走的是同一条路）。
    ///
    /// <paramref name="allowToolFallback"/> 为真时，接口明确表示不认 tools 的话
    /// 就**去掉工具重发一次**。见 <see cref="LlmClient.LooksLikeToolRejection"/>。
    /// </summary>
    private async Task<LlmStreamResult> AskModelAsync(LlmConfig cfg, JsonArray msgs, StreamState st,
                                                      bool allowToolFallback)
    {
        try
        {
            return await LlmClient.StreamAsync(cfg, msgs, OnDelta, OnReason, OnToolStart, st.Cts.Token);
        }
        catch (LlmException ex) when (allowToolFallback && cfg.Tools is { Count: > 0 }
                                      && LlmClient.LooksLikeToolRejection(ex.Message))
        {
            // 直接把 cfg 上的工具摘掉，而不是拷一份配置：这一轮剩下的几次请求也不该再带
            // 工具了（模型既然不认，下一轮还会报同一个错）。cfg 是本轮临时构造的
            // （LlmConfig.From），改它不影响设置，也不影响会话起名那条路
            // （那边用的是另一个 LlmConfig）。
            cfg.Tools = null;
            Trace.Log("tools rejected, retrying without: " + ex.Message);
            PostToUi(() => FlashStatus("当前模型不支持工具调用，本次已自动关闭"));
            return await LlmClient.StreamAsync(cfg, msgs, OnDelta, OnReason, null, st.Cts.Token);
        }

        void OnDelta(string s) { lock (st.Gate) st.Pending.Append(s); }

        void OnReason(string s)
        {
            lock (st.Gate)
            {
                if (st.ReasonStart == 0) st.ReasonStart = Environment.TickCount64;
                st.PendingReason.Append(s);
            }
        }

        void OnToolStart(string name) =>
            PostToUi(() => FlashStatus("正在调用 " + ToolRegistry.LabelOf(name) + "…"));
    }
}
