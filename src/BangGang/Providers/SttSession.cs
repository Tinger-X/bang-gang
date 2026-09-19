using System.Globalization;
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
    /// <summary>讯飞的 APPID。火山走新版控制台鉴权，不需要这一项。</summary>
    public string AppId = "";
    /// <summary>火山的 API Key（X-Api-Key）/ 讯飞的 APIKey。</summary>
    public string ApiKey = "";
    /// <summary>讯飞签名用的 APISecret（火山用不到）。</summary>
    public string ApiSecret = "";
    /// <summary>火山的资源 ID（存在档案的 "model" 键下，X-Api-Resource-Id）。讯飞用不到。</summary>
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

    /// <summary>是不是讯飞那一档 —— 两家的必填项不一样，判断只此一处。</summary>
    private bool IsXfyun => Provider.Contains("讯飞");

    /// <summary>还差什么才能开始转写；齐了返回 null。这句话会直接显示给用户。</summary>
    public string? Problem
    {
        get
        {
            const string where = "打开「设置 → 模型接入 → 语音转文字」补上。";
            if (Preset == null) return "尚未选择语音转写服务商：" + where;
            if (Url.Length == 0) return "尚未填写语音转写接口地址：" + where;
            // 火山走新版控制台鉴权，只要 API Key + 资源 ID；讯飞另要 APPID 与 APISecret。
            if (IsXfyun && AppId.Length == 0) return "尚未填写语音转写的 APPID：" + where;
            if (ApiKey.Length == 0)
                return "尚未填写语音转写的 " + (IsXfyun ? "APIKey" : "API Key") + "：" + where;
            if (IsXfyun && ApiSecret.Length == 0)
                return "尚未填写语音转写的 APISecret：" + where;
            if (!IsXfyun && ResourceId.Length == 0)
                return "尚未填写语音转写的资源 ID：" + where;
            return null;
        }
    }

    /// <summary>按服务商名分派协议实现 —— 两家协议互不相同，没有通用实现。</summary>
    public SttSession CreateSession() => IsXfyun
        ? new XfyunSttSession(this)
        : new VolcSttSession(this);
}

/// <summary>
/// 一条实时转写会话。音频由 <c>LiveDictation</c> 切成 <see cref="FrameBytes"/> 一帧喂进来；
/// 识别结果走事件回抛（都在接收线程上，调用方自己封送回 UI 线程）。
///
/// 两个结果事件是**一帧的两个部分**，顺序固定：先把这一帧里新定稿的分句挨个
/// <see cref="Sentence"/> 出来，再用 <see cref="Partial"/> 整体换掉「在途的那半句」
/// （<see cref="Partial"/> 收到空串 = 本帧没有在途半句，中间结果该清了）。
/// 调用方按顺序应用即可，不需要自己判断该不该清中间结果 ——
/// 「来了一句定稿就顺手把中间结果清掉」是错的：那句定稿未必是屏幕上这半句的归宿，
/// 清掉就会把用户已经看见的字吞掉（见 <see cref="VolcSttSession"/> 里那段注释）。
/// </summary>
internal abstract class SttSession : IDisposable
{
    /// <summary>本帧末尾**在途的半句**（同一句会随识别不断改写，直接整体替换显示）；空串 = 没有。</summary>
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

    /// <summary>
    /// 收尾时等终帧的上限。用户按下停止之后，服务商那边还有活儿没干完：它要把剩下的音频
    /// 过一遍、把最后那几分句定稿再回过来，双向流式还要等我们的末包。这几秒里**不能**把
    /// 会话拆掉 —— 拆了在途的结果就跟着没了，用户看到的就是「一停止，末尾的话就丢了」。
    /// 上限只是防服务商一声不吭地断了，不是「等这么久就够」。
    /// </summary>
    protected const int FinishTimeoutMs = 15000;

