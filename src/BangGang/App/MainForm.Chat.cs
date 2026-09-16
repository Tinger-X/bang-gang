using System.Drawing.Drawing2D;

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
    }

    private void DeleteConversation(Conversation c)
    {
        _conversations.Remove(c);
        if (_active == c)
        {
            _active = null;
            _chatUI.Visible = false;
            _welcome.Visible = true;
            _chatView.Load(null!);
            EnsureSidebarOpen();     // 顶栏（连同收起按钮）没了，侧栏就得自己回来
        }
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
        var m = _input.Flush();
        if (m == null) return;
        _active.Messages.Add(m);
        _chatView.AddMessage(m);
        _active.RefreshTitle();
        RebindConversations();
        _convTitle.Text = _active.Title;
        _chrome.SetStatus("已发送，等待模型回复…（尚未接入 LLM）");
        ScheduleDemoReply();
    }

    /// <summary>正在等回复的那个定时器。用户中途按「暂停」时要能掐掉它。</summary>
    private System.Windows.Forms.Timer? _replyTimer;

    private void ScheduleDemoReply()
    {
        StopReplyTimer();          // 上一轮还没回完就又发了一条：先把它掐掉，别让两条回复打架
        _input.Busy = true;        // 发送键变成「暂停模型回复」
        var timer = new System.Windows.Forms.Timer { Interval = 450 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (_replyTimer == timer) { _replyTimer = null; _input.Busy = false; }
            if (_active == null) return;
            var reply = new ChatMessage
            {
                Role = "assistant",
                Text = "（**对话模型尚未接入**，此处为占位回复。）\n\n"
                     + "配置好 LLM 后，这里将显示模型的 Markdown 回复。\n\n"
                     + "- 支持列表\n- 支持 `行内代码`\n\n"
                     + "```\n也支持代码块排版\n```"
            };
            _active.Messages.Add(reply);
            _chatView.AddMessage(reply);
            _active.RefreshTitle();
            RebindConversations();
        };
        _replyTimer = timer;
        timer.Start();
    }

    /// <summary>用户在输入框的「暂停」上点了：停掉这一轮，输入区回到可以接着发消息的状态。</summary>
    private void StopReply()
    {
        if (_replyTimer == null) return;
        StopReplyTimer();
        _input.Busy = false;
        _chrome.SetStatus("已暂停本次回复");
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void StopReplyTimer()
    {
        if (_replyTimer == null) return;
        _replyTimer.Stop();
        _replyTimer.Dispose();
        _replyTimer = null;
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
