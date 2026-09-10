using System.Text.Json;
using Microsoft.Win32;

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

    // ---------- 对话模型（OpenAI 兼容 / 多模态） ----------
    /// <summary>当前选中的服务商（对应 <see cref="Providers.Chat"/> 中的名称）。</summary>
    public string ChatProvider { get; set; } = "自定义";
    /// <summary>每个服务商各自的参数档位（providerName -> { key -> value }），切换服务商不会丢配置。</summary>
    public Dictionary<string, Dictionary<string, string>> ChatProfiles { get; set; } = new();
    /// <summary>模型是否支持图片等多媒体输入（多模态）。</summary>
    public bool ChatVision { get; set; } = true;

    // ---------- 实时语音转写（通用流式 STT） ----------
    public string SttProvider { get; set; } = "自定义";
    public Dictionary<string, Dictionary<string, string>> SttProfiles { get; set; } = new();

    /// <summary>录音方式："hold" 按住录音 / "toggle" 按一下开始、再按一下停止。</summary>
    public string RecordMode { get; set; } = "hold";

    // 旧版扁平字段：仅用于兼容旧 settings.json，加载时会迁移到“自定义”档位
    public string ChatApiUrl { get; set; } = "";
    public string ChatApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "";
    public string SttApiUrl { get; set; } = "";
    public string SttAppId { get; set; } = "";
    public string SttApiKey { get; set; } = "";
    public string SttModel { get; set; } = "";

    /// <summary>取某个服务商的参数档位（不存在则创建）。</summary>
    public Dictionary<string, string> ProfileOf(bool chat, string provider)
    {
        var map = chat ? ChatProfiles : SttProfiles;
        if (!map.TryGetValue(provider, out var p) || p == null)
        {
            p = new Dictionary<string, string>();
            map[provider] = p;
        }
        return p;
    }

    /// <summary>把旧版扁平字段迁移为“自定义”档位。</summary>
    private void MigrateLegacyProfiles()
    {
        ChatProfiles ??= new();
        SttProfiles ??= new();
        if (ChatProfiles.Count == 0 &&
            (ChatApiUrl.Length > 0 || ChatApiKey.Length > 0 || ChatModel.Length > 0))
        {
            ChatProfiles["自定义"] = new Dictionary<string, string>
            {
                ["url"] = ChatApiUrl,
                ["key"] = ChatApiKey,
                ["model"] = ChatModel,
            };
        }
        if (SttProfiles.Count == 0 &&
            (SttApiUrl.Length > 0 || SttAppId.Length > 0 || SttApiKey.Length > 0 || SttModel.Length > 0))
        {
            SttProfiles["自定义"] = new Dictionary<string, string>
            {
                ["url"] = SttApiUrl,
                ["appid"] = SttAppId,
                ["key"] = SttApiKey,
                ["model"] = SttModel,
            };
        }
        if (string.IsNullOrWhiteSpace(ChatProvider)) ChatProvider = "自定义";
        if (string.IsNullOrWhiteSpace(SttProvider)) SttProvider = "自定义";
    }

    // ---------- 外观 ----------
    /// <summary>"system" | "light" | "dark"</summary>
    public string ThemeMode { get; set; } = "system";
    /// <summary>是否显示主题色窗口边框。</summary>
    public bool WindowBorder { get; set; } = true;
    public int Accent { get; set; } = Color.FromArgb(47, 112, 224).ToArgb();
    public double Opacity { get; set; } = 1.0;

    // 旧版字段：仅作为“解析后调色板”的快照保留，界面不再单独编辑
    public int ChatBg { get; set; } = Color.White.ToArgb();
    public int SideBg { get; set; } = Color.FromArgb(246, 248, 251).ToArgb();
    public int PanelBg { get; set; } = Color.FromArgb(252, 253, 255).ToArgb();
    public int TextColor { get; set; } = Color.FromArgb(30, 34, 40).ToArgb();
    public int TextMutedColor { get; set; } = Color.FromArgb(120, 128, 138).ToArgb();

    public static List<ShortcutSetting> DefaultShortcuts() => new()
    {
        new ShortcutSetting { Action = "hide", Alt = true, Vk = 0x58 },
        new ShortcutSetting { Action = "shot", Alt = true, Vk = 0x43 },
        new ShortcutSetting { Action = "record", Alt = true, Vk = 0x56 },
    };

    /// <summary>系统是否使用亮色应用主题（“跟随系统”时读取）。</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* 读不到按亮色处理 */ }
        return true;
    }

    public bool ResolveDark() => ThemeMode switch
    {
        "dark" => true,
        "light" => false,
        _ => !SystemUsesLightTheme(),
    };

    /// <summary>按主题模式刷新整套调色板。</summary>
    public void ApplyTheme()
    {
        bool dark = ResolveDark();
        Theme.Dark = dark;

        Color chat, side, panel, text, muted, border, input, asst, user;
        if (dark)
        {
            chat = Color.FromArgb(23, 25, 29);
            side = Color.FromArgb(29, 32, 37);
            panel = Color.FromArgb(34, 37, 43);
            text = Color.FromArgb(232, 235, 239);
            muted = Color.FromArgb(150, 158, 168);
            border = Color.FromArgb(52, 58, 66);
            input = Color.FromArgb(43, 47, 54);
            asst = Color.FromArgb(40, 44, 50);
            user = Theme.Mix(chat, Theme.Accent, 0.34f);
        }
        else
        {
            chat = Color.White;
            side = Color.FromArgb(246, 248, 251);
            panel = Color.FromArgb(252, 253, 255);
            text = Color.FromArgb(30, 34, 40);
            muted = Color.FromArgb(120, 128, 138);
            border = Color.FromArgb(224, 229, 236);
            input = panel;
            asst = Color.FromArgb(240, 242, 246);
            user = Theme.Mix(Color.White, Theme.Accent, 0.16f);
        }

        Theme.ChatBg = chat;
        Theme.SideBg = side;
        Theme.PanelBg = panel;
        Theme.HeaderBg = Theme.Mix(panel, Theme.Accent, dark ? 0.08f : 0.05f);
        Theme.TextMain = text;
        Theme.TextMuted = muted;
        Theme.Border = border;
        Theme.InputBg = input;
        Theme.AsstBubble = asst;
        Theme.Accent = Color.FromArgb(Accent);
        Theme.UserBubble = user;
        Theme.WindowOpacity = Math.Clamp(Opacity, 0.5, 1.0);
        Theme.WindowBorder = WindowBorder;

        // 调色板快照（写入 settings.json 便于排查；界面不再单独编辑）
        ChatBg = chat.ToArgb();
        SideBg = side.ToArgb();
        PanelBg = panel.ToArgb();
        TextColor = text.ToArgb();
        TextMutedColor = muted.ToArgb();
    }

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
                    if (string.IsNullOrEmpty(s.ThemeMode)) s.ThemeMode = "system";
                    s.MigrateLegacyProfiles();
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
        ChatProvider = o.ChatProvider; ChatVision = o.ChatVision;
        ChatProfiles = o.ChatProfiles.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        SttProvider = o.SttProvider;
        SttProfiles = o.SttProfiles.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        ChatApiUrl = o.ChatApiUrl; ChatApiKey = o.ChatApiKey; ChatModel = o.ChatModel;
        SttApiUrl = o.SttApiUrl; SttAppId = o.SttAppId; SttApiKey = o.SttApiKey; SttModel = o.SttModel;
        RecordMode = o.RecordMode;
        ThemeMode = o.ThemeMode; WindowBorder = o.WindowBorder;
        Accent = o.Accent; Opacity = o.Opacity;
        ChatBg = o.ChatBg; SideBg = o.SideBg; PanelBg = o.PanelBg;
        TextColor = o.TextColor; TextMutedColor = o.TextMutedColor;
    }
}
