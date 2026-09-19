using System.IO.Compression;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BangGang;

/// <summary>
/// 一次实时语音转写需要的全部参数，从 <see cref="AppSettings"/> 解析而来。
/// 与 <see cref="LlmConfig"/> 同一约定：「还差什么」只有 <see cref="Problem"/> 一处判断，
/// 那句话直接显示给用户。
/// </summary>
internal sealed class SttConfig
{
    public string Provider = "";
    public string Url = "";
    public string AppId = "";
    public string ApiKey = "";
    public string ApiSecret = "";
    /// <summary>火山的资源 ID（存在档案的 "model" 键下）。讯飞用不到。</summary>
    public string ResourceId = "";

    /// <summary>把服务商档案读成配置。读不到某项时不抛异常，留空串由 <see cref="Problem"/> 说缺什么。</summary>
    public static SttConfig From(AppSettings s)
    {
        var cfg = new SttConfig { Provider = s.SttProvider ?? "" };
        // 只读，不调 ProfileOf —— 那个 getter 会顺手往字典里塞空档位（同 LlmConfig.From 的注释）。
        if (s.SttProfiles != null && s.SttProfiles.TryGetValue(cfg.Provider, out var p) && p != null)
        {
            cfg.Url = Get(p, "url");
            cfg.AppId = Get(p, "appid");
            cfg.ApiKey = Get(p, "key");
            cfg.ApiSecret = Get(p, "secret2");
            cfg.ResourceId = Get(p, "model");
        }
        // 旧版扁平字段兜底
        if (cfg.Url.Length == 0) cfg.Url = s.SttApiUrl ?? "";
        if (cfg.AppId.Length == 0) cfg.AppId = s.SttAppId ?? "";
        if (cfg.ApiKey.Length == 0) cfg.ApiKey = s.SttApiKey ?? "";
        if (cfg.ResourceId.Length == 0) cfg.ResourceId = s.SttModel ?? "";
        // 预设默认值兜底（地址 / 资源 ID 预设里带默认值，用户没填也能用）
        var preset = cfg.Preset;
        if (preset != null)
        {
            if (cfg.Url.Length == 0 && preset.Defaults.TryGetValue("url", out var du)) cfg.Url = du;
            if (cfg.ResourceId.Length == 0 && preset.Defaults.TryGetValue("model", out var dm)) cfg.ResourceId = dm;
        }
        return cfg;
    }

    private static string Get(Dictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) && v != null ? v.Trim() : "";

    /// <summary>当前选中的预设；服务商名对不上任何内置商家时为 null。</summary>
    public ProviderPreset? Preset => Array.Find(Providers.Stt, p => p.Name == Provider);

    /// <summary>还差什么才能开始转写；齐了返回 null。这句话会直接显示给用户。</summary>
    public string? Problem
    {
        get
        {
            const string where = "打开「设置 → 模型接入 → 语音转文字」补上。";
            if (Preset == null) return "尚未选择语音转写服务商：" + where;
            if (Url.Length == 0) return "尚未填写语音转写接口地址：" + where;
            if (AppId.Length == 0) return "尚未填写语音转写的 App ID：" + where;
            if (ApiKey.Length == 0) return "尚未填写语音转写的 Access Token / APIKey：" + where;
            if (Provider.Contains("讯飞") && ApiSecret.Length == 0)
                return "尚未填写语音转写的 APISecret：" + where;
            if (Provider.Contains("火山") && ResourceId.Length == 0)
                return "尚未填写语音转写的资源 ID：" + where;
            return null;
        }
    }

    /// <summary>按服务商名分派协议实现 —— 两家协议互不相同，没有通用实现。</summary>
    public SttSession CreateSession() => Provider.Contains("讯飞")
        ? new XfyunSttSession(this)
        : new VolcSttSession(this);
}

/// <summary>
/// 一条实时转写会话。音频由 <c>LiveDictation</c> 切成 <see cref="FrameBytes"/> 一帧喂进来；
/// 识别结果走事件回抛（都在接收线程上，调用方自己封送回 UI 线程）。
/// </summary>
internal abstract class SttSession : IDisposable
{
    /// <summary>中间结果（同一句会随识别不断改写，直接整体替换显示）。</summary>
    public event Action<string>? Partial;
    /// <summary>一句定稿（追加显示，不再变化）。</summary>
    public event Action<string>? Sentence;
    /// <summary>转写失败（鉴权错、网络断、服务端返回错误码）。</summary>
    public event Action<string>? Failed;

    /// <summary>每帧音频的字节数（16kHz 16bit 单声道 PCM）。</summary>
    public abstract int FrameBytes { get; }

