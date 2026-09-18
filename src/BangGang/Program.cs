using System.Runtime.InteropServices;

namespace BangGang;

internal static class Program
{
    [STAThread]
    static void Main()
    {
#if DEBUG
        // 离屏渲染一段 markdown 就退出（见 OfflineRender）。放在单实例锁之前：
        // 它不建窗口，没有理由被「已经开着一个帮帮」挡住。
        if (OfflineRender.TryRun()) return;
#endif

        // 单实例：重复启动时把已有窗口带到前台后退出
        using var mutex = new Mutex(true, "Local\\BangGang_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Native.NotifyExistingInstance();
            return;
        }

        ApplicationConfiguration.Initialize();
        Trace.Reset();          // Debug 构建才会写入交互日志（Release 编译后此调用消失）
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
        GC.KeepAlive(mutex);
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            if (ex == null) return;
            string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}

internal static class Native
{
    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int SW_RESTORE = 9;

    // 单实例激活：找到已运行实例的窗口并前置
    public static void NotifyExistingInstance()
    {
        IntPtr hwnd = FindWindow(null, MainForm.WindowTitle);
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
    }
}
