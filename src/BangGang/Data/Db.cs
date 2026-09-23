namespace BangGang;

/// <summary>
/// 应用级的那个数据库：开库、建表、版本闸门，以及一个全进程共用的连接。
///
/// **一个连接、全部在 UI 线程上用。** 这不是「没想到并发」，是想清楚了才这么定的：
/// 数据量是「一个人自己的聊天记录」，每次读写都是毫秒级的小事务；而本项目所有会碰
/// 存储的地方（发消息、切会话、改设置、关窗口收尾）本来就都在 UI 线程上。
/// 加连接池/锁换来的是「多个线程同时写」，而我们根本没有那个需求，只会多出一类
/// 「偶发的、只在启动或关闭瞬间出现」的 bug。将来真需要后台写，再按那一处的
/// 实际形状去加，而不是现在先架一套。
///
/// 库文件与其它数据放一起（exe 同目录），沿用 <c>settings.json</c> / <c>chats\</c> 的旧约定：
/// 安装器把程序装在 <c>{localappdata}\Programs\BangGang</c>，那里每用户可写。
/// </summary>
internal static class Db
{
    /// <summary>库文件。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "banggang.db");

    /// <summary>
    /// 建表方式的版本号，写进 <c>PRAGMA user_version</c>。
    /// 以后改了表结构就 +1，并在 <see cref="EnsureSchema"/> 里补一段从旧版本升上来的语句。
    /// </summary>
    private const int SchemaVersion = 2;

    private static SqliteDb? _db;
    private static bool _probed;
    private static bool _ok;
    private static string _reason = "";

    private static readonly object Gate = new();

    /// <summary>
    /// 数据库能不能用。**首次访问时才真的去探测**（查 DLL、开库、建表），结果缓存下来。
    ///
    /// 探测不过不该让程序起不来 —— 那会把「系统里的一个组件有问题」升级成
    /// 「软件打不开」。所以这里只记录原因，由界面去说明（见 <see cref="UnavailableReason"/>），
    /// 存储层则整体降级成不落盘。
    /// </summary>
    public static bool Available
    {
        get { ProbeOnce(); return _ok; }
    }

    /// <summary>探测失败的原因（中文，可直接给用户看）。可用时是空串。</summary>
    public static string UnavailableReason
    {
        get { ProbeOnce(); return _reason; }
    }

    /// <summary>数据库版本号字符串，给「软件说明」页显示用。取不到时空串。</summary>
    public static string VersionText => Sqlite.VersionText();

    /// <summary>当前连接。不可用时抛 —— 调用方应当先看 <see cref="Available"/>。</summary>
    public static SqliteDb Conn
    {
        get
        {
            ProbeOnce();
            if (!_ok || _db is null) throw new SqliteException("数据库不可用：" + _reason, -1);
            return _db;
        }
    }

    private static void ProbeOnce()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (!Sqlite.Probe(out string why))
                {
                    _reason = why;
                    return;
                }

                var db = SqliteDb.Open(FilePath);

                // foreign_keys 是**每连接**的开关，不是存在文件里的设置 —— 漏了它，
                // ON DELETE CASCADE 会静默不生效，删会话只删掉 conversations 那一行，
                // 消息全留在库里变成孤儿（而且界面完全看不出问题，直到某天要统计）。
                db.Exec("PRAGMA foreign_keys = ON");
                db.Exec("PRAGMA busy_timeout = 3000");
                EnsureSchema(db);