    protected void OnPartial(string t) { if (t.Length > 0) Partial?.Invoke(t); }
    /// <summary>本帧没有在途的半句 —— 中间结果作废（不是「空文本」，是「没有内容」）。</summary>
    protected void OnTailDone() => Partial?.Invoke("");
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
/// 火山引擎「双向流式语音识别」大模型 WebSocket（SAUC bigmodel 协议）。
/// 文档：https://docs.volcengine.com/docs/DoubaoVoice/bidirectional-streaming-automatic-speech-recognition-websocket
///
/// 接入地址三档：bigmodel_async（双向流式优化版，结果变化才回包，官方推荐）、
/// bigmodel（每收一包回一包）、bigmodel_nostream（流式输入，收尾才给结果）。
/// 默认取 <c>bigmodel_async</c>，地址在设置里可改。
///
/// 帧结构：[4 字节头][可选 4 字节序号][4 字节大端负载长度][负载]
///   byte0 = 版本(0x1) &lt;&lt;4 | 头长(以 4 字节计, 0x1)
///   byte1 = 消息类型 &lt;&lt;4 | 类型相关标志（0x1 正序号 / 0x2 最后一包 / 0x3 末包带负序号）
///   byte2 = 序列化(0=原始 1=JSON) &lt;&lt;4 | 压缩(0=无 1=gzip)
/// 客户端：type 0x1 全量请求（JSON+gzip）→ 服务端先回一包 → type 0x2 音频帧（raw+gzip）
///         → flags 0x2 空尾帧。
/// 服务端：type 0x9 全量响应（JSON+gzip，含 result.utterances，definite=true 为定稿），
///         type 0xF 错误（code / size / UTF-8 消息，没有 JSON 也没有 gzip）。
///
/// 鉴权走**新版控制台**：只要 API Key 与资源 ID 两个头，没有 App ID / Access Token。
/// </summary>
internal sealed class VolcSttSession : SttSession
{
    private readonly SttConfig _cfg;
    private ClientWebSocket? _ws;
    private readonly TaskCompletionSource _lastPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>服务端对全量请求的第一包（连上之后的握手回执），也当「可以发音频了」的信号。</summary>
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>已经定稿过的分句（按 start_time 去重，缺时间戳时退回按文本去重）。</summary>
    private readonly HashSet<long> _finalStarts = new();
    private readonly HashSet<string> _finalTexts = new();
    private int _frames;

    /// <summary>200ms 一帧：16000 × 2 × 0.2 —— 双向流式推荐的分包大小。</summary>
    public override int FrameBytes => 6400;

    public VolcSttSession(SttConfig cfg) => _cfg = cfg;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Api-Key", _cfg.ApiKey);
        ws.Options.SetRequestHeader("X-Api-Resource-Id", _cfg.ResourceId);
        ws.Options.SetRequestHeader("X-Api-Connect-Id", Guid.NewGuid().ToString());
        await ws.ConnectAsync(new Uri(_cfg.Url), ct);
        _ws = ws;

        // 全量请求：音频参数 + 识别参数。show_utterances 让结果按句带 definite 标记，
        // enable_nonstream 开二遍识别（VAD 分句后用语段模型重识别，定稿只在这一路出现）。
        string json = """
            {"user":{"uid":"bang-gang"},
             "audio":{"format":"pcm","codec":"raw","rate":16000,"bits":16,"channel":1},
             "request":{"model_name":"bigmodel","enable_punc":true,"enable_itn":true,
                        "enable_ddc":true,"show_utterances":true,"enable_nonstream":true}}
            """;
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        await SendFrameAsync(0x1, 0x0, 0x1, 0x1, Gzip(jsonBytes, jsonBytes.Length), ct);

        // 收包循环不 await：它的退出由 Dispose 里的 _ws.Abort() 促成，这里拿到 Task 也没人看
        _ = Task.Run(() => RecvLoopAsync());

