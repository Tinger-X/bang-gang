#if DEBUG
using System.Reflection;

namespace BangGang;

/// <summary>
/// 设置页高度的离屏探针：**把每一页的内容高度量出来，跟可视高度比一比**。
///
/// 为什么值得单独做：各页的行高、卡片留白都是硬编码像素，而内容区高度是死的
/// （880×640 的卡片扣掉顶栏 92、底栏 58、3px 内缩，只剩 484）。两者一旦分家，
/// 表现**只是**「滚动条变成一根拖不动、又像多余竖线的细条」—— 光看截图会以为
/// 那是设计如此，而实际上每页都该是「要么装得下、要么超得明显」。
///
/// 往哪一页加一行之后跑一下这个，比开程序、点开设置、目测滚动条可靠得多，
/// 而且不依赖桌面（同 <c>OfflineRender</c> / <c>OfflineDb</c> 的理由）。
///
/// <c>BANGGANG_PAGES=1</c> 触发。
/// </summary>
internal static class OfflinePages
{
    /// <summary>卡片内容区的可视高度。改 <c>SettingsOverlay</c> 的卡片尺寸时这里要跟着改。</summary>
    private const int View = 484;

    public static bool TryRun()
    {
        if (Environment.GetEnvironmentVariable("BANGGANG_PAGES") != "1") return false;

        try
        {
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 包不上就按默认走 */ }

        var settings = new AppSettings();
        settings.ApplyTheme();

        Console.WriteLine($"可视高度 {View}px");
        Console.WriteLine();
        Console.WriteLine($"{"页面",-10} {"内容",6}  判定");

        int bad = 0;
        foreach (var (name, page) in new (string, SettingsPage)[]
                 {
                     ("快捷按键", new ShortcutsPage()),
                     ("模型接入", new LlmPage()),
                     ("对话参数", new ChatPage()),
                     ("工具调用", new ToolsPage()),
                     ("界面外观", new UiPage()),
                     ("软件说明", new AboutPage()),
                 })
        {
            int h = Measure(page, settings);
            // 装得下是绿的；超了要看超多少 —— 只超几个像素时滚动条滑块几乎占满整条轨道，
            // 看着就是一根多余的竖线（ToolsPage 的注释里记着这个现象）。
            // 超出 15% 以上才算「明显是根滚动条」，那是可以接受的。
            bool ok = h <= View || h >= View + View * 0.15;
            if (!ok) bad++;
            string verdict = h <= View ? "装得下"
                           : ok ? $"超出 {h - View}（滚动条正常）"
                                : $"**超出 {h - View}，滚动条会像一根多余的竖线**";
            Console.WriteLine($"{name,-10} {h,6}  {verdict}");
        }

        Console.WriteLine();
        Console.WriteLine(bad == 0 ? "全部通过" : $"{bad} 个页面高度不合适");
        if (bad > 0) Environment.ExitCode = 1;
        return true;
    }

    /// <summary>
    /// 构造好的页面把每张卡的高度都算过了（<c>GroupCard.MeasureHeight</c> 在构造时就调用了），
    /// 所以 <c>StackPanel.ContentHeight</c> 直接就是答案，不需要真的走一遍布局 ——
    /// 也就不需要窗口、不需要桌面。
    /// <c>Stack</c> 是 protected，这里用反射取一下：探针里读一个字段，比为了测试
    /// 把它的可见性放宽要干净。
    /// </summary>
    private static int Measure(SettingsPage page, AppSettings settings)
    {
        try
        {
            page.Rebind(settings);
            var f = typeof(SettingsPage).GetField("Stack", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(page) is StackPanel stack) return stack.ContentHeight;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (某一页量不出来：{ex.Message})");
        }
        return -1;
    }
}
#endif
