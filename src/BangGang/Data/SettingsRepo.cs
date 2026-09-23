using System.Globalization;
using System.Text.Json;

namespace BangGang;

/// <summary>
/// <see cref="AppSettings"/> 与 <c>settings</c> 表之间的读写，以及敏感字段的加解密。
///
/// **核心是一张字段表 <see cref="Fields"/>。** 它同时驱动三件事：落库、读回、以及
/// <see cref="AppSettings.CopyFrom"/> 的逐字段拷贝。加一个设置项只需要往表里加一行，
/// 三处一起就有了 —— 而原来那套手写的 `CopyFrom` 漏一行是**静默**的（注释里自己都写着
/// 「新加字段不写进来就永远拷不过去」，症状是「设置里改了没反应」）。
///
/// 敏感字段的判据**只有一条**：<see cref="Models"/> 之外，见
/// <c>Providers.ProviderField.Secret</c> 的对应关系 —— 这里就是表里的 `Secret` 那一列。
/// 别在别处再立一份名单，两份早晚分叉。
///
/// 加密粒度是**整块**（整个 <c>ChatProfiles</c> 字典的 JSON 一次加密），不拆到每个 key。
/// 理由是拆开只能多保护「服务商名字」这种本就公开的信息，却要让那两个嵌套字典的读写
/// 复杂一大截。整块加密的代价只有一个，而且已经被处理掉了：**DPAPI 的密文每次都不同**
/// （随机盐），所以「设置有没有改过」绝不能拿密文比 —— 密文只在落库那一刻产生，
/// 比较一律发生在内存里的明文上。
/// </summary>
internal static class SettingsRepo
{
    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = false };

    /// <summary>一条设置项。</summary>
    internal readonly record struct Field(
        string Key, bool Secret, Func<AppSettings, string> Get, Action<AppSettings, string> Set);

    private static Field S(string key, Func<AppSettings, string?> get, Action<AppSettings, string> set, bool secret = false)
        => new(key, secret, s => get(s) ?? "", (s, v) => set(s, v));

    private static Field B(string key, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
        => new(key, false, s => get(s) ? "1" : "0", (s, v) => set(s, v == "1"));

    private static Field N(string key, Func<AppSettings, int> get, Action<AppSettings, int> set)
        => new(key, false, s => get(s).ToString(CultureInfo.InvariantCulture),
                        (s, v) => set(s, int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0));

    private static Field D(string key, Func<AppSettings, double> get, Action<AppSettings, double> set)
        => new(key, false, s => get(s).ToString("R", CultureInfo.InvariantCulture),
                        (s, v) => set(s, double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0));

    /// <summary>值为对象/集合的设置项（存成一段 JSON）。</summary>
    private static Field J<T>(string key, Func<AppSettings, T> get, Action<AppSettings, T> set, bool secret = false)
        => new(key, secret,
               s => JsonSerializer.Serialize(get(s), JsonOpt),
               (s, v) =>
               {
                   try
                   {
                       var parsed = JsonSerializer.Deserialize<T>(v);
                       if (parsed != null) set(s, parsed);
                   }
                   catch { /* 存坏了就保持默认值，不要让一行脏数据把整份设置拦住 */ }
               });

    /// <summary>
    /// 全部持久化字段。**加设置项就往这里加一行**，落库 / 读回 / <c>CopyFrom</c> 一起生效。
    /// 键名带点号只是为了打开数据库时好看、好认。
    /// </summary>
    internal static readonly Field[] Fields =
    {
        J("app.shortcuts",       s => s.Shortcuts,       (s, v) => s.Shortcuts = v),
        S("app.active_chat_id",  s => s.ActiveChatId,    (s, v) => s.ActiveChatId = v),

        S("chat.provider",       s => s.ChatProvider,    (s, v) => s.ChatProvider = v),
        J("chat.profiles",       s => s.ChatProfiles,    (s, v) => s.ChatProfiles = v, secret: true),
        B("chat.vision",         s => s.ChatVision,      (s, v) => s.ChatVision = v),
        D("chat.temperature",    s => s.ChatTemperature, (s, v) => s.ChatTemperature = v),
        N("chat.max_tokens",     s => s.ChatMaxTokens,   (s, v) => s.ChatMaxTokens = v),
        S("chat.system_prompt",  s => s.ChatSystemPrompt, (s, v) => s.ChatSystemPrompt = v),
        S("chat.reinforce",      s => s.ChatReinforce,   (s, v) => s.ChatReinforce = v),
        S("chat.context_mode",   s => s.ChatContextMode, (s, v) => s.ChatContextMode = v),
        N("chat.context_window", s => s.ChatContextWindow, (s, v) => s.ChatContextWindow = v),

        S("stt.provider",        s => s.SttProvider,     (s, v) => s.SttProvider = v),
        J("stt.profiles",        s => s.SttProfiles,     (s, v) => s.SttProfiles = v, secret: true),

        B("tools.enabled",       s => s.ToolsEnabled,    (s, v) => s.ToolsEnabled = v),
        B("tool.now",            s => s.ToolNow,         (s, v) => s.ToolNow = v),
        B("tool.calc",           s => s.ToolCalc,        (s, v) => s.ToolCalc = v),
        B("tool.clipboard",      s => s.ToolClipboard,   (s, v) => s.ToolClipboard = v),
        B("tool.file",           s => s.ToolFile,        (s, v) => s.ToolFile = v),
        B("tool.web_search",     s => s.ToolWebSearch,   (s, v) => s.ToolWebSearch = v),
        B("tool.web_fetch",      s => s.ToolWebFetch,    (s, v) => s.ToolWebFetch = v),
        B("tool.sysinfo",        s => s.ToolSysInfo,     (s, v) => s.ToolSysInfo = v),

        S("record.mode",         s => s.RecordMode,      (s, v) => s.RecordMode = v),

        S("ui.theme_mode",       s => s.ThemeMode,       (s, v) => s.ThemeMode = v),
        B("ui.window_border",    s => s.WindowBorder,    (s, v) => s.WindowBorder = v),
        N("ui.accent",           s => s.Accent,          (s, v) => s.Accent = v),
        D("ui.opacity",          s => s.Opacity,         (s, v) => s.Opacity = v),
        N("ui.chat_bg",          s => s.ChatBg,          (s, v) => s.ChatBg = v),
        N("ui.side_bg",          s => s.SideBg,          (s, v) => s.SideBg = v),
        N("ui.panel_bg",         s => s.PanelBg,         (s, v) => s.PanelBg = v),
        N("ui.text_color",       s => s.TextColor,       (s, v) => s.TextColor = v),
        N("ui.text_muted",       s => s.TextMutedColor,  (s, v) => s.TextMutedColor = v),
    };

    /// <summary>本次运行里解不开的敏感字段（换机器 / 换 Windows 账户后的预期现象）。</summary>
    private static readonly HashSet<string> Undecryptable = new(StringComparer.Ordinal);

    /// <summary>有没有解不开的敏感字段 —— 界面据此提示「API Key 需要重新填一次」。</summary>
    public static bool HasUndecryptable
    {
        get { lock (Undecryptable) return Undecryptable.Count > 0; }
    }

    public static AppSettings Load()
    {
        var s = new AppSettings();
        if (!Db.Available) return s;

        try
        {
            var rows = new Dictionary<string, (string Value, bool Secret)>(StringComparer.Ordinal);
            using (var st = Db.Conn.Prepare("SELECT key, value, secret FROM settings"))
                while (st.Step()) rows[st.Text(0)] = (st.Text(1), st.Int64(2) != 0);

            foreach (var f in Fields)
            {
                if (!rows.TryGetValue(f.Key, out var row)) continue;

                string value = row.Value;
                if (f.Secret)
                {
                    if (!Dpapi.TryUnprotect(value, out string plain))
                    {
                        // 解不开：**内存里当空的**，但原文留在库里不动（写回时见 Save）。
                        lock (Undecryptable) Undecryptable.Add(f.Key);
                        continue;
                    }
                    value = plain;
                }
                f.Set(s, value);
            }
        }
        catch { /* 读不动就用默认值起，不让一份坏设置拦住程序 */ }

        return s;
    }

    /// <summary>整份写回。只在「用户点了保存」这条路上调用。</summary>
    public static void Save(AppSettings s)
    {
        if (!Db.Available) return;
        try
        {
            var db = Db.Conn;
            db.Begin();
            try
            {
                foreach (var f in Fields) Write(db, f, s);
                db.Commit();
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }
        catch { /* 存不下就算了 */ }
    }

    /// <summary>
    /// 只写「上次开着哪条会话」这一个键。
    ///
    /// 单独开一条路是因为 <c>MainForm.RememberActive()</c> **每次切换会话都会写一次** ——
    /// 走整份写回的话，每点一下都要重新 DPAPI 加密那两个密钥块（毫秒级），
    /// 白白拖慢一次点击。
    /// </summary>
    public static void SaveActiveChat(string id)
    {
        if (!Db.Available) return;
        try
        {
            var f = Fields[1];
            if (f.Key != "app.active_chat_id") return;   // 表被改乱了就宁可什么都不写
            var db = Db.Conn;
            db.Begin();
            try
            {
                using (var st = db.Prepare("INSERT OR REPLACE INTO settings(key, value, secret) VALUES(?,?,0)"))
                {
                    st.Bind(1, f.Key).Bind(2, id ?? "");
                    st.Step();
                }
                db.Commit();
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }
        catch { }
    }

    private static void Write(SqliteDb db, Field f, AppSettings s)
    {
        string value = f.Get(s);

        // 解不开的密钥：**保留库里的密文，不写回**。
        // 不这么做的话，换机器后第一次保存就会把用户原来的密文抹成空串 ——
        // 而它本来只是「这台机器上解不开」，拿回原机器是好的。用户重新填一个新 Key
        // （值非空）就会正常写进去，所以这是一条会自愈的规则。
        // 副作用：这种情况下「把 Key 清空」不会生效。宁可如此。
        if (f.Secret && value.Length == 0)
        {
            lock (Undecryptable) if (Undecryptable.Contains(f.Key)) return;
        }

        bool secret = f.Secret && value.Length > 0;
        if (secret) value = Dpapi.Protect(value);

        using var st = db.Prepare("INSERT OR REPLACE INTO settings(key, value, secret) VALUES(?,?,?)");
        st.Bind(1, f.Key).Bind(2, value).Bind(3, secret);
        st.Step();
    }
}
