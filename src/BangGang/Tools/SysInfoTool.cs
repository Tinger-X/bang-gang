using System.Globalization;

namespace BangGang;

/// <summary>
/// 系统信息工具。回答的是「我这机器上」的问题：分辨率、缩放、系统版本、程序版本。
/// 直播场景里问分辨率与缩放是常事（配码率、对齐窗口），而这些模型都猜不到。
/// </summary>
internal static class SysInfoTool
{
    public static readonly ToolDef Def = new()
    {
        Name = "sys_info",
        Label = "系统信息",
        Desc = "读取用户这台电脑的基本信息：屏幕分辨率与缩放比例、Windows 版本、本程序版本、" +
               "以及窗口是否能被录屏拍到。用户问「我这屏幕多大」「缩放是多少」时用它。",
        Run = (_, ctx, _) => Task.FromResult(Describe(ctx)),
    };

    private static string Describe(ToolContext ctx)
    {
        var sb = new System.Text.StringBuilder();

        // 每块都各兜一层 try：这几个 API 在远程会话 / 无显示器的机器上会抛，
        // 而「拿不到分辨率」不该让整条系统信息都出不来。
        try
        {
            var scr = Screen.PrimaryScreen;
            if (scr != null)
            {
                var b = scr.Bounds;
                sb.Append("主屏幕分辨率：").Append(b.Width).Append('×').Append(b.Height)
                  .Append("（工作区 ").Append(scr.WorkingArea.Width).Append('×').Append(scr.WorkingArea.Height).Append('）');

                // 物理像素 ÷ 逻辑像素 = 缩放。用 GetDeviceCaps(DESKTOPHORZRES) 拿物理值，
                // 因为 Bounds 已经按 DPI 缩放过了 —— 只报 Bounds 的话，
                // 一台 4K 屏在 150% 缩放下会被报成「2560×1440」，和用户看到的对不上。
                int physW = PhysicalWidth();
                if (physW > 0 && physW != b.Width)
                    sb.Append("\n屏幕物理分辨率：").Append(physW).Append('×').Append(PhysicalHeight())
                      .Append("，缩放约 ").Append(Math.Round(physW * 100.0 / b.Width)).Append('%');
            }
            if (Screen.AllScreens.Length > 1)
                sb.Append("\n显示器数量：").Append(Screen.AllScreens.Length);
        }
        catch { sb.Append("屏幕信息：读不到"); }

        sb.Append("\nWindows 版本：").Append(OsName());
        sb.Append("\n本程序版本：").Append(MainForm.AppVersion);
        sb.Append("\n运行环境：.NET ").Append(Environment.Version);

        try
        {
            var f = ctx.UiHost?.FindForm();
            if (f != null)
                sb.Append("\n窗口尺寸：").Append(f.Width).Append('×').Append(f.Height)
                  .Append(f.WindowState == FormWindowState.Minimized ? "（已最小化）" : "");
        }
        catch { /* 没窗口就不报这一项 */ }

        // 「能不能被录屏拍到」是这个程序的核心设定，用户问起来时得答得准。
        // Debug + BANGGANG_SHOW_IN_CAPTURE=1 是唯一的例外（见 CaptureGuard）。
        sb.Append("\n录屏可见性：")
          .Append(CaptureGuard.Disabled
              ? "当前构建允许被录屏拍到（调试模式）"
              : "对录屏 / 截图 / 屏幕共享完全不可见（只有本机用户看得见）");

        return sb.ToString();
    }

    private static string OsName()
    {
        try
        {
            // Environment.OSVersion 在装了 manifest 的进程里报的是真实版本号；
            // Windows 10 与 11 的内核版本都是 10.0，光看版本号分不出来 ——
            // 11 的内部版本号从 22000 起，用这个分。
            var v = Environment.OSVersion.Version;
            string name = v.Build >= 22000 ? "Windows 11" : v.Major >= 10 ? "Windows 10" : "Windows " + v.Major;
            return name + "（" + v.ToString(3) + "）";
        }
        catch { return "未知"; }
    }

    // ---------------- 物理分辨率 ----------------

    private const int DESKTOPHORZRES = 118;
    private const int DESKTOPVERTRES = 117;

    private static int PhysicalWidth() => DeviceCap(DESKTOPHORZRES);
    private static int PhysicalHeight() => DeviceCap(DESKTOPVERTRES);

    private static int DeviceCap(int index)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 0;
        try { return GetDeviceCaps(dc, index); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);
}
