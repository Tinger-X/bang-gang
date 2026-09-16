namespace BangGang;

/// <summary>聊天消息区：垂直排布气泡，支持滚动。</summary>
internal sealed class ChatView : Panel, IThemed
{
    private readonly List<MessageBubble> _rows = new();
    public Conversation? Conv { get; private set; }

    /// <summary>点开了某条消息里的图片（主窗口据此弹出放大浮层）。</summary>
    public event Action<Attachment>? ImagePressed;

    public ChatView()
    {
        BackColor = Theme.ChatBg;
        AutoScroll = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle()
    {
        BackColor = Theme.ChatBg;
        Invalidate();
    }

    public void Load(Conversation c)
    {
        Conv = c;
        SuspendLayout();
        foreach (var b in _rows) { b.Dispose(); }
        _rows.Clear();
        Controls.Clear();
        if (c != null)
        {
            foreach (var m in c.Messages)
            {
                var b = new MessageBubble(m, m.Role == "user");
                b.ImagePressed += a => ImagePressed?.Invoke(a);
                _rows.Add(b);
                Controls.Add(b);
            }
        }
        ResumeLayout();
        LayoutRows();
        ScrollBottom();
    }

    public void Relayout() => LayoutRows();

    private void LayoutRows()
    {
        if (_rows.Count == 0) { AutoScrollMinSize = Size.Empty; return; }
        int margin = 26;
        int clientW = Math.Max(100, ClientSize.Width);

        // 已经滚下去多少（是负数）。WinForms 的 AutoScroll 是**物理**搬子控件的：子控件
        // 的 Location 读回来就是它此刻在屏幕上的位置，滚动多少它就被挪走多少，而滚过的
        // 距离又同时记在面板自己的 display rect 里（AutoScrollPosition 就是它的负数）。
        // 所以这里重排必须把当前偏移减掉 —— 直接写逻辑坐标等于把画面拽回没滚过的样子，
        // 而面板还以为自己滚着；等 ScrollBottom 再设一次同样的值是**空操作**（display rect
        // 没变就不会去搬子控件），画面于是永远停在顶部、底下那一截怎么都看不见。
        // 顺序上也不吃亏：先按旧偏移摆好，再改 AutoScrollMinSize / AutoScrollPosition，
        // 之后框架自己把子控件搬过去，落点仍然是「逻辑坐标 − 新偏移」。
        int dy = AutoScrollPosition.Y;

        int y = margin;
        foreach (var b in _rows)
        {
            int left = b.IsUser ? clientW - b.Width - margin - 6 : margin;
            b.Location = new Point(Math.Max(margin, left), y + dy);
            y += b.Height + 14;
        }
        AutoScrollMinSize = new Size(Math.Max(10, clientW - margin), y + margin);
    }

    /// <summary>
    /// 滚到底。
    ///
    /// 无条件设一次，而不是「只在内容超出时才设」：内容变短之后（换了会话、窗口拉高）
    /// 偏移量会停在比最大值还大的地方，而**把 AutoScrollPosition 设成它当前已经是的那个值
    /// 是空操作**，修不回来 —— 画面就会空着下半截、上面的行还被顶掉。设成 0 才收得回来。
    /// </summary>
    public void ScrollBottom()
    {
        int max = AutoScrollMinSize.Height - ClientSize.Height;
        AutoScrollPosition = new Point(0, Math.Max(0, max));
    }

    public void AddMessage(ChatMessage m)
    {
        var b = new MessageBubble(m, m.Role == "user");
        b.ImagePressed += a => ImagePressed?.Invoke(a);
        _rows.Add(b);
        Controls.Add(b);
        LayoutRows();
        ScrollBottom();
        Invalidate();
    }

    /// <summary>
    /// 取某条消息现在**活着**的那个气泡（不在本视图里则返回 null）。
    ///
    /// 流式回复要按消息对象来找气泡、而不是把气泡的引用攥在手里：用户切走再切回来时
    /// <see cref="Load"/> 会把所有气泡销毁重建，攥着的那一个已经是死控件了 ——
    /// 往它身上写东西要么抛异常，要么静默什么都不显示（回复「不动」了，但历史里其实有）。
    /// </summary>
    public MessageBubble? BubbleFor(ChatMessage m)
    {
        foreach (var b in _rows)
            if (ReferenceEquals(b.Msg, m) && !b.IsDisposed) return b;
        return null;
    }

    /// <summary>气泡长高了：重排一次，并且只在用户本来就贴着底部时才跟着滚下去。</summary>
    public void NotifyRowGrew()
    {
        bool pinned = IsAtBottom();
        LayoutRows();
        if (pinned) ScrollBottom();
    }

    /// <summary>
    /// 当前是不是贴着底部。流式回复每几十毫秒就长一行，如果每次无脑 <see cref="ScrollBottom"/>，
    /// 用户往上翻去看前面的内容时会被一直拽回底部，根本读不了。
    /// </summary>
    public bool IsAtBottom()
    {
        int max = AutoScrollMinSize.Height - ClientSize.Height;
        if (max <= 0) return true;
        return -AutoScrollPosition.Y >= max - 32;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Conv != null && _rows.Count == 0)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            using var f = Theme.UI(12f);
            string s = "开始对话吧 —— 在下方输入文字，或拖入 / 粘贴文件与图片";
            var sz = g.MeasureString(s, f);
            using var b = new SolidBrush(Theme.TextMuted);
            g.DrawString(s, f, b, (r.Width - sz.Width) / 2, (r.Height - sz.Height) / 2);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_rows.Count > 0) LayoutRows();
        else Invalidate();
    }
}