        // **不等**第一包。等着它，等于在「按下热键」和「状态栏说正在转写」之间插进一整个
        // 网络往返；而这一包只是回执，对界面没有任何用处（讯飞那边一度就在这等，实测
        // 服务端不回时用户要盯着一个没反应的界面等满 5 秒）。
        // 它唯一的用处是「音频必须跟在回执后面」，那一步挪到了 SendAsync —— 跑在混音线程上，
        // 前面有队列垫着，几百毫秒无感。
        // 鉴权错、资源 ID 不对、参数不合法都会以 0xF 错误帧回来，由 Failed 事件报出去。
    }

    public override async Task SendAsync(byte[] pcm, int len, CancellationToken ct)
    {
        // 双向流式的顺序是「全量请求 → 服务端回执 → 音频帧」，音频抢在回执前面发包是协议外的
        // 用法（文档的时序图里没有这一支，服务端不一定接得住）。等在这里而不是 ConnectAsync，
        // 理由见上面那段。回执没来也别干等：到点照发，服务商认不认由它自己决定。
        if (!_firstFrame.Task.IsCompleted)
        {
            try { await Task.WhenAny(_firstFrame.Task, Task.Delay(3000, ct)); } catch { }
        }
        await SendFrameAsync(0x2, 0x0, 0x0, 0x1, Gzip(pcm, len), ct);
    }

