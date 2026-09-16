
namespace BangGang;

/// <summary>
/// 设置浮窗绘制用字体缓存（避免在 OnPaint 里反复 new Font 造成 GDI 句柄泄漏）。
/// 注意：返回的 Font 由缓存持有，调用方**不要** Dispose，也不要赋给控件的 Font 属性，
/// 控件字体请用 <see cref="Theme.UI"/>（每次新建、由控件持有）。
/// </summary>
internal static class SF
{
    private static readonly Dictionary<(float size, FontStyle style), Font> Cache = new();

    public static Font Get(float size, FontStyle style = FontStyle.Regular)
    {
        var key = (size, style);
        if (!Cache.TryGetValue(key, out var f))
        {
            f = new Font("Microsoft YaHei UI", size, style);
            Cache[key] = f;
        }
        return f;
    }
}
