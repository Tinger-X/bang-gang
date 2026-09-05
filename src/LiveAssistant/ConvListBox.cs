using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>会话列表（owner-draw）。点击选中；悬浮在右侧显示删除按钮。</summary>
internal sealed class ConvListBox : ListBox
{
    public List<Conversation> Source { get; private set; } = new();
    public event Action<Conversation>? ConversationActivated;
    public event Action<Conversation>? ConversationDeleted;

    private int _hover = -1;

    public ConvListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 58;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        BackColor = Theme.SideBg;
        Font = Theme.UI(12f);
        _ = SystemInformation.VirtualScreen;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    public void Rebind(List<Conversation> list, string? activeId)
    {
        Source = list;
        BeginUpdate();
        Items.Clear();
        foreach (var c in list) Items.Add(c);
        SelectedIndex = list.FindIndex(c => c.Id == activeId);
        EndUpdate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = Items[e.Index] as Conversation;
        if (c == null) return;

        var rc = e.Bounds;
        bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        bool hover = e.Index == _hover;

        Color bg = sel ? Theme.Accent : hover ? Blend(Theme.SideBg, Color.White, .6f) : Theme.SideBg;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, rc);
        // 选中左侧小竖条
        if (sel)
        {
            using var accent = new SolidBrush(Color.FromArgb(80, 120, 200));
            g.FillRectangle(accent, rc.X, rc.Y, 3, rc.Height);
        }

        float tx = rc.X + 16;
        using (var title = new SolidBrush(sel ? Color.White : Theme.TextMain))
        using (var sub = new SolidBrush(sel ? Color.FromArgb(220, 235, 250) : Theme.TextMuted))
        {
            string t = c.Title.Length > 0 ? c.Title : "新对话";
            g.DrawString(t, Theme.UI(12f, sel ? FontStyle.Bold : FontStyle.Regular), title, tx, rc.Y + 8);
            string meta = c.Messages.Count + " 条 · " + c.UpdatedAt.ToString("MM-dd HH:mm");
            g.DrawString(meta, Theme.UI(9f), sub, tx, rc.Y + 32);
        }

        if (hover)
        {
            var dr = DelRect(rc);
            using var bg2 = new SolidBrush(Color.FromArgb(200, 210, 215, 220));
            g.FillEllipse(bg2, dr);
            using var pen = new Pen(sel ? Color.White : Color.FromArgb(120, 130, 140), 1.6f);
            g.DrawLine(pen, dr.Left + 5, dr.Top + 5, dr.Right - 5, dr.Bottom - 5);
            g.DrawLine(pen, dr.Right - 5, dr.Top + 5, dr.Left + 5, dr.Bottom - 5);
        }
    }

    private static Rectangle DelRect(Rectangle rc) =>
        new(rc.Right - 30, rc.Y + (rc.Height - 20) / 2, 20, 20);

    private Rectangle ItemRect(int idx) => new(0, idx * ItemHeight, ClientSize.Width, ItemHeight);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = IndexFromPoint(e.Location);
        if (h != _hover)
        {
            int old = _hover;
            _hover = h;
            Invalidate(ItemRect(old));
            if (h >= 0) Invalidate(ItemRect(h));
        }
        Cursor = h >= 0 && DelRect(ItemRect(h)).Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        int old = _hover;
        _hover = -1;
        if (old >= 0) Invalidate(ItemRect(old));
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int idx = IndexFromPoint(e.Location);
        if (idx < 0 || idx >= Items.Count) return;
        if (DelRect(ItemRect(idx)).Contains(e.Location))
        {
            ConversationDeleted?.Invoke(Source[idx]);
            return;
        }
        SelectedIndex = idx;
        ConversationActivated?.Invoke(Source[idx]);
        base.OnMouseClick(e);
    }

    private static Color Blend(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R * k + b.R * (1 - k)), (int)(a.G * k + b.G * (1 - k)), (int)(a.B * k + b.B * (1 - k)));
}
