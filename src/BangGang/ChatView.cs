namespace BangGang;

/// <summary>聊天消息区：垂直排布气泡，支持滚动。</summary>
internal sealed class ChatView : Panel, IThemed
{
    private readonly List<MessageBubble> _rows = new();
    public Conversation? Conv { get; private set; }

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
        int y = margin;
        foreach (var b in _rows)
        {
            int left = b.IsUser ? clientW - b.Width - margin - 6 : margin;
            b.Location = new Point(Math.Max(margin, left), y);
            y += b.Height + 14;
        }
        AutoScrollMinSize = new Size(Math.Max(10, clientW - margin), y + margin);
    }

    public void ScrollBottom()
    {
        if (AutoScrollMinSize.Height > ClientSize.Height)
        {
            AutoScrollPosition = new Point(0, AutoScrollMinSize.Height - ClientSize.Height);
        }
    }

    public void AddMessage(ChatMessage m)
    {
        var b = new MessageBubble(m, m.Role == "user");
        _rows.Add(b);
        Controls.Add(b);
        LayoutRows();
        ScrollBottom();
        Invalidate();
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
