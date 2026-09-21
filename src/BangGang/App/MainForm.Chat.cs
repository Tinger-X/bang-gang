using System.Drawing.Drawing2D;
using System.Text;

namespace BangGang;

partial class MainForm
{
    // ---------------- 会话管理 ----------------

    private void NewConversation()
    {
        // 新对话从空白开始：上一条对话里还没发出去的那半句**留在上一条上**，不跟过来
        // （切会话时存 / 装，见 ActivateConversation）。用户回头再点开上一条，那句还在。
        //
        // 这里是**唯一**的建会话入口 —— 侧栏的「+」、欢迎页的按钮与推荐问题，以及截图 /
        // 拖放 / 录音那几条 EnsureActive 都经过它，所以那一段不用在每条路上各写一遍。
        var c = new Conversation();
        _conversations.Add(c);
        ActivateConversation(c);
    }

    private void ActivateConversation(Conversation c)
    {
        // 草稿跟着对话走：先把现在这条的留下，再把要开的那条的装上。
        //
        // 这里是**离开一条对话的唯一出口**，所以「留住草稿」只需要写在这一处 —— 另一个出口是
        // 把这条删掉（DeleteConversation），那种情况下草稿本来就跟它一起没了，不用留。
        //
        // 点的是当前这条就整段跳过：存一遍再装回来会重设 _box.Text，光标于是跳回开头
        // （见 InputPanel.Apply 里钉光标那一段）—— 用户在列表里点一下自己正开着的那条，
        // 打了一半的位置就没了，而这中间屏幕上什么都没变。
        if (_active != c)
        {
            if (_active != null) _input.SaveDraftTo(_active);
            _input.LoadDraftFrom(c);
            RebaseDictationDraft(_input.Text);   // 录着音时输入框会被下一个中间结果整框重写，见那里
        }
        // 上一条的标题改到一半就切走了 —— 那一笔**不提交**（用户没按回车），
        // 输入条收起来，标题照旧。放在 _active 改之前：CancelRename 要按着旧的那条收尾。
        CancelRename();
        _active = c;
        ShowConvTitle(c);
        _chatView.Load(c);
        _chatUI.Visible = true;
        _welcome.Visible = false;
        RebindConversations();
        _input.FocusInput();
        RememberActive();
    }

    private void DeleteConversation(Conversation c)
    {
        CancelRename();              // 标题改到一半的输入条跟着这条一起收掉
        _conversations.Remove(c);
        ChatStore.Delete(c.Id);          // 会话文件跟着删；它的附件不删，见 ChatStore.ImageDir
        if (_active == c)
        {
            // 这条对话连同它的草稿一起没了。**不留存**：SaveDraftTo 是给「切走、回头还要回来」
            // 用的，这里没有回头路。
            //
            // 而这一清**必须在这里**，不能只靠 ActivateConversation：删完停在欢迎页，用户的下一个
            // 动作也可以是点开列表里**另一条**对话，那时装的是那一条自己的草稿，与这条无关 ——
            // 但输入框里若还留着这条的字，看见的就是「删掉那条的字出现在另一条对话里」。
            _active = null;
            _input.LoadDraftFrom(null);
            _chatUI.Visible = false;
            _welcome.Visible = true;
            _chatView.Load(null!);
            EnsureSidebarOpen();     // 顶栏（连同收起按钮）没了，侧栏就得自己回来
        }
        RebindConversations();
        RememberActive();
    }

    // ---------------- 对话标题 ----------------

    /// <summary>正在就地改标题的那条会话（null = 没在改）。输入条的可见性与它是同一件事。</summary>
    private Conversation? _renaming;

    /// <summary>
    /// 把一条会话的标题打到顶栏上。空标题显示成「新对话」—— 那是 <see cref="Conversation"/>
    /// 的默认值，只有手改过 chats\*.json 的文件才可能是空的。
    ///
    /// 长度不在这里管：标题条那个 Label 带 <c>AutoEllipsis</c>，超长由它收尾
    /// （中间那一段的宽度由 <c>ConvTitlePadX</c> 让开两侧按钮，见 ApplyLayout）。
    /// </summary>
    private void ShowConvTitle(Conversation? c)
    {
        _convTitle.Text = string.IsNullOrWhiteSpace(c?.Title) ? "新对话" : c!.Title;
    }

    /// <summary>
    /// 点标题条右端那枚铅笔：标题那一行就地变成输入框。
    ///
    /// 编辑期间标题条上的那行字**清空**：输入条只有 420 宽，一条长标题会从它两侧伸出来，
    /// 看着像两个标题叠在一起。清掉之后，屏幕上就只剩正在编辑的这一行字。
    /// </summary>
    private void BeginRenameTitle()
    {
        if (_active == null) return;
        _renaming = _active;
        _convTitle.Text = "";
        _titleEdit.BeginEdit(_renaming.Title);
    }

