using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class ColorDotPicker : Control, IThemed
{
    private const int DotSize = 22;
    private const int Pitch = 30;

    private readonly int[] _centerX;
    private float _hlX;
    private bool _hlVisible;
    private readonly System.Windows.Forms.Timer _anim;

    public Color[] Presets { get; }
    public int SelectedIndex { get; private set; } = -1;
    public Color Value { get; private set; }
    public event Action? Changed;

    public ColorDotPicker(Color[] presets)
    {
        Presets = presets;
        _centerX = new int[presets.Length];
        for (int i = 0; i < presets.Length; i++) _centerX[i] = Pitch / 2 + i * Pitch;
        Size = new Size(presets.Length * Pitch + 2, 32);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += (_, _) => StepAnimation();
        Value = presets.Length > 0 ? presets[0] : Color.White;
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>设置当前颜色：与某个预设相同则选中它并滑动过去，否则隐藏选中背景。</summary>
    public void SetValue(Color c, bool raise)
    {
        Value = c;
        int idx = Array.FindIndex(Presets, p => p.ToArgb() == c.ToArgb());
        if (idx == SelectedIndex && (idx >= 0) == _hlVisible)
        {
            Invalidate();
            if (raise) Changed?.Invoke();
            return;
        }
        SelectedIndex = idx;
        if (idx >= 0) StartSlideTo(idx);
        else _hlVisible = false;
        if (raise) Changed?.Invoke();
        Invalidate();
    }

    private void StartSlideTo(int idx)
    {
        float target = _centerX[idx];
        if (!_hlVisible) { _hlVisible = true; _hlX = target; Invalidate(); return; }
        if (Math.Abs(target - _hlX) < 0.5f) { _hlX = target; Invalidate(); return; }
        _anim.Start();
    }

    private void StepAnimation()
    {
        float target = _centerX[Math.Clamp(SelectedIndex, 0, _centerX.Length - 1)];
        float dx = target - _hlX;
        if (Math.Abs(dx) < 1f) { _hlX = target; _anim.Stop(); }
        else _hlX += dx * 0.4f;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int idx = e.X / Pitch;
            if (idx >= 0 && idx < Presets.Length)
            {
                if (idx == SelectedIndex) return;
                SelectedIndex = idx;
                Value = Presets[idx];
                StartSlideTo(idx);
                Invalidate();
                Changed?.Invoke();
            }
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 选中背景（平滑滑动到目标颜色球，描边用该颜色本身，未保存时也能看出选中的是哪一个）
        if (_hlVisible && SelectedIndex >= 0)
        {
            var hl = new Rectangle((int)Math.Round(_hlX - 15), 1, 30, 30);
            RP.Fill(g, hl, 15, SC.CardBg);
            RP.Stroke(g, hl, 15, Presets[Math.Clamp(SelectedIndex, 0, Presets.Length - 1)], 1.6f);
        }

        for (int i = 0; i < Presets.Length; i++)
        {
            var dot = new Rectangle(_centerX[i] - DotSize / 2, 16 - DotSize / 2, DotSize, DotSize);
            using (var b = new SolidBrush(Presets[i])) g.FillEllipse(b, dot);
            using var pen = new Pen(SC.Mix(SC.GroupBg, SC.Ink, 0.18f), 1f);
            g.DrawEllipse(pen, dot);
        }
        base.OnPaint(e);
    }
}

/// <summary>圆角卡片容器（设置浮窗主体）：圆角用抗锯齿绘制，不做 Region 硬裁剪（避免锯齿）。</summary>
