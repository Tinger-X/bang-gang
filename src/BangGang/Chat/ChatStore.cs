using System.Text.Json;

namespace BangGang;

/// <summary>落盘文件的整体结构。单独一个类型而不是匿名对象，是为了让版本号有地方放。</summary>
internal sealed class ChatStoreFile
{
    public int Version { get; set; } = 1;
    public string? ActiveId { get; set; }
    public List<Conversation> Conversations { get; set; } = new();
}

/// <summary>
/// 会话的本地持久化：把对话内容和设置放一起（<c>AppContext.BaseDirectory</c>），
/// 重启后照原样接着聊。
///
/// 三条约定：
///
/// 1. **没有消息的会话不写**。空会话是「点了加号但还没开口」的产物，它只在内存里活着 ——
///    真要写下去，重启一次就多一条，用户点几下加号，列表里就攒一串「新对话」永远删不完。
///    同理，正文和附件都为空的助手消息也不写：那是流式回复还没落地时的占位气泡，
///    程序中途被杀掉才会留着它，写进去只会让下一次启动冒出一个空气泡。
/// 2. **先写临时文件再改名**。直接覆盖原文件的话，写到一半断电 / 被杀，留下的是一个
///    截断的 JSON —— 下一次启动**整个历史都读不出来**。改名在同一分区上是原子的，
///    最坏也只是丢掉这一次的新增。
/// 3. 反序列化回来的东西**当成外来的**：字段可能缺失、可能是 null（见 <see cref="Attachment"/>，
///    <c>"Text": null</c> 能绕过声明的非空），逐个补齐再用。
/// </summary>
internal static class ChatStore
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "conversations.json");

    /// <summary>
    /// 应用自己产生的图片（粘贴的、截图的）落盘的目录。
    ///
    /// 特意**不**放 <c>%TEMP%</c>：那里的文件随时可能被系统或清理工具删掉，而它现在是
    /// 一条会话消息的附件 —— 重启之后历史还在、图片却读不出来了，用户看到的是一个个空槽。
    ///
    /// 只增不删：删会话时**不**去回收它的图。同一张图可能被多条消息引用着（引用是路径，
    /// 没有归属），按引用删早晚会删到还在用的那张 —— 那是不可逆的，而留着一张孤儿图
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

    /// <summary>读出历史会话。文件不在 / 读不动 / 解析失败都返回空结果 ——
    /// 一份坏掉的存档不该拦住程序启动。</summary>
    public static (List<Conversation> List, string? ActiveId) Load()
    {
        var empty = (new List<Conversation>(), (string?)null);
        try
        {
            if (!File.Exists(FilePath)) return empty;
            var f = JsonSerializer.Deserialize<ChatStoreFile>(File.ReadAllText(FilePath), ReadOpt);
            if (f?.Conversations == null) return empty;

            foreach (var c in f.Conversations)
            {
                if (c.Id == null) c.Id = Guid.NewGuid().ToString("N");
                if (c.Messages == null) c.Messages = new();
                foreach (var m in c.Messages)
                {
                    m.Text = m.Text ?? "";
                    if (m.Attachments == null) m.Attachments = new();
                    foreach (var a in m.Attachments)
                    {
                        a.Name = a.Name ?? "";
                        a.Kind = string.IsNullOrEmpty(a.Kind) ? "file" : a.Kind;
                    }
                }
            }
            return (f.Conversations, f.ActiveId);
        }
        catch { return empty; }
    }

    /// <summary>整份写回。调用点都在「会话内容有了一次定稿」的地方 —— 用户发了消息、
    /// 贴了一条助手消息、一期回复收尾、删了会话、换了会话 —— 而不是流式刷新的每一帧上：
    /// 一期回复几十帧，写几十次盘没有意义。</summary>
    public static void Save(IEnumerable<Conversation> all, string? activeId)
    {
        try
        {
            var f = new ChatStoreFile { ActiveId = activeId };
            foreach (var c in all)
            {
                if (c.Messages.Count == 0) continue;
                var msgs = c.Messages.Where(m => (m.Text ?? "").Length > 0 || m.Attachments.Count > 0).ToList();
                if (msgs.Count == 0) continue;
                f.Conversations.Add(new Conversation
                {
                    Id = c.Id,
                    Title = c.Title,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    Messages = msgs,
                });
            }

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(f, WriteOpt));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 存不下就算了：这是「记住历史」，不该把正在进行的对话打断 */ }
    }
}
