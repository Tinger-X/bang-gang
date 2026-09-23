using System.Text;

namespace BangGang;

/// <summary>
/// 上下文这一块的接线：把「现在用了多少」推给输入框底部那枚仪表，
/// 以及在超窗口时决定这一轮到底发什么出去。
///
/// 单独开一个 partial 是因为 <c>MainForm.cs</c> 已经很长了（现有七个 partial 都是这个理由），
/// 而这一块和会话生命周期、工具循环都不是同一件事。
/// </summary>
partial class MainForm
{
    /// <summary>上一轮收尾时的生成速度，空闲时仪表显示的就是它（不是 0）。</summary>
    private double _lastTokPerSec;

    /// <summary>本轮生成速度的指数滑动平均，免得数字一跳一跳。</summary>
    private double _tokEma;

    // ---------------- 推给界面 ----------------

    /// <summary>
    /// 按当前会话重算并推送一次。
    ///
    /// 触发点：切会话、发消息、删会话、启动。**流式期间不走这里** ——
    /// 那时有更准的数（真在涨的 token、真在走的秒表），见 <see cref="PushStreamContext"/>。
    /// </summary>
    private void UpdateContextUi()
    {
        var c = _active;
        // 判据与 CanRenameTitle 相同：「这条会话已经开过口」。还没开口就显示快捷键提示 ——
        // 用户要求的是「第一轮对话开始后」才换成上下文信息。
        if (c == null || !c.Messages.Any(m => m.Role == "user"))
        {
            _input.ClearContext();
            return;
        }
        _input.SetContext(new CtxInfo(ContextManager.Used(c, LlmConfig.From(_settings)),
                                      _settings.ChatContextWindow, _lastTokPerSec, false));
    }

    /// <summary>
    /// 流式期间的那一版：多一个「真在生成」的速度。
    ///
    /// 走的是 StartReply 里那个已有的 40ms 时钟，**不额外加 Timer** ——
    /// 它本来就在为气泡刷新转，顺手带一个数值出去是免费的。
    /// </summary>
    private void PushStreamContext(StreamState st)
    {
        if (!ReferenceEquals(_active, st.Conv)) return;

        // 分子优先用估算：流式期间还没有 usage（它只在最后那一帧才来）。
        // 生成速度的分子**把思考也算进去** —— 分母（耗时）里本来就含思考时间，
        // 剔掉会让思考那几十秒显示成 0 tok/s，而模型明明在干活。
        int gen = ContextManager.Estimate(st.Msg.Text) + ContextManager.Estimate(st.Msg.Reasoning);
        double rate = _lastTokPerSec;
        if (st.FirstDelta > 0)
        {
            double sec = Math.Max(0.25, (Environment.TickCount64 - st.FirstDelta) / 1000.0);
            double sample = gen / sec;
            // EMA：第一拍直接取样本（否则要从 0 慢慢爬上来，看着像卡住了）
            _tokEma = _tokEma <= 0 ? sample : _tokEma * 0.7 + sample * 0.3;
            rate = _tokEma;
        }

        _input.SetContext(new CtxInfo(ContextManager.Used(st.Conv, LlmConfig.From(_settings)),
                                      _settings.ChatContextWindow, rate, true));
    }

    /// <summary>一轮收尾：用服务端报的真实用量把估算钉住，并记下最终速度。</summary>
    private void FinishContext(StreamState st)
    {
        // 钉的是**第一轮**的 prompt_tokens：工具的返回全文只在当轮发给模型、不进历史，
        // 最后一轮的输入里含着它们，拿它当「下次要发多少」会平白高估一大截。
        //
        // 锚点只在真拿到过 usage 时才钉 —— 没拿到（网关不报）就让它保持 0，
        // 下次仍走全量估算。写一个「估算出来的锚点」进去等于把猜测当成事实存档。
        if (st.FirstPromptTokens > 0)
        {
            st.Conv.LastPromptTokens = st.FirstPromptTokens;
            st.Conv.AnchorMsgs = st.AnchoredMsgs;
        }

        // 分子优先用服务端权威值（本来就含思考 token）；拿不到就退回估算。
        int gen = st.SumCompletion > 0
            ? st.SumCompletion
            : ContextManager.Estimate(st.Msg.Text) + ContextManager.Estimate(st.Msg.Reasoning);
        if (st.SumGenMs >= 250) _lastTokPerSec = gen / (st.SumGenMs / 1000.0);
        _tokEma = 0;
        UpdateContextUi();
    }

