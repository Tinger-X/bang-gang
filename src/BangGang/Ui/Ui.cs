using System.Diagnostics;

namespace BangGang;

/// <summary>
/// 全局 UI 规则：应用内鼠标指针始终保持默认箭头形态，
/// 禁止文本插入符（IBeam）、手型（Hand）等其它任何形态。
/// 本规则对当前所有控件以及之后动态新增的控件统一生效。
/// </summary>
internal static class Ui
{
    /// <summary>
    /// 用系统默认浏览器打开一个链接。
    ///
    /// **只放行 http / https**：<c>UseShellExecute = true</c> 是把整个字符串交给 ShellExecute，
    /// 别的协议等于让外部字符串决定执行什么（<c>file:///C:/…/x.exe</c> 会被直接跑起来）。
    /// 今天这个入参是程序里写死的常量表，一行白名单看着多余；但「打开外部链接」这个动作
    /// 早晚会被接到别的地方去（配置里的自定义地址、消息里的链接），封在这里只要一行。
    ///
    /// 失败不抛异常、只返回 false：没有默认浏览器、被组策略拦下都是环境问题，
    /// 不该让消息循环崩掉，由调用方决定要不要说一句。
    /// </summary>
    public static bool OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        try
        {
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Trace.Log("open link failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 递归把整棵控件树的光标设为默认箭头，并订阅 ControlAdded，
    /// 使之后新加入的控件也自动沿用该规则（对未来新增元素同样有效）。
    /// </summary>
    public static void EnforceArrowCursor(Control root)
    {
        root.Cursor = Cursors.Default;
        root.ControlAdded -= OnControlAdded;
        root.ControlAdded += OnControlAdded;
        foreach (Control child in root.Controls)
            EnforceArrowCursor(child);
    }

    private static void OnControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is not null)
            EnforceArrowCursor(e.Control);
    }

    /// <summary>
    /// 主题切换后递归刷新整棵控件树：凡是缓存过主题色的控件都实现 <see cref="IThemed"/>，
    /// 在这里统一 Restyle，避免暗色模式下残留浅色底。
    /// </summary>
    public static void RestyleTree(Control root)
    {
        if (root is IThemed t) t.Restyle();
        foreach (Control c in root.Controls) RestyleTree(c);
        root.Invalidate(true);
    }

    /// <summary>
    /// 把单行 TextBox 里文字的**左起点**钉到客户区左边缘。
    ///
    /// EDIT 默认自带一小段左内边距，而占位文字层是用 TextRenderer 从 0 开始画的，
    /// 两者天然差 1~3px：占位时看着是一个位置，一输入文字就“跳”一下。
    /// 这里用 EM_SETMARGINS 把 EDIT 的左边距显式清零，让两边共用同一个起点。
    ///
    /// 句柄重建后（字体 / DPI 变化会重建）需要重新调用，所以调用点要挂在
    /// <c>HandleCreated</c> 上而不是构造函数里。
    /// </summary>
    public static void PinEditTextLeft(TextBox tb)
    {
        if (!tb.IsHandleCreated) return;
        const int EM_SETMARGINS = 0x00D3;
        const int EC_LEFTMARGIN = 0x0001;
        Win32.SendMessage(tb.Handle, EM_SETMARGINS, (IntPtr)EC_LEFTMARGIN, IntPtr.Zero);
    }

    /// <summary>单行 EDIT 的盒子在字体行高之外预留的余量（像素）。</summary>
    public const int EditBoxPadY = 6;

    /// <summary>
    /// 多行 EDIT 排版时**两行之间的真实行距** —— <c>tmHeight + tmExternalLeading</c>，
    /// 不是 GDI+ 的 <c>Font.Height</c>。
    ///
    /// 本程序的字号下两者差 1px（22 vs 23），看着无所谓，但多行 EDIT 只画**完整装得下**的行：
    /// 它按 23 算「这一行放不放得下」，我们按 22 算盒子高度，于是 3 * 22 = 66 的盒子它只画
    /// 2 行，剩下二十多像素空着 —— 就是「才两行就开始往上滚、底部明明还放得下一行却空着」。
    /// 盒子高度取成行距的整数倍，两个数才对得上。
    ///
    /// 也不要用 <c>Font.GetHeight()</c>：那是 GDI+ 按 DPI 折算的行高，同样不等于 GDI 的
    /// tmHeight（换字号 / 换 DPI 时会再分家）。这里问的就是 EDIT 自己排版用的那个 DC。
    /// </summary>
    public static int EditLinePitch(Font f)
    {
        IntPtr dc = Win32.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return Math.Max(1, f.Height);
        IntPtr hf = f.ToHfont();
        IntPtr old = Win32.SelectObject(dc, hf);
        int pitch = Math.Max(1, f.Height);
        if (Win32.GetTextMetrics(dc, out var tm))
            pitch = tm.tmHeight + tm.tmExternalLeading;
        Win32.SelectObject(dc, old);
        Win32.DeleteObject(hf);
        Win32.DeleteDC(dc);
        return Math.Max(1, pitch);
    }

