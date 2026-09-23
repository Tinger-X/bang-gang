using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace BangGang;

/// <summary>
/// 附件（截图、粘贴图、用户拖进来的文件）的落盘与回收。
///
/// **这里最重要的不是去重，是「什么能删、什么绝对不能删」的分界。**
/// 附件分两类，混为一谈的后果是不可逆的：
///
/// <list type="bullet">
///   <item><b>托管附件</b> —— 截图、粘贴图。字节是<b>我们</b>写进 <see cref="Dir"/> 的，
///     我们拥有它，所以它没人引用时可以删。</item>
///   <item><b>非托管附件</b> —— 用户拖进来的文档与音频。我们<b>只有路径</b>，
///     从没复制过它（<see cref="Attachment.Path"/> 直接指向用户自己的文件）。
///     这类附件<b>永远不删</b> —— 那不是我们的文件，删掉就是删用户的资料。</item>
/// </list>
///
/// 分界不是靠 <see cref="Attachment.Kind"/>（那是给界面用的「图片还是文件」），
/// 而是靠 <see cref="IsManaged"/>：**路径落不落在我们自己的 <see cref="Dir"/> 里**。
/// 这条判据只有一个来源，不会跟界面语义漂。
///
/// 目录叫 <c>attachments</c> 而不是 <c>images</c>：它是**所有附件的家**，
/// 眼下只有图片真的被复制进来（文档与音频可能几十 MB，同步拷一份会卡住界面，
/// 这一点是权衡过的），但名字先摆正，将来要收别的类型不用再动一次目录。
///
/// 内容寻址（文件名 = 内容的 sha256）带来三件事：同一张图贴两次只存一份；
/// 库里那行的 key 直接从文件名拿、**不用为了记账去重算哈希**；删除时按名字就能定位文件。
/// 用 SHA256 而不是 MD5：两者都是一次过字节，开销一样，而 SHA256 抗碰撞 ——
/// 拿哈希当身份标识的时候，这是唯一的区别，也是唯一重要的区别。
/// </summary>
internal static class AttachmentStore
{
    /// <summary>
    /// 附件目录。
    ///
    /// 特意**不**放 <c>%TEMP%</c>：那里的文件随时可能被系统或清理工具删掉，而它现在是
    /// 一条会话消息的附件 —— 重启之后历史还在、图片却读不出来了，用户看到的是一个个空槽。
    /// </summary>
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "attachments");

    /// <summary>一条非托管附件的库内标识：<c>p:原始路径</c>。</summary>
    private const string PathKeyPrefix = "p:";

    /// <summary>字节是不是我们自己的（即：这个文件是我们写进 <see cref="Dir"/> 的）。</summary>
    public static bool IsManaged(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string dir = Full(Dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Full(path);
            string? parent = Path.GetDirectoryName(full);
            return parent != null &&
                   string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                 dir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // 路径非法就按「不是我们的」处理 —— 保守的一侧
    }

    /// <summary>
    /// 这条附件在库里叫什么。托管附件的文件名就是哈希，所以这一步是**零成本**的
    /// ——这点很重要：<c>ChatStore.Save</c> 每发一条消息都会跑一遍，
    /// 若这里要去重算文件哈希，就成了「每发一次消息读一遍所有图片」。
    /// </summary>
    public static string KeyOf(Attachment a)
    {
        if (IsManaged(a.Path)) return Path.GetFileNameWithoutExtension(a.Path!);
        return PathKeyPrefix + (a.Path ?? a.Name);
    }

    /// <summary>64 位十六进制 —— 认得出是我们自己算的 sha256，也就认得出「这条是托管附件」。</summary>
    private static bool LooksLikeHash(string s)
    {
        if (s.Length != 64) return false;
        foreach (char c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// 把一张图存进 <see cref="Dir"/>，按内容寻址后返回它的路径。
    /// **同一张图第二次存会命中同一个文件**（这正是「同一张图被两条会话引用」的来源）。
    ///
    /// 落盘的同时就在库里登记一行（引用数 0）。这一点是刻意的：
    /// 「用户截了图、又把它从输入框里删掉」以及「截完图直接关掉程序」这两种情况，
    /// 文件都已经在磁盘上了、却还没被任何消息引用 —— 只有先登记，
    /// 启动时的清扫才认得出它们是孤儿。**没登记的托管文件永远不会被删**（见 <see cref="Sweep"/>），
    /// 所以漏登记只会留下垃圾，不会误删。
    /// </summary>
    public static string Store(Image img)
    {
        Directory.CreateDirectory(Dir);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            img.Save(ms, ImageFormat.Png);
            bytes = ms.ToArray();
        }
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string dst = Path.Combine(Dir, sha + ".png");

        if (!File.Exists(dst))
        {
            // 先写临时名再改名：断电时留下的是一个 .tmp，而不是一个**名字是哈希、
            // 内容却只有一半**的文件 —— 后者最恶劣，因为按内容寻址意味着那个名字
            // 从此被一个坏文件占住，同一张图再也存不进来。
            string tmp = dst + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, dst, overwrite: false); }
            catch (IOException) { /* 已经有同名文件了（内容相同），丢掉临时文件即可 */ }
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        Register(sha, bytes.LongLength);
        return dst;
    }

    /// <summary>登记一行（已存在就不动）。名字留空，等消息落库时再补 —— 那时才知道用户给它起的名字。</summary>
    private static void Register(string hash, long size)
    {
        if (!Db.Available) return;
        try
        {
            using var st = Db.Conn.Prepare(
                "INSERT OR IGNORE INTO attachments(hash, managed, name, size, created_at, refcount) " +
                "VALUES(?,1,'',?,?,0)");
            st.Bind(1, hash).Bind(2, size).Bind(3, DateTime.Now.ToString("o"));
            st.Step();
        }
        catch { /* 登记不上只是可能留个孤儿，不该影响「把图存下来」这件事 */ }
    }

    /// <summary>当下有多少个会话在用它。0 = 可以回收。</summary>
    public static long RefCount(string hash)
    {
        using var st = Db.Conn.Prepare("SELECT refcount FROM attachments WHERE hash = ?");
        st.Bind(1, hash);
        return st.Step() ? st.Int64(0) : 0;
    }

    /// <summary>
    /// 从输入框的草稿里移除一条附件时调用。
    ///
    /// 三种情况要分开：
    /// <list type="number">
    ///   <item>非托管（用户拖进来的文档）—— 什么都不做，那不是我们的文件。</item>
    ///   <item>还被别的会话或别的草稿拿着 —— 文件留着，只可能删掉那一行无关的记录。</item>
    ///   <item>确实没人要了 —— 删文件、删行。</item>
    /// </list>
    ///
    /// <paramref name="held"/> 是**别处还拿着**的那些路径（当前各条会话的草稿）。
    /// 为什么需要它：草稿里的图还没有任何消息引用它，引用数必然是 0，
    /// 但用户只是把它从这一条草稿里拿掉、并没有想让文件消失（同一条图可能正躺在
    /// 另一条会话的草稿里）。
    /// </summary>
    public static void Release(Attachment a, IReadOnlyCollection<string>? held)
    {
        if (!IsManaged(a.Path)) return;
        if (!Db.Available) return;

        string full = Full(a.Path!);
        if (held != null)
            foreach (string p in held)
                if (!string.IsNullOrEmpty(p) && string.Equals(Full(p), full, StringComparison.OrdinalIgnoreCase))
                    return;

        string hash = Path.GetFileNameWithoutExtension(a.Path!);
        if (!LooksLikeHash(hash)) return;

        try
        {
            using (var st = Db.Conn.Prepare("SELECT refcount FROM attachments WHERE hash = ?"))
            {
                st.Bind(1, hash);
                if (st.Step() && st.Int64(0) > 0) return;   // 还有会话在用
            }
            using (var st = Db.Conn.Prepare("DELETE FROM attachments WHERE hash = ?"))
            {
                st.Bind(1, hash);
                st.Step();
            }
        }
        catch { return; }

        // 先删库、后删文件 —— 顺序与 ChatStore.Delete 同一条理由：
        // 崩在中间只留一个几 KB 的孤儿文件，反过来则是「记录没了、文件也没了」。
        try { File.Delete(full); } catch { /* 删不掉就留着，下次删会话还会再捡到 */ }
    }

    /// <summary>
    /// 回收一批附件文件。**必须在删那条会话的事务提交之后调**（见 <see cref="ChatStore.Delete"/>）。
    ///
    /// 只删三类都满足的：**是托管附件**（哈希形态 + 文件确实在 <see cref="Dir"/> 里）、
    /// **引用数已经归零**、且**不在 <paramref name="keepPaths"/> 里**。
    /// 任何一条不满足就跳过 —— 漏删只是占几 KB 磁盘，误删是不可逆的。
    /// </summary>
    public static void Reclaim(IReadOnlyList<string> hashes, IReadOnlyCollection<string>? keepPaths)
    {
        if (hashes.Count == 0) return;
        if (!Db.Available) return;   // 库都读不了，就没资格判「没人引用」—— 那正是红线

        HashSet<string>? keep = null;
        if (keepPaths is { Count: > 0 })
        {
            keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in keepPaths)
                if (!string.IsNullOrEmpty(p)) keep.Add(Full(p));
        }

        foreach (string h in hashes)
        {
            // 非托管附件的 key 是 "p:..."，这一句就把它们全挡在外面了
            if (!LooksLikeHash(h)) continue;

            string file = Path.Combine(Dir, h + ".png");
            if (!IsManaged(file)) continue;      // 双保险：拼出来的路径必须真的在 attachments/ 里

            try
            {
                if (keep != null && keep.Contains(Full(file))) continue;   // 草稿还拿着它
                if (RefCount(h) != 0) continue;  // 还有别的会话用着
                if (File.Exists(file)) File.Delete(file);
                using var st = Db.Conn.Prepare("DELETE FROM attachments WHERE hash = ?");
                st.Bind(1, h);
                st.Step();
            }
            catch { /* 删不掉就留着，下次删会话还会再捡到它 */ }
        }
    }

    /// <summary>
    /// 启动时扫一次孤儿：库里有行、引用数为 0 的托管附件，连同文件一起收掉。
    ///
    /// **只在启动时做**，因为此刻一定没有草稿（草稿是进程内状态），
    /// 「引用数为 0」才等于「真的没人要」。平时那条路是各自的精确回收。
    ///
    /// **红线：绝不去删「库里根本没有登记」的文件。** 只处理表里有行的。
    /// 反过来的话，数据库一旦损坏被重建，`attachments/` 下所有文件都会变成
    /// 「没登记」，那样会一次删光用户全部截图 —— 而那些文件其实还好好的。
    /// 目录里多出来的文件就让它留着，与「宁可留着也不误删」这条一贯的取舍一致。
    /// </summary>
    public static void Sweep()
    {
        if (!Db.Available) return;
        try
        {
            var doomed = new List<string>();
            using (var st = Db.Conn.Prepare("SELECT hash FROM attachments WHERE managed = 1 AND refcount = 0"))
                while (st.Step()) doomed.Add(st.Text(0));
            if (doomed.Count == 0) return;

            using (var st = Db.Conn.Prepare("DELETE FROM attachments WHERE managed = 1 AND refcount = 0"))
                st.Step();

            Reclaim(doomed, null);
            Trace.Log($"attachment sweep: removed {doomed.Count} orphan(s)");
        }
        catch { /* 清扫失败不影响使用 */ }
    }

    /// <summary>绝对路径归一。比路径一律走它 —— 大小写与相对路径的差异会让上面的比较失手。</summary>
    private static string Full(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }
}
