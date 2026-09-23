namespace BangGang;

/// <summary>
/// 对话上下文的管理：算「现在用了多少 token」，以及在超出窗口时怎么处理。
///
/// 这个类里**全是纯函数**（除了 <see cref="BuildSummaryRequest"/> 造一条消息），
/// 只吃 <see cref="ChatMessage"/> 列表、只返回新的列表，**绝不改动 <c>Conversation.Messages</c>**。
/// 这不是风格洁癖：那份列表同时是界面渲染、侧栏搜索、文件工具找附件路径的账本，
/// 被截断一次就意味着用户看见消息凭空消失。裁掉的永远只是「这一轮要发出去的快照」，
/// 而快照在 <c>MainForm.StartReply</c> 里本来就是单独拍的一份。
///
/// token 估算**故意往多里估**：高估只是让压缩早一点触发（无害，顶多损失一点早期细节），
/// 低估会把请求撑爆、接口直接 400，整轮白跑。失败方向不对称，所以取保守的一侧。
/// </summary>
internal static class ContextManager
{
    /// <summary>用到窗口的多少就动手。留 20% 给这一轮的回复和误差。</summary>
    public const double TriggerRatio = 0.80;

    /// <summary>最近多少个「用户轮」原样保留，不打摘要也不裁。</summary>
    public const int KeepTurns = 4;

    /// <summary>额外留的余量：系统提示词的增长、消息包装、以及估偏的那一点。</summary>
    public const int ReserveTokens = 1024;

    /// <summary><c>ChatMaxTokens == 0</c>（不限）时，按这个估给回复留的位置。</summary>
    private const int AssumedMaxReply = 4096;

    // ---------------- token 估算 ----------------

