using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>界面外观设置页：应用主题（亮色 / 暗色 / 跟随系统）、主色、窗口选项。</summary>
internal sealed class UiPage : SettingsPage
{
    private static readonly Color[] AccentPresets =
    {
        Color.FromArgb(47, 112, 224),
        Color.FromArgb(124, 92, 226),
        Color.FromArgb(16, 152, 132),
        Color.FromArgb(232, 122, 44),
        Color.FromArgb(214, 60, 54),
    };

    private static readonly string[] Modes = { "亮色", "暗色", "跟随系统" };

    private readonly SegmentedControl _mode = new(Modes, 34);
    private readonly SwatchChip _accent = new(112);
    private readonly SliderBar _opacity = new(300);
    private readonly ToggleSwitch _border = new();
    private readonly ColorDotPicker _dots = new(AccentPresets);

    private string _bMode = "system";
    private int _bAccent;
    private int _bOpacityPct;
    private bool _bBorder;

    private static int Pct(double v) => (int)Math.Round(Math.Clamp(v, 0.5, 1.0) * 100);

    public UiPage() : base("界面外观", "主题、主色与窗口效果，保存后立即应用到整个应用")
    {
        ResetContent();

        // ---- 主题配色 ----
        var theme = new GroupCard("主题配色", "选择应用主题与主色");
        _mode.Changed += MarkChanged;

        // 预设色球：选中用一块会滑动的背景表示；自定义颜色时选中背景自动隐藏
        _dots.Changed += () =>
        {
            _accent.Value = _dots.Value;
            MarkChanged();
        };
        _accent.Changed += () =>
        {
            // 用户用取色器选了自定义颜色：只有刚好等于某个预设时才点亮对应色球
            _dots.SetValue(_accent.Value, raise: false);
            MarkChanged();
        };

        var accents = new Panel { Size = new Size(_dots.Width + 6 + 112, 32), BackColor = SC.GroupBg };
        accents.Controls.Add(_dots);
        _dots.Location = new Point(0, 0);
        accents.Controls.Add(_accent);
        _accent.Location = new Point(_dots.Width + 6, 0);

        theme.Add(new SettingRow("应用主题", "跟随系统随 Windows 自动切换", _mode));
        theme.Add(new SettingRow("主色", "按钮、选中态与强调文字", accents));
        theme.Height = theme.MeasureHeight();
        Stack.Controls.Add(theme);

        // ---- 窗口 ----
        _opacity.Changed += MarkChanged;
        _border.Changed += MarkChanged;
        var win = new GroupCard("窗口", "透明度只影响观感，不影响防录屏与热键");
        win.Add(new SettingRow("不透明度", "50% – 100%，调整后依然对录屏不可见", _opacity));
        win.Add(new SettingRow("窗口边框", "在窗口四周显示一圈主题色边框", _border));
        win.Height = win.MeasureHeight();
        Stack.Controls.Add(win);

        FinishContent();
    }

    private static int ModeIndex(string mode) => mode switch { "light" => 0, "dark" => 1, _ => 2 };
    private static string ModeValue(int idx) => idx switch { 0 => "light", 1 => "dark", _ => "system" };

    public override void Rebind(AppSettings s)
    {
        // 先落基线，再写控件值
        _bMode = s.ThemeMode; _bAccent = s.Accent; _bOpacityPct = Pct(s.Opacity); _bBorder = s.WindowBorder;

        _mode.Select(ModeIndex(s.ThemeMode), false);
        _accent.Value = Color.FromArgb(s.Accent);
        _dots.SetValue(Color.FromArgb(s.Accent), raise: false);
        _opacity.Value = _bOpacityPct;
        _border.On = s.WindowBorder;
        MarkClean();
    }

    public override void ApplyTo(AppSettings target)
    {
        target.ThemeMode = ModeValue(_mode.SelectedIndex);
        target.Accent = _accent.Value.ToArgb();
        target.Opacity = _opacity.Value / 100.0;
        target.WindowBorder = _border.On;
    }
    protected override bool ComputeDirty() =>
        ModeValue(_mode.SelectedIndex) != _bMode ||
        _accent.Value.ToArgb() != _bAccent ||
        _opacity.Value != _bOpacityPct ||
        _border.On != _bBorder;
}
