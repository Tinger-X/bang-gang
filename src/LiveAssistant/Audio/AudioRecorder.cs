using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveAssistant;

/// <summary>
/// 非独占音频采集：
///  - 系统声音：默认渲染设备的 WASAPI 共享模式 LOOPBACK（不影响任何正在播放的声音）；
///  - 麦克风：默认采集设备的共享模式 capture（设备被独占占用时降级为仅系统音）；
/// 两路共享模式，因此不会独占设备、不会干扰正在进行的直播/会议。
/// 两路各自转成"主采样率 + 双声道"后实时混音，写为 16bit PCM WAV。
/// </summary>
internal sealed class AudioMixRecorder
{
    private WasapiSink? _sys;
    private WasapiSink? _mic;
    private int _rate;                 // 输出采样率（取系统 loopback 设备采样率）
    private readonly BlockingCollection<float[]> _sysQ = new();
    private readonly BlockingCollection<float[]> _micQ = new();
    private FileStream? _fs;
    private BinaryWriter? _bw;
    private long _dataBytes;
    private Thread? _writer;
    private volatile bool _recording;

    /// <summary>是否因麦克风被独占占用而降级为仅录系统声音。</summary>
    public bool SystemOnlyMic { get; private set; }
    /// <summary>麦克风附加提示（如"无麦克风设备"），无则空。</summary>
    public string? MicNote { get; private set; }
    public bool IsRecording => _recording;
    public string SavePath { get; private set; } = "";
    public int SampleRate => _rate;

    /// <summary>开始录音。麦克风不可用时不抛错（降级系统音），系统音不可用时抛异常。</summary>
    public void Start(string path)
    {
        // 1) 系统声音（loopback）——失败则整体失败
        _sys = new WasapiSink(true);
        _sys.Open(AudioApi.eRender, AudioApi.eMultimedia, 0);
        _rate = _sys.SourceRate;
        if (_rate <= 0) throw new InvalidOperationException("无法获取系统音频采样率。");

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
                ? "麦克风正被其他程序独占使用，本次仅录制系统声音。"
                : "无法打开麦克风，本次仅录制系统声音。";
            _mic = null;
        }
        catch (Exception)
        {
            SystemOnlyMic = true;
            MicNote = "未检测到可用麦克风，本次仅录制系统声音。";
            _mic = null;
        }

        // 3) 建 WAV 文件
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _bw = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: true);
        _dataBytes = 0;
        WriteHeaderPlaceholder();

        _recording = true;
        SavePath = path;

        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "WavWriter" };
        _writer.Start();

        _sys.StartPump(_sysQ);
        _mic?.StartPump(_micQ);
    }

    /// <summary>停止录音，等待数据排空并封口 WAV 头。</summary>
    public void Stop()
    {
        if (!_recording) return;
        _sys?.StopPump();
        _mic?.StopPump();
        _writer?.Join(4000);
        FinalizeHeader();
        _recording = false;
    }

    // ---------- WAV 写入 ----------

    private void WriteHeaderPlaceholder()
    {
        byte[] hdr = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(hdr, 0);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(hdr, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(hdr, 12);
        hdr[16] = 16; // fmt chunk 大小
        hdr[20] = 1;  // PCM
        hdr[21] = 0;
        hdr[22] = 2;  // 声道
        hdr[23] = 0;
        WriteInt(hdr, 24, _rate);
        WriteInt(hdr, 28, _rate * 4); // byteRate = rate * blockAlign(4)
        hdr[32] = 4;  // blockAlign
        hdr[33] = 0;
        hdr[34] = 16; // 16bit
        hdr[35] = 0;
        Encoding.ASCII.GetBytes("data").CopyTo(hdr, 36);
        _bw!.Write(hdr);
    }

    private void FinalizeHeader()
    {
        if (_bw == null || _fs == null) return;
        long total = 36 + _dataBytes;
        _bw.Flush();
        _fs.Seek(4, SeekOrigin.Begin);
        _bw.Write((int)total);
        _fs.Seek(40, SeekOrigin.Begin);
        _bw.Write((int)_dataBytes);
        _bw.Flush();
        _bw.Dispose();
        _bw = null;
        _fs.Dispose();
        _fs = null;
    }

    private static void WriteInt(byte[] b, int off, int v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16);
        b[off + 3] = (byte)(v >> 24);
    }

    private void WriteSample(float l, float r)
    {
        short sl = ToS16(l);
        short sr = ToS16(r);
        _bw!.Write(sl);
        _bw.Write(sr);
        _dataBytes += 4;
    }

    private static short ToS16(float v)
    {
        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
        return (short)(v * 32767f);
    }

    // ---------- 混音排空循环 ----------

    /// <summary>每路源的读取游标（跨数组保持偏移）。</summary>
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

    private void WriterLoop()
    {
        var sys = new SrcCursor(_sysQ);
        var mic = _mic != null ? new SrcCursor(_micQ) : null;

        while (true)
        {
            bool sysD = sys.Drained;
            bool micD = mic == null || mic.Drained;
            if (sysD && micD) break;

            // 麦克风先结束：只排空系统音
            if (mic != null && micD)
            {
                if (sys.HasAny) { _ = sys.Next(out float sl, out float sr); WriteSample(sl, sr); }
                else Thread.Sleep(1);
                continue;
            }
            // 系统音先结束：只排空麦克风
            if (sysD)
            {
                if (mic!.HasAny) { _ = mic.Next(out float ml, out float mr); WriteSample(ml, mr); }
                else Thread.Sleep(1);
                continue;
            }
            // 没有麦克风：仅系统音
            if (mic == null)
            {
                if (sys.HasAny) { _ = sys.Next(out float sl, out float sr); WriteSample(sl, sr); }
                else Thread.Sleep(1);
                continue;
            }

            // 两路都活跃：同时取，保证时间对齐
            if (sys.HasAny && mic.HasAny)
            {
                _ = sys.Next(out float sl, out float sr);
                _ = mic.Next(out float ml, out float mr);
                WriteSample(sl + ml, sr + mr);
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }
}