    public abstract Task ConnectAsync(CancellationToken ct);
    /// <summary>发一帧音频。len ≤ FrameBytes；不足一帧的尾巴也走这里。</summary>
    public abstract Task SendAsync(byte[] pcm, int len, CancellationToken ct);
    /// <summary>发结束标记并等服务端把最后结果吐完（或超时）。</summary>
    public abstract Task FinishAsync(CancellationToken ct);
    public abstract void Dispose();

    protected void OnPartial(string t) { if (t.Length > 0) Partial?.Invoke(t); }
    protected void OnSentence(string t) { if (t.Length > 0) Sentence?.Invoke(t); }
    protected void OnFailed(string m) => Failed?.Invoke(m);

    /// <summary>把一整条 WebSocket 消息收全（可能分片）。远端关闭时抛 IOException。</summary>
    protected static async Task<byte[]> ReceiveFullAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new IOException("服务端关闭了连接");
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return ms.ToArray();
        }
    }

    protected static byte[] Gzip(byte[] data, int len)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(data, 0, len);
        return ms.ToArray();
    }

    protected static byte[] Gunzip(byte[] data)
    {
        using var src = new MemoryStream(data);
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// 火山引擎「流式语音识别大模型」单向流式 WebSocket（SAUC bigmodel 协议）。
/// 文档：https://docs.volcengine.com/docs/DoubaoVoice/unidirectional-streaming-automatic-speech-recognition-websocket
///
/// 帧结构：[4 字节头][可选 4 字节序号][4 字节大端负载长度][负载]
///   byte0 = 版本(0x1) &lt;&lt;4 | 头长(以 4 字节计, 0x1)
///   byte1 = 消息类型 &lt;&lt;4 | 类型相关标志（0x2 = 最后一包）
///   byte2 = 序列化(0=原始 1=JSON) &lt;&lt;4 | 压缩(0=无 1=gzip)
/// 客户端：type 0x1 全量请求（JSON+gzip）→ type 0x2 音频帧（raw+gzip）→ flags 0x2 空尾帧。
/// 服务端：type 0x9 全量响应（JSON+gzip，含 result.utterances，definite=true 为定稿），
///         type 0xF 错误（JSON，含 code / message）。
/// </summary>
internal sealed class VolcSttSession : SttSession
{
    private readonly SttConfig _cfg;
    private ClientWebSocket? _ws;
    private Task? _recvLoop;
    private readonly TaskCompletionSource _lastPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>200ms 一帧：16000 × 2 × 0.2。</summary>
    public override int FrameBytes => 6400;

    public VolcSttSession(SttConfig cfg) => _cfg = cfg;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Api-App-Key", _cfg.AppId);
        ws.Options.SetRequestHeader("X-Api-Access-Key", _cfg.ApiKey);
        ws.Options.SetRequestHeader("X-Api-Resource-Id", _cfg.ResourceId);
        ws.Options.SetRequestHeader("X-Api-Connect-Id", Guid.NewGuid().ToString());
        await ws.ConnectAsync(new Uri(_cfg.Url), ct);
        _ws = ws;

        // 全量请求：音频参数 + 识别参数。show_utterances 让结果按句带 definite 标记，
        // result_type=single 让定稿按句增量返回（而不是每次全量重发）。
        string json = """
            {"user":{"uid":"bang-gang"},
             "audio":{"format":"pcm","codec":"raw","rate":16000,"bits":16,"channel":1},
             "request":{"model_name":"bigmodel","enable_punc":true,"enable_itn":true,
                        "show_utterances":true,"result_type":"single"}}
            """;
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        await SendFrameAsync(0x1, 0x0, 0x1, 0x1, Gzip(jsonBytes, jsonBytes.Length), ct);

        _recvLoop = Task.Run(() => RecvLoopAsync());
    }

    public override Task SendAsync(byte[] pcm, int len, CancellationToken ct) =>
        SendFrameAsync(0x2, 0x0, 0x0, 0x1, Gzip(pcm, len), ct);

    public override async Task FinishAsync(CancellationToken ct)
    {
        // 空负载 + flags 0x2 = 最后一包；之后等服务端的收尾响应（flags 0x2/0x3）。
        if (_ws?.State == WebSocketState.Open)
            await SendFrameAsync(0x2, 0x2, 0x0, 0x1, Array.Empty<byte>(), ct);
        var done = Task.WhenAny(_lastPacket.Task, Task.Delay(5000, ct));
        try { await done; } catch { /* 超时就到此为止，已收到的部分不丢 */ }
    }

    private async Task SendFrameAsync(byte type, byte flags, byte ser, byte comp, byte[] payload, CancellationToken ct)
    {
        if (_ws == null) throw new InvalidOperationException("尚未连接");
        var buf = new byte[8 + payload.Length];
        buf[0] = 0x11;
        buf[1] = (byte)((type << 4) | flags);
        buf[2] = (byte)((ser << 4) | comp);
        buf[3] = 0;
        buf[4] = (byte)(payload.Length >> 24);
        buf[5] = (byte)(payload.Length >> 16);
        buf[6] = (byte)(payload.Length >> 8);
        buf[7] = (byte)payload.Length;
        Array.Copy(payload, 0, buf, 8, payload.Length);
        await _ws.SendAsync(buf, WebSocketMessageType.Binary, true, ct);
    }

    private async Task RecvLoopAsync()
    {
        try
        {
            while (_ws is { State: WebSocketState.Open })
            {
                byte[] msg = await ReceiveFullAsync(_ws, CancellationToken.None);
                ParseFrame(msg);
            }
        }
        catch (Exception ex)
        {
            // 尾帧之后的关闭不算错误
            if (!_lastPacket.Task.IsCompleted) OnFailed("连接中断：" + ex.Message);
            _lastPacket.TrySetResult();
        }
    }

    private void ParseFrame(byte[] msg)
    {
        if (msg.Length < 8) return;
        int headerSize = (msg[0] & 0x0F) * 4;
        int type = msg[1] >> 4;
        int flags = msg[1] & 0x0F;
        int comp = msg[2] & 0x0F;
        int off = headerSize;
        if (flags is 0x1 or 0x3) off += 4;                 // 带序号
        if (off + 4 > msg.Length) return;
        int size = (msg[off] << 24) | (msg[off + 1] << 16) | (msg[off + 2] << 8) | msg[off + 3];
        off += 4;
        if (size <= 0 || off + size > msg.Length)
        {
            if (flags is 0x2 or 0x3) _lastPacket.TrySetResult();   // 空尾包
            return;
        }
        byte[] payload = new byte[size];
        Array.Copy(msg, off, payload, 0, size);
        if (comp == 0x1) payload = Gunzip(payload);

        if (type == 0xF)
        {
            string detail = Encoding.UTF8.GetString(payload);
            try
            {
                using var doc = JsonDocument.Parse(detail);
                string code = doc.RootElement.TryGetProperty("code", out var c) ? c.ToString() : "?";
                string message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                detail = $"{code} {message}".Trim();
            }
            catch { }
            OnFailed("火山引擎返回错误：" + detail);
            _lastPacket.TrySetResult();
            return;
        }
        if (type != 0x9) return;
        if (flags is 0x2 or 0x3) _lastPacket.TrySetResult();

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return;
            if (result.TryGetProperty("utterances", out var utts) && utts.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in utts.EnumerateArray())
                {
                    string t = u.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
                    bool definite = u.TryGetProperty("definite", out var d) && d.ValueKind == JsonValueKind.True;
                    if (definite) OnSentence(t);
                    else OnPartial(t);
                }
            }
            else if (result.TryGetProperty("text", out var text))
            {
                OnPartial(text.GetString() ?? "");
            }
        }
        catch { /* 单帧解析失败不打断整段转写 */ }
    }

    public override void Dispose()
    {
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
    }
}

