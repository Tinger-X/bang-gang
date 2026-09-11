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
}
