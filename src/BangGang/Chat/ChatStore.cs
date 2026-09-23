using System.Globalization;

namespace BangGang;

/// <summary>
/// 会话的本地持久化：**SQLite 库里的行**（<c>conversations</c> / <c>messages</c> /
/// <c>tool_calls</c> / <c>message_attachments</c>），重启后照原样接着聊。
///
/// 从「一条会话一个 JSON 文件」换过来之后，原来那四条约定有的作废、有的换了形态，
/// 但**语义一条都没丢**——它们是被踩出来的，不是文风：
///
/// 1. **没有消息的会话不占行**。空会话是「点了加号但还没开口」的产物，它只在内存里活着；
///    正文和附件都为空的助手消息同样不算数（那是流式回复还没落地时的占位气泡，
///    程序中途被杀才会留着它）。所以 <see cref="Save"/> 发现一条会话已经空了时会
///    **把它的行删掉** —— 规则是「库里有行 ⟺ 有内容」。判据仍是
///    <see cref="ChatMessage.IsEmpty"/>，那里把**思考过程**与**工具调用**都算作内容。
/// 2. **写是原子的**。原来靠「先写 .tmp 再改名」，现在靠 SQLite 事务 ——
///    整个 <see cref="Save"/> 在一个事务里，断电最坏也只是丢掉这一次的新增。
/// 3. **读回来的东西当成外来的**。建表时列全是 <c>NOT NULL DEFAULT ''</c>，
///    所以 NULL 那类问题在库这一层就没了；剩下要兜的是「反序列化回来的时间戳解析不了」
///    与「标题是空的」这两种。
/// 4. **id 仍然要在写入前校验字符集**（<see cref="IsId"/>）。它的原意是防路径穿越，
///    现在 id 只是主键、不拼路径，参数化绑定也本来就挡住了注入 —— 留着它是**数据卫生**：
///    库里不该出现一个界面无法安全显示的 id。
///
/// 附件：**托管附件**（截图/粘贴图，字节是我们的）在没人引用时回收；
/// **非托管附件**（用户拖进来的文件）永远不碰。分界见 <see cref="AttachmentStore"/>。
/// </summary>
internal static class ChatStore
{
    /// <summary>托管附件的目录（转发到 <see cref="AttachmentStore.Dir"/>，老调用点照旧）。</summary>
    public static string ImageDir => AttachmentStore.Dir;