    public override async Task FinishAsync(CancellationToken ct)
    {
        // 空负载 + flags 0x2 = 最后一包；之后等服务端的收尾响应（flags 0x3 负序号）。
        // 空负载也必须走一遍 gzip：压缩位一旦声明了 gzip，服务端就会去解压，
        // 空字节流解出来是 "unable to ungzip payload: EOF"，整条收尾被拒 ——
        // 用户听到的就是「末尾那句永远不出现」。20 字节的空 gzip 才是自洽的。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool sent = _ws?.State == WebSocketState.Open;
        if (sent)
        {
            byte[] empty = Gzip(Array.Empty<byte>(), 0);
            await SendFrameAsync(0x2, 0x2, 0x0, 0x1, empty, ct);
        }
        Trace.Log($"stt-finish: last packet sent={sent}");
        var done = Task.WhenAny(_lastPacket.Task, Task.Delay(FinishTimeoutMs, ct));
        try { await done; } catch { /* 超时就到此为止，已收到的部分不丢 */ }
        Trace.Log($"stt-finish: terminal={_lastPacket.Task.IsCompleted} after {sw.ElapsedMilliseconds}ms");
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
            _firstFrame.TrySetResult();
        }
    }

    private static int Be32(byte[] b, int off) =>
        (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

    private void ParseFrame(byte[] msg)
    {
        if (msg.Length < 8) return;
        int headerSize = (msg[0] & 0x0F) * 4;
        int type = msg[1] >> 4;
        int flags = msg[1] & 0x0F;
        int comp = msg[2] & 0x0F;

        _frames++;
        bool terminal = flags is 0x2 or 0x3;
        if (terminal || type == 0xF || _frames <= 3 || _frames % 20 == 0)
            Trace.Log($"stt-frame #{_frames} type=0x{type:X} flags=0x{flags:X} len={msg.Length}"
                      + (terminal ? " TERMINAL" : ""));

        // 错误帧的负载不是「长度 + 数据」，而是 code(uint32) + size(uint32) + UTF-8 消息，
        // 既没有 JSON 也不压缩 —— 先于通用解析单独处理，否则读到的「大小」是错误码。
        if (type == 0xF)
        {
            string detail = "未知错误";
            int p = headerSize;
            if (p + 8 <= msg.Length)
            {
                int code = Be32(msg, p);
                int len = Be32(msg, p + 4);
                p += 8;
                string text = len > 0 && p + len <= msg.Length
                    ? Encoding.UTF8.GetString(msg, p, len)
                    : Encoding.UTF8.GetString(msg, p, msg.Length - p);
                detail = (code + " " + text).Trim();
            }
            Trace.Log("stt-error " + detail);
            OnFailed("火山引擎返回错误：" + detail);
            // 回执不会来了，别让 SendAsync 那边干等；收尾也别等了。
            _firstFrame.TrySetResult();
            _lastPacket.TrySetResult();
            return;
        }

        int off = headerSize;
        if (flags is 0x1 or 0x3) off += 4;                 // 带序号
        if (off + 4 > msg.Length) return;
        int size = Be32(msg, off);
        off += 4;
        if (size <= 0 || off + size > msg.Length)
        {
            if (terminal) _lastPacket.TrySetResult();      // 空尾包
            _firstFrame.TrySetResult();
            return;
        }
        byte[] payload = new byte[size];
        Array.Copy(msg, off, payload, 0, size);
        if (comp == 0x1) payload = Gunzip(payload);

        _firstFrame.TrySetResult();
        if (type == 0x9)
        {
            try { ApplyResult(payload); }
            catch { /* 单帧解析失败不打断整段转写 */ }
        }

        // 终帧标记放在**结果落完之后**：FinishAsync 拿它当「服务商说完了」，
        // 提前置位就等于允许收尾流程在一句话还没写进输入框的时候把会话拆掉 ——
        // 日志上那一毫秒的差，用户看到的就是末尾丢字（0.9.14 的 stt-finish 与
        // stt-sentence 是同一毫秒打出来的，顺序全看线程调度）。
        if (terminal) _lastPacket.TrySetResult();
    }

    /// <summary>
    /// 把一帧识别结果落成事件。一帧里是「到目前为止的全部结果」：前面是已经定稿的分句，
    /// 末尾那一条是在途的半句。
    /// </summary>
    private void ApplyResult(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return;
        if (!result.TryGetProperty("utterances", out var utts) || utts.ValueKind != JsonValueKind.Array)
        {
            // 没有分句数组的形态：整段当中间结果
            if (result.TryGetProperty("text", out var text)) OnPartial(text.GetString() ?? "");
            return;
        }

        string tail = "";
        foreach (var u in utts.EnumerateArray())
        {
            string t = u.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
            bool definite = u.TryGetProperty("definite", out var d) && d.ValueKind == JsonValueKind.True;
            if (!definite) { tail = t; continue; }         // 在途的那句总排在最后，取到最后一条就对了

            // 每包都是「到目前为止的全部结果」，已定稿的分句会被反复重发 ——
            // 不去重的话同一句会在输入框里叠上好几遍。按 start_time 认同一句，
            // 服务端没给时间戳时退回按文本认。
            long start = u.TryGetProperty("start_time", out var s) && s.TryGetInt64(out var sv) ? sv : -1;
            bool seen = start >= 0 ? !_finalStarts.Add(start) : !_finalTexts.Add(t);
            if (!seen)
            {
                Trace.Log($"stt-sentence t={start} '{t}'");
                OnSentence(t);
            }
        }

        // 中间结果**只由本帧自己说了算**：本帧末尾没有在途半句就是「没有」，清掉。
        // 不能在 OnSentence 里顺手清 —— 那句定稿未必是屏幕上这半句的归宿（服务端分句会变，
        // 「且没有特」+「特殊要求的情况下」与「且没有特殊要求的情况下」是同一段语音的两种切法），
        // 顺手清掉屏幕上就少一截，而替换它的那句话往往等不到下一帧才来。
        if (tail.Length > 0) OnPartial(tail);
        else OnTailDone();
    }

    public override void Dispose()
    {
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
    }
}

