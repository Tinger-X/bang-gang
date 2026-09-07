using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BangGang;

/// <summary>
/// 进程级防录屏：拦截本线程创建的每个顶层窗口，立即为其应用
/// WDA_EXCLUDEFROMCAPTURE，确保主窗口、ToolTip 提示框、颜色/文件对话框等
/// 应用的任何内容都不会被录屏 / 截图捕获。
/// </summary>
internal static class CaptureProtector
{
    private const int WH_CBT = 5;
    private const int HCBT_CREATEWND = 3;
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static HookProc? _proc;
    private static IntPtr _hook;

    /// <summary>在当前线程安装 CBT 钩子，之后创建的顶层窗口会被自动保护。</summary>
    public static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = OnCbt;
        _hook = SetWindowsHookEx(WH_CBT, _proc, IntPtr.Zero, GetCurrentThreadId());
    }

    /// <summary>对进程内的 ToolTip 窗口重新应用排除（隐藏后再显示可能失效）。</summary>
    public static void ProtectTooltips()
    {
        EnumWindows(OnEnumTooltip, IntPtr.Zero);
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

    private static bool OnEnumTooltip(IntPtr hWnd, IntPtr lParam)
    {
        var sb = new StringBuilder(64);
        GetClassName(hWnd, sb, sb.Capacity);
        if (sb.ToString() == "tooltips_class32")
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == (uint)Process.GetCurrentProcess().Id)
            {
                Native.SetWindowDisplayAffinity(hWnd, Native.WDA_NONE);
                Native.SetWindowDisplayAffinity(hWnd, Native.WDA_EXCLUDEFROMCAPTURE);
            }
        }
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
