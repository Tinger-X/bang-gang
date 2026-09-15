using System.Diagnostics;
using System.Text;

namespace BangGang;

/// <summary>
/// 调试期交互日志：只在 Debug 构建里存在（<see cref="ConditionalAttribute"/> 会在 Release
/// 编译时把调用点整个删掉，因此发布版本没有任何运行时可开关），用于自动化脚本核对
/// “点击落到了哪个控件上、下拉是否真的展开/收起、组合键何时提交”。
/// </summary>
internal static class Trace
{
    private static readonly object Gate = new();

    public static string FilePath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            return Path.Combine(dir, "ui-trace.log");
        }
    }

    [Conditional("DEBUG")]
    public static void Reset()
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"===== app start {DateTime.Now:HH:mm:ss.fff} =====" + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 调试日志失败不影响功能 */ }
    }

    [Conditional("DEBUG")]
    public static void Log(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("HH:mm:ss.fff ") + message + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 调试日志失败不影响功能 */ }
    }

    /// <summary>
    /// Debug 专用：延迟把某个控件自身的绘制结果存成 PNG。
    /// 不经过屏幕抓图，便于排查“屏幕上看是黑的 / 抓图看不到”一类问题。
    /// 只有设置 BANGGANG_DUMP=1 时才写文件（Release 构建里整个调用点都会被删掉）。
    /// </summary>
    [Conditional("DEBUG")]
    public static void DumpAfter(Control control, string name, int delayMs)
    {
        if (Environment.GetEnvironmentVariable("BANGGANG_DUMP") != "1") return;
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(50, delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            Dump(control, name);
        };
        timer.Start();
    }

    [Conditional("DEBUG")]
    public static void Dump(Control control, string name)
    {
        try
        {
            if (control.Width <= 0 || control.Height <= 0) { Log($"dump {name}: empty"); return; }
            using var bmp = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bmp, new Rectangle(0, 0, control.Width, control.Height));
            bmp.Save(Path.Combine(AppContext.BaseDirectory, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            Log($"dump {name} -> {bmp.Width}x{bmp.Height}");
        }
        catch (Exception ex)
        {
            Log($"dump {name} failed: {ex.GetType().Name} {ex.Message}");
        }
    }
}
