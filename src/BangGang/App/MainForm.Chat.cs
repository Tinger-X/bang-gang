using System.Drawing.Drawing2D;
using System.Text;

namespace BangGang;

partial class MainForm
{
    // ---------------- 会话管理 ----------------

    private void NewConversation()
    {
        var c = new Conversation();
        _conversations.Add(c);
        ActivateConversation(c);
    }

    private void ActivateConversation(Conversation c)
    {
        _active = c;
        _convTitle.Text = string.IsNullOrWhiteSpace(c.Title) ? "新对话" : c.Title;
        _chatView.Load(c);
        _chatUI.Visible = true;
        _welcome.Visible = false;
        RebindConversations();
        _input.FocusInput();
        RememberActive();
    }

    private void DeleteConversation(Conversation c)
    {
        _conversations.Remove(c);
        ChatStore.Delete(c.Id);          // 会话文件跟着删；它的附件不删，见 ChatStore.ImageDir
        if (_active == c)
        {
            _active = null;
            _chatUI.Visible = false;
            _welcome.Visible = true;
            _chatView.Load(null!);
            EnsureSidebarOpen();     // 顶栏（连同收起按钮）没了，侧栏就得自己回来
        }
        RebindConversations();
        RememberActive();
    }

    /// <summary>把一条会话写进它自己的文件。调用点是「内容定稿」的几处，见 <see cref="ChatStore.Save"/>。</summary>
    private void PersistChat(Conversation c) => ChatStore.Save(c);

    /// <summary>
    /// 记住「用户现在开的是哪条」。
    ///
    /// 注意它**不再影响启动**：启动一律停在欢迎页，见 <see cref="RestoreConversations"/>。
    /// 留着它是「用户最后开过哪条」这个事实本身还有用，也一直有探针在断言它。
    ///
    /// 没变就不写：切会话是常事，每次重写一遍 settings.json 没必要。
    /// </summary>
    private void RememberActive()
    {
        string id = _active?.Id ?? "";
        if (_settings.ActiveChatId == id) return;
        _settings.ActiveChatId = id;
        _settings.Save();
    }

    /// <summary>
    /// 启动时把历史会话读进侧栏列表，但**一条都不打开** —— 一律停在欢迎页。
    ///
    /// 这里以前会挑一条激活（settings.json 里的 <c>ActiveChatId</c>，没有就取最近更新的
    /// 那条），用户明确要求改掉：一打开程序就是上一次那段对话，想开个新的还得先手动退出来。
    /// 历史仍然一条不少地列在左边，点一下就进去 —— 「记得住」和「自动进去」是两件事。
    ///
    /// 不激活这件事本身不需要额外代码：<c>_active</c> 保持 null，
    /// <c>_chatUI</c> 就是构造函数里设的不可见、<c>_welcome</c> 默认可见，
    /// 于是自然停在欢迎页。**别在这里补一句「让欢迎页可见」** —— 那会多出一个
    /// 只在启动那一瞬成立的状态，往后再有人加一条退出会话的路径就又对不上了。
    ///
    /// <see cref="ChatStore.Load"/> 的 0.8.1 单文件导入照跑：历史要迁进 <c>chats\</c>，
    /// 只是迁完不再顺手打开它。它一起带回来的「当时开着哪条」同理不再被读。
    /// </summary>
    private void RestoreConversations()
    {
        var (list, _) = ChatStore.Load();
        if (list.Count == 0) return;
        _conversations.AddRange(list);
        RebindConversations();
    }

    private void RebindConversations()
    {
        string q = _search.Text.Trim();
        List<Conversation> list;
        if (q.Length == 0)
        {
            list = _conversations.OrderByDescending(x => x.UpdatedAt).ToList();
        }
        else
        {
            list = _conversations
                .Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || x.Messages.Any(m => m.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.UpdatedAt).ToList();
        }
        _convList.Rebind(list, _active?.Id);
    }

