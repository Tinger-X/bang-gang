#if DEBUG
using System.Drawing;

namespace BangGang;

/// <summary>
/// 数据库层的离屏探针：**不建窗口、不碰桌面**，跑完把结果打到 stdout。
///
/// 为什么要有这个：SQLite 这块唯一没法靠「读代码」确认的东西太多了 ——
/// <c>sqlite3_stmt*</c> 的封送对不对、中文 UTF-8 往返会不会掉字节、
/// <c>PRAGMA foreign_keys</c> 到底有没有生效、级联删是不是真的级联。
/// 这些错了的表现都很安静（数据没存进去、孤儿行留着、附件永远回收不掉），
/// 而靠界面去发现它们要求「先有一条会话、再发消息、再删、再去翻目录」。
///
/// 这个探针把上面每一条都变成一行 PASS/FAIL。它证明不了界面接得对，
/// 但那本来就是 <c>BANGGANG_ASK</c> 与手工点的活；它证明的是**存储层本身是诚实的**。
///
/// <c>BANGGANG_DB_PROBE=1</c> 触发。失败时把进程退出码设成 1，脚本可以据此判。
/// </summary>
internal static class OfflineDb
{
    private static int _pass, _fail;

    public static bool TryRun()
    {
        if (Environment.GetEnvironmentVariable("BANGGANG_DB_PROBE") != "1") return false;

        // 与 OfflineTool / OfflineAsk 同一套：显式把 stdout 包成 UTF-8，
        // 否则重定向到文件时中文全是乱码 —— 而重定向正是最常用的用法。
        try
        {
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 包不上就按默认走 */ }

        try { Run(); }
        catch (Exception ex) { Console.WriteLine("db probe failed: " + ex); _fail++; }

        Console.WriteLine();
        Console.WriteLine($"==== {_pass} passed, {_fail} failed ====");
        if (_fail > 0) Environment.ExitCode = 1;
        return true;
    }

