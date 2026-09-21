using System.Text.Json;

namespace BangGang;

/// <summary>
/// 一条会话的落盘结构。外面包一层而不是直接把 <see cref="Conversation"/> 序列化出去，
/// 是为了给版本号留个位置：将来字段的含义变了，能靠它分辨新旧文件。
/// </summary>
internal sealed class ChatFile
{
    public int Version { get; set; } = 1;
    public Conversation? Conversation { get; set; }
}

/// <summary>
/// 0.8.1 及以前「所有会话挤在一个 conversations.json 里」的格式。
///
/// 现在只剩一个用途：升级后第一次启动时把老数据搬进 <see cref="ChatStore.Dir"/>，
/// 搬完这个类型就没用了。留着它，是为了不让用户已经聊出来的历史因为换了个存放方式
/// 就消失（见 <see cref="ChatStore.Load"/>）。
/// </summary>
internal sealed class LegacyStoreFile
{
    public int Version { get; set; } = 1;
    public string? ActiveId { get; set; }
    public List<Conversation>? Conversations { get; set; }
}

/// <summary>
/// 会话的本地持久化：**一条会话一个文件**，都放 <c>&lt;BaseDirectory&gt;\chats\</c>（和设置放一起），
/// 文件名就是会话的 id，重启后照原样接着聊。
///
/// 四条约定：
///
/// 1. **文件名说了算**。id 是要拼进路径的，而文件里的那个 <c>Id</c> 字段是磁盘上的内容 ——
///    谁都能往里写 <c>"../../x"</c>。所以读取时一律认文件名，并且只认字符集合法、长度合适的
///    文件名（<see cref="IsId"/>），别的一概跳过；写的时候也只写这种 id。这样「文件内容
///    指使程序往目录外写文件」这件事从根上不成立。
/// 2. **没有消息的会话不占文件**。空会话是「点了加号但还没开口」的产物，它只在内存里活着 ——
///    真要写下去，重启一次就多一个文件，用户点几下加号，列表里就攒一串「新对话」永远删不完。
///    同理，正文和附件都为空的助手消息也不写：那是流式回复还没落地时的占位气泡，程序中途
///    被杀掉才会留着它，写进去只会让下一次启动冒出一个空气泡。所以 <see cref="Save"/> 在
///    发现一条会话已经空了时会**把它的文件删掉** —— 规则是「文件存在 ⟺ 有内容」。
/// 3. **先写临时文件再改名**。直接覆盖原文件的话，写到一半断电 / 被杀，留下的是一个截断的
///    JSON —— 那一条会话就读不回来了。改名在同一分区上是原子的，最坏也只是丢掉这一次的新增。
/// 4. 反序列化回来的东西**当成外来的**：字段可能缺失、可能是 null（见 <see cref="Attachment"/>，
///    <c>"Text": null</c> 能绕过声明的非空），逐个补齐再用。
///
/// 附件（<see cref="ImageDir"/> 里的图）**不跟着会话一起删** —— 删会话时只删会话文件。
/// </summary>
internal static class ChatStore
{
    /// <summary>会话目录：一个文件一条会话，文件名即会话 id。</summary>
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "chats");

    /// <summary>0.8.1 的单文件存档位置，只在 <see cref="TryImportLegacy"/> 里读一次。</summary>
    private static string LegacyPath => Path.Combine(AppContext.BaseDirectory, "conversations.json");

    /// <summary>
    /// 应用自己产生的图片（粘贴的、截图的）落盘的目录。
    ///
    /// 特意**不**放 <c>%TEMP%</c>：那里的文件随时可能被系统或清理工具删掉，而它现在是
    /// 一条会话消息的附件 —— 重启之后历史还在、图片却读不出来了，用户看到的是一个个空槽。
    ///
    /// 只增不删：删会话**不**回收它的图。同一张图可能被多条消息引用着（附件里存的是路径，
    /// 没有归属关系），按引用删早晚会删到还在用的那张 —— 那是不可逆的，而留着一张孤儿图
    /// 只是占几 KB 磁盘。真要清理，得先给附件加引用计数，不能靠猜。
    /// </summary>
    public static string ImageDir => Path.Combine(AppContext.BaseDirectory, "images");

    /// <summary>取一个还没被占用的图片路径（目录不存在就建）。</summary>
    public static string NewImagePath(string prefix)
    {
        Directory.CreateDirectory(ImageDir);
        return Path.Combine(ImageDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
    }

    private static readonly JsonSerializerOptions WriteOpt = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpt = new() { PropertyNameCaseInsensitive = true };

    /// <summary>id 能不能直接当文件名用。字符集限定在 <c>[0-9A-Za-z_-]</c>，于是 <c>.</c>、
    /// <c>\</c>、<c>/</c> 都进不来，「../ 跑到目录外面」也就无从谈起。
    /// <see cref="Conversation.Id"/> 生成的 Guid("N") 是 32 位十六进制，天然落在这个集合里。</summary>
    private static bool IsId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        return true;
    }

    private static string PathFor(string id) => Path.Combine(Dir, id + ".json");

    /// <summary>
    /// 这条会话里真正值得写下去的消息。空会话（一条都没有）返回空表。
    ///
    /// 「值得」的判据在 <see cref="ChatMessage.IsEmpty"/> 里 —— 那里把**思考过程**
    /// 也算作内容：暂停在一轮思考中途、或者长度上限全被思考吃掉时，正文是空的而
    /// 思考是满的，用户明明看见了一整屏字，只按正文判就等于凭空吃掉这一轮。
    /// </summary>
    private static List<ChatMessage> Persistable(Conversation c) =>
        c.Messages.Where(m => m != null && !m.IsEmpty).ToList();

    /// <summary>
    /// 把一条会话写进它自己的文件。
    ///
    /// 调用点都在「这条会话的内容有了一次定稿」的地方 —— 用户发了消息、贴了一条助手消息、
    /// 一期回复收尾 —— 而不是流式刷新的每一帧上：一期回复几十帧，写几十次盘没有意义。
    /// </summary>
    public static void Save(Conversation c)
    {
        if (!IsId(c.Id)) return;

        // 没内容的会话不配有自己的文件；已经有文件的（内容被删光了、或者刚立起空气泡就被
        // 杀掉）那就把文件收走 —— 不变量是「文件在 ⟺ 里面有内容」。
        var msgs = Persistable(c);
        if (msgs.Count == 0) { Delete(c.Id); return; }

        try
        {
            var f = new ChatFile
            {
                Conversation = new Conversation
                {
                    Id = c.Id,
                    Title = c.Title,
                    TitleLocked = c.TitleLocked,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    Messages = msgs,
                },
            };
            Directory.CreateDirectory(Dir);
            string tmp = PathFor(c.Id) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(f, WriteOpt));
            File.Move(tmp, PathFor(c.Id), overwrite: true);
        }
        catch { /* 存不下就算了：这是「记住历史」，不该把正在进行的对话打断 */ }
    }

    /// <summary>
    /// 删掉一条会话的文件（连带上次写到一半留下的临时文件）。
    /// **附件不删**，理由见 <see cref="ImageDir"/>。
    /// </summary>
    public static void Delete(string id)
    {
        if (!IsId(id)) return;
        try
        {
            string p = PathFor(id);
            if (File.Exists(p)) File.Delete(p);
            string tmp = p + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch { /* 删不掉（文件被占用 / 没权限）就算了，下次 Save 会把它覆盖掉 */ }
    }

    /// <summary>
    /// 读出全部历史会话。目录不在 / 单个文件读不动 / 解析失败都跳过 ——
    /// 一份坏掉的存档不该拦住程序启动，也不该连累别的会话。
    ///
    /// <paramref name="LegacyActiveId"/> 只有一种情况会非空：这次启动刚刚把 0.8.1 的单文件
    /// 存档搬进 <c>chats/</c>，顺手把「当时开着哪条」也带了回来。平时它是 null ——
    /// 新布局里没有这个指针，它归 <see cref="AppSettings.ActiveChatId"/> 管。
    /// </summary>
    public static (List<Conversation> List, string? LegacyActiveId) Load()
    {
        var list = ReadAll();
        if (list.Count > 0) return (list, null);

        // chats/ 是空的：可能只是没聊过，也可能是刚从 0.8.1 升上来。后者要把老存档搬过来。
        string? active = TryImportLegacy();
        if (active == null) return (list, null);
        return (ReadAll(), active);
    }

    private static List<Conversation> ReadAll()
    {
        var list = new List<Conversation>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (string path in Directory.GetFiles(Dir))
            {
                // 临时文件（xxx.json.tmp）的扩展名不是 .json，这里天然被排除；
                // 再挡一道 IsId，别的杂七杂八的文件也一并跳过去。
                if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) continue;
                string id = Path.GetFileNameWithoutExtension(path);
                if (!IsId(id)) continue;
                var c = ReadOne(path, id);
                if (c != null) list.Add(c);
            }
        }
        catch { /* 目录读不动就当没有历史 */ }
        return list;
    }

    private static Conversation? ReadOne(string path, string id)
    {
        try
        {
            var f = JsonSerializer.Deserialize<ChatFile>(File.ReadAllText(path), ReadOpt);
            var c = f?.Conversation;
            if (c == null) return null;
            c.Id = id;                      // 文件名说了算，见类注释第 1 条
            Coerce(c);
            // 空的会话文件不当历史（正常不会出现，是上一版或手改留下的）。跳过而不删：
            // 读取路径不该动磁盘 —— 判断错了就是直接吃掉用户的一份聊天记录。
            if (Persistable(c).Count == 0) return null;
            return c;
        }
        catch { return null; }
    }

    /// <summary>把反序列化回来的东西补齐成能直接用的样子（见类注释第 4 条）。</summary>
    private static void Coerce(Conversation c)
    {
        if (c.Title == null) c.Title = "新对话";
        if (c.Messages == null) c.Messages = new();
        foreach (var m in c.Messages)
        {
            m.Text = m.Text ?? "";
            // 思考过程和截断说明同样可能是 JSON 里的 null：反序列化不看非空声明，
            // 而气泡那边（以及文本发回模型那条路）都当它们是普通字符串在用。
            m.Reasoning = m.Reasoning ?? "";
            m.Warning = m.Warning ?? "";
            if (m.Attachments == null) m.Attachments = new();
            foreach (var a in m.Attachments)
            {
                a.Name = a.Name ?? "";
                a.Kind = string.IsNullOrEmpty(a.Kind) ? "file" : a.Kind;
            }
        }
    }

    /// <summary>
    /// 把 0.8.1 的单文件存档拆成一条一个文件。返回当时开着的那条会话的 id（没得搬就返回 null）。
    ///
    /// 只有 <c>chats/</c> 里一条会话都没有时才会走这里 —— 也就是「换过来之后第一次启动」。
    /// 搬完把老文件删掉：内容已经按新规则安置好了，留着只会在下次启动再搬一遍。
    /// 写文件失败（磁盘满 / 没权限）时**不删**老文件，宁可下次启动再试一次，也不能把
    /// 用户的聊天记录变成孤儿。
    /// </summary>
    private static string? TryImportLegacy()
    {
        try
        {
            if (!File.Exists(LegacyPath)) return null;
            var f = JsonSerializer.Deserialize<LegacyStoreFile>(File.ReadAllText(LegacyPath), ReadOpt);
            var convs = f?.Conversations;
            if (convs == null || convs.Count == 0) return null;

            var keep = new List<Conversation>();
            foreach (var c in convs)
            {
                if (c == null) continue;
                if (string.IsNullOrEmpty(c.Id) || !IsId(c.Id)) c.Id = Guid.NewGuid().ToString("N");
                Coerce(c);
                if (Persistable(c).Count == 0) continue;   // 空会话照旧不落盘，不为迁移破例
                Save(c);
                keep.Add(c);
            }

            if (!keep.All(c => File.Exists(PathFor(c.Id)))) return null;   // 没搬干净，留着下次再来
            File.Delete(LegacyPath);
            Trace.Log($"chat store: imported {keep.Count} conversation(s) into chats/");
            return f!.ActiveId;
        }
        catch { return null; }
    }
}
