namespace BangGang;

/// <summary>全局主题色（运行时由设置刷新，支持亮色 / 暗色两套调色板）。</summary>
public static class Theme
{
    public static Color ChatBg = Color.White;
    public static Color SideBg = Color.FromArgb(246, 248, 251);
    public static Color PanelBg = Color.FromArgb(252, 253, 255);
    public static Color HeaderBg = Color.FromArgb(240, 245, 251);
    public static Color TextMain = Color.FromArgb(30, 34, 40);
    public static Color TextMuted = Color.FromArgb(120, 128, 138);
    public static Color Accent = Color.FromArgb(47, 112, 224);
    public static Color UserBubble = Color.FromArgb(219, 233, 255);
    public static Color AsstBubble = Color.FromArgb(240, 242, 246);
    public static Color Border = Color.FromArgb(225, 229, 235);
    public static Color InputBg = Color.White;
    public static Color Danger = Color.FromArgb(214, 60, 54);
    public static double WindowOpacity = 1.0;

    /// <summary>当前是否暗色主题（部分推导颜色需要区分）。</summary>
    public static bool Dark;

    /// <summary>是否绘制主题色窗口边框。</summary>
    public static bool WindowBorder = true;

    public static Font UI(float size, FontStyle st = FontStyle.Regular) => new("Microsoft YaHei UI", size, st);
    public static Font Mono(float size) => new("Consolas", size);

    /// <summary>把 a 按权重 k 混向 b。</summary>
    public static Color Mix(Color a, Color b, float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * k),
            (int)Math.Round(a.G + (b.G - a.G) * k),
            (int)Math.Round(a.B + (b.B - a.B) * k));
    }
}
