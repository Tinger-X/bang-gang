using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 实验：验证 WDA_EXCLUDEFROMCAPTURE (0x11)
// 1) 对自身窗口：设置后 GDI BitBlt 截屏中该窗口区域应显示其后方内容（而非窗口本身）
// 2) 对托盘图标工具条（explorer 进程的 ToolbarWindow32）跨进程设置是否成功
// 全程可逆：结束后对托盘恢复 WDA_NONE

static class Native
{
    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    public static uint LastError() => (uint)Marshal.GetLastWin32Error();
}

class Program
{
    [STAThread]
    static int Main()
    {
        // ---- Part 1: 自身窗口 ----
        using var form = new Form
        {
            Text = "AffinityTest",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(100, 100, 300, 200),
            BackColor = Color.Lime,          // 特征色，便于检测是否被截到
            TopMost = true,
            ShowInTaskbar = false,
        };
        form.Show();
        Application.DoEvents();

        bool setOk = Native.SetWindowDisplayAffinity(form.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        uint err = Native.LastError();
        uint readBack = 0;
        bool getOk = Native.GetWindowDisplayAffinity(form.Handle, out readBack);
        Console.WriteLine($"[own-window] Set={setOk} err={err} Get={getOk} affinity=0x{readBack:X}");

        Application.DoEvents();
        Color captured = CapturePixel(new Rectangle(form.Bounds.X + 150, form.Bounds.Y + 100, 1, 1));
        Console.WriteLine($"[own-window] BitBlt pixel at window center = {captured} (Lime=被截到 / 其他=被排除)");

        // ---- Part 2: 托盘图标工具条（跨进程） ----
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        Console.WriteLine($"[tray] Shell_TrayWnd = 0x{tray.ToInt64():X}");
        IntPtr trayNotify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr pager = Native.FindWindowEx(trayNotify, IntPtr.Zero, "SysPager", null);
        IntPtr toolbar = Native.FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
        Console.WriteLine($"[tray] TrayNotifyWnd=0x{trayNotify.ToInt64():X} SysPager=0x{pager.ToInt64():X} ToolbarWindow32=0x{toolbar.ToInt64():X}");

        if (toolbar != IntPtr.Zero)
        {
            // 记录工具条屏幕位置，用于截屏对比
            var r = GetRect(toolbar);
            Console.WriteLine($"[tray] toolbar rect = {r}");
            Color before = CapturePixel(new Rectangle(r.X + r.Width / 2, r.Y + r.Height / 2, 1, 1));

            bool tSet = Native.SetWindowDisplayAffinity(toolbar, Native.WDA_EXCLUDEFROMCAPTURE);
            uint tErr = Native.LastError();
            uint tRead = 0;
            bool tGet = tSet ? Native.GetWindowDisplayAffinity(toolbar, out tRead) : false;
            Console.WriteLine($"[tray] Set={tSet} err={tErr} Get={tGet} affinity=0x{tRead:X}");

            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Color after = CapturePixel(new Rectangle(r.X + r.Width / 2, r.Y + r.Height / 2, 1, 1));
            Console.WriteLine($"[tray] toolbar 像素 截屏前={before} 截屏后={after} (变化=图标被排除出截屏)");

            // 还原，不留副作用
            bool revert = Native.SetWindowDisplayAffinity(toolbar, Native.WDA_NONE);
            Console.WriteLine($"[tray] revert WDA_NONE = {revert}");
        }

        Console.WriteLine("DONE");
        return 0;
    }

    static Rectangle GetRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out RECT r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    // GDI BitBlt 全屏后取像素（模拟普通截屏软件的行为）
    static Color CapturePixel(Rectangle rect)
    {
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdcDest = g.GetHdc();
            BitBlt(hdcDest, 0, 0, rect.Width, rect.Height,
                   GetDC(IntPtr.Zero), rect.X, rect.Y, SRCCOPY | CAPTUREBLT);
            g.ReleaseHdc(hdcDest);
        }
        return bmp.GetPixel(0, 0);
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("gdi32.dll")] static extern int BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    const int SRCCOPY = 0x00CC0020;
    const int CAPTUREBLT = 0x40000000;

    struct RECT { public int Left, Top, Right, Bottom; }
}
