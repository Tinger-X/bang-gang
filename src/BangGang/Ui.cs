namespace BangGang;

/// <summary>
/// 全局 UI 规则：应用内鼠标指针始终保持默认箭头形态，
/// 禁止文本插入符（IBeam）、手型（Hand）等其它任何形态。
/// 本规则对当前所有控件以及之后动态新增的控件统一生效。
/// </summary>
internal static class Ui
{
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
}

/// <summary>
/// 输入框里的「占位文字」层，与同位置的 TextBox 左对齐、共用一个矩形。
///
/// 为什么不用 <see cref="Label"/>：Label 内部走 TextRenderer 时会带上字体的
/// glyph overhang 内边距，起点和 EDIT 对不齐；这里显式带 <c>NoPadding</c>，
/// 文字起点就是客户区左边缘，与 <see cref="Ui.PinEditTextLeft"/> 处理过的
/// EDIT 完全一致。
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
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