    /// <summary>
    /// 改完了：把用户敲的那串字收成标题。
    ///
    /// <b>空串不算一次修改</b> —— 把框里清光再回车，读作「算了，不改了」。
    /// 反过来的话，用户清空输入框这个动作会把一条好好的标题抹成「新对话」，
    /// 而那是不可逆的（模型不会再为这条会话起第二次名）。
    /// </summary>
    private void CommitRename(string text)
    {
        var c = _renaming;
        _renaming = null;
        _titleEdit.Close();

        string t = Conversation.TidyTitle(text);
        if (c != null && t.Length > 0)
        {
            c.Title = t;
            // 用户起的名是定稿：模型和「首句截断」都不许再覆盖它（见 Conversation.TitleLocked）。
            c.TitleLocked = true;
            RebindConversations();
            PersistChat(c);
        }
        if (ReferenceEquals(_active, c)) ShowConvTitle(c);
    }

    /// <summary>不改了（Esc、或者改到一半切走了）：标题保持原样。</summary>
    private void CancelRename()
    {
        var c = _renaming;
        if (c == null) return;
        _renaming = null;
        _titleEdit.Close();
        if (ReferenceEquals(_active, c)) ShowConvTitle(c);
    }

    /// <summary>
    /// 把一条会话写进它自己的文件。调用点是「内容定稿」的几处，见 <see cref="ChatStore.Save"/>。
    ///
    /// <b>已经被删掉的会话一律不写。</b>这几处都在一轮回复的收尾里，而那一轮可能跑了好几秒
    /// （起名一两秒、回复更久），中途用户完全来得及把这条会话删掉 —— 删的时候文件已经跟着
    /// 删了，收尾这一笔再写一次，下一次启动它就会从「已删除」里回来。判据不用另存一个标记：
    /// 会话列表就是这个事实的账本。
    /// </summary>
    private void PersistChat(Conversation c)
    {
        if (!_conversations.Contains(c)) return;
        ChatStore.Save(c);
    }

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
        // 首条消息的标题由模型来起（见 StartReply 里那一趟），这里先不动它 ——
        // 模型没回上名字时，那一趟自己会退回到「首句截断」。其余每一轮照旧按首句算一次，
        // 那只是让 UpdatedAt 跟着刷新（标题这时已经定稿，RefreshTitle 不会再改它）。
        if (!WantsGeneratedTitle(_active))
        {
            _active.RefreshTitle();
            RebindConversations();
            ShowConvTitle(_active);
        }
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
        try
        {
            // 首条消息：先让模型给这次对话起个名字，再开始真正的对话。
            //
            // 起名这一步**不进界面**：上面那个空气泡照转它那三个点，落在用户眼里就是
            // 「模型在回话」—— 这正是「确认标题的过程不用显示」那条要求的落点，
            // 所以这里既不加第二个气泡，也不为它单开一条状态提示。
            if (WantsGeneratedTitle(conv)) await NameConversation(conv, cfg, st);

            // 用户在这中间按了暂停（名字还没回来）：这一轮到此为止。
            // 名字本身不受影响 —— NameConversation 会在取消时退回「首句截断」，见那里。
            if (!st.Cts.IsCancellationRequested)
            {
                FlashStatus("正在等待模型回复…");
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

    // ---------------- 让模型给会话起名 ----------------

    /// <summary>
    /// 「起名」用的系统提示词。三条要求都是照着模型实际会犯的毛病写的：
    /// 不说「只输出标题」，它会先来一句「好的，我来帮你总结一下」；不说字数，
    /// 它会写成一整句摘要；不说「用消息本身的语言」，它会把中文消息总结成英文标题。
    /// </summary>
    private const string TitleSystemPrompt =
        "你是一个会话标题生成器。读用户的第一条消息，为这次对话起一个标题。\n" +
        "要求：\n" +
        "1. 用这条消息本身所用的语言，不要翻译；\n" +
        "2. 不超过 16 个字（英文不超过 6 个单词）；\n" +
        "3. 只输出标题本身：不要引号、不要句号、不要「标题：」之类的前缀，不要任何解释。";

    /// <summary>
    /// 这一轮要不要请模型起名：会话里的**第一条**用户消息，且标题还没有定稿。
    ///
    /// 两个条件缺一不可。后一个是为了让用户先手改过名的会话不被模型覆盖
    /// （<see cref="Conversation.TitleLocked"/>）；前一个则保证一条会话只起一次名 ——
    /// 后面每一轮都去总结一次，标题会随着对话越聊越飘。
    /// </summary>
    private static bool WantsGeneratedTitle(Conversation c) =>
        !c.TitleLocked && c.Messages.Count(m => m.Role == "user") == 1;

    /// <summary>
    /// 请模型按首条用户消息总结一个标题，写进标题栏与会话列表。
    ///
    /// 拿不到标题（网络错 / 模型回空 / 用户中途按了暂停）一律退回
    /// <see cref="Conversation.RefreshTitle"/> 那套「首句截断」—— 差别只是标题像不像人写的，
    /// 而不是一条没名字的会话。
    ///
    /// 标题在这一刻就落盘，不等整轮回复跑完：它就是这一步的产物，而回复还要跑好几秒，
    /// 期间被杀掉的话，用户下次打开看见的会是一条名字对不上的会话。
    /// </summary>
    private async Task NameConversation(Conversation conv, LlmConfig cfg, StreamState st)
    {
        string? title = null;
        try
        {
            title = await AskTitle(conv, cfg, st.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户按了暂停。名字还是得有，走下面的兜底 —— 这里什么都不做。
        }

        if (title != null)
        {
            conv.Title = title;
            // 模型起的名也是定稿，理由同用户手改（否则下一轮 RefreshTitle 会用首句盖掉它）。
            conv.TitleLocked = true;
            Trace.Log("title: model named it '" + title + "'");
        }
        else
        {
            conv.RefreshTitle();
            Trace.Log("title: fallback to the first message");
        }

        if (ReferenceEquals(_active, conv)) ShowConvTitle(conv);
        RebindConversations();
        PersistChat(conv);
    }

    /// <summary>
    /// 一次起名请求。走的是和对话**完全相同**的客户端与接入配置，只有三处不同：
    /// 系统提示词换成起名的那条、不带「强化信息」、关掉图片（标题用不着把截图发出去，
    /// 附图仍会以「[图片] 名字」的形式留在消息里）。
    ///
    /// 拿不到内容返回 null；取消原样抛出去，由调用方分辨「暂停」和「出错」。
    /// </summary>
    private static async Task<string?> AskTitle(Conversation conv, LlmConfig chat, CancellationToken ct)
    {
        var first = conv.Messages.FirstOrDefault(m => m.Role == "user");
        if (first == null) return null;

        var sb = new StringBuilder();
        try
        {
            await LlmClient.StreamAsync(TitleConfig(chat), new[] { first },
                                        s => sb.Append(s), _ => { }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.Log("title request failed: " + ex.Message);
            return null;
        }

        string t = Conversation.TidyTitle(CleanModelTitle(sb.ToString()));
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// 起名请求的配置。
    ///
    /// <c>Temperature</c> 压到 0.3：标题要的是稳，不是花样 —— 用户设的对话温度（可能到 1.5）
    /// 是给正文的，套在标题上会得到一堆每次都不同的怪名字。
    ///
    /// <c>MaxTokens</c> 给到 1024 而不是几十：推理模型的**思考也算在这个上限里**，
    /// 卡到 128 的话思考还没写完预算就没了，正文一个字都吐不出来（同
    /// <see cref="TruncationNote"/> 那条坑）。上限宽一点没有代价 —— 提示词已经把它压在
    /// 十几个字上，模型自己就会停。
    /// </summary>
    private static LlmConfig TitleConfig(LlmConfig chat) => new()
    {
        Url = chat.Url,
        ApiKey = chat.ApiKey,
        Model = chat.Model,
        Vision = false,
        Temperature = 0.3,
        MaxTokens = 1024,
        SystemPrompt = TitleSystemPrompt,
        Reinforce = "",       // 「强化信息」是给对话那一边的，别混进起名请求
    };

    /// <summary>
    /// 把模型回的那串字收拾成一条标题。
    ///
    /// 提示词里已经写了「只输出标题」，但模型时常还是会加个「标题：」前缀、给标题套上引号、
    /// 或者先客气一句再换行写标题 —— 都得在这里剥掉，否则标题栏上挂着的就是
    /// 「标题：“东京三日游”」。剥离是**宽容**的：多剥掉一层引号不痛不痒，漏一层则一眼可见。
    /// </summary>
    private static string CleanModelTitle(string raw)
    {
        string s = (raw ?? "").Trim();
        // 只认第一行：多出来的那几行多半是解释。
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) s = s[..nl];
        s = s.Trim();

        foreach (string p in new[] { "标题", "题目", "title" })
        {
            if (!s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            s = s[p.Length..].TrimStart(':', '：', ' ', '\t');
            break;
        }

        // 剥壳要**来回剥到不动为止**，一遍不够：`标题：“东京三日游”。` 里那个收尾的
        // 句号把右引号顶到了倒数第二格，一遍 `Trim(引号)` 时它已经不在末尾、剥不掉，
        // 于是标题栏上挂出来的就是 `东京三日游”` —— 探针第一次跑就是这个结果。
        // 顺序也重要：先剥句读再剥引号，`“x”。` 才收得干净。三轮足够（模型不会套四层壳）。
        for (int i = 0; i < 3; i++)
        {
            string before = s;
            s = s.Trim(TitleWrap).TrimEnd(TitleTail).TrimEnd(TitleWrap);
            if (s == before) break;
        }
        return s;
    }

    /// <summary>
    /// 标题两端可能被套上的壳：引号、书名号、括号、空白。
    /// 写成字段而不是就地一个字面量数组：它每轮起名都要过一遍，而数组字面量每次都新建一个。
    /// </summary>
    private static readonly char[] TitleWrap =
        { '"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', '《', '》', '（', '）', '(', ')', ' ', '\t' };

    /// <summary>收尾的句读。模型很爱给标题补一个句号，而标题栏上那个句号只是噪声。</summary>
    private static readonly char[] TitleTail =
        { '.', '。', '!', '！', '?', '？', ',', '，', ';', '；', ':', '：', '~', '～', '…' };

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
        return s + "调大「设置 → 对话参数 → 最大回复长度」再试。";
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
