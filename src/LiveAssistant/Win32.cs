using System.Runtime.InteropServices;

namespace LiveAssistant;

/// <summary>Win32 P/Invoke：全局热键、屏幕 DC、BitBlt 截屏等。</summary>
internal static class Win32
{
    // 热键修饰键
    public const int MOD_ALT = 0x0001;
    public const int MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    // 虚拟键码
    public const int VK_O = 0x4F; // Alt+O 显隐
    public const int VK_C = 0x43; // Alt+C 截屏
    public const int VK_V = 0x56; // Alt+V 录音

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern int BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
        IntPtr hdcSrc, int x1, int y1, int rop);

    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;
}
