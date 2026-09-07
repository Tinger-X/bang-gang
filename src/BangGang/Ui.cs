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

    private static ToolTipLayer? _tipLayer;

    /// <summary>
    /// 为控件绑定一个悬浮提示。提示以主窗口子控件的形式绘制（见 <see cref="ToolTipLayer"/>），
    /// 与主窗口共享同一 WDA_EXCLUDEFROMCAPTURE 排除表面，因此不会被录屏 / 截图捕获。
    /// </summary>
    public static void SetToolTip(Control control, string text)
    {
        control.MouseEnter += (_, _) => EnsureLayer(control).Arm(control, text);
        control.MouseLeave += (_, _) => EnsureLayer(control).Disarm(control);
    }

    /// <summary>主窗口隐藏 / 关闭时调用，清除可能残留的提示浮层。</summary>
    public static void HideToolTip() => _tipLayer?.HideNow();

    private static ToolTipLayer EnsureLayer(Control control)
    {
        if (_tipLayer is null || _tipLayer.IsDisposed)
            _tipLayer = new ToolTipLayer();

        var form = control.FindForm();
        if (form is not null && _tipLayer.Parent != form)
        {
            _tipLayer.Parent?.Controls.Remove(_tipLayer);
            form.Controls.Add(_tipLayer);
            _tipLayer.BringToFront();
        }
        return _tipLayer;
    }
}
