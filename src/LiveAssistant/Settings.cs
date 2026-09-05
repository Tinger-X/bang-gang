using System.Text.Json;

namespace LiveAssistant;

/// <summary>全局主题色（运行时由设置刷新）。</summary>
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

    public static Font UI(float size, FontStyle st = FontStyle.Regular) => new("Microsoft YaHei UI", size, st);
    public static Font Mono(float size) => new("Consolas", size);
}

/// <summary>单个快捷键绑定。</summary>
public class ShortcutSetting
{
    public string Action { get; set; } = "";   // "hide"|"shot"|"record"
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public int Vk { get; set; }

    public string Label
    {
        get
        {
            string s = "";
            if (Ctrl) s += "Ctrl+";
            if (Alt) s += "Alt+";
            if (Shift) s += "Shift+";
            s += VkToName(Vk);
            return s;
        }
    }

    public uint Modifiers()
    {
        uint m = 0;
        if (Ctrl) m |= 0x0002;   // MOD_CONTROL
        if (Alt) m |= 0x0001;    // MOD_ALT
        if (Shift) m |= 0x0004;  // MOD_SHIFT
        return m;
    }

    public static string VkToName(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        var k = (Keys)vk;
        return k switch
        {
            Keys.F1 => "F1", Keys.F2 => "F2", Keys.F3 => "F3", Keys.F4 => "F4",
            Keys.F5 => "F5", Keys.F6 => "F6", Keys.F7 => "F7", Keys.F8 => "F8",
            Keys.F9 => "F9", Keys.F10 => "F10", Keys.F11 => "F11", Keys.F12 => "F12",
            _ => "?"
        };
    }

    public static bool IsUsable(int vk, bool anyMod) =>
        (vk >= 0x41 && vk <= 0x5A) || (vk >= 0x30 && vk <= 0x39) || vk is >= 0x70 and <= 0x7B;
}

/// <summary>应用设置（System.Text.Json 持久化）。</summary>
public class AppSettings
{
    public List<ShortcutSetting> Shortcuts { get; set; } = DefaultShortcuts();
    public string ChatApiUrl { get; set; } = "";
    public string ChatApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "";
    public string SttApiUrl { get; set; } = "";
    public string SttApiKey { get; set; } = "";
    public string SttModel { get; set; } = "";
    public int ChatBg { get; set; } = Color.White.ToArgb();
    public int SideBg { get; set; } = Color.FromArgb(246, 248, 251).ToArgb();
    public int PanelBg { get; set; } = Color.FromArgb(252, 253, 255).ToArgb();
    public int TextColor { get; set; } = Color.FromArgb(30, 34, 40).ToArgb();
    public int TextMutedColor { get; set; } = Color.FromArgb(120, 128, 138).ToArgb();
    public int Accent { get; set; } = Color.FromArgb(47, 112, 224).ToArgb();
    public double Opacity { get; set; } = 1.0;

    public static List<ShortcutSetting> DefaultShortcuts() => new()
    {
        new ShortcutSetting { Action = "hide", Alt = true, Vk = 0x58 },
        new ShortcutSetting { Action = "shot", Alt = true, Vk = 0x43 },
        new ShortcutSetting { Action = "record", Alt = true, Vk = 0x56 },
    };

    public void ApplyTheme()
    {
        Theme.ChatBg = Color.FromArgb(ChatBg);
        Theme.SideBg = Color.FromArgb(SideBg);
        Theme.PanelBg = Color.FromArgb(PanelBg);
        Theme.TextMain = Color.FromArgb(TextColor);
        Theme.TextMuted = Color.FromArgb(TextMutedColor);
        Theme.Accent = Color.FromArgb(Accent);
        Theme.UserBubble = Blend(Theme.Accent, Color.White, 0.88f);
        Theme.AsstBubble = Color.FromArgb(240, 242, 246);
        Theme.InputBg = Theme.PanelBg;
        Theme.Border = Color.FromArgb(224, 229, 236);
        Theme.WindowOpacity = Math.Clamp(Opacity, 0.5, 1.0);
    }

    private static Color Blend(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R * k + b.R * (1 - k)), (int)(a.G * k + b.G * (1 - k)), (int)(a.B * k + b.B * (1 - k)));

    public static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (s != null)
                {
                    if (s.Shortcuts == null || s.Shortcuts.Count == 0) s.Shortcuts = DefaultShortcuts();
                    s.ApplyTheme();
                    return s;
                }
            }
        }
        catch { }
        var d = new AppSettings();
        d.ApplyTheme();
        return d;
    }

    public void Save()
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* 目录只读时忽略 */ }
    }

    public void CopyFrom(AppSettings o)
    {
        Shortcuts = o.Shortcuts.Select(x => new ShortcutSetting { Action = x.Action, Ctrl = x.Ctrl, Alt = x.Alt, Shift = x.Shift, Vk = x.Vk }).ToList();
        ChatApiUrl = o.ChatApiUrl; ChatApiKey = o.ChatApiKey; ChatModel = o.ChatModel;
        SttApiUrl = o.SttApiUrl; SttApiKey = o.SttApiKey; SttModel = o.SttModel;
        ChatBg = o.ChatBg; SideBg = o.SideBg; PanelBg = o.PanelBg;
        TextColor = o.TextColor; TextMutedColor = o.TextMutedColor; Accent = o.Accent; Opacity = o.Opacity;
    }
}
