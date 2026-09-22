using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace BangGang;

/// <summary>查到的一个新版本，以及该下哪个包。</summary>
/// <param name="Version">服务端报的版本号，形如 <c>v0.9.26</c>。</param>
/// <param name="Variant">档位 id（<c>with-runtime</c> / <c>without-runtime</c>）。</param>
/// <param name="Sha256">服务端给的校验值；<b>可能是 null</b>（见 <see cref="Updater.Verify"/>）。</param>
internal sealed record UpdateInfo(string Version, string Variant, string FileName, long Size, string? Sha256);

/// <summary>
/// 检查更新：问官网的 <c>/api/stats</c>（版本号与发布物信息都在那儿，见
/// <c>web/functions/api/stats.js</c>），挑一个档位下载，核对完整性，然后交给用户点确认才运行。
///
/// 三件事值得写下来：
///
/// 1. **下哪个档位由本机决定。** 官网有两个包，差 17 倍体积（自带运行时 49MB / 依赖已装运行时 2.8MB），
///    而用户无从判断该选哪个。所以这里去看注册表里有没有 .NET 8 桌面运行时，有就下小的那个。
///
/// 2. **下完的东西是要运行的，所以必须核对。** 校验值由 <c>publish-artifacts.ps1</c> 上传到
///    R2 的 <c>&lt;app&gt;/sha256.json</c>、经 <c>/api/stats</c> 发出来。服务端没给校验值时
///    （老发布物上传时还没有那个文件）只核对大小并**明说未校验** —— 不把「没有校验值」
///    谎报成「校验通过」，也不当成失败把更新挡死。
///
/// 3. **不替用户运行。** 下载完只把路径返回给界面，由界面把按钮换成「运行安装程序」等第二次点击。
///    本程序自己去拉起一个 exe 这件事，值得多一次明确的确认。
/// </summary>
internal static class Updater
{
    private const string StatsUrl = "https://bang-gang.tin.edu.kg/api/stats";

    /// <summary>两个档位 id，与 <c>web/functions/_lib/site.js</c> 里的 variants 一一对应。</summary>
    private const string WithRuntime = "with-runtime";
    private const string WithoutRuntime = "without-runtime";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 问一次官网。返回 <c>(null, 说明)</c> 表示「已经是最新」或「查不了」，
    /// 两种都由 <paramref name="message"/> 说清 —— 界面只管把这句话显示出来。
    /// </summary>
    public static async Task<(UpdateInfo? Info, string Message)> CheckAsync(string current, CancellationToken ct)
    {
        string json;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await Http.GetAsync(StatsUrl, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, "检查更新失败：官网返回 " + (int)resp.StatusCode + "。");
            json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "检查更新失败：" + ex.Message);
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return (null, "检查更新失败：官网返回的内容看不懂。");

            string latest = root["version"]?.GetValue<string>() ?? "";
            var mine = ParseVersion(current);
            var theirs = ParseVersion(latest);
            if (theirs == null) return (null, "检查更新失败：官网没报出版本号。");
            if (mine != null && theirs <= mine)
                return (null, "已是最新版本（" + current + "）。");

            string variant = HasDesktopRuntime() ? WithoutRuntime : WithRuntime;
            var v = root["variants"] as JsonObject;
            // 想要的档位不在就退回第一个有的：档位表在 site.js 里，将来改了名字也不该让这里崩掉。
            var node = (v?[variant] ?? v?.FirstOrDefault().Value) as JsonObject;
            if (node == null) return (null, "检查更新失败：官网没有可下载的安装包。");

            string file = node["filename"]?.GetValue<string>() ?? "";
            long size = node["size"]?.GetValue<long>() ?? 0;
            string? sha = node["sha256"]?.GetValue<string>();
            if (file.Length == 0) return (null, "检查更新失败：官网没给出安装包文件名。");