                _db = db;
                _ok = true;
            }
            catch (Exception ex)
            {
                _reason = ex.Message;
            }
        }
    }

    /// <summary>
    /// 建表 + 版本闸门。**幂等**：每次开库都跑一遍，全是 <c>IF NOT EXISTS</c>。
    ///
    /// 只用 SQLite 3.8 时代就有的语法（见 <see cref="Sqlite"/> 的注释）——
    /// 这个 DLL 由 Windows Update 单独升级，不同机器上的版本能差出好几年，
    /// 用了新语法就会在别人的机器上炸，而我们自己的机器上一切正常。
    ///
    /// 探针也调它：这样探针验的就是**应用真正用的那套 DDL**，而不是另抄一份
    /// （抄一份的那一刻起，两者就开始各自漂了）。
    /// </summary>
    internal static void EnsureSchema(SqliteDb db)
    {
        db.Exec("""
            CREATE TABLE IF NOT EXISTS settings (
              key    TEXT PRIMARY KEY,
              value  TEXT NOT NULL,
              secret INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS conversations (
              id                 TEXT PRIMARY KEY,
              title              TEXT NOT NULL DEFAULT '',
              title_locked       INTEGER NOT NULL DEFAULT 0,
              created_at         TEXT NOT NULL,
              updated_at         TEXT NOT NULL,
              summary            TEXT NOT NULL DEFAULT '',
              summary_upto       INTEGER NOT NULL DEFAULT 0,
              last_prompt_tokens INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS messages (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              conv_id      TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
              seq          INTEGER NOT NULL,
              role         TEXT NOT NULL DEFAULT '',
              when_utc     TEXT NOT NULL DEFAULT '',
              text         TEXT NOT NULL DEFAULT '',
              reasoning    TEXT NOT NULL DEFAULT '',
              reasoning_ms INTEGER NOT NULL DEFAULT 0,
              warning      TEXT NOT NULL DEFAULT '');

            CREATE TABLE IF NOT EXISTS tool_calls (
              id      INTEGER PRIMARY KEY AUTOINCREMENT,
              msg_id  INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              seq     INTEGER NOT NULL,
              call_id TEXT NOT NULL DEFAULT '',
              name    TEXT NOT NULL DEFAULT '',
              args    TEXT NOT NULL DEFAULT '',
              result  TEXT NOT NULL DEFAULT '',
              brief   TEXT NOT NULL DEFAULT '',
              ok      INTEGER NOT NULL DEFAULT 1,
              ms      INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS attachments (
              hash       TEXT PRIMARY KEY,
              managed    INTEGER NOT NULL DEFAULT 0,
              name       TEXT NOT NULL DEFAULT '',
              size       INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL DEFAULT '',
              refcount   INTEGER NOT NULL DEFAULT 0);

            -- 一个附件被哪些**会话**用着。是对 message_attachments 的物化：
            -- 每次写会话时连带重算，同一个事务里完成，所以不会漂。
            -- 物化它的理由很实际：「这个附件还有没有人用」是这个功能里唯一要紧的问题，
            -- 而它每次删会话、每次从输入框移除附件都要问一遍。
            CREATE TABLE IF NOT EXISTS attachment_uses (
              conv_id TEXT NOT NULL,
              hash    TEXT NOT NULL,
              PRIMARY KEY (conv_id, hash));

            CREATE TABLE IF NOT EXISTS message_attachments (
              msg_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              seq    INTEGER NOT NULL,
              hash   TEXT NOT NULL,
              kind   TEXT NOT NULL DEFAULT 'file',
              name   TEXT NOT NULL DEFAULT '',
              src    TEXT,
              PRIMARY KEY (msg_id, seq));

            CREATE INDEX IF NOT EXISTS ix_msg_conv ON messages(conv_id, seq);
            CREATE INDEX IF NOT EXISTS ix_att_hash  ON message_attachments(hash);
            """);

        long ver = db.QueryLong("PRAGMA user_version");

        // **这里没有任何迁移代码，是有意的。** 项目尚未推广，数据结构改了就直接改，
        // 旧库由使用者自己删掉重来 —— 写一套「从上一版升上来」的代码，在没有真实用户
        // 数据要保的前提下只是凭空多一份要维护、要测试、还测不到的东西。
        //
        // 代价是结构一变，手上那个旧库就打不开了。所以下面**明说一句**，
        // 而不是让它在某条冷路径上以「no such column」的形式炸出来 ——
        // 那种错看上去像程序坏了，实际只是数据是上一版的。
        if (ver != 0 && ver != SchemaVersion)
            throw new SqliteException(
                "数据库结构是另一个版本的（库 " + ver + "，本程序 " + SchemaVersion +
                "）。本程序不做旧库迁移，删掉这个文件重新开始即可：" + FilePath, -1);

        // 新库盖个章，下次打开才知道它是不是本版的。
        if (ver == 0) db.Exec("PRAGMA user_version = " + SchemaVersion);
    }
}
