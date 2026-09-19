using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BangGang;

/// <summary>
/// 实时听写：系统声音（loopback）+ 麦克风两路共享模式采集，混音后重采样成
/// 16kHz / 16bit / 单声道 PCM，按 <see cref="SttSession.FrameBytes"/> 一帧实时发给
/// 转写接口。与旧的 <c>AudioMixRecorder</c>（已删）的区别只在落点：那版写 WAV 文件，
/// 这版直接进 WebSocket，转写文字由 <see cref="SttSession"/> 的事件带回去。
/// </summary>
internal sealed class LiveDictation : IDisposable
{
    private const int TargetRate = 16000;

    private readonly SttSession _session;
    private WasapiSink? _sys;
    private WasapiSink? _mic;
    private int _rate;                       // 混音前的采样率（取系统 loopback 设备速率）
    private readonly BlockingCollection<float[]> _sysQ = new();
    private readonly BlockingCollection<float[]> _micQ = new();
    private Thread? _mixer;
    private volatile bool _running;

    /// <summary>是否因麦克风被独占占用而降级为仅系统声音。</summary>
    public bool SystemOnlyMic { get; private set; }
    /// <summary>麦克风附加提示（如"无麦克风设备"），无则空。</summary>
    public string? MicNote { get; private set; }
    public bool IsRecording => _running;

    /// <summary>转写会话（事件挂这上面；由 MainForm 订阅后进 UI 线程）。</summary>
    public SttSession Session => _session;

    public LiveDictation(SttSession session) => _session = session;

    /// <summary>
    /// 开设备、连转写接口、起采集。麦克风不可用时不抛错（降级系统音）；
    /// 系统音不可用或接口连不上时抛异常，且会把已开的设备收干净。
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        // 1) 系统声音（loopback）——失败则整体失败
        Trace.Log("dictation: open sys loopback");
        _sys = new WasapiSink(true);
        _sys.Open(AudioApi.eRender, AudioApi.eMultimedia, 0);
        _rate = _sys.SourceRate;
        Trace.Log($"dictation: sys rate={_rate}");
        if (_rate <= 0) throw new InvalidOperationException("无法获取系统音频采样率。");