            return (new UpdateInfo(latest, variant, file, size, string.IsNullOrWhiteSpace(sha) ? null : sha.Trim()),
                    "发现新版本 " + latest + "。");
        }
        catch (Exception ex)
        {
            return (null, "检查更新失败：" + ex.Message);
        }
    }

    /// <summary>本机有没有 .NET 8 桌面运行时。有就下那个 2.8MB 的小包。</summary>
    private static bool HasDesktopRuntime()
    {
        // 只看 x64：本程序装的是 64 位（见 installer 的 ArchitecturesInstallIn64BitMode）。
        // 32 位那份 key 就算存在也没用。
        foreach (string path in new[]
        {
            @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App",
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App",
        })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;
                foreach (string name in key.GetValueNames())
                    if (name.StartsWith("8.", StringComparison.Ordinal)) return true;
            }
            catch { /* 读不到这个 key 就当没装，宁可下大包 */ }
        }
        return false;
    }

    /// <summary>把 <c>v0.9.26</c> / <c>0.9.26</c> 解析成可比较的版本号；认不出返回 null。</summary>
    private static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Version.TryParse(s.Trim().TrimStart('v', 'V'), out var v) ? v : null;
    }

    /// <summary>多久没收到数据就认为这一趟断了。</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    /// <summary>断了之后自动重来的次数。</summary>
    private const int Retries = 1;

    /// <summary>
    /// 下载安装包到临时目录。返回文件路径；失败抛出（由界面显示）。
    /// <paramref name="onProgress"/> 收 0..1，**调用方负责节流**（这里每读一块都会报）。
    ///
    /// 卡的是**停滞**超时而不是总时长：慢线路上传一个 49MB 的包本来就该花很久，
    /// 设总超时会把「慢」误判成「失败」；而真卡住（连接断了却没人告诉 socket）时，
    /// 没有看门狗就会永远挂在那里、界面一片安静 —— 那比报错难查得多。
    ///
    /// 停住之后**自动重来一次**：实测这条链路会中途静默几十秒（12% 处卡过一次），
    /// 而重来是从头下的（<c>/download</c> 不支持断点续传），所以只重试一次 ——
    /// 再多就变成「明明不通还硬耗着」。用户看到的进度会跳回 0，那正是发生了的事。
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, Action<double> onProgress, CancellationToken ct)
    {
        string dir = Path.Combine(Path.GetTempPath(), "BangGang-Update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, info.FileName);

        string url = StatsUrl[..StatsUrl.IndexOf("/api/", StringComparison.Ordinal)]
                   + "/download?variant=" + Uri.EscapeDataString(info.Variant);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await FetchOnceAsync(info, url, path, onProgress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryDelete(path);
                if (attempt >= Retries)
                    throw new InvalidOperationException(
                        "下载卡住了：" + (int)Stall.TotalSeconds + " 秒没有收到任何数据，重试也没成功。"
                        + "网络不通畅的话，可以到官网手动下载安装包。");
                Trace.Log("update: download stalled, retrying");
                onProgress(0);
            }
        }
    }

    private static async Task<string> FetchOnceAsync(UpdateInfo info, string url, string path,
                                                     Action<double> onProgress, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(Stall);

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, watchdog.Token)
                                   .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("下载失败：官网返回 " + (int)resp.StatusCode + "。");

        long total = resp.Content.Headers.ContentLength ?? info.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
        await using var dst = File.Create(path);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[81920];
        long done = 0;
        while (true)
        {
            int n = await src.ReadAsync(buf, watchdog.Token).ConfigureAwait(false);
            if (n <= 0) break;
            watchdog.CancelAfter(Stall);          // 有进展就续命
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            hash.AppendData(buf, 0, n);
            done += n;
            if (total > 0) onProgress(Math.Min(1.0, (double)done / total));
        }
        await dst.FlushAsync(ct).ConfigureAwait(false);

        string got = Convert.ToHexString(hash.GetHashAndReset());
        return Verify(path, info, got, done);
    }

    /// <summary>
    /// 核对刚下完的文件：**校验通过才返回路径**，不通过就删掉它并抛异常。
    ///
    /// 服务端没给校验值时只比大小、并在消息里**明说这次没校验**。把「没有校验值」
    /// 当成「校验通过」是在撒谎；当成失败又会把老发布物的更新路径整个堵死。
    ///
    /// 是 internal 而不是 private：这是「要不要运行这个 exe」的唯一判据，
    /// 值得被探针确定性地跑一遍（见 OfflineTool 的 BANGGANG_UPDATE），而不是只靠读代码相信它。
    /// </summary>
    internal static string Verify(string path, UpdateInfo info, string got, long size)
    {
        if (info.Size > 0 && size != info.Size)
        {
            TryDelete(path);
            throw new InvalidOperationException(
                "下载不完整：收到 " + size + " 字节，官网说应该是 " + info.Size + " 字节。已删除，请重试。");
        }

        if (info.Sha256 == null)
        {
            Trace.Log($"update: no sha256 from server, size-only check ok ({size} bytes)");
            return path;      // 消息由调用方补一句「本次未校验」
        }

        if (!string.Equals(got, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidOperationException(
                "校验不通过：文件与官网公布的不一致，已删除、不会运行。请稍后重试，或到官网手动下载。");
        }
        Trace.Log($"update: sha256 ok ({got[..12]}…)");
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 删不掉也不该让报错盖住真正的失败原因 */ }
    }

    /// <summary>
    /// 运行安装程序。调用方必须**先让用户明确点过一次**（本程序自己拉起一个 exe）。
    ///
    /// 不在这里关掉本程序：安装包用 Restart Manager（Inno 的 CloseApplications）自己会温和地
    /// 关掉正在运行的帮帮 —— 由安装包来做这件事，比我们抢先退出更稳（我们退了而安装包没起来，
    /// 用户就只剩一个空桌面）。
    /// </summary>
    public static bool RunInstaller(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Trace.Log("update: installer launched");
            return true;
        }
        catch (Exception ex)
        {
            Trace.Log("update: launch failed " + ex.Message);
            return false;
        }
    }
}
