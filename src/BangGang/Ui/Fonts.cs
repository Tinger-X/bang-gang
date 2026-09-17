
namespace BangGang;

/// <summary>
/// 自绘绘制用字体缓存（避免在 OnPaint 里反复 new Font 造成 GDI 句柄泄漏）。
/// 注意：返回的 Font 由缓存持有，调用方**不要** Dispose，也不要赋给控件的 Font 属性，
/// 控件字体请用 <see cref="Theme.UI"/>（每次新建、由控件持有）。
///
/// 缓存键含**字族**而不只是字号：等宽那一支（<see cref="Mono"/>）走的是 Consolas，
/// 和界面字体的字号相同、实例完全不同。键里不带字族的话，先来的那个把键占了，
/// 后来的那个拿到的是**另一种字体的同一个字号** —— 代码块会画成比例字体，
/// 而 12 号的比例字体和 12 号的等宽字体不一样宽，两者在同一个键上永远分不出胜负。
/// </summary>
internal static class SF
{
    /// <summary>界面字体。与 <see cref="Theme.UI"/> 同一个字族，区别只在这个是共享的。</summary>
    public const string UiFamily = "Microsoft YaHei UI";

    /// <summary>等宽字体：代码块与行内代码。</summary>
    public const string MonoFamily = "Consolas";

    private static readonly Dictionary<(string family, float size, FontStyle style), Font> Cache = new();

    public static Font Get(float size, FontStyle style = FontStyle.Regular)
        => Get(UiFamily, size, style);

    /// <summary>等宽那一支。字号与 <see cref="Get"/> 同刻度，可混着传。</summary>
    public static Font Mono(float size, FontStyle style = FontStyle.Regular)
        => Get(MonoFamily, size, style);

    public static Font Get(string family, float size, FontStyle style)
    {
        var key = (family, size, style);
        if (!Cache.TryGetValue(key, out var f))
        {
            f = new Font(family, size, style);
            Cache[key] = f;
        }
        return f;
    }
}
