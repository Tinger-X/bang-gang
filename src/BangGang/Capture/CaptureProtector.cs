using System.Runtime.InteropServices;

namespace BangGang;

/// <summary>
/// 防录屏总开关。
///
/// <para><b>Release 构建恒为 false</b>：正式版本不存在任何运行期后门，
/// 无论环境变量如何设置，主窗口与设置浮窗都强制保持“对录屏 / 截图完全不可见”
/// （WDA_EXCLUDEFROMCAPTURE：捕获画面里直接看不到该窗口，而不是显示成黑框）。</para>
///
/// <para>只有 Debug 构建才认 BANGGANG_SHOW_IN_CAPTURE=1，用于本地截图核对界面细节。</para>
/// </summary>
internal static class CaptureGuard
{
    public static bool Disabled { get; } =
#if DEBUG
        Environment.GetEnvironmentVariable("BANGGANG_SHOW_IN_CAPTURE") == "1";
#else
        false;
#endif
}

/// <summary>
/// 进程级防录屏：拦截本线程创建的每个顶层窗口，立即为其应用
/// WDA_EXCLUDEFROMCAPTURE，确保颜色/文件对话框等由本线程弹出的顶层窗口
/// 都不会被录屏 / 截图捕获。
/// （应用内已不再有任何 ToolTip 浮层：那种独立置顶小窗口一旦漏配防录屏
/// 就会被录进去，而且它属于辅助信息，直接去掉最稳。）
/// </summary>
internal static class CaptureProtector
{
    private const int WH_CBT = 5;
    private const int HCBT_CREATEWND = 3;
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static HookProc? _proc;
    private static IntPtr _hook;

    /// <summary>在当前线程安装 CBT 钩子，之后创建的顶层窗口会被自动保护。</summary>
    public static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        if (CaptureGuard.Disabled) return;   // 本地界面调试：不拦截后续顶层窗口
        _proc = OnCbt;
        _hook = SetWindowsHookEx(WH_CBT, _proc, IntPtr.Zero, GetCurrentThreadId());
    }

    private static IntPtr OnCbt(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HCBT_CREATEWND && wParam != IntPtr.Zero)
        {
            int style = GetWindowLong(wParam, GWL_STYLE);
            if ((style & WS_CHILD) == 0)
                Native.SetWindowDisplayAffinity(wParam, Native.WDA_EXCLUDEFROMCAPTURE);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}