    // ---------------- 这一轮到底发什么 ----------------

    /// <summary>
    /// 决定这一轮实际发给模型的历史。**永远返回新列表，绝不改动 <c>conv.Messages</c>。**
    ///
    /// 装得下就一个字节都不动 —— 上下文管理只在真超了的时候才该有存在感。
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> PrepareHistoryAsync(
        Conversation conv, LlmConfig cfg, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        // 上一轮（或更早）压出来的摘要要一直带着，否则被它覆盖掉的那段就凭空消失了。
        cfg.CtxSummary = conv.CtxSummary ?? "";

        // 已经被摘要覆盖过的那一段不再重复发送（它现在以摘要的形式存在于 system 里）。
        // 下标是安全的：一条会话里的消息只增不减，压缩时记下的下标一直有效。
        int from = Math.Clamp(conv.CtxSummaryUpto, 0, history.Count);
        var live = new List<ChatMessage>(history.Count - from);
        for (int i = from; i < history.Count; i++) live.Add(history[i]);

        int window = _settings.ChatContextWindow;
        if (window <= 0) return live;

        int used = ContextManager.Used(conv, cfg);
        if (!ContextManager.OverLimit(used, window)) return live;

        int budget = ContextManager.InputBudget(window, cfg.MaxTokens);

        if (_settings.ChatContextMode != "latest")
        {
            int cut = ContextManager.Cut(live);
            if (cut > 0)
            {
                Trace.Log($"ctx compact: used={used} window={window} cut={cut}/{live.Count}");
                string? summary = await AskSummaryAsync(conv.CtxSummary, live, cut, cfg, ct);
                if (summary != null)
                {
                    conv.CtxSummary = summary;
                    conv.CtxSummaryUpto = from + cut;
                    // **锚点不用动**：Used() 会因为 AnchorMsgs 落到 CtxSummaryUpto 之前而
                    // 自动作废它、退回估算。下一轮拿到真实 usage 时会重新钉上。
                    cfg.CtxSummary = summary;
                    PersistChat(conv);
                    FlashStatus($"已把较早的 {cut} 条对话压成摘要");
                    return ContextManager.Trim(Slice(live, cut, live.Count), budget);
                }
                FlashStatus("上下文压缩失败，本次按「最新」处理");
            }
        }

        var keep = ContextManager.Trim(live, budget);
        int dropped = live.Count - keep.Count;
        if (dropped > 0)
        {
            Trace.Log($"ctx trim: used={used} window={window} dropped={dropped}/{live.Count}");
            // 告诉模型「前面还有话，是我没给它」—— 不说的话它会以为用户的第一句就是刚才那句，
            // 对着没头没尾的上下文发呆。
            cfg.CtxNote = $"（更早的 {dropped} 条消息因超出上下文已省略）";
        }
        return keep;
    }

    private static List<ChatMessage> Slice(IReadOnlyList<ChatMessage> src, int from, int to)
    {
        int a = Math.Clamp(from, 0, src.Count), b = Math.Clamp(to, a, src.Count);
        var list = new List<ChatMessage>(b - a);
        for (int i = a; i < b; i++) list.Add(src[i]);
        return list;
    }

    /// <summary>
    /// 把一段历史交给模型压成摘要。**拿不到就返回 null**，由调用方退回滑窗 ——
    /// 摘要失败绝不能让这轮对话发不出去。
    /// </summary>
    private static async Task<string?> AskSummaryAsync(
        string? previous, IReadOnlyList<ChatMessage> live, int cut, LlmConfig chat, CancellationToken ct)
    {
        try
        {
            var req = ContextManager.BuildSummaryRequest(Slice(live, 0, cut), previous);
            var sb = new StringBuilder();

            // HttpClient 的超时是 Infinite（见 LlmClient.Http），这里必须自己加一道 ——
            // 一个卡住不动的网关会把整轮回复吊死在门口，而用户看到的是「点了没反应」。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15_000);

            await LlmClient.StreamAsync(ContextManager.SummaryConfig(chat), new[] { req },
                s => { lock (sb) sb.Append(s); }, _ => { }, cts.Token).ConfigureAwait(true);

            lock (sb)
            {
                string t = sb.ToString().Trim();
                return t.Length > 0 ? t : null;
            }
        }
        // 用户自己按的暂停要继续往上抛（那会让整轮正常取消）；
        // 只有我们那个 15 秒超时才当作「摘要失败」吞掉。
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Trace.Log("ctx summary failed: " + ex.Message);
            return null;
        }
    }
}
