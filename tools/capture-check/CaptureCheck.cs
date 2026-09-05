// 验证工具：找到"帮帮"窗口，读取其 display affinity，并用 BitBlt 截屏取窗口中心像素
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dw);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("gdi32.dll")] public static extern int BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int x1, int y1, int rop);
    public struct RECT { public int Left, Top, Right, Bottom; }
}

class Check
{
    [STAThread]
    static int Main()
    {
        IntPtr hwnd = Native.FindWindow(null, "帮帮");
        if (hwnd == IntPtr.Zero) { Console.WriteLine("RESULT: window NOT found"); return 1; }

        uint affinity = 0;
        bool ok = Native.GetWindowDisplayAffinity(hwnd, out affinity);
        Console.WriteLine($"affinity: Get={ok} value=0x{affinity:X} (0x11=EXCLUDEFROMCAPTURE)");

        Native.RECT r;
        Native.GetWindowRect(hwnd, out r);
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        Console.WriteLine($"window rect: L={r.Left} T={r.Top} R={r.Right} B={r.Bottom}, center=({cx},{cy})");

        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            Native.BitBlt(hdc, 0, 0, 1, 1, Native.GetDC(IntPtr.Zero), cx, cy, 0x00CC0020 | 0x40000000);
            g.ReleaseHdc(hdc);
        }
        Color px = bmp.GetPixel(0, 0);
        Console.WriteLine($"captured pixel at window center: {px}");
        Console.WriteLine($"RESULT: {(affinity == 0x11 ? "PASS - 录屏/截屏中窗口不可见" : "FAIL")}");
        return affinity == 0x11 ? 0 : 2;
    }
}
