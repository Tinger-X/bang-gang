
namespace BangGang;

internal sealed class StackPanel : Panel, IThemed, IArranged
{
    public int Gap { get; set; } = 16;

    public StackPanel()
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>所有子项高度 + 间距的总高度（供外层滚动容器使用）。</summary>
    public int ContentHeight
    {
        get
        {
            int y = 0;
            foreach (Control c in Controls) y += c.Height + Gap;
            return Math.Max(0, y > 0 ? y - Gap : 0);
        }
    }

    /// <summary>按当前宽度与内容高度重新排布（不依赖 WinForms 的自动布局触发）。</summary>
    public void ArrangeAndResize(int width)
    {
        Width = Math.Max(20, width);
        Height = ContentHeight;
        Arrange();
    }

    public void Arrange()
    {
        int y = 0;
        foreach (Control c in Controls)
        {
            c.SetBounds(0, y, Math.Max(20, Width), c.Height);
            y += c.Height + Gap;
            if (c is IArranged a) a.Arrange();
        }
    }

    public void Restyle()
    {
        BackColor = SC.CardBg;
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }
}

/// <summary>分组卡片：圆角浅底 + 标题 + 可选副标题 + 若干行设置项。</summary>
