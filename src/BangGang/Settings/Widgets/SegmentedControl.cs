using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class SegmentedControl : Control, IThemed
{
    private const int PadX = 14;        // 每段文字左右留白
    private const int MinSegW = 48;
    private const int OuterPad = 3;

    private int _hover = -1;
    private int[] _widths = Array.Empty<int>();
    private float _indicatorX;
    private readonly System.Windows.Forms.Timer _anim;

    public string[] Items { get; }
    public event Action? Changed;

    public int SelectedIndex { get; private set; }

    public SegmentedControl(string[] items, int height = 34, int unusedWidth = 0)
    {
        Items = items;
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += (_, _) => StepAnimation();
        Measure();
        Size = new Size(TotalWidth(), height);
        _indicatorX = SegX(SelectedIndex);
    }

    /// <summary>按文字量算每一段的宽度（宽度跟着内容走）。</summary>
    private void Measure()
    {
        var widths = new int[Items.Length];
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        for (int i = 0; i < Items.Length; i++)
        {
            var sz = g.MeasureString(Items[i], SF.Get(10.5f));
            widths[i] = Math.Max(MinSegW, (int)Math.Ceiling(sz.Width) + PadX * 2);
        }
        _widths = widths;
    }

    private int TotalWidth()
    {
        int w = OuterPad * 2;
        foreach (var x in _widths) w += x;
        return w;
    }

    private int SegX(int idx)
    {
        int x = OuterPad;
        for (int i = 0; i < idx && i < _widths.Length; i++) x += _widths[i];
        return x;
    }

    public void Select(int idx, bool raise)
    {
        idx = Math.Clamp(idx, 0, Math.Max(0, Items.Length - 1));
        if (idx == SelectedIndex) return;
        SelectedIndex = idx;
        StartAnimation();
        if (raise) Changed?.Invoke();
    }

    /// <summary>选中背景平滑滑动到目标段。</summary>
    private void StartAnimation()
    {
        float target = SegX(SelectedIndex);
        if (Math.Abs(target - _indicatorX) < 0.5f) { _indicatorX = target; Invalidate(); return; }
        _anim.Start();
    }

    private void StepAnimation()
    {
        float target = SegX(SelectedIndex);
        float dx = target - _indicatorX;
        if (Math.Abs(dx) < 1f)
        {
            _indicatorX = target;
            _anim.Stop();
        }
        else
        {
            _indicatorX += dx * 0.45f;      // 简单缓动
        }
        Invalidate();
    }

    public void Restyle()
    {
        Measure();
        Width = TotalWidth();
        _indicatorX = SegX(SelectedIndex);
        BackColor = SC.GroupBg;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }

    private int IndexAt(int x)
    {
        int acc = OuterPad;
        for (int i = 0; i < _widths.Length; i++)
        {
            if (x >= acc && x < acc + _widths[i]) return i;
            acc += _widths[i];
        }
        return x < OuterPad ? 0 : Math.Max(0, _widths.Length - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = IndexAt(e.X);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int i = IndexAt(e.X);
            if (i >= 0) Select(i, true);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, SC.FieldBg);
        RP.Stroke(g, rc, 9, SC.FieldBorder);

        // 选中背景（平滑滑动）
        int sw = _widths.Length > 0 ? _widths[Math.Clamp(SelectedIndex, 0, _widths.Length - 1)] : Width;
        var ind = new Rectangle((int)Math.Round(_indicatorX), OuterPad, sw, Height - OuterPad * 2 - 1);
        RP.Fill(g, ind, 7, SC.CardBg);
        RP.Stroke(g, ind, 7, SC.Mix(SC.FieldBorder, Theme.Accent, 0.45f));

        int x = OuterPad;
        for (int i = 0; i < Items.Length; i++)
        {
            var cell = new Rectangle(x, OuterPad, _widths[i], Height - OuterPad * 2 - 1);
            if (i == _hover && i != SelectedIndex)
                RP.Fill(g, cell, 7, SC.Mix(SC.FieldBg, Theme.Accent, 0.07f));
            Color ink = i == SelectedIndex ? SC.Accent : SC.InkMuted;
            // 选中不改变字号与粗细
            TextRenderer.DrawText(g, Items[i], SF.Get(10.5f), cell, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += _widths[i];
        }
        base.OnPaint(e);
    }
}

/// <summary>
/// 预设主色选择器：一排颜色球 + 一块**平滑滑动**的选中背景。
/// 只有当前颜色正好等于某个预设时才点亮对应颜色；自定义颜色时选中背景隐藏。
/// </summary>