/// <summary>
/// 讯飞星火大模型实时语音转写（rtasr_llm）。
/// 文档：https://www.xfyun.cn/doc/spark/asr_llm/rtasr_llm.html
///
/// 握手：URL 上带 appid / ts(秒级时间戳) / signa，
///   signa = UrlEncode( Base64( HMAC-SHA1(APISecret, Md5Hex(appid + ts)) ) )。
/// 音频：JSON 文本帧 {"data":{"status":0|1|2,"format":"audio/L16;rate=16000","encoding":"raw","audio":base64}}，
///   每帧 40ms（1280 字节），status 0 首帧 / 1 中间 / 2 末帧。
/// 返回：{"action":"started|result|error","code":"0","data":{...}}；
///   data.cn.st.type "0" 定稿 / "1" 中间，文字在 cn.st.rt[].ws[].cw[].w，
///   data.ls == true 表示最后一条。
/// </summary>
internal sealed class XfyunSttSession : SttSession
{
    private readonly SttConfig _cfg;
    private ClientWebSocket? _ws;
    private Task? _recvLoop;
    private bool _first = true;
    private int _sent;
    private readonly TaskCompletionSource _lastResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>40ms 一帧：16000 × 2 × 0.04。</summary>
    public override int FrameBytes => 1280;

