using System.Drawing.Drawing2D;

namespace BangGang;

partial class MainForm
{
    // ---------------- 左侧栏收起 / 展开 ----------------

    private int _sideW = SideW;          // 当前动画宽度，0 = 完全收起
    private int _sideTarget = SideW;     // 动画目标

    /// <summary>
    /// 收起 / 展开左侧栏。宽度不是一下跳过去的：<see cref="_sideTimer"/> 每帧把
    /// <see cref="_sideW"/> 往目标推掉剩余距离的一部分，走一条缓出曲线。
    /// 图标在这一刻就翻转（而不是等动画结束），点下去马上有反馈。
    /// </summary>
    private void ToggleSidebar()
    {
        bool collapse = _sideTarget > 0;             // 当前是展开的 -> 这一次要收起
        _sideTarget = collapse ? 0 : SideW;
        _btnSideToggle.Icon = collapse ? IconButton.Kind.Expand : IconButton.Kind.Collapse;
        _btnSideToggle.Invalidate();
        _sideTimer.Start();                          // 重复点只是换目标，不会叠出第二个动画
    }

    private void SideTick()
    {
        int d = _sideTarget - _sideW;

        // 收尾：snap 到整数目标并停表。不能只判 d == 0 —— 指数逼近永远差一点点。
        if (Math.Abs(d) <= 2)
        {
            _sideW = _sideTarget;
            _sideTimer.Stop();
        }
        else
        {
            _sideW += (int)Math.Round(d * SideAnimEase);
        }

        ApplyLayout();
    }

    /// <summary>
    /// 确保侧栏是展开的。<see cref="ToggleSidebar"/> 的按钮长在「对话栏」顶栏上，而那个
    /// 顶栏只在有会话时可见，所以「侧栏收起 + 没有会话」是个死局：既没有按钮，也没有
    /// 行可以点。
    ///
    /// 这个死局目前进不去 —— 侧栏一收，会话列表就跟着被父面板裁掉（不显示也点不到），
    /// 删不掉最后一个会话。所以这里是一条保险：只要「活跃会话没了」这件事还能从别的
    /// 路径发生，退到欢迎页时就把侧栏一并展开，不留一个回不来的界面。
    /// </summary>
    private void EnsureSidebarOpen()
    {
        if (_sideTarget > 0) return;
        _sideTimer.Stop();
        _sideTarget = SideW;
        _sideW = SideW;
        _btnSideToggle.Icon = IconButton.Kind.Collapse;
        ApplyLayout();
    }

}
