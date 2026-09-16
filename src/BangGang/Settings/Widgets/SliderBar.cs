using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class SliderBar : Control, IThemed
{
    private int _value = 100;
    private bool _drag, _hover;

    public int Min { get; set; } = 50;
    public int Max { get; set; } = 100;

    /// <summary>
    /// 取值步长（默认 1，也就是老行为）。滑块只有两百多像素宽却要覆盖 0–8192 时，
    /// 每像素合几十个单位，拖出来的数是 2917 这种谁也没打算设的值 ——
    /// 按步长吸附之后读数才是 2944 / 3000 这类能对得上的数。
    /// </summary>
    public int Step { get; set; } = 1;

    /// <summary>
    /// 右侧读数的写法。默认百分比（不透明度那一处就是它）；对话温度那种小数走
    /// <c>v =&gt; (v / 100.0).ToString("0.0")</c>。
    ///
    /// 是个委托而不是一个后缀字符串：温度的值域是 0–200 的整数、显示却要除以 100，
    /// 加后缀解决不了。
    /// </summary>
    public Func<int, string> Format { get; set; } = v => v + "%";

    public event Action? Changed;

    public int Value
    {
        get => _value;
        set
        {
            int v = Snap(Math.Clamp(value, Min, Max));
            if (v == _value) return;
            _value = v;
            Invalidate();
            Changed?.Invoke();
        }
    }

    /// <summary>把值吸附到 <see cref="Step"/> 的整数倍（以 <see cref="Min"/> 为起点）。</summary>
    private int Snap(int v)
    {
        if (Step <= 1) return v;
        return Math.Clamp(Min + (int)Math.Round((v - Min) / (double)Step) * Step, Min, Max);
    }

    public SliderBar(int width = 320)
    {
        Size = new Size(width, 34);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    private int TrackW => Math.Max(40, Width - 66);
    private Rectangle TrackRect() => new(0, Height / 2 - 3, TrackW, 6);
    private int KnobX() => TrackRect().X + (int)Math.Round((TrackW - 16) * (_value - Min) / (float)(Max - Min));

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _drag = true; SetFromX(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); base.OnMouseMove(e); }

    private void SetFromX(int x)
    {
        float k = (x - 8) / (float)Math.Max(1, TrackW - 16);
        Value = Min + (int)Math.Round(Math.Clamp(k, 0f, 1f) * (Max - Min));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var tr = TrackRect();
        RP.Fill(g, tr, 3, SC.Mix(SC.FieldBg, Theme.Border, 0.9f));
        int kx = KnobX();
        if (kx > tr.X) RP.Fill(g, new Rectangle(tr.X, tr.Y, kx - tr.X + 8, tr.Height), 3, Theme.Accent);

        var knob = new Rectangle(kx, Height / 2 - 8, 16, 16);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, knob);
        using (var pen = new Pen(Theme.Accent, _drag || _hover ? 2.6f : 2f)) g.DrawEllipse(pen, knob);

        var vr = new Rectangle(Width - 58, 0, 58, Height);
        TextRenderer.DrawText(g, Format(_value), SF.Get(10.5f, FontStyle.Bold), vr, SC.Ink,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        base.OnPaint(e);
    }
}

/// <summary>颜色样本块：显示色块 + 十六进制值，点击打开取色对话框。</summary>
