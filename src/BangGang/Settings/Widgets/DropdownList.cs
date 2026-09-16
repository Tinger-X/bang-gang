using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class DropdownList : Control, IThemed
{
    private const int RowH = 30;
    private const int Pad = 6;          // 四周留白：仅够画一圈很淡的投影
    /// <summary>投影留白（供下拉框对齐列表本体用）。</summary>
    public const int ProjectionPad = Pad;
    private int _hover = -1;
    private readonly int _selected;
    private int _scroll;                // 列表比可视区高时的滚动偏移（像素）

    public string[] Items { get; }
    public event Action<int>? ItemChosen;
    public event Action? Closed;

    /// <summary>请求收起（供点击过滤器调用）。</summary>
    public void RequestClose() => Closed?.Invoke();

    public DropdownList(string[] items, int selected, int width)
    {
        Items = items;
        _selected = selected;
        Size = new Size(width + Pad * 2, items.Length * RowH + 10 + Pad * 2);
        BackColor = SC.CardBg;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    /// <summary>列表本体区域（去掉投影留白）。</summary>
    public Rectangle BodyRect() =>
        new(Pad, Pad, Math.Max(10, Width - Pad * 2), Math.Max(10, Height - Pad * 2));

    /// <summary>按可用高度裁剪列表高度（不够高时内部滚动）。可反复调用：每次按内容重新计算，不会越缩越小。</summary>
    public void FitHeight(int available)
    {
        int wanted = Items.Length * RowH + 10;                                  // 内容高度
        int body = Math.Min(wanted, Math.Max(RowH + 10, available));            // 至少留一行可滚动
        Size = new Size(Width, body + Pad * 2);
    }

    private int ContentHeight => Items.Length * RowH + 10;
    private int MaxScroll => Math.Max(0, ContentHeight - BodyRect().Height);

    public void Restyle() { BackColor = SC.CardBg; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (MaxScroll > 0)
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 120 * RowH, 0, MaxScroll);
            Invalidate();
        }
        Trace.Log($"list wheel delta={e.Delta} scroll={_scroll} maxScroll={MaxScroll}");
        base.OnMouseWheel(e);
    }

    private int ItemAt(Point p)
    {
        int y = p.Y - Pad - 5 + _scroll;
        if (y < 0) return -1;
        int i = y / RowH;
        return i >= 0 && i < Items.Length ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = ItemAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (_hover != -1) { _hover = -1; Invalidate(); } base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = ItemAt(e.Location);
        Trace.Log($"list mousedown at {e.Location.X},{e.Location.Y} scroll={_scroll} -> item {i}");
        if (e.Button == MouseButtons.Left && i >= 0) ItemChosen?.Invoke(i);
        else Closed?.Invoke();       // 点列表以外的任何位置都收起
        base.OnMouseDown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Closed?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>失焦即收起（点其它地方时行为自然）。</summary>
    protected override void OnLeave(EventArgs e) { Closed?.Invoke(); base.OnLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = BodyRect();

        // 很淡的一圈投影：只用来把菜单和背景轻轻分开，不做厚重阴影
        for (int i = Pad; i >= 1; i--)
        {
            using var sp = RP.Path(Rectangle.Inflate(body, i, i), RowRadius + i);
            using var sb = new SolidBrush(Color.FromArgb(3, 12, 16, 28));
            g.FillPath(sb, sp);
        }

        RP.Fill(g, body, RowRadius, SC.CardBg);
        var oldClip = g.Clip;
        g.SetClip(body);
        for (int i = 0; i < Items.Length; i++)
        {
            var row = new Rectangle(body.X + 5, body.Y + 5 + i * RowH - _scroll, body.Width - 10, RowH);
            if (i == _selected) RP.Fill(g, row, 7, SC.Mix(SC.CardBg, SC.Accent, 0.10f));
            else if (i == _hover) RP.Fill(g, row, 7, SC.AccentSoft);
            var ink = i == _selected ? SC.Accent : SC.Ink;
            // 选中只改颜色，不改字号/粗细
            TextRenderer.DrawText(g, Items[i], SF.Get(10.5f),
                new Rectangle(row.X + 10, row.Y, row.Width - 34, row.Height), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (i == _selected)
                Gfx.DrawGlyph(g, Glyph.Check, new RectangleF(row.Right - 22, row.Y + 8, 13, 13), SC.Accent, 1.6f);
        }
        g.Clip = oldClip;
        RP.Stroke(g, body, RowRadius, SC.FieldBorder);
        base.OnPaint(e);
    }

    private const int RowRadius = 10;
}
