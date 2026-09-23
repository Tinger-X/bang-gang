using System.Runtime.InteropServices;
using System.Text;

namespace BangGang;

/// <summary>
/// 敏感字段的加密：Windows DPAPI（<c>CryptProtectData</c>），当前用户范围。
///
/// 选它的理由：密钥由 Windows 从**当前账户的登录凭据**派生，我们既不生成也不保存任何密钥 ——
/// 换来的效果是「同一个文件拷到别的机器或别的账户上，密文就是一串解不开的字节」，
/// 而代价只有一条：**换机器/重装系统后解不开**。那条代价的表现见 <see cref="TryUnprotect"/>。
///
/// 密文形如 <c>dp:v1:&lt;base64&gt;</c>。前缀不是装饰：<c>settings.secret</c> 那一列
/// 已经标了「这行是密文」，但如果哪天那个标志写错了（迁移、手工改库、将来的新字段漏登记），
/// 前缀能让解密**当场认出来并老实返回失败**，而不是把 base64 当明文塞回界面 ——
/// 后者用户看到的是一串乱码 Key，比空着更让人摸不着头脑。
/// </summary>
internal static class Dpapi
{
    private const string Prefix = "dp:v1:";

    /// <summary><c>CRYPTPROTECT_UI_FORBIDDEN</c>：不许弹任何 UI。
    /// 本程序是无窗口后台逻辑在调它，弹窗会卡在一个没人看得见的地方。</summary>
    private const int UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, [MarshalAs(UnmanagedType.LPWStr)] string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr h);

    /// <summary>是不是本程序写下的密文。</summary>
    public static bool IsProtected(string? s) => s is not null && s.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 加密成可入库的字符串。空串原样返回 —— 没有秘密要保护，也就不必造一段密文出来
    /// （否则「没设置过」和「设置成空」会变成两个不同的密文，白白让设置表变脏）。
    /// 失败抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";

        byte[] raw = Encoding.UTF8.GetBytes(plain);
        IntPtr buf = Marshal.AllocHGlobal(raw.Length);
        try
        {
            Marshal.Copy(raw, 0, buf, raw.Length);
            var inBlob = new DataBlob { CbData = raw.Length, PbData = buf };
            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, out DataBlob outBlob))
                throw new InvalidOperationException("加密失败（Win32 错误码 " + Marshal.GetLastWin32Error() + "）");
            try
            {
                return Prefix + Convert.ToBase64String(Take(outBlob));
            }
            finally { LocalFree(outBlob.PbData); }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// 解密。**解不开不是异常路径，是预期情况**：用户换了机器、重装了系统、
    /// 或者用另一个 Windows 账户打开了这份数据，DPAPI 就是解不开的。
    ///
    /// 所以这里返回 false 而不是抛 —— 调用方据此**把那个字段当成空的**（提示用户重填一次），
    /// 而不是让整份设置加载失败。**返回 false 时调用方必须保留原来的密文原文**：
    /// 用空串覆盖掉，用户下次就真的丢了那个 Key，而它本来只是「这台机器上解不开」而已。
    /// </summary>
    public static bool TryUnprotect(string? s, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(s)) return true;          // 空就是空，没什么可解
        if (!IsProtected(s)) return false;                 // 不是我们写的密文，别猜

        byte[] enc;
        try { enc = Convert.FromBase64String(s[Prefix.Length..]); }
        catch (FormatException) { return false; }

        if (enc.Length == 0) return false;
        IntPtr buf = Marshal.AllocHGlobal(enc.Length);
        try
        {
            Marshal.Copy(enc, 0, buf, enc.Length);
            var inBlob = new DataBlob { CbData = enc.Length, PbData = buf };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UI_FORBIDDEN, out DataBlob outBlob))
                return false;
            try
            {
                if (outBlob.CbData <= 0) { plain = ""; return true; }
                byte[] raw = new byte[outBlob.CbData];
                Marshal.Copy(outBlob.PbData, raw, 0, outBlob.CbData);
                plain = Encoding.UTF8.GetString(raw);
                return true;
            }
            finally { LocalFree(outBlob.PbData); }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static byte[] Take(DataBlob b)
    {
        if (b.CbData <= 0) return Array.Empty<byte>();
        byte[] a = new byte[b.CbData];
        Marshal.Copy(b.PbData, a, 0, b.CbData);
        return a;
    }
}