        try
        {
            // 2) 麦克风（尽力而为，失败降级）
            try
            {
                _mic = new WasapiSink(false);
                _mic.Open(AudioApi.eCapture, AudioApi.eMultimedia, _rate);
            }
            catch (COMException ex)
            {
                SystemOnlyMic = true;
                MicNote = ex.HResult == AudioApi.AUDCLNT_E_DEVICE_IN_USE
                    ? "麦克风正被其他程序独占使用，本次仅转写系统声音。"
                    : "无法打开麦克风，本次仅转写系统声音。";
                _mic = null;
            }
            catch (Exception)
            {
                SystemOnlyMic = true;
                MicNote = "未检测到可用麦克风，本次仅转写系统声音。";
                _mic = null;
            }

            // 3) 连转写接口（网络，最可能失败的一步）——失败则把已开的设备停掉再抛
            Trace.Log($"dictation: mic={(_mic != null)} note={MicNote ?? "-"}; connecting");
            await _session.ConnectAsync(ct);
            Trace.Log("dictation: connected");

            _running = true;
            _mixer = new Thread(MixLoop) { IsBackground = true, Name = "DictationMixer" };
            _mixer.Start();
            _sys.StartPump(_sysQ);
            _mic?.StartPump(_micQ);
        }
        catch
        {
            try { _sys?.StopPump(); } catch { }
            try { _mic?.StopPump(); } catch { }
            _sys = null;
            _mic = null;
            throw;
        }
    }

    /// <summary>停止采集、排空缓冲、发结束标记并等服务端把最后结果吐完。</summary>
    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;
        _sys?.StopPump();
        _mic?.StopPump();
        _mixer?.Join(4000);
        try { await _session.FinishAsync(CancellationToken.None); }
        catch { /* 收尾失败不挡界面，已收到的部分早已落进输入框 */ }
    }

    public void Dispose() => _session.Dispose();

    /// <summary>不等收尾，立即停采集并断开（退出程序时用，等那几秒收尾没有意义）。</summary>
    public void Abort()
    {
        _running = false;
        try { _sys?.StopPump(); } catch { }
        try { _mic?.StopPump(); } catch { }
        _session.Dispose();
    }

    // ---------- 混音 → 16k 单声道 s16 → 分帧发送 ----------

    private void MixLoop()
    {
        var down = new PcmResampler(_rate, 2, TargetRate);   // 双声道 masterRate → 双声道 16k
        var pending = new byte[_session.FrameBytes * 2];
        int pendingLen = 0;
        var sys = new SrcCursor(_sysQ);
        var mic = _mic != null ? new SrcCursor(_micQ) : null;
        // 一路源的游标：队列里的块与块内偏移都跨调用保持（与旧录音器的 SrcCursor 同款）。
        var stereoBuf = new List<float>(4096);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastLog = 0;
            while (true)
            {
                bool sysD = sys.Drained;
                bool micD = mic == null || mic.Drained;
                if (sysD && micD) break;

                if (sw.ElapsedMilliseconds - lastLog > 3000)
                {
                    lastLog = sw.ElapsedMilliseconds;
                    Trace.Log($"mixer: t={sw.ElapsedMilliseconds} sysQ={_sysQ.Count} micQ={_micQ.Count} sysD={sysD} micD={micD} buf={stereoBuf.Count}");
                }

                // 哪路有数据就吃哪路；另一路暂时没有就当作静音混零。
                // 桌面完全静音时 loopback 引擎可以不派发任何包（事件一直不触发，
                // 连静音包都没有 —— 0.9.8 探针实测 sys 泵整段 0 包），如果坚持
                // 「两路都有才混」，对着麦克风说话也一个字都发不出去，全部积压到停止。
                bool got = false;
                bool s = sys.HasAny, m = mic != null && mic.HasAny;
                if (s && m && !micD && !sysD)
                {
                    _ = sys.Next(out float sl, out float sr);
                    _ = mic!.Next(out float ml, out float mr);
                    stereoBuf.Add(sl + ml); stereoBuf.Add(sr + mr);
                    got = true;
                }
                else if (s)
                {
                    _ = sys.Next(out float sl, out float sr);
                    stereoBuf.Add(sl); stereoBuf.Add(sr);
                    got = true;
                }
                else if (m)
                {
                    _ = mic!.Next(out float ml, out float mr);
                    stereoBuf.Add(ml); stereoBuf.Add(mr);
                    got = true;
                }
                if (!got) { Thread.Sleep(1); continue; }

                // 攒够一批再重采样（PcmResampler 内部按块保持跨块插值状态）
                if (stereoBuf.Count < 2048 && !(sysD && micD)) continue;
                if (stereoBuf.Count == 0) continue;
                float[] chunk = down.Process(stereoBuf.ToArray(), stereoBuf.Count / 2);
                stereoBuf.Clear();
                pendingLen = Feed(chunk, pending, pendingLen);
            }

            // 收尾：重采样器里还压着最后几个采样
            float[] tail = down.Flush(2);
            if (tail.Length > 0) pendingLen = Feed(tail, pending, pendingLen);
            if (pendingLen > 0)
            {
                try { _session.SendAsync(pending, pendingLen, CancellationToken.None).GetAwaiter().GetResult(); }
                catch { }
            }
        }
        catch { /* 发送失败等：静默收尾，界面状态由 Failed 事件或停止路径负责 */ }
    }

    /// <summary>把 16k 双声道交错 float 折成单声道 s16 追加进待发缓冲，满一帧就发。</summary>
    private int Feed(float[] stereo, byte[] pending, int pendingLen)
    {
        int frames = stereo.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            float mono = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
            if (mono > 1f) mono = 1f; else if (mono < -1f) mono = -1f;
            short s = (short)(mono * 32767f);
            pending[pendingLen++] = (byte)s;
            pending[pendingLen++] = (byte)(s >> 8);
            if (pendingLen == _session.FrameBytes)
            {
                _session.SendAsync(pending, pendingLen, CancellationToken.None).GetAwaiter().GetResult();
                pendingLen = 0;
            }
        }
        return pendingLen;
    }

    /// <summary>一路源的读取游标（跨数组保持偏移），与旧录音器里的同款。</summary>
    private sealed class SrcCursor
    {
        private readonly BlockingCollection<float[]> _q;
        private float[] _cur = Array.Empty<float>();
        private int _i;

        public SrcCursor(BlockingCollection<float[]> q) => _q = q;
        public bool HasAny => _i + 1 < _cur.Length || _q.Count > 0;
        public bool Drained => _q.IsAddingCompleted && _q.Count == 0 && _i >= _cur.Length;

        public bool Next(out float l, out float r)
        {
            while (true)
            {
                if (_i + 1 < _cur.Length)
                {
                    l = _cur[_i];
                    r = _cur[_i + 1];
                    _i += 2;
                    return true;
                }
                if (_q.IsAddingCompleted && _q.Count == 0) { l = 0; r = 0; return false; }
                if (_q.TryTake(out float[]? b) && b != null) { _cur = b; _i = 0; continue; }
                l = 0; r = 0; return false;
            }
        }
    }
}
