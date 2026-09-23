using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace BangGang;

/// <summary>
/// 应用自己产生的那些图片（截图、粘贴图）的落盘与回收。
///
/// **这里最重要的不是去重，是「什么能删、什么绝对不能删」的分界。**
/// 附件分两类，混为一谈的后果是不可逆的：
///
/// <list type="bullet">
///   <item><b>托管附件</b> —— 截图、粘贴图。字节是<b>我们</b>写进 <see cref="Dir"/> 的，
///     我们拥有它，所以它没人引用时可以删。</item>
///   <item><b>非托管附件</b> —— 用户拖进来 / 选进来的文件。我们<b>只有路径</b>，
///     从没复制过它（<see cref="Attachment.Path"/> 直接指向用户自己的文件）。
///     这类附件<b>永远不删</b> —— 那不是我们的文件，删掉就是删用户的资料。</item>
/// </list>
///
/// 分界不是靠 <see cref="Attachment.Kind"/>（那是给界面用的「图片还是文件」），
/// 而是靠 <see cref="IsManaged"/>：**路径落不落在我们自己的 <see cref="Dir"/> 里**。
/// 这条判据只有一个来源，不会跟界面语义漂。
///
/// 内容寻址（文件名 = 内容的 sha256）带来三件事：同一张图贴两次只存一份；
/// 库里那行的 key 直接从文件名拿、**不用为了记账去重算哈希**；删除时按名字就能定位文件。
/// </summary>
internal static class AttachmentStore
{
    /// <summary>
    /// 托管附件的目录。
    ///
    /// 特意**不**放 <c>%TEMP%</c>：那里的文件随时可能被系统或清理工具删掉，而它现在是
    /// 一条会话消息的附件 —— 重启之后历史还在、图片却读不出来了，用户看到的是一个个空槽。
    /// </summary>
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "images");

    /// <summary>一条附件的库内标识：托管的用内容哈希，非托管的用 <c>p:原始路径</c>。</summary>
    private const string PathKeyPrefix = "p:";

    /// <summary>字节是不是我们自己的（即：这个文件是我们写进 <see cref="Dir"/> 的）。</summary>
    public static bool IsManaged(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string dir = Path.GetFullPath(Dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path);
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
        if (File.Exists(dst)) return dst;

        // 先写临时名再改名：断电时留下的是一个 .tmp，而不是一个**名字是哈希、
        // 内容却只有一半**的文件 —— 后者最恶劣，因为按内容寻址意味着那个名字
        // 从此被一个坏文件占住，同一张图再也存不进来。
        string tmp = dst + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, dst, overwrite: false); }
        catch (IOException) { /* 已经有同名文件了（内容相同），丢掉临时文件即可 */ }
        if (File.Exists(tmp)) File.Delete(tmp);
        return dst;
    }

    /// <summary>当下有多少条消息引用着这个附件。0 = 可以回收。</summary>
    public static long RefCount(string hash)
    {
        using var st = Db.Conn.Prepare("SELECT COUNT(*) FROM message_attachments WHERE hash = ?");
        st.Bind(1, hash);
        return st.Step() ? st.Int64(0) : 0;
    }

    /// <summary>
    /// 回收一批附件文件。**必须在删那条会话的事务提交之后调**（见 <see cref="ChatStore.Delete"/>）。
    ///
    /// 只删三类都满足的：**是托管附件**（哈希形态 + 文件确实在 <see cref="Dir"/> 里）、
    /// **引用数已经归零**、且**不在 <paramref name="keepPaths"/> 里**。
    /// 任何一条不满足就跳过 —— 漏删只是占几 KB 磁盘，误删是不可逆的。
    ///
    /// <paramref name="keepPaths"/> 是调用方声明「这些文件现在别动」，
    /// 用途是输入框草稿：草稿里的图还没被任何消息引用（引用数必然是 0），
    /// 但它显然不该被回收。
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
            if (!IsManaged(file)) continue;      // 双保险：拼出来的路径必须真的在 images/ 里

            try
            {
                if (!File.Exists(file)) continue;
                if (keep != null && keep.Contains(Full(file))) continue;   // 草稿还拿着它
                if (RefCount(h) != 0) continue;  // 还有别的会话用着
                File.Delete(file);
            }
            catch { /* 删不掉就留着，下次删会话还会再捡到它 */ }
        }
    }

    /// <summary>绝对路径归一。比路径一律走它 —— 大小写与相对路径的差异会让上面的比较失手。</summary>
    private static string Full(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }
}
