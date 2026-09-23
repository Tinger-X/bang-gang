using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static BangGang.Sqlite;   // 下面对 DLL 的调用一律裸名，前缀写着太吵

namespace BangGang;

/// <summary>
/// SQLite 报错。带着原始返回码，调用方需要区分对待时（比如 <see cref="Sqlite.BUSY"/>）
/// 可以按码分支，不需要去解析中文文案。
/// </summary>
internal sealed class SqliteException : Exception
{
    public int Code { get; }

    public SqliteException(string message, int code) : base(message) => Code = code;
}

/// <summary>
/// <c>winsqlite3.dll</c> 的互操作层：**只做「把 C API 搬进来」这一件事**，
/// 不含任何建表 / 业务逻辑（那些在 <see cref="Db"/> 与各 Store 里）。
///
/// 为什么是这个 DLL：Windows 10 build 10586（1511）起系统自带，而 .NET 8 桌面运行时
/// 最低就要 Win10 1607 —— **跑得动本程序的机器必然有它**。代价是不受我们控制：
/// 这个 DLL 由 Windows Update 单独升级，不同机器上可能是 3.23 也可能是 3.51。
/// 所以：
///
/// 1. <see cref="Db"/> 建表时**只用 SQLite 3.8 时代就有的 SQL**，不碰
///    <c>UPSERT</c>(3.24+)、<c>RETURNING</c>(3.35+)、<c>STRICT</c> 表(3.37+)、窗口函数(3.25+)。
/// 2. 启动时用 <see cref="Probe"/> **实测**能不能用，而不是假设。不通过就报一句中文，绝不崩。
///
/// 只用 <c>DllImport</c> 不用 <c>LibraryImport</c>：后者的源码生成器要求 unsafe，
/// 而本项目的 <c>AllowUnsafeBlocks</c> 是关的（与 <c>Audio/AudioApi.cs</c> 同一取舍）。
///
/// **每条声明都钉了 <c>[DefaultDllImportSearchPaths(System32)]</c>**（它只能打在方法或
/// 程序集上，打不到类型上，所以这里是一遍遍重复）。这不是洁癖，是堵一个真实的劫持面：
/// 本程序装在 <c>{localappdata}\Programs\BangGang</c>（每用户可写 —— 装 Program Files
/// 会因为要写设置而失败，见 installer 的注释），而 <c>winsqlite3.dll</c> **不在
/// KnownDLLs 名单里**。默认的搜索顺序会先看程序所在目录，于是往安装目录丢一个同名 DLL
/// 就能被加载执行 —— 一个普通用户权限就能完成的代码执行。钉死 System32 之后这条路就没了。
/// </summary>
internal static class Sqlite
{
    private const string Lib = "winsqlite3.dll";

    // ---- sqlite3_step / 各接口的返回码 ----
    public const int OK = 0;
    public const int ROW = 100;
    public const int DONE = 101;

    // ---- 会单独给中文文案的几个错误码（其余走通用文案）----
    public const int BUSY = 5;
    public const int LOCKED = 6;
    public const int READONLY = 8;
    public const int CORRUPT = 11;
    public const int CANTOPEN = 14;
    public const int CONSTRAINT = 19;
    public const int NOTADB = 26;

    // ---- sqlite3_open_v2 的 flags ----
    public const int OPEN_READWRITE = 2;
    public const int OPEN_CREATE = 4;

    // ---- sqlite3_column_type 的返回值 ----
    public const int TYPE_INTEGER = 1;
    public const int TYPE_FLOAT = 2;
    public const int TYPE_TEXT = 3;
    public const int TYPE_BLOB = 4;
    public const int TYPE_NULL = 5;

    /// <summary>
    /// <c>sqlite3_bind_*</c> 末尾那个析构器参数。传 <c>SQLITE_TRANSIENT</c>
    /// （即 <c>(void(*)(void*))-1</c>）的意思是「你立刻拷一份」——于是调用方传进来的
    /// 缓冲区在这次调用返回后就可以回收，我们不必手工管它的生命周期。
    /// </summary>
    public static readonly IntPtr Transient = new(-1);

    /// <summary>能用这个 DLL 的最低 SQLite 版本（3.8.0）。<see cref="Db"/> 建表用的 SQL 不超出它。</summary>
    public const int MinVersionNumber = 3008000;

