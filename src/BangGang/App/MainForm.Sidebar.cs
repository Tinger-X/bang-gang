using System.Drawing.Drawing2D;

namespace BangGang;

partial class MainForm
{
    // ---------------- 左侧栏收起 / 展开 ----------------

    private int _sideW = SideW;          // 当前动画宽度，0 = 完全收起
    private int _sideTarget = SideW;     // 动画目标

    // 时间驱动动画的状态：起点宽度与起跑时刻。曲线由这两者现算，不靠「每帧消掉一个
    // 固定比例」递推 —— 递推那种写法里 tick 晚到一次，整段动画的墙钟就被拉长一次。
    private int _sideFrom;
    private readonly System.Diagnostics.Stopwatch _sideClock = new();

    /// <summary>
    /// 收起 / 展开左侧栏。宽度不是一下跳过去的：<see cref="_sideTimer"/> 的每一帧按
    /// 「动画已经跑了多久」走一条 ease-out cubic 曲线算出当前宽度（<see cref="SideTick"/>），
    /// 墙钟总时长恒为 <see cref="SideAnimDurMs"/>。图标在这一刻就翻转（而不是等动画结束），
    /// 点下去马上有反馈。
    ///
    /// 动画起步前先把系统定时器分辨率提到 1ms（<see cref="SideClockBegin"/>）：
    /// WM_TIMER 的实际节拍受系统分辨率钳制，默认 15.6ms 下 18ms 的间隔会滑成 ~31ms
    /// 一拍。时间驱动之后节拍漂移不再拉长动画，但它仍决定**帧率**（30fps vs 60fps），
    /// 所以这一个提升仍然值得。
    /// </summary>
    private void ToggleSidebar()
    {
        bool collapse = _sideTarget > 0;             // 当前是展开的 -> 这一次要收起
        _sideTarget = collapse ? 0 : SideW;
        _btnSideToggle.Icon = collapse ? IconButton.Kind.Expand : IconButton.Kind.Collapse;
        _btnSideToggle.Invalidate();
        // 起点取「此刻的宽度」而不是上一次的起点：动画中途反向点击时，曲线从当前位置
        // 平滑地折回去，不会从旧起点跳一下。
        _sideFrom = _sideW;
        _sideClock.Restart();
        SideClockBegin();
        _sideTimer.Start();                          // 重复点只是换目标与起点，不会叠出第二个动画
    }

    // 动画期间持有 timeBeginPeriod(1) 的证据；Begin/End 必须成对，重复点不能重复 Begin。
    private bool _sideHiRes;

    private void SideClockBegin()
    {
        if (_sideHiRes) return;
        _sideHiRes = true;
        Win32.timeBeginPeriod(1);
    }

    private void SideClockEnd()
    {
        if (!_sideHiRes) return;
        _sideHiRes = false;
        Win32.timeEndPeriod(1);
    }

    private void SideTick()
    {
        // 时间驱动的 ease-out cubic：t 是「动画已进行的墙钟比例」，e 从 1 陡起步、平缓收尾。
        // tick 晚到（WM_TIMER 节拍漂移）只是 t 采样得远一点，总时长不变 —— 这正是把它从
        // 「每帧消掉剩余距离的固定比例」改过来的原因：那种写法下每一次晚到都把墙钟拉长。
        double t = _sideClock.Elapsed.TotalMilliseconds / SideAnimDurMs;
        bool done = t >= 1.0;
        if (done)
        {
            _sideW = _sideTarget;
            _sideTimer.Stop();
            _sideClock.Stop();
            SideClockEnd();
        }
        else
        {
            double u = 1.0 - t;
            double e = 1.0 - u * u * u;
            _sideW = _sideFrom + (int)Math.Round((_sideTarget - _sideFrom) * e);
        }

        // 动画中间帧只挪位置（liveResize: true），最后一帧才真正重排一次文字。
        // 别把这一对拆开：只传 true 的话文字会永远停在动画中途的折行上；
        // 只传 false 就退回到「逐帧重排全部消息」，也就是这次要修的那个卡顿。
        ApplyLayout(liveResize: !done);
        if (done) _chatView.SettleLayout();
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
        _sideClock.Stop();
        SideClockEnd();
        _sideTarget = SideW;
        _sideW = SideW;
        _btnSideToggle.Icon = IconButton.Kind.Collapse;
        ApplyLayout();
    }

}
