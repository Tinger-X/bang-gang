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
}