    private void SendFromInput()
    {
        if (_active == null) return;

        // 还没配好模型就一个字都不发，只在顶栏说一句「差什么」。
        //
        // 守卫放在 **Flush() 之前**：拦下来之后用户刚敲的字必须原样留在输入框里。
        // 先清空再报错，等于把他打的一段话吃掉，比「点了没反应」更糟。
        //
        // 「还差什么」只有 LlmConfig.Problem 一处判断（Providers/LlmClient.cs），
        // 那句话本来就是写给用户看的，顶栏直接照用 —— 在这里另拼一句，
        // 两处说法早晚会分叉（改了校验规则、提示还在说老的那一条）。
        //
        // 发送按钮此时仍然是可点的（InputPanel 只在输入框为空时才让它失效）：
        // 按钮点不动的话，「企图发送」这个动作根本无从发生，这句提示也就永远不会出现。
        if (LlmConfig.From(_settings).Problem is { } problem) { FlashStatus(problem); return; }

        var m = _input.Flush();
        if (m == null) return;
        _active.Messages.Add(m);
        _chatView.AddMessage(m);
        _active.RefreshTitle();
        RebindConversations();
        _convTitle.Text = _active.Title;
        // 用户这句先落盘，再等回复：回复要跑好几秒（还可能中途暂停、被杀），
        // 不能让刚打出来的问题跟着那一轮一起悬着。
        PersistChat(_active);
        StartReply();
    }

    // ---------------- 流式回复 ----------------

    /// <summary>
    /// 一轮流式回复的私有状态。
    ///
    /// 做成一个对象而不是散着的几个字段，是为了让「上一轮」和「这一轮」彻底分开：
    /// 用户在一轮还没回完时又发了一条（或按暂停后立刻再发），上一轮迟到的尾巴如果落进
    /// 共用的缓冲里，就会被追加到**新一轮**的消息上 —— 而且不报任何错，只是回答里凭空
    /// 多出一截上一轮的结尾。
    /// </summary>
    private sealed class StreamState
    {
        public readonly StringBuilder Pending = new();

        /// <summary>思考增量。和正文分两个缓冲，收尾时才知道哪一路有东西。</summary>
        public readonly StringBuilder PendingReason = new();

        public readonly object Gate = new();
        public required Conversation Conv;
        public required ChatMessage Msg;
        public readonly CancellationTokenSource Cts = new();
        public System.Windows.Forms.Timer? Timer;

        /// <summary>第一段思考到达的时刻（<see cref="Environment.TickCount64"/>）。0 = 还没开始思考。</summary>
        public long ReasonStart;
    }

    private StreamState? _stream;

