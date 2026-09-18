using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class ScrollArea : Panel, IThemed
{
    private const int BarW = 6;        // 滑块宽度
    private const int BarGap = 5;      // 距右边缘
    private Control? _content;
    private int _offset;
    private bool _dragBar;
    private bool _hoverBar;

    public ScrollArea()
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetContent(Control content)
    {
        _content = content;
        if (!Controls.Contains(content)) Controls.Add(content);
        Apply();
    }

    /// <summary>内容高度变化 / 尺寸变化后重新计算滚动范围。</summary>
    public void Relayout(int width, int contentHeight)
    {
        if (_content == null) return;
        _content.SetBounds(0, -_offset, Math.Max(20, width), Math.Max(1, contentHeight));
        Apply();
    }

    private int MaxOffset => _content == null ? 0 : Math.Max(0, _content.Height - Height);
    private Rectangle BarRect()
    {
        if (_content == null || _content.Height <= Height) return Rectangle.Empty;
        int trackH = Height - 8;
        int h = Math.Max(32, (int)Math.Round(trackH * (Height / (double)_content.Height)));
        int y = 4 + (int)Math.Round((trackH - h) * (_offset / (double)Math.Max(1, MaxOffset)));
        return new Rectangle(Width - BarW - BarGap, y, BarW, h);
    }

    /// <summary>内容滚动后触发（下拉弹窗据此跟随移动）。</summary>
    public event Action? Scrolled;

    private void Apply()
    {
        if (_content == null) return;
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        _content.Top = -_offset;
        Invalidate();
        Scrolled?.Invoke();
    }

    public void ScrollBy(int dy)
    {
        _offset = Math.Clamp(_offset + dy, 0, MaxOffset);
        Apply();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(-e.Delta / 120 * 60);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var bar = BarRect();
        if (e.Button == MouseButtons.Left && !bar.IsEmpty && bar.Contains(e.Location))
        {
            _dragBar = true;
            // 滑块只有 6px 宽，不捕获的话光标稍微一偏 MouseMove 就不再送进来，
            // 拖动看起来就像「滚条死了」；松开时（OnMouseUp）归还。
            Capture = true;
        }
        else if (e.Button == MouseButtons.Left && bar.Width > 0)
        {
            // 点击滚动条轨道：翻页
            ScrollBy(e.Y < BarRect().Y ? -Height + 80 : Height - 80);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = !BarRect().IsEmpty && Rectangle.Inflate(BarRect(), 4, 4).Contains(e.Location);
        if (over != _hoverBar) { _hoverBar = over; Invalidate(); }
        if (_dragBar)
        {
            int trackH = Height - 8;
            int h = BarRect().Height;
            int y = Math.Clamp(e.Y - h / 2 - 4, 0, Math.Max(1, trackH - h));
            _offset = MaxOffset == 0 ? 0 : (int)Math.Round(y / (double)Math.Max(1, trackH - h) * MaxOffset);
            Apply();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragBar = false; Capture = false; base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { if (_hoverBar) { _hoverBar = false; Invalidate(); } base.OnMouseLeave(e); }

    /// <summary>鼠标滚轮交给本控件处理（内容控件不再单独滚动）。</summary>
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        var added = e.Control;
        if (added != null) added.MouseWheel += (_, ev) => OnMouseWheel(ev);
    }

    public void Restyle()
    {
        BackColor = SC.CardBg;
        Invalidate(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bar = BarRect();
        if (bar.IsEmpty) return;
        float k = _hoverBar || _dragBar ? 0.42f : 0.28f;
        RP.Fill(g, bar, BarW / 2, SC.Mix(SC.CardBg, SC.Ink, k));
        base.OnPaint(e);
    }
}

/// <summary>
/// 下拉选择框：左侧显示当前选项，右侧箭头；点开后在本浮窗内弹出选项列表（自绘，圆角 + 细阴影）。
/// </summary>