    /// <summary>
    /// 中英混排的 token 估算。
    ///
    /// 三个系数是**经验值，不是事实**，取的都是保守的一侧：
    /// ASCII 约 4 字符/token 是英文的常识值，这里取 3.5 让它偏多；
    /// CJK 在现代 BPE 上约 0.6–1.0 token/字，取 0.9 当上界；
    /// 其余宽字符（西里尔、emoji）取 0.7。
    ///
    /// 真值由每轮回来的 <c>usage.prompt_tokens</c> 校准 —— 见 <see cref="Used"/> 里怎么用锚点。
    /// </summary>
    public static int Estimate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int ascii = 0, cjk = 0, other = 0;
        foreach (char c in s)
        {
            if (c < 0x80) ascii++;
            else if (IsWide(c)) cjk++;
            else other++;
        }
        return (int)Math.Ceiling(ascii / 3.5 + cjk * 0.9 + other * 0.7);
    }

    /// <summary>汉字 / 假名 / 谚文 / 全角标点 —— 这些按「一字约一个 token」算。</summary>
    private static bool IsWide(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||     // 基本汉字
        (c >= 0x3400 && c <= 0x4DBF) ||     // 扩展 A
        (c >= 0x3000 && c <= 0x303F) ||     // 中日韩标点
        (c >= 0xFF00 && c <= 0xFFEF) ||     // 全角字符
        (c >= 0x3040 && c <= 0x30FF) ||     // 平假名 / 片假名
        (c >= 0xAC00 && c <= 0xD7AF);       // 谚文

    /// <summary>
    /// 一条消息发出去要占多少 —— 正文 + 首行附件提示 + 工具摘要 + 一点包装开销。
    /// **思考过程不算**（<c>BuildMessages</c> 根本不回放它），算了会白白高估一大截。
    /// </summary>
    public static int EstimateMessage(ChatMessage m)
    {
        if (m == null) return 0;
        int n = Estimate(m.Text) + 4;

        // 工具调用只回放一行 Brief（见 LlmClient.ToolNote），全文不回放
        if (m.ToolCalls != null)
            foreach (var t in m.ToolCalls)
                if (t != null) n += Estimate(t.Brief) + 8;

        // 图片按多模态的开销算。各家公式不同，这里取一条常见的形状当近似；
        // 拿不到尺寸时按一个中等值估，宁可多算。
        if (m.Attachments != null)
            foreach (var a in m.Attachments)
                if (a != null && a.Kind == "image") n += 400;

        return n;
    }

    /// <summary>真正能塞进历史的输入预算：窗口 − 这一轮回复要占的 − 余量。</summary>
    public static int InputBudget(int window, int maxTokens)
    {
        int reply = maxTokens > 0 ? maxTokens : AssumedMaxReply;
        return Math.Max(1024, window - reply - ReserveTokens);
    }

    /// <summary>
    /// 这条会话现在占了窗口多少 token。
    ///
    /// 两种口径，**优先用第一种**：
    /// <list type="number">
    ///   <item><b>有锚点</b> —— 上一轮服务端报的真实输入量 + 那次之后新增的消息。
    ///     这是准的，而且误差不随对话变长累积（新增的几条本来就是小量）。</item>
    ///   <item><b>没锚点</b> —— 全程估算。只在这条会话还没发过任何一次请求、
    ///     或者网关不报 usage 时才走到。</item>
    /// </list>
    ///
    /// 两种口径都**从 <see cref="Conversation.CtxSummaryUpto"/> 往后数** ——
    /// 被摘要覆盖过的那一段不再逐条计入，它现在以摘要的形式存在着。
    /// 漏掉这一点的后果很具体：压缩过一次之后每一轮都判「还是超」，于是**反复压缩同一段内容**。
    /// </summary>
    public static int Used(Conversation c, LlmConfig cfg)
    {
        int from = Math.Clamp(c.CtxSummaryUpto, 0, c.Messages.Count);

        // 锚点覆盖的范围必须和当前口径一致：压缩会把 from 推后，那时旧锚点量的是
        // 另一份历史，作废（退回全量估算）。下一轮拿到新的真实值会重新钉上。
        if (c.LastPromptTokens > 0 && c.AnchorMsgs >= from && c.AnchorMsgs <= c.Messages.Count)
        {
            int since = 0;
            for (int i = c.AnchorMsgs; i < c.Messages.Count; i++) since += Count(c.Messages[i]);
            return c.LastPromptTokens + since;
        }

        int est = Estimate(cfg.SystemPrompt) + Estimate(cfg.Reinforce) + Estimate(c.CtxSummary) + 8;
        for (int i = from; i < c.Messages.Count; i++) est += Count(c.Messages[i]);
        return est;
    }

    private static int Count(ChatMessage? m) => m != null && !m.IsEmpty ? EstimateMessage(m) : 0;

    /// <summary>这条会话是不是已经越过阈值了。</summary>
    public static bool OverLimit(int used, int window)
        => window > 0 && used >= window * TriggerRatio;

    // ---------------- 切点与滑窗 ----------------

    /// <summary>
    /// 「最近 <paramref name="keepTurns"/> 个用户轮」的第一条消息的下标。
    /// 返回 0 表示整段都该保留（轮数本来就不够）。
    ///
    /// 往后数的单位是**用户轮**而不是消息条数：一轮里可能有七八条（工具调用的来回），
    /// 按条数切会把「刚才那次提问」连同它的工具结果一起切掉。
    /// </summary>
    public static int Cut(IReadOnlyList<ChatMessage> history, int keepTurns = KeepTurns)
    {
        int seen = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i]?.Role != "user") continue;
            if (++seen < keepTurns) continue;
            return i;
        }
        return 0;
    }

    /// <summary>
    /// 「最新」策略：从后往前贪心地装，装不下就停，更老的一律丢。
    /// 返回保留的那一段（**新列表**，不动传进来的那份）。
    /// </summary>
    public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> history, int budget)
    {
        int total = 0, start = history.Count;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            int t = EstimateMessage(history[i]);
            if (total + t > budget && start < history.Count) break;
            total += t;
            start = i;
        }

        // 保留段从一条 user 消息开始。以 assistant 开头会让一部分网关犯迷糊
        // （它们期待的是 user / assistant 交替），而且模型看到的第一句是「自己说过的话」，
        // 容易接着自说自话。
        while (start < history.Count && history[start]?.Role != "user") start++;
        if (start >= history.Count) start = Math.Max(0, history.Count - 1);

        var keep = new List<ChatMessage>(history.Count - start);
        for (int i = start; i < history.Count; i++) keep.Add(history[i]);
        return keep;
    }

    // ---------------- 「压缩」策略 ----------------

    /// <summary>摘要请求用的那段指令。写成一段「照着做」的话，而不是一个问题。</summary>
    private const string SummaryInstruction =
        "请把下面这段对话压缩成一段摘要，供后续对话继续使用。要求：\n" +
        "1. 保留事实性信息：用户在做什么、已经确定的结论、提到过的具体名称与数字；\n" +
        "2. 保留未完成的事：还没回答的问题、待办、用户明确提出的要求；\n" +
        "3. 丢掉寒暄与重复；不要评论、不要补充新内容；\n" +
        "4. 用第三人称陈述，直接输出摘要正文，不要任何前缀或标题。";

    /// <summary>
    /// 造一条「请把这段历史压成摘要」的请求消息。
    ///
    /// 历史在塞进去之前**按回灌的同一套规矩压过**：正文照发，工具调用只留一行 Brief，
    /// **不发思考过程、不发截断说明** —— 与 <c>LlmClient.BuildMessages</c> 回放历史时的
    /// 取舍完全一致。摘要请求要是把思考过程也喂进去，输入会平白涨几倍，
    /// 而摘要本身用不上「模型当时想了什么」。
    ///
    /// <paramref name="previous"/> 是上一次的摘要（没有就是空串）。带上它做**迭代摘要**：
    /// 否则每压一次都从头总结一遍，摘要长度会随对话轮数线性膨胀，最后自己变成新的问题。
    /// </summary>
    public static ChatMessage BuildSummaryRequest(IReadOnlyList<ChatMessage> older, string? previous)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(previous))
        {
            sb.AppendLine("【此前已有的摘要】");
            sb.AppendLine(previous.Trim());
            sb.AppendLine();
            sb.AppendLine("【在那之后新发生的对话】");
        }
        foreach (var m in older)
        {
            if (m == null || m.IsEmpty) continue;
            sb.Append(m.Role == "user" ? "用户：" : "助手：");
            sb.AppendLine((m.Text ?? "").Trim());
            if (m.ToolCalls == null) continue;
            foreach (var t in m.ToolCalls)
                if (t != null && t.Brief.Length > 0) sb.AppendLine("（" + t.Brief + "）");
        }
        sb.AppendLine();
        sb.AppendLine(SummaryInstruction);

        return new ChatMessage { Role = "user", Text = sb.ToString() };
    }

    /// <summary>
    /// 摘要请求要用的配置：拿对话配置改出来的一份。
    ///
    /// **必须把 <c>CtxSummary</c> / <c>CtxNote</c> 清空**：不清的话这次摘要请求自己
    /// 又会被塞进上一版摘要，越滚越大。温度调低是因为这是归纳不是创作。
    /// </summary>
    public static LlmConfig SummaryConfig(LlmConfig chat) => new()
    {
        Url = chat.Url,
        ApiKey = chat.ApiKey,
        Model = chat.Model,
        Vision = false,
        Temperature = 0.3,
        MaxTokens = 1024,
        SystemPrompt = "",
        Reinforce = "",
    };
}