    private async void StartReply()
    {
        var conv = _active;
        if (conv == null) return;

        var cfg = LlmConfig.From(_settings);
        if (cfg.Problem is { } problem)
        {
            AppendAssistant(conv, "⚠️ " + problem);
            FlashStatus("尚未配置对话模型");
            return;
        }

        // 先把这一轮要发的历史拍下来：下面紧接着就往 Messages 里塞了一条空的助手消息，
        // 它是给界面放回复用的，带着一起发出去等于让模型接着自己的空回复往下写。
        var history = conv.Messages.ToList();

        var st = new StreamState { Conv = conv, Msg = new ChatMessage { Role = "assistant", Text = "" } };
        conv.Messages.Add(st.Msg);

        CancelStream();                       // 上一轮（如果有）到此为止
        _stream = st;
        _input.Busy = true;
        // 先立一个空气泡，并让它进入「等第一个字」的占位动画：这几秒钟里它否则只有
        // 顶上那行「帮帮」，看着像半截断掉的气泡（用户要求）。
        if (ReferenceEquals(_active, conv)) _chatView.AddMessage(st.Msg, waiting: true);

        st.Timer = new System.Windows.Forms.Timer { Interval = 40 };
        st.Timer.Tick += (_, _) => Flush(st);
        st.Timer.Start();

        Trace.Log($"llm request provider={_settings.ChatProvider} model={cfg.Model} msgs={history.Count}");
        FlashStatus("正在等待模型回复…");
        try
        {
            var res = await LlmClient.StreamAsync(
                cfg, history,
                s => { lock (st.Gate) st.Pending.Append(s); },
                s =>
                {
                    lock (st.Gate)
                    {
                        if (st.ReasonStart == 0) st.ReasonStart = Environment.TickCount64;
                        st.PendingReason.Append(s);
                    }
                },
                st.Cts.Token);

            Flush(st);
            // 上限用完时流是「正常」结束的（finish_reason=length），不主动看一眼就只剩
            // 「回复说着说着没了」这一个现象。见 LlmStreamResult 的注释。
            if (res.Finish == "length") st.Msg.Warning = TruncationNote(cfg, res, st.Msg);
            if (st.Msg.Text.Length == 0 && st.Msg.Warning.Length == 0)
                st.Msg.Text = "（模型没有返回内容）";
            Trace.Log($"llm done chars={st.Msg.Text.Length} reason={st.Msg.Reasoning.Length} finish={res.Finish}");
        }
        catch (OperationCanceledException)
        {
            Flush(st);                        // 暂停时已经收到的部分要留下，不能连它一起丢
            Trace.Log("llm cancelled chars=" + st.Msg.Text.Length);
        }
        catch (Exception ex)
        {
            Flush(st);
            st.Msg.Text = (st.Msg.Text.Length > 0 ? st.Msg.Text + "\n\n" : "") + "⚠️ " + ex.Message;
            Trace.Log("llm error: " + ex.Message);
        }
        finally
        {
            st.Timer?.Stop();
            st.Timer?.Dispose();
            st.Timer = null;
            st.Cts.Dispose();
            Flush(st);                              // 收尾时还可能压着最后一段思考没落地
            // 出错 / 暂停 / 空回复这几条路都到不了上面那句「收到第一个字符」，
            // 不收这一笔，三个点会一直转下去 —— 而这一轮早就结束了。
            _chatView.SetWaiting(st.Msg, false);
            // 思考了多久只有这里知道（回调只看得到「第一段是什么时候到的」）。
            // 已经在计时器里定过就不再覆盖：一轮里 PaintStream 只该让它变一次。
            if (st.ReasonStart != 0 && st.Msg.ReasoningMs == 0)
                st.Msg.ReasoningMs = (int)(Environment.TickCount64 - st.ReasonStart);
            PaintStream(st);                        // 收尾那一笔（错误说明 / 空回复提示）也要落到气泡上
            if (ReferenceEquals(_stream, st))
            {
                _stream = null;
                _input.Busy = false;          // 只有最新那一轮才有资格把发送键变回「发送」
            }
            conv.RefreshTitle();
            if (ReferenceEquals(_active, conv)) RebindConversations();
            PersistChat(conv);      // 这一轮的正文（含暂停/出错留下的那半截）到此定稿
        }
    }

    /// <summary>把缓冲里的增量落到气泡上。后台线程只管往两个 Pending 里塞，一个字都不碰控件。</summary>
    private void Flush(StreamState st)
    {
        string add;
        bool grew;
        lock (st.Gate)
        {
            grew = st.Pending.Length > 0 || st.PendingReason.Length > 0;
            add = st.Pending.ToString();
            st.Pending.Clear();
            if (st.PendingReason.Length > 0)
            {
                st.Msg.Reasoning += st.PendingReason.ToString();
                st.PendingReason.Clear();
            }
        }
        if (!grew) return;
        st.Msg.Text += add;
        PaintStream(st);
    }

    /// <summary>重画这一轮的气泡。没在看这个会话时什么都不做 —— 内容已经写进 Messages 了，
    /// 切回来时 <see cref="ChatView.Load"/> 会照常画出来。</summary>
    private void PaintStream(StreamState st)
    {
        if (!ReferenceEquals(_active, st.Conv)) return;
        var b = _chatView.BubbleFor(st.Msg);
        if (b == null) return;
        // 第一个有效字符到了（正文或思考都算）：占位动画让位给真内容。
        // 交给 ChatView 而不是气泡自己判，是因为动画的时钟也在那儿（见 UpdateWaitTimer）。
        if (st.Msg.Text.Length > 0 || st.Msg.Reasoning.Length > 0) _chatView.SetWaiting(st.Msg, false);
        b.RefreshText();
        _chatView.NotifyRowGrew();
    }