    private static void Run()
    {
        Console.WriteLine("---- 1. DLL 探测 ----");
        string ver = Sqlite.VersionText();
        Console.WriteLine($"winsqlite3 版本        : {(ver.Length > 0 ? ver : "(取不到)")}");
        bool ok = Sqlite.Probe(out string why);
        Check("DLL 与导出符号齐全", ok, ok ? ver : why);

        Console.WriteLine();
        Console.WriteLine("---- 2. 真实数据库（exe 同目录）----");
        Console.WriteLine($"路径                   : {Db.FilePath}");
        Console.WriteLine($"可用                   : {Db.Available}");
        if (!Db.Available) Console.WriteLine($"原因                   : {Db.UnavailableReason}");
        Check("真实库开得起来", Db.Available, Db.UnavailableReason);

        Console.WriteLine();
        Console.WriteLine("---- 3. 建表与版本闸门（内存库）----");
        using var db = SqliteDb.Open(":memory:");
        db.Exec("PRAGMA foreign_keys = ON");
        Check("PRAGMA foreign_keys 生效", db.QueryLong("PRAGMA foreign_keys") == 1,
              "关着的话下面所有级联删除都会静默失效");

        Db.EnsureSchema(db);
        long v1 = db.QueryLong("PRAGMA user_version");
        Check("user_version 盖了章", v1 == 2, "实际 " + v1);

        Db.EnsureSchema(db);   // 再跑一遍必须是幂等的
        Check("重复建表不报错、版本号不变（幂等）", db.QueryLong("PRAGMA user_version") == v1);

        var tables = new List<string>();
        using (var st = db.Prepare("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"))
            while (st.Step()) tables.Add(st.Text(0));
        Console.WriteLine($"表                     : {string.Join(", ", tables)}");
        foreach (string want in new[] { "settings", "conversations", "messages", "tool_calls",
                                        "attachments", "attachment_uses", "message_attachments" })
            Check("有表 " + want, tables.Contains(want));

        Console.WriteLine();
        Console.WriteLine("---- 4. 文字与绑定 ----");

        // 中文 / emoji / 组合字符 / 长串，全过一遍参数绑定。用参数而不是拼 SQL 字面量，
        // 因为「绑定路径」才是应用真正走的那条。
        const string tricky = "中文😀测试 · café · ΑΒΓ · 日本語 · \t制表符\n换行 · '单引号' \"双引号\" · ; DROP TABLE--";
        using (var st = db.Prepare("INSERT INTO settings(key, value, secret) VALUES(?,?,?)"))
        {
            st.Bind(1, tricky).Bind(2, tricky).Bind(3, false);
            st.Step();
        }
        using (var st = db.Prepare("SELECT key, value FROM settings WHERE key = ?"))
        {
            st.Bind(1, tricky);
            bool row = st.Step();
            Check("tricky 字符串能读回来", row && st.Text(0) == tricky, "键比对");
            Check("tricky 字符串逐字相等", row && st.Text(1) == tricky, "值比对");
            if (row)
            {
                Console.WriteLine($"  写进去 : {tricky.Length} 字符");
                Console.WriteLine($"  读回来 : {st.Text(1).Length} 字符");
            }
        }
        Check("参数化挡住了 SQL 注入", db.QueryLong("SELECT COUNT(*) FROM settings") == 1);

        // NULL 与空串必须是两回事：「没设置过」和「设置成空」语义不同。
        // 用一张探针专用的可空表 —— settings.value 是 NOT NULL（有意的，
        // 见下一条断言），拿它测不出这件事。
        db.Exec("CREATE TABLE probe_nulls(k TEXT, v TEXT)");
        using (var st = db.Prepare("INSERT INTO probe_nulls(k, v) VALUES(?,?)"))
        {
            st.Bind(1, "k-null").Bind(2, (string?)null);
            st.Step();
            st.Reset();
            st.Bind(1, "k-empty").Bind(2, "");
            st.Step();
        }
        using (var st = db.Prepare("SELECT v FROM probe_nulls WHERE k=?"))
        {
            st.Bind(1, "k-null");
            bool r = st.Step();
            Check("绑定 null 读回来是 NULL 不是空串", r && st.IsNull(0));
            st.Reset();
            st.Bind(1, "k-empty");
            r = st.Step();
            Check("绑定空串读回来是空串不是 NULL", r && !st.IsNull(0) && st.Text(0).Length == 0);
        }

        // settings.value 是 NOT NULL：这条约束在替我们挡「写设置时漏了 null」。
        // 写设置那条路必须把 null 归一成空串再绑，否则用户会看到一次莫名的保存失败。
        bool nullRejected = false;
        try
        {
            using var st = db.Prepare("INSERT INTO settings(key, value, secret) VALUES('k','x',0)");
            st.Bind(1, "k").Bind(2, (string?)null);
            st.Step();
        }
        catch (SqliteException) { nullRejected = true; }
        Check("settings.value 拒收 null（写设置那条路必须自己归一成空串）", nullRejected);

        // 整数 / 布尔 / 浮点
        using (var st = db.Prepare("INSERT INTO conversations(id,title,title_locked,created_at,updated_at,last_prompt_tokens,summary_upto) VALUES(?,?,?,?,?,?,?)"))
        {
            st.Bind(1, "c1").Bind(2, "标题").Bind(3, true).Bind(4, "2026-09-23").Bind(5, "2026-09-23")
              .Bind(6, 123456789012L).Bind(7, 0);
            st.Step();
        }
        using (var st = db.Prepare("SELECT title_locked, last_prompt_tokens FROM conversations WHERE id='c1'"))
        {
            st.Step();
            Check("bool 往返", st.Int64(0) == 1);
            Check("超出 int 的 long 往返", st.Int64(1) == 123456789012L);
        }

        Console.WriteLine();
        Console.WriteLine("---- 5. 事务 ----");
        long before = db.QueryLong("SELECT COUNT(*) FROM conversations");
        db.Begin();
        using (var st = db.Prepare("INSERT INTO conversations(id,title,created_at,updated_at) VALUES('tx','x','a','b')"))
            st.Step();
        db.Rollback();
        Check("回滚之后那一行不在", db.QueryLong("SELECT COUNT(*) FROM conversations") == before);

        db.Begin();
        using (var st = db.Prepare("INSERT INTO conversations(id,title,created_at,updated_at) VALUES('tx2','x','a','b')"))
            st.Step();
        db.Commit();
        Check("提交之后那一行在", db.QueryLong("SELECT COUNT(*) FROM conversations") == before + 1);

        Console.WriteLine();
        Console.WriteLine("---- 6. 级联删除与附件引用计数 ----");

        // 一条会话：1 条消息 + 1 个附件。附件同时被另一条会话的消息引用（引用数 2）。
        db.Exec("DELETE FROM conversations");
        db.Exec("DELETE FROM attachments");
        db.Exec("""
            INSERT INTO conversations(id,title,created_at,updated_at) VALUES('a','会话A','x','x');
            INSERT INTO conversations(id,title,created_at,updated_at) VALUES('b','会话B','x','x');
            INSERT INTO messages(id,conv_id,seq,role,when_utc,text) VALUES(1,'a',0,'user','x','甲');
            INSERT INTO messages(id,conv_id,seq,role,when_utc,text) VALUES(2,'b',0,'user','x','乙');
            INSERT INTO tool_calls(msg_id,seq,name) VALUES(1,0,'calc');
            INSERT INTO attachments(hash,managed,name,size,created_at) VALUES('h1',1,'图.png',100,'x');
            INSERT INTO message_attachments(msg_id,seq,hash,kind,name) VALUES(1,0,'h1','image','图.png');
            INSERT INTO message_attachments(msg_id,seq,hash,kind,name) VALUES(2,0,'h1','image','图.png');
            """);

        long RefCount(string hash)
        {
            using var st = db.Prepare("SELECT COUNT(*) FROM message_attachments WHERE hash = ?");
            st.Bind(1, hash);
            return st.Step() ? st.Int64(0) : 0;
        }

        Check("同一张图被两条会话引用，引用数 = 2", RefCount("h1") == 2);

        // 删掉会话 A —— 只走 conversations 的 DELETE，其余全靠外键级联
        db.Exec("DELETE FROM conversations WHERE id='a'");
        Check("消息被级联删掉", db.QueryLong("SELECT COUNT(*) FROM messages WHERE conv_id='a'") == 0);
        Check("工具调用被级联删掉", db.QueryLong("SELECT COUNT(*) FROM tool_calls WHERE msg_id=1") == 0);
        Check("附件关联被级联删掉", db.QueryLong("SELECT COUNT(*) FROM message_attachments WHERE msg_id=1") == 0);
        Check("**B 会话的引用还在**", RefCount("h1") == 1);
        Check("**attachments 行本身还在**（还没到回收的时候）",
              db.QueryLong("SELECT COUNT(*) FROM attachments WHERE hash='h1'") == 1);

        // 再删会话 B —— 这下引用归零，才轮到回收
        db.Exec("DELETE FROM conversations WHERE id='b'");
        Check("引用归零", RefCount("h1") == 0);

        // 回收就是这一句：只删「表里存在、且确实没人引用」的托管附件
        db.Exec("DELETE FROM attachments WHERE managed = 1 AND hash NOT IN (SELECT hash FROM message_attachments)");
        Check("孤儿附件行被回收", db.QueryLong("SELECT COUNT(*) FROM attachments WHERE hash='h1'") == 0);

        Console.WriteLine();
        Console.WriteLine("---- 7. 非托管附件永不回收 ----");
        // 用户拖进来的文件：managed = 0，字节是用户的。哪怕引用归零也不许删。
        db.Exec("""
            INSERT INTO conversations(id,title,created_at,updated_at) VALUES('c','会话C','x','x');
            INSERT INTO messages(id,conv_id,seq,role,when_utc,text) VALUES(3,'c',0,'user','x','丙');
            INSERT INTO attachments(hash,managed,name,size,created_at) VALUES('C:\\用户\\报告.docx',0,'报告.docx',1,'x');
            INSERT INTO message_attachments(msg_id,seq,hash,kind,name,src) VALUES(3,0,'C:\\用户\\报告.docx','file','报告.docx','C:\\用户\\报告.docx');
            DELETE FROM conversations WHERE id='c';
            """);
        db.Exec("DELETE FROM attachments WHERE managed = 1 AND hash NOT IN (SELECT hash FROM message_attachments)");
        Check("非托管附件的行不被回收（它指向的是用户的文件）",
              db.QueryLong("SELECT COUNT(*) FROM attachments WHERE managed = 0") == 1);

        Console.WriteLine();
        Console.WriteLine("---- 8. DPAPI 加解密 ----");
        foreach (string s in new[] { "sk-abcdef1234567890", "中文密钥·🔑", new string('x', 4000) })
        {
            string enc = Dpapi.Protect(s);
            Check($"往返一致（{s.Length} 字符）", Dpapi.TryUnprotect(enc, out string back) && back == s);
            Check("密文里看不到明文", !enc.Contains(s, StringComparison.Ordinal));
        }
        Check("空串不产生密文（免得「没设置过」和「设成空」变成两段不同的密文）", Dpapi.Protect("") == "");
        Check("空串解得回空串", Dpapi.TryUnprotect("", out string empty) && empty.Length == 0);

        // 这两条是「换机器之后会怎样」的验收点：**必须返回失败，而不是抛异常、
        // 也不是解出一段乱码**。抛异常会让整份设置加载不出来，乱码则会让用户
        // 以为 Key 还在、只是显示坏了 —— 两种都比明说「重填一次」糟。
        string tampered = Dpapi.Protect("sk-secret");
        char last = tampered[^1];
        tampered = tampered[..^1] + (last == 'A' ? 'B' : 'A');
        Check("密文被改一个字节 → 老实返回失败", !Dpapi.TryUnprotect(tampered, out _));
        Check("不是本程序写的密文 → 老实返回失败", !Dpapi.TryUnprotect("sk-plaintext", out _));

        // 记下这条事实：DPAPI 每次都加随机盐，同一明文两次加密结果不同。
        // 所以**判「设置有没有改过」绝不能拿密文比** —— 那会永远判成「改过」。
        // 密文只在落库那一刻产生，比较一律在内存里的明文上做。
        Check("同一明文两次加密结果不同（所以脏检查不能比密文）",
              Dpapi.Protect("same") != Dpapi.Protect("same"));

        EndToEnd();
    }