    public XfyunSttSession(SttConfig cfg) => _cfg = cfg;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        string ts = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        string md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_cfg.AppId + ts))).ToLowerInvariant();
        string signa = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(_cfg.ApiSecret), Encoding.UTF8.GetBytes(md5)));
        string sep = _cfg.Url.Contains('?') ? "&" : "?";
        string url = $"{_cfg.Url}{sep}appid={Uri.EscapeDataString(_cfg.AppId)}&ts={ts}&signa={Uri.EscapeDataString(signa)}";

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        _ws = ws;
        _recvLoop = Task.Run(() => RecvLoopAsync());
    }

    public override async Task SendAsync(byte[] pcm, int len, CancellationToken ct)
    {
        int status = _first ? 0 : 1;
        _first = false;
        int n = Interlocked.Increment(ref _sent);
        if (n <= 3 || n % 50 == 0) Trace.Log($"stt-send #{n} bytes={len}");
        await SendStatusAsync(status, Convert.ToBase64String(pcm, 0, len), ct);
        if (n <= 3 || n % 50 == 0) Trace.Log($"stt-send #{n} done");
    }

    public override async Task FinishAsync(CancellationToken ct)
    {
        if (_ws?.State == WebSocketState.Open)
            await SendStatusAsync(2, "", ct);
        var done = Task.WhenAny(_lastResult.Task, Task.Delay(5000, ct));
        try { await done; } catch { /* 超时就到此为止 */ }
    }

    private async Task SendStatusAsync(int status, string audioB64, CancellationToken ct)
    {
        if (_ws == null) throw new InvalidOperationException("尚未连接");
        string json = "{\"data\":{\"status\":" + status
            + ",\"format\":\"audio/L16;rate=16000\",\"encoding\":\"raw\",\"audio\":\"" + audioB64 + "\"}}";
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

    private async Task RecvLoopAsync()
    {
        try
        {
            while (_ws is { State: WebSocketState.Open })
            {
                byte[] msg = await ReceiveFullAsync(_ws, CancellationToken.None);
                Trace.Log($"stt-recv bytes={msg.Length}");
                ParseMessage(Encoding.UTF8.GetString(msg));
            }
        }
        catch (Exception ex)
        {
            if (!_lastResult.Task.IsCompleted) OnFailed("连接中断：" + ex.Message);
            _lastResult.TrySetResult();
        }
    }

    private void ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            if (action == "error")
            {
                string desc = root.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                string code = root.TryGetProperty("code", out var c) ? c.ToString() : "?";
                OnFailed($"讯飞返回错误 {code}：{desc}");
                _lastResult.TrySetResult();
                return;
            }
            if (root.TryGetProperty("code", out var codeEl) && codeEl.ToString() != "0")
            {
                string desc = root.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                OnFailed($"讯飞返回错误 {codeEl}：{desc}");
                _lastResult.TrySetResult();
                return;
            }
            if (action != "result") return;   // "started" 等握手回执
            if (!root.TryGetProperty("data", out var data)) return;

            // 有的版本 data 是内嵌的 JSON 字符串，再解一层
            if (data.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(data.GetString()!);
                HandleResult(inner.RootElement);
            }
            else
            {
                HandleResult(data);
            }
        }
        catch { /* 单帧解析失败不打断整段转写 */ }
    }

    private void HandleResult(JsonElement data)
    {
        bool final = false;
        if (data.TryGetProperty("cn", out var cn) && cn.TryGetProperty("st", out var st)
            && st.TryGetProperty("type", out var typeEl))
            final = typeEl.GetString() == "0";

        string text = ConcatWords(data);
        if (final) OnSentence(text);
        else OnPartial(text);

        if (data.TryGetProperty("ls", out var ls) && ls.ValueKind == JsonValueKind.True)
            _lastResult.TrySetResult();
    }

    private static string ConcatWords(JsonElement data)
    {
        var sb = new StringBuilder();
        if (data.TryGetProperty("cn", out var cn) && cn.TryGetProperty("st", out var st)
            && st.TryGetProperty("rt", out var rt) && rt.ValueKind == JsonValueKind.Array)
            foreach (var r in rt.EnumerateArray())
                if (r.TryGetProperty("ws", out var ws) && ws.ValueKind == JsonValueKind.Array)
                    foreach (var w in ws.EnumerateArray())
                        if (w.TryGetProperty("cw", out var cw) && cw.ValueKind == JsonValueKind.Array)
                            foreach (var c in cw.EnumerateArray())
                                if (c.TryGetProperty("w", out var t) && t.ValueKind == JsonValueKind.String)
                                    sb.Append(t.GetString());
        return sb.ToString();
    }

    public override void Dispose()
    {
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
    }
}