    /// <summary>
    /// 探测要用的全部导出符号。<see cref="Probe"/> 会逐个 <c>GetProcAddress</c>，
    /// 少一个就报出来 —— 光靠 <c>DllImport</c> 只能测到「实际调到的那一个」，
    /// 漏掉的符号会在运行到某条冷路径时才炸，那时候用户已经在用了。
    /// </summary>
    private static readonly string[] Required =
    {
        "sqlite3_open_v2", "sqlite3_close_v2", "sqlite3_exec", "sqlite3_free",
        "sqlite3_errmsg", "sqlite3_errcode", "sqlite3_extended_errcode",
        "sqlite3_extended_result_codes", "sqlite3_prepare_v2", "sqlite3_step",
        "sqlite3_finalize", "sqlite3_reset", "sqlite3_clear_bindings",
        "sqlite3_bind_null", "sqlite3_bind_int64", "sqlite3_bind_double",
        "sqlite3_bind_text", "sqlite3_bind_blob",
        "sqlite3_column_count", "sqlite3_column_type", "sqlite3_column_int64",
        "sqlite3_column_double", "sqlite3_column_text", "sqlite3_column_bytes",
        "sqlite3_column_blob", "sqlite3_column_name",
        "sqlite3_changes", "sqlite3_last_insert_rowid", "sqlite3_busy_timeout",
        "sqlite3_libversion", "sqlite3_libversion_number",
    };