    /// <summary>
    /// 直接往会话里补一条助手消息（提问没发出去时用它说明原因）。
    ///
    /// 这里也要落盘，不能指望 <see cref="StartReply"/> 的 finally：走「还没配模型」那条路时
    /// 是在 try **之前**就 return 的，收尾根本不执行 —— 于是重启之后用户看到自己的问题孤零零
    /// 挂在上面，下面那句说明没了。凡是往 Messages 里加消息的地方都得自己负责存下来。
    /// </summary>
    private void AppendAssistant(Conversation conv, string text)
    {
        var m = new ChatMessage { Role = "assistant", Text = text };
        conv.Messages.Add(m);
        conv.RefreshTitle();
        if (ReferenceEquals(_active, conv))
        {
            _chatView.AddMessage(m);
            RebindConversations();
        }
        PersistChat(conv);
    }

    /// <summary>
    /// 这一轮被长度上限截断时贴在气泡下面的那句话。
    ///
    /// 必须把「去哪儿改」写出来：上限是本程序自己发出去的参数，用户看到的现象只是
    /// 「回复说到一半就没了」，不给这句话就只能靠猜。而推理模型这一条尤其要讲清楚 ——
    /// 思考的 token 和正文**共用**这一个上限，把上限全用在思考上时正文干脆是空的，
    /// 看上去完全像是「模型坏了」，其实是设置小了。
    /// </summary>
    private static string TruncationNote(LlmConfig cfg, LlmStreamResult res, ChatMessage msg)
    {
        // 设了「不限」时请求里根本没有 max_tokens，还收到 length 就是服务商 / 模型自己的上限，
        // 「调大设置」这句话对它不成立。
        if (cfg.MaxTokens <= 0)
            return "回复被截断：本次请求没有设长度上限，是服务商或模型自身的上限到了。";
        string s = $"回复被长度上限截断：单次最多 {cfg.MaxTokens} tokens，已经用完。";
        if (res.ReasoningTokens > 0)
            s += $"其中思考占了 {res.ReasoningTokens} tokens —— 推理模型的思考也算在这个上限里。";
        else if (msg.Reasoning.Length > 0)
            s += "推理模型的思考也算在这个上限里。";
        return s + "调大「设置 → 对话设置 → 最大回复长度」再试。";
    }

    /// <summary>用户在输入框的「暂停」上点了：掐掉网络读取，已经收到的部分留在气泡里。</summary>
    private void StopReply()
    {
        if (_stream == null) return;
        CancelStream();
        FlashStatus("已暂停本次回复");
    }

    /// <summary>取消当前这一轮（不负责收尾：收尾在 <see cref="StartReply"/> 的 finally 里做）。</summary>
    private void CancelStream()
    {
        var st = _stream;
        if (st == null) return;
        try { st.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已经收过尾了 */ }
    }

    private void EnsureActive()
    {
        if (_active == null) NewConversation();
    }

    // ---------------- 拖放 / 粘贴 进输入框 ----------------

    private void Main_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data!.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
    }

    private void Main_DragDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop)) return;
        EnsureActive();
        // 只按「真收下了几个」报数：被拒的那几个 AddFile 已经各自报过理由了，
        // 这里再无条件说一句「已添加」，用户就只看得到那句成功（后说的覆盖先说的）。
        int ok = 0;
        foreach (string f in (string[])e.Data.GetData(DataFormats.FileDrop)!)
            if (_input.AddFile(f)) ok++;
        if (ok > 0) _chrome.SetStatus(ok == 1 ? "已添加到输入框" : $"已添加 {ok} 个文件到输入框");
    }

}