    /// <summary>id 能不能当主键用。字符集限定在 <c>[0-9A-Za-z_-]</c>；见类注释第 4 条。</summary>
    private static bool IsId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        return true;
    }

    /// <summary>
    /// 这条会话里真正值得写下去的消息。一条都没有就返回空表（见类注释第 1 条）。
    /// </summary>
    private static List<ChatMessage> Persistable(Conversation c) =>
        c.Messages.Where(m => m != null && !m.IsEmpty).ToList();

    // 时间统一按 ISO 8601 往返格式存成 TEXT：既能排序，又能精确解析回 DateTime，
    // 且不受机器区域设置影响（用的是 InvariantCulture）。存成 Unix 秒也行，
    // 但那样打开数据库看是一串数字，排查时还得心算。
    private static string Iso(DateTime t) =>
        t.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string? s, DateTime fallback)
    {
        if (string.IsNullOrEmpty(s)) return fallback;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                 DateTimeStyles.RoundtripKind, out var t) ? t : fallback;
    }

    /// <summary>
    /// 把一条会话写进库。
    ///
    /// 调用点都在「这条会话的内容有了一次定稿」的地方 —— 用户发了消息、贴了一条助手消息、
    /// 一期回复收尾 —— 而不是流式刷新的每一帧上。
    ///
    /// 实现上是**先把会话整个删掉再重建**（消息、工具调用、附件关联一并重来）。
    /// 这么做而不是逐条 upsert，是因为 <see cref="ChatMessage"/> 没有稳定 id：
    /// 全量重建让「库里的附件引用」永远等于「消息此刻真实持有的附件」，
    /// 不会残留上一次保存留下的悬空引用。代价是每轮保存 O(消息数) 次插入 ——
    /// 在一个事务里跑，几百条消息也是毫秒级。
    /// </summary>
    public static void Save(Conversation c)
    {
        if (!IsId(c.Id)) return;
        if (!Db.Available) return;

        var msgs = Persistable(c);
        if (msgs.Count == 0) { Delete(c.Id); return; }

        try
        {
            var db = Db.Conn;
            db.Begin();
            try
            {
                // 删旧行。ON DELETE CASCADE 会把这条会话的 messages / tool_calls /
                // message_attachments 一并带走（外键是每连接开关，在 Db 开库时打开，
                // 探针里有一条断言专门盯着它）。
                Exec(db, "DELETE FROM conversations WHERE id = ?", c.Id);

                using (var st = db.Prepare(
                    "INSERT INTO conversations(id, title, title_locked, created_at, updated_at, " +
                    "summary, summary_upto, last_prompt_tokens, anchor_msgs) VALUES(?,?,?,?,?,?,?,?,?)"))
                {
                    st.Bind(1, c.Id)
                      .Bind(2, c.Title ?? "")
                      .Bind(3, c.TitleLocked)
                      .Bind(4, Iso(c.CreatedAt))
                      .Bind(5, Iso(c.UpdatedAt))
                      .Bind(6, c.CtxSummary ?? "")
                      .Bind(7, c.CtxSummaryUpto)
                      .Bind(8, c.LastPromptTokens)
                      .Bind(9, c.AnchorMsgs);
                    st.Step();
                }

                for (int i = 0; i < msgs.Count; i++) WriteMessage(db, c.Id, i, msgs[i]);

                // 附件的**会话级使用关系**（物化）+ 引用计数。和消息写在同一个事务里，
                // 所以永远不会跟 message_attachments 漂开。
                Exec(db, "DELETE FROM attachment_uses WHERE conv_id = ?", c.Id);
                using (var st = db.Prepare(
                    "INSERT OR IGNORE INTO attachment_uses(conv_id, hash) " +
                    "SELECT DISTINCT ?, ma.hash FROM message_attachments ma " +
                    "JOIN messages m ON m.id = ma.msg_id WHERE m.conv_id = ?"))
                {
                    st.Bind(1, c.Id).Bind(2, c.Id);
                    st.Step();
                }
                RecomputeRefs(db);

                db.Commit();
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }
        catch { /* 存不下就算了：这是「记住历史」，不该把正在进行的对话打断 */ }
    }

    private static void WriteMessage(SqliteDb db, string convId, int seq, ChatMessage m)
    {
        using (var st = db.Prepare(
            "INSERT INTO messages(conv_id, seq, role, when_utc, text, reasoning, reasoning_ms, warning) " +
            "VALUES(?,?,?,?,?,?,?,?)"))
        {
            st.Bind(1, convId)
              .Bind(2, (long)seq)
              .Bind(3, m.Role ?? "")
              .Bind(4, Iso(m.When))
              .Bind(5, m.Text ?? "")
              .Bind(6, m.Reasoning ?? "")
              .Bind(7, (long)m.ReasoningMs)
              .Bind(8, m.Warning ?? "");
            st.Step();
        }
        long msgId = db.LastInsertRowid;

        var calls = m.ToolCalls;
        if (calls != null)
            for (int i = 0; i < calls.Count; i++)
            {
                var t = calls[i];
                if (t == null) continue;
                using var st = db.Prepare(
                    "INSERT INTO tool_calls(msg_id, seq, call_id, name, args, result, brief, ok, ms) " +
                    "VALUES(?,?,?,?,?,?,?,?,?)");
                st.Bind(1, msgId).Bind(2, (long)i)
                  .Bind(3, t.Id ?? "").Bind(4, t.Name ?? "").Bind(5, t.Args ?? "")
                  .Bind(6, t.Result ?? "").Bind(7, t.Brief ?? "").Bind(8, t.Ok)
                  .Bind(9, (long)t.Ms);
                st.Step();
            }

        var files = m.Attachments;
        if (files != null)
            for (int i = 0; i < files.Count; i++)
            {
                var a = files[i];
                if (a == null) continue;
                string key = AttachmentStore.KeyOf(a);
                bool managed = AttachmentStore.IsManaged(a.Path);

                // 附件本体。INSERT OR IGNORE：同一张图被多条消息引用时只有第一行会写进去，
                // 后面几次是空操作。
                using (var st = db.Prepare(
                    "INSERT OR IGNORE INTO attachments(hash, managed, name, size, created_at, refcount) " +
                    "VALUES(?,?,?,?,?,0)"))
                {
                    st.Bind(1, key).Bind(2, managed).Bind(3, a.Name ?? "")
                      .Bind(4, a.Size).Bind(5, Iso(DateTime.Now));
                    st.Step();
                }
                // 名字只有到这一步才知道（用户在界面上看到的是「截图_142233.png」这种），
                // 而 Store() 登记那一行时还早。**只在原名字为空时补**：同一份字节在不同消息里
                // 可能被起不同的名字，先到先得，后面的不覆盖。
                if (!string.IsNullOrEmpty(a.Name))
                {
                    using var st = db.Prepare("UPDATE attachments SET name = ? WHERE hash = ? AND name = ''");
                    st.Bind(1, a.Name).Bind(2, key);
                    st.Step();
                }

                // 关联。非托管附件多存一个 src —— 它才是真正的路径来源，
                // 而托管附件的路径由哈希现拼（见 Load），不必存两份。
                using (var st = db.Prepare(
                    "INSERT INTO message_attachments(msg_id, seq, hash, kind, name, src) VALUES(?,?,?,?,?,?)"))
                {
                    st.Bind(1, msgId).Bind(2, (long)i).Bind(3, key)
                      .Bind(4, string.IsNullOrEmpty(a.Kind) ? "file" : a.Kind)
                      .Bind(5, a.Name ?? "")
                      .Bind(6, a.Path);
                    st.Step();
                }
            }
    }

    /// <summary>
    /// 删掉一条会话。
    ///
    /// <paramref name="keepPaths"/> 是**当前输入框草稿里还拿着的那些附件路径**，
    /// 它们不参与回收。为什么要它：草稿里的图已经落盘了、但还没有任何消息引用它，
    /// 所以引用数是 0；如果用户刚把同一张图发出去过、又把这张删掉的会话一并带上，
    /// 按引用数判就会把草稿里那张也收走，发送时变成一个破图。
    /// 草稿是进程内状态，只有调用方知道，所以由调用方传进来。
    ///
    /// **顺序是有讲究的：先提交库、再删文件。** 崩在中间只会在磁盘上留几个孤儿文件
    /// （几 KB，下次删会话或启动清扫还会捡到）；反过来则是「库里有行、文件没了」——
    /// 用户看到打不开的空槽，**不可逆**。
    /// </summary>
    public static void Delete(string id, IReadOnlyCollection<string>? keepPaths = null)
    {
        if (!IsId(id)) return;
        if (!Db.Available) return;

        var doomed = new List<string>();
        try
        {
            var db = Db.Conn;

            // 先记下这条会话碰过哪些托管附件 —— 事务之后要照这份名单去回收文件
            using (var st = db.Prepare(
                "SELECT DISTINCT a.hash FROM message_attachments ma " +
                "JOIN attachments a ON a.hash = ma.hash " +
                "WHERE a.managed = 1 AND ma.msg_id IN (SELECT id FROM messages WHERE conv_id = ?)"))
            {
                st.Bind(1, id);
                while (st.Step()) doomed.Add(st.Text(0));
            }

            db.Begin();
            try
            {
                Exec(db, "DELETE FROM conversations WHERE id = ?", id);
                // 会话级使用关系跟着走；引用计数随即重算，于是「还有没有别人用」当场就是准的。
                Exec(db, "DELETE FROM attachment_uses WHERE conv_id = ?", id);
                RecomputeRefs(db);

                // **这里不顺手删附件行。** 那会误伤草稿里刚存进来、还没被任何消息引用的图
                // （它们本来就是 refcount 0）。行与文件一起交给事务提交之后的 Reclaim，
                // 由它统一按「引用数为 0 且不在草稿里」来收 —— keepPaths 也才管得住。
                // 崩在这中间最坏是留下一行 refcount 0 的记录，下次启动的清扫会捡走。
                db.Commit();
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }
        catch { return; }

        // 事务提交之后才动磁盘
        AttachmentStore.Reclaim(doomed, keepPaths);
    }

    /// <summary>
    /// 把 <c>attachments.refcount</c> 按 <c>attachment_uses</c> 整表重算一遍。
    ///
    /// **不维护增量。** 增量意味着每次插入、每次删除都要记得加减，漏一处就永久错位，
    /// 而错位的表现是「明明还有人用的图被删了」—— 那是不可逆的。
    /// 整表重算是一条 SQL，附件表的量级（几百行）下是毫秒级，换来的是「不可能漂」。
    /// </summary>
    private static void RecomputeRefs(SqliteDb db) =>
        db.Exec("UPDATE attachments SET refcount = " +
                "(SELECT COUNT(*) FROM attachment_uses u WHERE u.hash = attachments.hash)");

    /// <summary>
    /// 读出全部历史会话。
    ///
    /// **保持「全量载入内存」**：侧栏搜索（<c>RebindConversations</c>）是纯内存的
    /// <c>Title.Contains</c> + <c>Messages.Any(...)</c>，气泡渲染、文件工具按附件找路径
    /// 也都遍历 <c>Messages</c>。改成按需查询要动这几处的调用契约，而这是个人聊天工具，
    /// 全量载入的量级完全撑得住 —— 这次换存储要的是「可靠 + 加密 + 附件能回收」，不是省内存。
    ///
    /// 四条查询铺一次全库，没有 N+1。
    /// </summary>
    public static List<Conversation> Load()
    {
        var list = new List<Conversation>();
        if (!Db.Available) return list;

        try
        {
            var db = Db.Conn;
            var byId = new Dictionary<string, Conversation>(StringComparer.Ordinal);

            using (var st = db.Prepare(
                "SELECT id, title, title_locked, created_at, updated_at, summary, summary_upto, " +
                "last_prompt_tokens, anchor_msgs FROM conversations"))
            {
                while (st.Step())
                {
                    string id = st.Text(0);
                    if (!IsId(id)) continue;
                    var c = new Conversation
                    {
                        Id = id,
                        Title = st.Text(1),
                        TitleLocked = st.Int64(2) != 0,
                        CreatedAt = ParseTime(st.Text(3), DateTime.Now),
                        UpdatedAt = ParseTime(st.Text(4), DateTime.Now),
                        CtxSummary = st.Text(5),
                        CtxSummaryUpto = (int)st.Int64(6),
                        LastPromptTokens = (int)st.Int64(7),
                        AnchorMsgs = (int)st.Int64(8),
                    };
                    if (string.IsNullOrEmpty(c.Title)) c.Title = "新对话";
                    byId[id] = c;
                }
            }

            // msgId -> 它落在哪条消息上。工具调用与附件关联都靠它归位。
            var slot = new Dictionary<long, ChatMessage>();

            using (var st = db.Prepare(
                "SELECT id, conv_id, role, when_utc, text, reasoning, reasoning_ms, warning " +
                "FROM messages ORDER BY conv_id, seq"))
            {
                while (st.Step())
                {
                    if (!byId.TryGetValue(st.Text(1), out var c)) continue;
                    var m = new ChatMessage
                    {
                        Role = st.Text(2),
                        When = ParseTime(st.Text(3), c.UpdatedAt),
                        Text = st.Text(4),
                        Reasoning = st.Text(5),
                        ReasoningMs = (int)st.Int64(6),
                        Warning = st.Text(7),
                    };
                    slot[st.Int64(0)] = m;
                    c.Messages.Add(m);
                }
            }

            using (var st = db.Prepare(
                "SELECT msg_id, call_id, name, args, result, brief, ok, ms " +
                "FROM tool_calls ORDER BY msg_id, seq"))
            {
                while (st.Step())
                {
                    if (!slot.TryGetValue(st.Int64(0), out var m)) continue;
                    m.ToolCalls.Add(new ToolCall
                    {
                        Id = st.Text(1), Name = st.Text(2), Args = st.Text(3),
                        Result = st.Text(4), Brief = st.Text(5),
                        Ok = st.Int64(6) != 0, Ms = (int)st.Int64(7),
                    });
                }
            }

            using (var st = db.Prepare(
                "SELECT ma.msg_id, ma.hash, ma.kind, ma.name, ma.src, a.managed, a.size " +
                "FROM message_attachments ma JOIN attachments a ON a.hash = ma.hash " +
                "ORDER BY ma.msg_id, ma.seq"))
            {
                while (st.Step())
                {
                    if (!slot.TryGetValue(st.Int64(0), out var m)) continue;
                    string hash = st.Text(1);
                    bool managed = st.Int64(5) != 0;
                    m.Attachments.Add(new Attachment
                    {
                        Kind = st.Text(2),
                        Name = st.Text(3),
                        // 托管附件的路径由哈希现拼（文件名就是哈希），非托管的用存下来的 src
                        Path = managed ? Path.Combine(AttachmentStore.Dir, hash + ".png")
                                       : (st.Text(4) ?? ""),
                        Size = st.Int64(6),
                    });
                }
            }

            foreach (var c in byId.Values)
            {
                // 一条消息都没有的会话不该出现在列表里（正常不会发生，
                // 是手改库或将来某个 bug 留下的）。跳过而不删：读取路径不该动数据。
                if (Persistable(c).Count == 0) continue;
                list.Add(c);
            }
        }
        catch { /* 库读不动就当没有历史 */ }

        return list;
    }

    /// <summary>
    /// 启动时收一次孤儿附件。**只在这里做「全库对照」式的清扫，不在别处做**：
    /// 此刻一定没有草稿（草稿是进程内状态，而这是启动），所以「引用数为 0」
    /// 才真的等于「没人要」。平时那两条路（删会话、从输入框移除）走的是精确回收。
    ///
    /// 实现与红线都在 <see cref="AttachmentStore.Sweep"/> 里。
    /// </summary>
    public static void SweepOrphans() => AttachmentStore.Sweep();

    private static void Exec(SqliteDb db, string sql, string arg)
    {
        using var st = db.Prepare(sql);
        st.Bind(1, arg);
        st.Step();
    }
}
