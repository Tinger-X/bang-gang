using System.Text.Json;
using Microsoft.Win32;

namespace BangGang;

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

/// <summary>
/// 应用设置。持久化在 SQLite 的 <c>settings</c> 表里：一项一行，敏感项走 DPAPI 加密
/// （见 <see cref="SettingsRepo"/>）。
///
/// 内存里这份**永远是明文** —— 加解密只发生在读写库的那两个边界上。
/// 这样上层（<c>LlmConfig.From</c>、各个设置页、语音那套）一行都不用改，
/// 也就不会出现「某处拿到的是密文」这种事。
/// </summary>
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

    // ---------- 实时语音转写（火山 / 讯飞，见 Providers.Stt） ----------
    /// <summary>默认选第一个内置商家：语音侧没有「自定义」档（协议按服务商名分派，自定义无从实现）。</summary>
    public string SttProvider { get; set; } = "火山引擎（流式）";
    public Dictionary<string, Dictionary<string, string>> SttProfiles { get; set; } = new();

    // ---------- 对话参数（每次请求都带上，见 ChatPage） ----------
    /// <summary>采样温度，0–2。越低越确定，越高越发散。</summary>
    public double ChatTemperature { get; set; } = 0.7;
    /// <summary>单次回复的 token 上限；<b>0 = 不限</b>（请求不带 max_tokens，见 <see cref="LlmConfig.MaxTokens"/>）。</summary>
    public int ChatMaxTokens { get; set; } = 2048;
    /// <summary>出厂时的系统提示词；设置页的「恢复默认」也回到这一段。</summary>
    public const string DefaultSystemPrompt = "你是帮帮，运行在用户 Windows 桌面上的 AI 助手。回答简洁准确，默认使用中文。";
    /// <summary>系统提示词（每次对话都放在最前面）。留空表示不发送这一段。</summary>
    public string ChatSystemPrompt { get; set; } = DefaultSystemPrompt;
    /// <summary>强化信息：附在每次提问之后，用来把模型拉回当前话题。留空表示不发送。</summary>
    public string ChatReinforce { get; set; } = "";

    /// <summary>
    /// 上下文处理方式：<c>"latest"</c> = 丢弃最老的消息（滑窗）；<c>"compact"</c> = 用模型把
    /// 早期对话压成一段摘要。默认压缩 —— 滑窗丢掉的是一整段事实，摘要把它们留了下来。
    /// </summary>
    public string ChatContextMode { get; set; } = "compact";

    /// <summary>
    /// 模型的上下文窗口（token）。**默认取小不取大**，因为失败方向不对称：
    /// 填小了只是提前压缩、损失一点早期细节；**填大了会被接口直接拒绝，整轮对话发不出去**。
    /// 所以不确定时宁可按小的填。可填范围见设置页的滑条（64K–1M）。
    /// 真值由每轮回来的 usage 校准（见 <c>Conversation.LastPromptTokens</c>）。
    /// </summary>
    public int ChatContextWindow { get; set; } = 128 * 1024;

    // ---------- 工具调用（见 Tools/，开关在 ToolsPage） ----------
    /// <summary>工具调用总开关。关掉之后下面那几个单开关一律失效（请求里根本不带 tools）。</summary>
    public bool ToolsEnabled { get; set; } = true;

    // 单个工具的开关。**默认全开** —— 装完就能用，不然用户会以为功能没做。
    // 字段名与工具的对应关系写在 <see cref="ToolRegistry.Enabled"/> 里，加工具时两处一起改。
    public bool ToolNow { get; set; } = true;
    public bool ToolCalc { get; set; } = true;
    public bool ToolClipboard { get; set; } = true;
    public bool ToolFile { get; set; } = true;
    public bool ToolWebSearch { get; set; } = true;
    public bool ToolWebFetch { get; set; } = true;
    public bool ToolSysInfo { get; set; } = true;

    /// <summary>录音方式："hold" 按住录音 / "toggle" 按一下开始、再按一下停止。</summary>
    public string RecordMode { get; set; } = "hold";

    /// <summary>
    /// 上次开着的那条会话的 id（空串 = 没有）。
    ///
    /// 和主题、透明度一样属于「应用自己的状态」，所以留在设置里、跟着设置一起落库，
    /// 而不是混进会话存储 —— 那边「一条会话一行」的约定不该为一个指针开特例。
    ///
    /// **不给它加指向 conversations.id 的外键**：它会合法地指向一条已被删掉的会话
    /// （<c>DeleteConversation</c> 之后到 <c>RememberActive()</c> 之间有一瞬），
    /// 加了外键就得在每条删除路径上补 NULL。
    /// </summary>
    public string ActiveChatId { get; set; } = "";

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

    // 旧版的七个扁平字段（ChatApiUrl / ChatApiKey / ChatModel / SttApiUrl / SttAppId /
    // SttApiKey / SttModel）**已经彻底删除，不存在兼容路径**。
    // 别再加回来：那三个 `*ApiKey` 曾经是全仓唯一一处明文存密钥的地方
    // （LlmConfig.From 拿它们兜底、LlmPage.ApplyTo 写它们），
    // 留着它们，「敏感信息一律加密」这句话就永远有个例外。

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

    /// <summary>
    /// 从库里读一份设置；库不可用（或还是空的）就给一份出厂设置。
    ///
    /// 下面这几个兜底留着：快捷方式空表、主题模式空串、上下文窗口非正数。
    /// 换到 SQLite 之后它们不再是「手改文件改坏了」的产物，而是「某一版写下去时就是空的」——
    /// 但兜底本身照样有价值，一版都不该少。
    /// </summary>
    public static AppSettings Load()
    {
        var s = SettingsRepo.Load();
        if (s.Shortcuts == null || s.Shortcuts.Count == 0) s.Shortcuts = DefaultShortcuts();
        if (string.IsNullOrEmpty(s.ThemeMode)) s.ThemeMode = "system";
        if (string.IsNullOrEmpty(s.ChatProvider)) s.ChatProvider = "自定义";
        if (string.IsNullOrEmpty(s.SttProvider)) s.SttProvider = "火山引擎（流式）";
        if (s.ChatContextWindow <= 0) s.ChatContextWindow = 128 * 1024;
        // 夹到设置页滑条的可选范围内。老库里那份是个更早版本的默认值（32K），
        // 不夹的话它在滑条上会显示成最左端、而内存里仍是 32K —— 界面和实际行为分家。
        s.ChatContextWindow = Math.Clamp(s.ChatContextWindow, 64 * 1024, 1024 * 1024);
        if (s.ChatContextMode != "latest" && s.ChatContextMode != "compact") s.ChatContextMode = "compact";
        s.ApplyTheme();
        return s;
    }

    /// <summary>整份写回。只在「用户点了保存」这条路上调用。</summary>
    public void Save() => SettingsRepo.Save(this);

    /// <summary>
    /// 只写「上次开着哪条会话」这一项。
    ///
    /// 单独开一条路是因为 <c>MainForm.RememberActive()</c> **每次切换会话都会写一次** ——
    /// 走整份写回的话，每点一下都要重新 DPAPI 加密那两个密钥块，白白拖慢一次点击。
    /// </summary>
    public void SaveActiveChat() => SettingsRepo.SaveActiveChat(ActiveChatId);

    /// <summary>
    /// 用设置界面里那份改好的设置覆盖当前这份。
    ///
    /// 实现是**照着 <see cref="SettingsRepo.Fields"/> 那张表逐字段拷**，而不是手写一长串赋值。
    /// 手写的那种，新加一个字段忘了写进来是**静默**的 —— 症状是「设置里改了、保存、
    /// 回来又变回去」，而原因藏在另一个文件的另一段代码里。照表拷就不会漏，而且新字段
    /// 只要登记进表就自动生效。
    ///
    /// 字符串进、字符串出这一趟不是白绕的：它顺带把集合类字段做了一次深拷贝
    /// （序列化 + 反序列化），不会出现两份设置共用同一个字典的隐患。
    ///
    /// <see cref="ActiveChatId"/> **故意跳过**：设置界面里没有这一项，浮窗手上那份是
    /// 打开浮窗那一刻抄的快照。照抄回来就等于「用户开着浮窗切了个会话，一点保存又被拽回去」。
    /// </summary>
    public void CopyFrom(AppSettings o)
    {
        foreach (var f in SettingsRepo.Fields)
        {
            if (f.Key == "app.active_chat_id") continue;
            f.Set(this, f.Get(o));
        }
    }
}