    /// <summary>
    /// 盒子下边缘与容器下边缘之间至少留出的空隙。输入框的圆角描边画在容器的最后两行像素上，
    /// 而 EDIT 会用不透明的底色铺满自己的客户区 —— 盒子一直顶到容器底部就会把那道边盖掉半截。
    /// </summary>
    public const int EditBoxBottomGap = 3;

    /// <summary>
    /// 单行 EDIT（以及它的占位文字层）应该占据的矩形。
    ///
    /// 两条实测出来的硬事实，决定了这个函数只能长这样：
    ///
    /// 1) **EDIT 把单行文字顶对齐在自己的客户区里** —— 同一串字，盒子高 18 和 24，
    ///    墨迹都从第 5 行开始。也就是说<strong>盒子的上边缘决定文字位置</strong>，
    ///    盒子加高只会把多出来的空间全部留在下方，文字不会跟着往下走。
    ///
    /// 2) WinForms 把单行 TextBox 的高度锁死在 <c>PreferredHeight</c>（≈ 字体行高，
    ///    不含任何余量），而雅黑这类 CJK 字体的下缘实笔略超出 GDI 报的行高，
    ///    于是 g / j / y / p / q 的尾巴正好被客户区裁掉约 1px —— 全选时最明显：
    ///    高亮块的下边界切在字上。
    ///
    /// 所以：上边缘取「行高盒子竖直居中」的位置（<strong>文字位置与加余量之前完全一致</strong>，
    /// 仍然居中），高度再加 <see cref="EditBoxPadY"/> —— 余量只加在下方。
    /// 要是上下对称地加，文字会被顶到偏上 pad/2，反而不居中了。
    /// 容器不够高时（搜索框那种 32px 胶囊）余量被 <see cref="EditBoxBottomGap"/> 削掉一部分，
    /// 但不会削到低于行高 —— 那等于又回到「下缘实笔被裁」的老问题。
    ///
    /// 配套要求：
    /// - <c>tb.AutoSize = false</c>，否则传给 SetBounds 的高度会被改回 PreferredHeight；
    /// - 占位层用 <see cref="TextFormatFlags.Top"/> 画（而不是 VerticalCenter），
    ///   它才会和 EDIT 一样顶对齐到同一个位置。
    /// </summary>
    public static Rectangle EditBox(int left, int width, int containerHeight, TextBox tb)
    {
        int line = tb.PreferredHeight;
        int top = Math.Max(0, (containerHeight - line) / 2);
        int h = Math.Min(line + EditBoxPadY,
                         Math.Max(line, containerHeight - top - EditBoxBottomGap));
        return new Rectangle(left, top, width, h);
    }
}

/// <summary>
/// 输入框里的「占位文字」层，与同位置的 TextBox 左对齐、共用一个矩形。
///
/// 为什么不用 <see cref="Label"/>：Label 内部走 TextRenderer 时会带上字体的
/// glyph overhang 内边距，起点和 EDIT 对不齐；这里显式带 <c>NoPadding</c>，
/// 文字起点就是客户区左边缘，与 <see cref="Ui.PinEditTextLeft"/> 处理过的
/// EDIT 完全一致。
///
/// 为什么竖直方向用 <see cref="TextFormatFlags.Top"/> 而不是 VerticalCenter：
/// EDIT 把单行文字**顶对齐**在自己的客户区里，VerticalCenter 则按矩形高度居中 ——
/// 两者只在「矩形高度恰好等于行高」时才碰巧重合，盒子一加余量就错开 3px。
/// 实测 Top 与 EDIT 的墨迹逐像素一致，且与矩形高度无关。
///
/// 为什么必须是浮在 TextBox 之上的独立子控件：TextBox 会用不透明底色铺满
/// 自己的客户区，父层 <c>OnPaint</c> 里画的占位文字会被它整片盖掉。
/// </summary>
internal sealed class HintText : Control
{
    private string _hint = "";

    public HintText()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
    }

    public string Hint
    {
        get => _hint;
        set
        {
            value ??= "";
            if (_hint == value) return;
            _hint = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_hint.Length == 0) return;
        TextRenderer.DrawText(e.Graphics, _hint, Font, new Rectangle(0, 0, Width, Height), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.Top
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