/// <summary>
/// 讯飞「星火大模型实时语音转写」（rtasr_llm）。
/// 文档：https://www.xfyun.cn/doc/spark/asr_llm/rtasr_llm.html
///
/// 握手：URL `wss://office-api-ast-dx.iflyaisol.com/ast/communicate/v1?{请求参数}`，
///   参数为 appId / accessKeyId / utc / signature / lang / audio_encode / samplerate（+ uuid）。
///   signature = Base64( HmacSHA1(accessKeySecret, baseString) )，baseString 是把除 signature
///   外的参数**按参数名升序**排列、键值各自 URL 编码、用 `&` 拼起来。
///   控制台三件套在这里的角色：APPID → appId，APIKey → accessKeyId，
///   APISecret → accessKeySecret（只当 HMAC 密钥，不上行）。
/// 音频：**二进制** WebSocket 消息，16kHz / 16bit / 单声道 PCM，每 40ms 发 1280 字节
///   （文档：发太快会让引擎出错；间隔超 15 秒服务端主动断开）。
/// 收尾：文本消息 {"end":true,"sessionId":"<会话 id>"}，会话 id 取 started 回执里的 sid。
/// 返回：{"action":"started|result|error","code":…,"data":…,"desc":…,"sid":…}；
///   data.cn.st.type "0" 确定性结果 / "1" 中间结果，文字在 cn.st.rt[].ws[].cw[].w，
///   data.ls == true 表示最后一帧。
/// </summary>
internal sealed class XfyunSttSession : SttSession
{
    private readonly SttConfig _cfg;
    private ClientWebSocket? _ws;
    private readonly TaskCompletionSource _lastResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>握手带的 uuid；started 还没到就收尾时，它就是会话 id。</summary>
    private string _uuid = "";
    /// <summary>started 回执里的 sid，收尾消息要把它带回去。</summary>
    private string _sid = "";
    private int _frames;
    /// <summary>已定稿的分句，按（bg,ed）去重：确定性结果可能被重复下发。</summary>
    private readonly HashSet<string> _finalSegs = new();

    /// <summary>40ms 一帧：16000 × 2 × 0.04 —— 文档建议每 40ms 发 1280 字节。</summary>
    public override int FrameBytes => 1280;

    public XfyunSttSession(SttConfig cfg) => _cfg = cfg;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        _uuid = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;
        // utc 形如 2025-09-04T15:38:07+0800（时区偏移不带冒号）
        string utc = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                     + now.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");