    /// <summary>
    /// 用**真实的 Store** 走一遍完整往返：存会话 → 读回来 → 删掉 → 附件被回收。
    ///
    /// 前面那些段验的是机制，这一段验的是「接起来对不对」—— 字段有没有漏存、
    /// 托管附件的路径能不能按哈希重建、删会话时该回收的回收了、不该碰的没碰。
    /// 注意它写的是 exe 同目录那个真库（`build/bin/.../banggang.db`），
    /// 并且**会清空 settings 表**；Debug 构建才编得进来，且那个目录是构建产物，不是安装目录。
    /// </summary>
    private static void EndToEnd()
    {
        Console.WriteLine();
        Console.WriteLine("---- 9. 会话 / 消息 / 附件 端到端（真实库）----");
        if (!Db.Available) { Console.WriteLine("  跳过：库不可用"); return; }

        const string cid = "probe-e2e-conversation";
        ChatStore.Delete(cid);   // 清掉上一次跑剩下的

        // 造一张真图（走真实的 Store，所以顺带验了内容寻址）
        string img = MakePng(Color.Red);
        Check("托管附件落在 attachments/ 下", AttachmentStore.IsManaged(img));
        Check("同一份内容存两次是同一个文件", MakePng(Color.Red) == img);

        // 非托管附件：用户自己的文件，我们只有路径
        string userDoc = Path.Combine(AppContext.BaseDirectory, "probe-unmanaged.docx");
        File.WriteAllText(userDoc, "x");

        var conv = new Conversation
        {
            Id = cid,
            Title = "探针会话",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        conv.Messages.Add(new ChatMessage
        {
            Role = "user",
            When = DateTime.Now,
            Text = "你好😀 中文",
            Attachments = new List<Attachment>
            {
                Attachment.ForImage("图.png", img),
                Attachment.ForFile("报告.docx", userDoc),
            },
        });
        conv.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            When = DateTime.Now,
            Text = "答",
            Reasoning = "想了一会儿",
            ReasoningMs = 42,
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "call_1", Name = "calc", Args = "{\"expr\":\"1+1\"}",
                        Result = "2", Brief = "计算 1+1", Ok = true, Ms = 3 },
            },
        });
        ChatStore.Save(conv);

        var back = ChatStore.Load().FirstOrDefault(c => c.Id == cid);
        Check("会话读得回来", back != null);
        Check("标题", back?.Title == "探针会话");
        Check("消息条数", back?.Messages.Count == 2);
        Check("中文与 emoji 逐字相等", back?.Messages[0].Text == "你好😀 中文");
        Check("思考过程与耗时", back?.Messages[1].Reasoning == "想了一会儿" && back!.Messages[1].ReasoningMs == 42);
        Check("工具调用（含回灌要用的 call_id）",
              back?.Messages[1].ToolCalls.Count == 1 && back.Messages[1].ToolCalls[0].Id == "call_1"
              && back.Messages[1].ToolCalls[0].Brief == "计算 1+1");
        Check("附件个数", back?.Messages[0].Attachments.Count == 2);
        Check("**托管附件的路径由哈希重建**（存的是哈希，不是路径）",
              back?.Messages[0].Attachments[0].Path == img);
        Check("**非托管附件的路径原样保留**（那是用户的文件）",
              back?.Messages[0].Attachments[1].Path == userDoc);

        string hashOfImg = Path.GetFileNameWithoutExtension(img);
        Check("库里引用数是 1", AttachmentStore.RefCount(hashOfImg) == 1);

        // 引用计数是**存下来的**（用户要的），而 attachment_uses 才是真正的账本。
        // 两者必须永远一致 —— 一旦漂开，表现就是「还有人用的图被删了」，不可逆。
        // 所以这里逐量核对，不只信其中一边。
        long DerivedRefs(string h)
        {
            using var st = Db.Conn.Prepare("SELECT COUNT(*) FROM attachment_uses WHERE hash = ?");
            st.Bind(1, h);
            return st.Step() ? st.Int64(0) : 0;
        }
        Check("**存的引用计数 == 使用表里数出来的**", AttachmentStore.RefCount(hashOfImg) == DerivedRefs(hashOfImg));
        using (var st = Db.Conn.Prepare(
            "SELECT COUNT(*) FROM attachments WHERE refcount <> " +
            "(SELECT COUNT(*) FROM attachment_uses u WHERE u.hash = attachments.hash)"))
        {
            st.Step();
            Check("全表没有一行引用计数是错的", st.Int64(0) == 0);
        }
        Check("使用表记下了「哪条会话用了它」", DerivedRefs(hashOfImg) == 1);

        // 删会话 → 托管附件该回收，用户那个文件一个字节都不许动
        ChatStore.Delete(cid);
        Check("**删会话后托管附件被回收**", !File.Exists(img));
        Check("**用户自己的文件还在**", File.Exists(userDoc));
        Check("会话行没了", ChatStore.Load().All(c => c.Id != cid));
        Check("使用关系跟着会话一起没了", DerivedRefs(hashOfImg) == 0);
        Check("附件行也收掉了",
              Db.Conn.QueryLong("SELECT COUNT(*) FROM attachments WHERE hash = ?", (1, hashOfImg)) == 0);

        try { File.Delete(userDoc); } catch { }

        Console.WriteLine();
        Console.WriteLine("---- 9b. 截图取了没发就删掉 ----");

        // 这正是「截了图、又把它从输入框里删掉、从没发出去」那个场景。
        // 文件在落盘那一刻就登记了一行（引用数 0），所以这里收得掉它。
        string shot = MakePng(Color.Blue);
        string shotHash = Path.GetFileNameWithoutExtension(shot);
        Check("落盘即登记（还没发出去，引用数是 0）", AttachmentStore.RefCount(shotHash) == 0);
        Check("库里有它这一行",
              Db.Conn.QueryLong("SELECT COUNT(*) FROM attachments WHERE hash = ?", (1, shotHash)) == 1);

        AttachmentStore.Release(new Attachment { Kind = "image", Name = "截图.png", Path = shot }, null);
        Check("**从输入框删掉后，文件没了**", !File.Exists(shot));
        Check("库里的行也没了",
              Db.Conn.QueryLong("SELECT COUNT(*) FROM attachments WHERE hash = ?", (1, shotHash)) == 0);

        // 同一个文件要是已被某条消息引用着，就必须留下 —— 「还有别处在用」优先于「用户删了它」
        string kept = MakePng(Color.Green);
        var keepConv = new Conversation { Id = "probe-keep", Title = "k", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        keepConv.Messages.Add(new ChatMessage
        {
            Role = "user", When = DateTime.Now, Text = "带图",
            Attachments = new List<Attachment> { Attachment.ForImage("图.png", kept) },
        });
        ChatStore.Save(keepConv);
        AttachmentStore.Release(new Attachment { Kind = "image", Name = "图.png", Path = kept }, null);
        Check("**已被消息引用的图，删草稿时不会被删**", File.Exists(kept));

        // 红线：没登记过的文件，任何清扫都不许碰
        string stray = Path.Combine(AttachmentStore.Dir, "deadbeef.png");
        File.WriteAllText(stray, "not ours");
        AttachmentStore.Sweep();
        Check("**未登记的文件绝不被清扫删掉**（库损坏重建时靠这条保命）", File.Exists(stray));
        try { File.Delete(stray); } catch { }

        ChatStore.Delete("probe-keep");
        Check("收尾：那张被引用的图随会话一起走了", !File.Exists(kept));

        Console.WriteLine();
        Console.WriteLine("---- 10. 设置读写与加密（真实库）----");
        var s = new AppSettings
        {
            ChatProvider = "探针",
            ChatTemperature = 1.25,
            ChatContextWindow = 65536,
            ChatContextMode = "compact",
        };
        s.ChatProfiles["自定义"] = new Dictionary<string, string>
        {
            ["key"] = "sk-probe-secret-000111222",
            ["model"] = "probe-model",
        };
        SettingsRepo.Save(s);

        using (var st = Db.Conn.Prepare("SELECT value, secret FROM settings WHERE key='chat.profiles'"))
        {
            st.Step();
            Check("chat.profiles 被标记成密文", st.Int64(1) == 1);
            Check("**库里看不到明文 Key**", !st.Text(0).Contains("sk-probe-secret", StringComparison.Ordinal));
        }
        using (var st = Db.Conn.Prepare("SELECT secret FROM settings WHERE key='chat.temperature'"))
        {
            st.Step();
            Check("非敏感项不加密（省得白扛一次解不开的风险）", st.Int64(0) == 0);
        }

        var back2 = SettingsRepo.Load();
        Check("普通设置的往返", back2.ChatTemperature == 1.25 && back2.ChatProvider == "探针"
                               && back2.ChatContextWindow == 65536);
        Check("**敏感字段解密后与原文一致**",
              back2.ChatProfiles.TryGetValue("自定义", out var pr) && pr["key"] == "sk-probe-secret-000111222");

        // CopyFrom 走的是同一张字段表，所以新加的字段不会再出现「改了没反应」
        var dst = new AppSettings { ActiveChatId = "keep-me" };
        dst.CopyFrom(back2);
        Check("CopyFrom 带上了新加的上下文字段",
              dst.ChatContextMode == "compact" && dst.ChatContextWindow == 65536);
        Check("CopyFrom 不覆盖 ActiveChatId（浮窗拿着的是打开那一刻的快照）",
              dst.ActiveChatId == "keep-me");
        Check("CopyFrom 是深拷贝，两份设置不共用同一个字典",
              !ReferenceEquals(dst.ChatProfiles, back2.ChatProfiles));

        ContextMath();
    }

    /// <summary>
    /// 「现在用了多少 token」这个数是怎么来的。这是上下文仪表上那个数字的**唯一来源**，
    /// 而它有两个容易搞错的地方：锚点该不该信、压缩之后口径有没有变。
    /// 两处都只体现在数字上，肉眼看不出来，所以在这里钉住。
    /// </summary>
    private static void ContextMath()
    {
        Console.WriteLine();
        Console.WriteLine("---- 11. 上下文用量计算 ----");

        var cfg = new LlmConfig { SystemPrompt = "你是助手", MaxTokens = 2048 };
        var conv = new Conversation { Id = "probe-ctx", Title = "c" };
        for (int i = 0; i < 3; i++)
            conv.Messages.Add(new ChatMessage { Role = "user", Text = "你好", When = DateTime.Now });

        int est = ContextManager.Used(conv, cfg);
        Check("没有锚点时走全量估算", est > 0);

        // 钉一个远小于估算的「真实值」——模拟估算偏高（系数是故意保守的那些）
        conv.LastPromptTokens = 5;
        conv.AnchorMsgs = conv.Messages.Count;
        Check("**有锚点时以服务端报的真实值为准，不再被高估的估算盖住**",
              ContextManager.Used(conv, cfg) == 5, "实际 " + ContextManager.Used(conv, cfg));

        conv.Messages.Add(new ChatMessage { Role = "assistant", Text = "答", When = DateTime.Now });
        Check("锚点之后新增的那条按估算加上去", ContextManager.Used(conv, cfg) > 5);

        // 压缩：前 3 条被摘要覆盖。**真实流程里压缩总是发生在发请求之前**，
        // 所以紧接着会拿到一次新的真实值、把锚点重新钉到压缩后的口径上。
        conv.CtxSummary = "前面互相打了招呼。";
        conv.CtxSummaryUpto = 3;
        conv.LastPromptTokens = 50;
        conv.AnchorMsgs = conv.Messages.Count;
        Check("压缩后用量反映的是压缩后的历史，不是压缩前的",
              ContextManager.Used(conv, cfg) == 50, "实际 " + ContextManager.Used(conv, cfg));

        // 锚点落在被摘要覆盖的区间之前 = 它量的是另一份历史，必须作废
        conv.LastPromptTokens = 99999;
        conv.AnchorMsgs = 0;
        Check("**锚点早于摘要覆盖区间时自动作废，退回估算**（不然会永远判超、反复压缩）",
              ContextManager.Used(conv, cfg) < 99999, "实际 " + ContextManager.Used(conv, cfg));
    }

    /// <summary>造一张纯色小图并走真实的内容寻址落盘。</summary>
    private static string MakePng(Color c)
    {
        using var bmp = new Bitmap(8, 8);
        using (var g = Graphics.FromImage(bmp)) g.Clear(c);
        return AttachmentStore.Store(bmp);
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("  [OK]   " + what); }
        else
        {
            _fail++;
            Console.WriteLine("  [FAIL] " + what + (detail.Length > 0 ? "  <- " + detail : ""));
        }
    }
}
#endif
