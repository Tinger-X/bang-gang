
namespace BangGang;

/// <summary>
/// 设置浮窗配色：全部动态读取 <see cref="Theme"/>，主题变化后只需 Restyle 即可刷新，
/// 不需要重建控件树。
/// </summary>
internal static class SC
{
    /// <summary>按权重把 a 混向 b（k=0 取 a，k=1 取 b）。</summary>
    public static Color Mix(Color a, Color b, float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * k),
            (int)Math.Round(a.G + (b.G - a.G) * k),
            (int)Math.Round(a.B + (b.B - a.B) * k));
    }

    public static Color CardBg => Theme.PanelBg;
    /// <summary>分组卡片底色：比卡片略深一点点的浅灰蓝。</summary>
    public static Color GroupBg => Mix(Theme.PanelBg, Theme.SideBg, 0.55f);
    public static Color RailBg => Mix(Theme.PanelBg, Theme.SideBg, 0.85f);
    /// <summary>输入框 / 分段控件底色：直接跟随主题的输入区颜色（亮暗色都对）。</summary>
    public static Color FieldBg => Theme.InputBg;
    public static Color FieldBorder => Mix(Theme.Border, Theme.TextMuted, 0.16f);
    public static Color CardBorder => Mix(Theme.Border, Theme.TextMuted, 0.10f);
    public static Color Ink => Theme.TextMain;
    public static Color InkMuted => Theme.TextMuted;
    public static Color InkFaint => Mix(Theme.TextMuted, Theme.PanelBg, 0.35f);
    public static Color Accent => Theme.Accent;
    public static Color AccentSoft => Mix(RailBg, Theme.Accent, 0.16f);
    public static Color AccentHover => Mix(RailBg, Theme.Accent, 0.08f);
    public static Color Danger => Theme.Danger;
    /// <summary>遮罩色：把主界面压暗，突出居中浮窗。</summary>
    public static Color Scrim => Mix(Mix(Theme.ChatBg, Theme.SideBg, 0.45f), Color.FromArgb(16, 20, 28), 0.30f);
}