        // 除 signature 外的参数按参数名升序，键值各自 URL 编码后拼接
        var prms = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["accessKeyId"] = _cfg.ApiKey,
            ["appId"] = _cfg.AppId,
            ["audio_encode"] = "pcm_s16le",
            ["lang"] = "autodialect",
            ["samplerate"] = "16000",
            ["utc"] = utc,
            ["uuid"] = _uuid,
        };
        var sb = new StringBuilder();
        foreach (var kv in prms)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
        }
        string baseString = sb.ToString();
        string signature = Convert.ToBase64String(
            HMACSHA1.HashData(Encoding.UTF8.GetBytes(_cfg.ApiSecret), Encoding.UTF8.GetBytes(baseString)));

        string sep = _cfg.Url.Contains('?') ? "&" : "?";
        string url = _cfg.Url + sep + baseString + "&signature=" + Uri.EscapeDataString(signature);

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        _ws = ws;
        // 同 bigmodel 那一路：收包循环不 await，退出由 Dispose 里的 _ws.Abort() 促成
        _ = Task.Run(() => RecvLoopAsync());
        Trace.Log("stt-xfyun: connected");

        // **不等**握手回执。原以为服务端总会先回一条 started，拿它当「连上了」的证据好早失败；
        // 实测它**不保证**发（0.9.14 的日志里有一次连接从头到尾没有 started，识别照常出结果），
        // 于是每次开录都要把 5 秒超时走满 —— 用户按下热键后盯着一个没反应的界面等五秒，
        // 才等到「正在转写」。鉴权错、APPID 不匹配、参数不合法都会以 error 帧回来，
        // 由 Failed 事件报出去（见 ParseMessage），不必在这里堵着等。
    }

    /// <summary>音频走二进制消息（不是 JSON），原样发 PCM。</summary>
    public override async Task SendAsync(byte[] pcm, int len, CancellationToken ct)
    {
        if (_ws == null) throw new InvalidOperationException("尚未连接");
        _frames++;
        if (_frames <= 3 || _frames % 100 == 0) Trace.Log($"stt-send #{_frames} bytes={len}");
        await _ws.SendAsync(pcm.AsMemory(0, len), WebSocketMessageType.Binary, true, ct);
    }

    public override async Task FinishAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string sid = _sid.Length > 0 ? _sid : _uuid;
        bool sent = _ws?.State == WebSocketState.Open;
        if (sent)
        {
            string end = "{\"end\":true,\"sessionId\":\"" + sid + "\"}";
            await _ws!.SendAsync(Encoding.UTF8.GetBytes(end), WebSocketMessageType.Text, true, ct);
        }
        Trace.Log($"stt-finish: end marker sent={sent} frames={_frames}");
        var done = Task.WhenAny(_lastResult.Task, Task.Delay(FinishTimeoutMs, ct));
        try { await done; } catch { /* 超时就到此为止，已收到的部分不丢 */ }
        Trace.Log($"stt-finish: last={_lastResult.Task.IsCompleted} after {sw.ElapsedMilliseconds}ms");
    }

    private async Task RecvLoopAsync()
    {
        try
        {
            while (_ws is { State: WebSocketState.Open })
            {
                byte[] msg = await ReceiveFullAsync(_ws, CancellationToken.None);
                ParseMessage(Encoding.UTF8.GetString(msg));
            }
        }
        catch (Exception ex)
        {
            if (!_lastResult.Task.IsCompleted) OnFailed("连接中断：" + ex.Message);
            _lastResult.TrySetResult();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 文档字段表写的是 action，示例里却是 msg_type/res_type —— 两套都认。
            string action = Str(root, "action");
            string msgType = Str(root, "msg_type");
            string desc = Str(root, "desc");
            int code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32() : 0;

            if (action == "error" || msgType == "error" || code != 0)
            {
                string detail = ((code != 0 ? code + " " : "") + desc).Trim();
                Trace.Log("stt-error " + detail);
                OnFailed("讯飞返回错误：" + detail);
                _lastResult.TrySetResult();
                return;
            }
            if (action == "started" || msgType == "started")
            {
                _sid = Str(root, "sid");
                Trace.Log($"stt-xfyun: started sid={_sid.Length}");
                return;
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;

            // 功能异常帧（res_type=frc / normal=false）走这里
            if (Str(data, "res_type") == "frc"
                || (data.TryGetProperty("normal", out var norm) && norm.ValueKind == JsonValueKind.False))
            {
                string d = Str(data, "desc");
                if (d.Length == 0) d = "功能异常";
                Trace.Log("stt-error " + d);
                OnFailed("讯飞：" + d);
                _lastResult.TrySetResult();
                return;
            }

            string type = "";
            string seg = "";
            if (data.TryGetProperty("cn", out var cn) && cn.TryGetProperty("st", out var st))
            {
                type = Str(st, "type");
                seg = Str(st, "bg") + "-" + Str(st, "ed");
            }
            string text = ConcatWords(data);
            bool last = data.TryGetProperty("ls", out var ls) && ls.ValueKind == JsonValueKind.True;

            if (text.Length > 0)
            {
                // type "0" = 确定性结果（整句），"1" = 中间结果；没有该字段时按中间结果走。
                if (type == "0")
                {
                    if (_finalSegs.Add(seg.Length > 1 ? seg : text)) OnSentence(text);
                    // 这一句定稿了，它对应的中间结果也就没了 —— 讯飞的中间结果永远属于
                    // 「正在说的那一句」，两者是一条消息里的两半，不像火山那样一帧全带。
                    OnTailDone();
                }
                else OnPartial(text);
                Trace.Log($"stt-xfyun: type={type} last={last} '{text}'");
            }
            if (last) _lastResult.TrySetResult();
        }
        catch (Exception ex) { Trace.Log("stt-parse-error " + ex.Message); /* 单帧解析失败不打断整段转写 */ }
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