    #region 原始声明

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_open_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_close_v2(IntPtr db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_exec(
        IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sqlite3_free(IntPtr p);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_errcode(IntPtr db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_extended_errcode(IntPtr db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_extended_result_codes(IntPtr db, int on);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_prepare_v2(
        IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_step(IntPtr stmt);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_finalize(IntPtr stmt);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_reset(IntPtr stmt);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_clear_bindings(IntPtr stmt);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_null(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_text(
        IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string? value, int n, IntPtr destructor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[]? value, int n, IntPtr destructor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_count(IntPtr stmt);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_type(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_column_int64(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern double sqlite3_column_double(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_bytes(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_name(IntPtr stmt, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_changes(IntPtr db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_last_insert_rowid(IntPtr db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_libversion();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_libversion_number();

    #endregion

    /// <summary>把 SQLite 的返回码翻成一句中文的 <see cref="SqliteException"/>。除 OK/ROW/DONE 外都算失败。</summary>
    public static void Check(int rc, IntPtr db)
    {
        if (rc == OK || rc == ROW || rc == DONE) return;
        throw new SqliteException(Describe(rc, db), rc);
    }

    /// <summary>
    /// 返回码 → 中文文案。几个后果差异很大的码单独说清楚，其余带上 DLL 自己的原文
    /// （英文，但对排查有用，且用户回报问题时会把它一起贴出来）。
    /// </summary>
    public static string Describe(int rc, IntPtr db)
    {
        string raw = raw_msg(db);
        string tail = raw.Length > 0 ? "：" + raw : "";
        switch (rc & 0xFF)
        {
            case BUSY:
            case LOCKED:
                return "数据库正被占用，请稍后重试" + tail;
            case READONLY:
                return "数据库文件是只读的 —— 这个位置不允许写入" + tail;
            case CORRUPT:
                return "数据库文件已损坏" + tail;
            case NOTADB:
                return "这个文件不是 SQLite 数据库" + tail;
            case CANTOPEN:
                return "打不开数据库文件" + tail;
            case CONSTRAINT:
                return "违反数据约束" + tail;
            default:
                return "数据库操作失败（错误码 " + rc + "）" + tail;
        }
    }

    /// <summary>取 <c>sqlite3_errmsg</c> 的原文。它是 NUL 结尾的 UTF-8。</summary>
    public static string raw_msg(IntPtr db)
    {
        if (db == IntPtr.Zero) return "";
        IntPtr p = sqlite3_errmsg(db);
        return p == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(p) ?? "");
    }

    /// <summary>读一个 TEXT 列。**必须带长度** —— SQLite 的 TEXT 允许内嵌 NUL，按 NUL 截断会静默丢字符。</summary>
    public static string ColumnText(IntPtr stmt, int index)
    {
        IntPtr p = sqlite3_column_text(stmt, index);
        if (p == IntPtr.Zero) return "";           // NULL 列
        int n = sqlite3_column_bytes(stmt, index);
        if (n <= 0) return "";
        byte[] buf = new byte[n];
        Marshal.Copy(p, buf, 0, n);
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>本机 SQLite 版本号，形如 <c>3.43.2</c>。取不到时返回空串。</summary>
    public static string VersionText()
    {
        try
        {
            IntPtr p = sqlite3_libversion();
            return p == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(p) ?? "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "";
        }
    }

    #region 探测

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    /// <summary><c>LOAD_LIBRARY_SEARCH_SYSTEM32</c>：只在 System32 里找。</summary>
    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    /// <summary>
    /// 实测这个 DLL 在不在、符号齐不齐、版本够不够。**不看返回值当假设，真的去查**：
    /// <c>LoadLibrary</c> 拿模块句柄，再逐个 <c>GetProcAddress</c>。
    ///
    /// 为什么不靠「调用一下试试」：<c>DllImport</c> 是按调用点惰性绑定的，
    /// 试出来的是「恰好走到的那一个符号」，漏掉的会在将来某条冷路径上才炸。
    ///
    /// 这里也用 <c>LOAD_LIBRARY_SEARCH_SYSTEM32</c>，与类上那个
    /// <see cref="DefaultDllImportSearchPathsAttribute"/> 保持**同一套搜索规则** ——
    /// 两边规则不一致的话，探针会对着一个被掉包的 DLL 报「一切正常」，
    /// 而真正干活的调用走的是另一个文件。
    /// </summary>
    public static bool Probe(out string reason)
    {
        reason = "";
        IntPtr mod;
        try
        {
            mod = LoadLibraryExW(Lib, IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);
        }
        catch (Exception ex)
        {
            reason = "加载 " + Lib + " 失败：" + ex.Message;
            return false;
        }
        if (mod == IntPtr.Zero)
        {
            reason = "系统里没有 " + Lib + "。本程序需要 Windows 10 1607 或更新的版本。";
            return false;
        }

        foreach (string name in Required)
        {
            if (GetProcAddress(mod, name) != IntPtr.Zero) continue;
            reason = Lib + " 里缺少 " + name + "，这个系统上的 SQLite 组件不完整。";
            return false;
        }

        int ver = sqlite3_libversion_number();
        if (ver < MinVersionNumber)
        {
            reason = "系统自带的 SQLite 版本过低（" + VersionText() + "），至少需要 3.8.0。";
            return false;
        }
        return true;
    }

    #endregion
}

/// <summary>
/// 一个打开的数据库连接（<c>sqlite3*</c>）。
///
/// **这里用 <see cref="SafeHandle"/> 是刻意的例外**：本项目至今一处都没有
/// （WASAPI 那边的 COM 对象靠 RCW 的终结器释放就够了）。但 <c>sqlite3*</c> 与
/// <c>sqlite3_stmt*</c> 是裸指针，忘了关/忘了 finalize 不只是内存泄漏 ——
/// 语句没 finalize 会占着库的锁，表现是后面所有写操作莫名失败，而那个现场
/// 离真正的错误点隔着很远。SafeHandle 让「忘了写 using」退化成一次
/// 非确定但**必然会发生**的释放，而不是永久泄漏。
///
/// 并发：本项目的数据库访问**全部在 UI 线程**上（见 <see cref="Db"/> 的注释），
/// 这个类型本身不做额外同步。
/// </summary>
internal sealed class SqliteDb : SafeHandleZeroOrMinusOneIsInvalid
{
    private SqliteDb(IntPtr h) : base(true) => SetHandle(h);

    /// <summary>打开（不存在就创建）。失败抛 <see cref="SqliteException"/>。</summary>
    public static SqliteDb Open(string path)
    {
        int rc = sqlite3_open_v2(path, out IntPtr p, OPEN_READWRITE | OPEN_CREATE, IntPtr.Zero);
        if (rc == OK) return new SqliteDb(p);

        // 失败时 sqlite3 往往仍然给了一个句柄，必须关掉。**先取 errmsg 再关** ——
        // 关完再问就成 use-after-free 了。
        string msg = raw_msg(p);
        if (p != IntPtr.Zero) sqlite3_close_v2(p);
        throw new SqliteException("打不开数据库" + (msg.Length > 0 ? "：" + msg : "") + "（" + path + "）", rc);
    }

    protected override bool ReleaseHandle()
    {
        sqlite3_close_v2(handle);
        return true;   // 从终结器路径调过来，这里**不能**抛
    }

    /// <summary>执行一条或多条语句，不取结果。建表、PRAGMA、BEGIN/COMMIT 都走它。</summary>
    public void Exec(string sql)
    {
        int rc = sqlite3_exec(handle, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr err);
        if (rc == OK) return;
        // sqlite3_exec 的错误串是它自己 malloc 的，用完要还
        string msg = err != IntPtr.Zero ? (Marshal.PtrToStringUTF8(err) ?? "") : raw_msg(handle);
        if (err != IntPtr.Zero) sqlite3_free(err);
        if (msg.Length == 0) msg = Describe(rc, handle);
        throw new SqliteException("执行 SQL 失败：" + msg, rc);
    }

    /// <summary>编译一条语句。</summary>
    public SqliteStmt Prepare(string sql)
    {
        int rc = sqlite3_prepare_v2(handle, sql, -1, out IntPtr stmt, IntPtr.Zero);
        if (rc != OK) throw new SqliteException("SQL 编译失败：" + raw_msg(handle) + "（" + sql + "）", rc);
        return new SqliteStmt(this, stmt);
    }

    /// <summary>跑一条只返回单个整数的语句（<c>PRAGMA user_version</c> / <c>SELECT COUNT(*)</c>）。</summary>
    public long QueryLong(string sql, params (int Index, object Value)[] args)
    {
        using var st = Prepare(sql);
        foreach (var (i, v) in args) st.Bind(i, v);
        return st.Step() ? st.Int64(0) : 0;
    }

    public long Changes => sqlite3_changes(handle);

    /// <summary>刚插进去那一行的 rowid（同一连接上，最近一次成功的 INSERT）。</summary>
    public long LastInsertRowid => sqlite3_last_insert_rowid(handle);

    /// <summary>事务。**不要嵌套** —— SQLite 不支持，嵌套的 BEGIN 会直接报错。</summary>
    public void Begin() => Exec("BEGIN");

    public void Commit() => Exec("COMMIT");

    public void Rollback()
    {
        // 回滚本身失败（比如事务早就没了）不该盖住调用方真正要抛的那个异常
        try { Exec("ROLLBACK"); } catch (SqliteException) { }
    }
}

/// <summary>一条编译好的语句（<c>sqlite3_stmt*</c>）。用法见 <see cref="SqliteDb.Prepare"/>。</summary>
internal sealed class SqliteStmt : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly SqliteDb _db;   // 只为出错时能取 errmsg；同时让连接不会先于语句被回收

    internal SqliteStmt(SqliteDb db, IntPtr h) : base(true)
    {
        _db = db;
        SetHandle(h);
    }

    protected override bool ReleaseHandle()
    {
        sqlite3_finalize(handle);
        return true;   // 同上，终结器路径不能抛
    }

    private void Chk(int rc)
    {
        if (rc == OK) return;
        throw new SqliteException(Describe(rc, _db.DangerousGetHandle()), rc);
    }

    // ---- 绑定（索引从 1 开始，这是 SQLite 的约定）----

    public SqliteStmt Bind(int i, string? v)
    {
        // null 要走 bind_null，不能当空串 —— 「没设置过」和「设成了空」在设置表里是两回事
        Chk(v is null ? sqlite3_bind_null(handle, i)
                      : sqlite3_bind_text(handle, i, v, -1, Transient));
        return this;
    }

    public SqliteStmt Bind(int i, long v)
    {
        Chk(sqlite3_bind_int64(handle, i, v));
        return this;
    }

    public SqliteStmt Bind(int i, bool v) => Bind(i, v ? 1L : 0L);

    public SqliteStmt Bind(int i, double v)
    {
        Chk(sqlite3_bind_double(handle, i, v));
        return this;
    }

    /// <summary>给 <c>QueryLong</c> 那种「按位置传参」的便利重载用。</summary>
    public SqliteStmt Bind(int i, object v) => v switch
    {
        string s => Bind(i, s),
        long n => Bind(i, n),
        int n => Bind(i, (long)n),
        bool b => Bind(i, b),
        null => Bind(i, (string?)null),
        _ => Bind(i, v.ToString() ?? ""),
    };

    // ---- 取值 ----

    /// <summary>走一步。有行返回 true，结束返回 false，出错抛。</summary>
    public bool Step()
    {
        int rc = sqlite3_step(handle);
        if (rc == ROW) return true;
        if (rc == DONE) return false;
        Chk(rc);
        return false;   // Chk 一定抛了，这里只为让编译器满意
    }

    /// <summary>复位以便复用。**顺带清掉全部绑定** —— 不清的话上一轮的参数会漏进下一轮，
    /// 这种错很难看出来（值看着「对」但其实是上一次的）。</summary>
    public void Reset()
    {
        sqlite3_reset(handle);
        sqlite3_clear_bindings(handle);
    }

    public string Text(int i) => ColumnText(handle, i);
    public long Int64(int i) => sqlite3_column_int64(handle, i);
    public double Double(int i) => sqlite3_column_double(handle, i);
    public int TypeOf(int i) => sqlite3_column_type(handle, i);
    public bool IsNull(int i) => sqlite3_column_type(handle, i) == TYPE_NULL;
    public int ColumnCount => sqlite3_column_count(handle);

    /// <summary>列名。给探针按名字打表用，业务代码一律按下标取。</summary>
    public string ColumnName(int i)
    {
        IntPtr p = sqlite3_column_name(handle, i);
        return p == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(p) ?? "");
    }
}
