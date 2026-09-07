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

    private static ToolTipForm? _tipForm;

    /// <summary>
    /// 为控件绑定一个悬浮提示。提示是一个带每像素 Alpha 的置顶窗口（见 <see cref="ToolTipForm"/>），
    /// 圆角与文字抗锯齿渲染、四角透明，并应用 WDA_EXCLUDEFROMCAPTURE，不会被录屏 / 截图捕获。
    /// </summary>
    public static void SetToolTip(Control control, string text)
    {
        control.MouseEnter += (_, _) => EnsureForm().Arm(control, text);
        control.MouseLeave += (_, _) => EnsureForm().Disarm(control);
    }

    /// <summary>主窗口隐藏 / 关闭时调用，清除可能残留的提示浮层。</summary>
    public static void HideToolTip() => _tipForm?.HideNow();

    private static ToolTipForm EnsureForm()
    {
        if (_tipForm is null || _tipForm.IsDisposed)
            _tipForm = new ToolTipForm();
        return _tipForm;
    }
}
