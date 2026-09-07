using System.Drawing.Drawing2D;

namespace BangGang;

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
            using var bg2 = new SolidBrush(Color.FromArgb(64, 226, 64, 60));
            g.FillEllipse(bg2, dr);
            Color red = sel ? Color.White : Color.FromArgb(224, 60, 54);
            float cx = dr.X + dr.Width / 2f;
            float cy = dr.Y + dr.Height / 2f;
            using var pen = new Pen(red, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            // 垃圾桶：盖子 + 提手 + 桶身
            g.DrawLine(pen, cx - 5.2f, cy - 4.2f, cx + 5.2f, cy - 4.2f);
            g.DrawLine(pen, cx - 1.7f, cy - 6.6f, cx - 1.7f, cy - 4.2f);
            g.DrawLine(pen, cx + 1.7f, cy - 6.6f, cx + 1.7f, cy - 4.2f);
            g.DrawLine(pen, cx - 4.4f, cy - 2.2f, cx - 4.4f, cy + 5.0f);
            g.DrawLine(pen, cx + 4.4f, cy - 2.2f, cx + 4.4f, cy + 5.0f);
            g.DrawLine(pen, cx - 6.0f, cy + 5.0f, cx + 6.0f, cy + 5.0f);
            using var stripe = new Pen(red, 1.3f);
            g.DrawLine(stripe, cx - 1.0f, cy - 1.6f, cx - 1.0f, cy + 3.8f);
            g.DrawLine(stripe, cx + 1.0f, cy - 1.6f, cx + 1.0f, cy + 3.8f);
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
