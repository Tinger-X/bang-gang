using System.Runtime.InteropServices;

namespace LiveAssistant;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // 单实例：重复启动时把已有窗口带到前台后退出
        using var mutex = new Mutex(true, "Local\\LiveAssistant_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Native.NotifyExistingInstance();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
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
