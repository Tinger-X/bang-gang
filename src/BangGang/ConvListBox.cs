using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 会话列表：完全自绘（不依赖系统 ListBox，因此悬浮移动不会闪烁）。
/// 每条记录只有标题 + 右侧删除图标，记录之间留间隙，记录宽度为 95% 并左右居中；
/// 右侧是自己的细圆角滚动条（悬浮/拖动加深，滚轮滚动）。
/// </summary>
internal sealed class ConvListBox : Control, IThemed
{
    /// <summary>每条记录占的高度（只放一行标题，所以比原来矮很多）。</summary>
    private const int ItemH = 38;
    /// <summary>记录之间的间隙。</summary>
    private const int Gap = 6;
    /// <summary>记录宽度占控件宽度的比例（左右居中）。</summary>
    private const float WidthRatio = 0.95f;
    private const int Radius = 9;
    /// <summary>标题区域右侧给删除图标预留的宽度。</summary>
    private const int DelSlot = 32;
    private const int DelSize = 22;
    private const int BarW = 6;
    private const int BarGap = 3;

    public List<Conversation> Source { get; private set; } = new();
    public event Action<Conversation>? ConversationActivated;
    public event Action<Conversation>? ConversationDeleted;

    private int _hover = -1;
    private int _hoverDel = -1;
    private int _offset;
    private bool _dragBar;
    private bool _hoverBar;

