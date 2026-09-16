using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class GroupCard : Panel, IThemed, IArranged
{
    private readonly List<SettingRow> _rows = new();
    public string Title { get; }
    public string Subtitle { get; }
    public int RowH { get; set; } = 52;

    private int RowTop => string.IsNullOrEmpty(Subtitle) ? 48 : 60;

    public GroupCard(string title, string subtitle = "")
    {
        Title = title;
        Subtitle = subtitle;
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Add(SettingRow row)
    {
        _rows.Add(row);
        Controls.Add(row);
    }

    /// <summary>移除一行（切换服务商时重建参数行用）。</summary>
    public void RemoveRow(SettingRow row)
    {
        if (!_rows.Remove(row)) return;
        Controls.Remove(row);
        row.Dispose();
    }

    public int MeasureHeight()
    {
        int n = _rows.Count(r => r.Shown);
        return RowTop + n * RowH + 12;
    }

    public void Arrange()
    {
        int y = RowTop;
        foreach (var r in _rows)
        {
            if (!r.Shown) continue;        // 隐藏的行不占位置（服务商切换时用）
            r.SetBounds(18, y, Math.Max(20, Width - 36), RowH - 2);
            y += RowH;
            if (r is IArranged a) a.Arrange();
        }
    }

    public void Restyle()
    {
        BackColor = SC.GroupBg;
        foreach (var r in _rows) if (r is IThemed t) t.Restyle();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 14, SC.GroupBg);
        RP.Stroke(g, rc, 14, SC.Mix(SC.GroupBg, Theme.Border, 1.0f));
        var tr = new Rectangle(20, string.IsNullOrEmpty(Subtitle) ? 15 : 13, Width - 40, 22);
        TextRenderer.DrawText(g, Title, SF.Get(11.5f, FontStyle.Bold), tr, SC.Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (!string.IsNullOrEmpty(Subtitle))
        {
            var sr = new Rectangle(20, 35, Width - 40, 20);
            TextRenderer.DrawText(g, Subtitle, SF.Get(9f), sr, SC.InkMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        base.OnPaint(e);
    }
}

/// <summary>设置行：左侧标题 + 说明，右侧控件右对齐并垂直居中。</summary>