    public ConvListBox()
    {
        BackColor = Theme.SideBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle()
    {
        BackColor = Theme.SideBg;
        Invalidate();
    }

    public void Rebind(List<Conversation> list, string? activeId)
    {
        Source = list;
        _selectedId = activeId;
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        Invalidate();
    }

    private string? _selectedId;

    private int SelectedIndex => Source.FindIndex(c => c.Id == _selectedId);

    private int ContentHeight => Source.Count == 0 ? 0 : Source.Count * (ItemH + Gap) - Gap;
    private int MaxOffset => Math.Max(0, ContentHeight - Height);

    private Rectangle ItemRect(int i)
    {
        int w = (int)Math.Round(Width * WidthRatio);
        int x = (Width - w) / 2;
        return new Rectangle(x, i * (ItemH + Gap) - _offset, Math.Max(40, w), ItemH);
    }

    private Rectangle DelRect(Rectangle item) =>
        new(item.Right - 6 - DelSize, item.Y + (item.Height - DelSize) / 2, DelSize, DelSize);

    private Rectangle BarRect()
    {
        if (ContentHeight <= Height || Height <= 0) return Rectangle.Empty;
        int trackH = Height - 8;
        int h = Math.Max(28, (int)Math.Round(trackH * (Height / (double)ContentHeight)));
        int y = 4 + (int)Math.Round((trackH - h) * (_offset / (double)Math.Max(1, MaxOffset)));
        return new Rectangle(Width - BarW - BarGap, y, BarW, h);
    }

    private int IndexAt(Point p)
    {
        if (p.Y < -_offset) return -1;
        int i = (p.Y + _offset) / (ItemH + Gap);
        if (i < 0 || i >= Source.Count) return -1;
        return ItemRect(i).Contains(p) ? i : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int sel = SelectedIndex;
        for (int i = 0; i < Source.Count; i++)
        {
            var rc = ItemRect(i);
            if (rc.Bottom < 0 || rc.Top > Height) continue;
            bool selected = i == sel;
            bool hover = i == _hover;

            Color bg = selected ? Theme.Accent
                     : hover ? Theme.Mix(Theme.SideBg, Theme.TextMain, 0.075f)
                     : Color.Empty;
            if (bg != Color.Empty) RP.Fill(g, rc, Radius, bg);

            // 标题：单行 + 省略号
            var titleRc = new Rectangle(rc.X + 12, rc.Y, Math.Max(20, rc.Width - 12 - DelSlot), rc.Height);
            string t = Source[i].Title.Length > 0 ? Source[i].Title : "新对话";
            TextRenderer.DrawText(g, t, SF.Get(11f), titleRc,
                selected ? Color.White : Theme.TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            DrawDelete(g, DelRect(rc), i, selected, hover);
        }

        // 细圆角滚动条
        var bar = BarRect();
        if (!bar.IsEmpty)
        {
            float k = _hoverBar || _dragBar ? 0.40f : 0.26f;
            RP.Fill(g, bar, BarW / 2, Theme.Mix(Theme.SideBg, Theme.TextMain, k));
        }
        base.OnPaint(e);
    }

    /// <summary>删除图标：圆形浅底 + 垃圾桶线条；鼠标压在图标上才变红。</summary>
    private void DrawDelete(Graphics g, Rectangle rc, int index, bool selected, bool rowHover)
    {
        bool hot = index == _hoverDel;
        if (hot)
        {
            using var hb = new SolidBrush(Theme.Mix(Theme.SideBg, Theme.Danger, selected ? 0.55f : 0.16f));
            g.FillEllipse(hb, rc);
        }
        Color ink = hot ? (selected ? Color.White : Theme.Danger)
                  : selected ? Theme.Mix(Theme.Accent, Color.White, 0.82f)
                  : rowHover ? Theme.Mix(Theme.SideBg, Theme.TextMain, 0.45f)
                  : Theme.Mix(Theme.SideBg, Theme.TextMain, 0.26f);
        Gfx.DrawTrash(g, rc, ink, 1.5f);
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // 只重画受影响的两条记录，配合双缓冲，悬浮移动不会闪烁
        int h = IndexAt(e.Location);
        int hd = h >= 0 && DelRect(ItemRect(h)).Contains(e.Location) ? h : -1;
        if (h != _hover || hd != _hoverDel)
        {
            int old = _hover;
            _hover = h; _hoverDel = hd;
            if (old >= 0) InvalidateItem(old);
            if (h >= 0) InvalidateItem(h);
        }
        var bar = BarRect();
        bool over = !bar.IsEmpty && Rectangle.Inflate(bar, 4, 4).Contains(e.Location);
        if (over != _hoverBar) { _hoverBar = over; Invalidate(BarArea()); }

        if (_dragBar && !bar.IsEmpty)
        {
            int trackH = Height - 8;
            int y = Math.Clamp(e.Y - bar.Height / 2 - 4, 0, Math.Max(1, trackH - bar.Height));
            _offset = MaxOffset == 0 ? 0 : (int)Math.Round(y / (double)Math.Max(1, trackH - bar.Height) * MaxOffset);
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    private Rectangle BarArea() => new(Math.Max(0, Width - BarW - BarGap - 4), 0, BarW + 8, Height);

    private void InvalidateItem(int index)
    {
        if (index < 0 || index >= Source.Count) return;
        var rc = ItemRect(index);
        rc.Inflate(2, 2);
        Invalidate(rc);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        int old = _hover;
        _hover = -1; _hoverDel = -1;
        if (old >= 0) InvalidateItem(old);
        if (_hoverBar) { _hoverBar = false; Invalidate(BarArea()); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var bar = BarRect();
        if (e.Button == MouseButtons.Left && !bar.IsEmpty && bar.Contains(e.Location)) _dragBar = true;
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragBar = false; base.OnMouseUp(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var bar = BarRect();
        if (!bar.IsEmpty && bar.Contains(e.Location)) return;
        int idx = IndexAt(e.Location);
        if (idx < 0) return;
        if (DelRect(ItemRect(idx)).Contains(e.Location))
        {
            ConversationDeleted?.Invoke(Source[idx]);
            return;
        }
        _selectedId = Source[idx].Id;
        Invalidate();
        ConversationActivated?.Invoke(Source[idx]);
        base.OnMouseClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(-e.Delta / 120 * 54);
        base.OnMouseWheel(e);
    }

    public void ScrollBy(int dy)
    {
        int before = _offset;
        _offset = Math.Clamp(_offset + dy, 0, MaxOffset);
        if (_offset != before) Invalidate();
    }
}
