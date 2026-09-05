using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LiveAssistant;

/// <summary>
/// 单路 WASAPI 共享模式采集泵：事件驱动捕获设备音频包，
/// 转换为目标采样率/双声道 float 后放入队列。
/// </summary>
internal sealed class WasapiSink
{
    private readonly bool _loopback;
    private IAudioClient? _client;
    private IAudioCaptureClient? _cap;
    private SampleFormat _fmt;
    private int _targetRate;
    private readonly AutoResetEvent _evt = new(false);
    private PcmResampler? _conv;
    private volatile bool _stop;
    private Thread? _thread;
    private byte[] _raw = new byte[64 * 1024];

    public int SourceRate => _fmt.SampleRate;
    public int OutputRate => _targetRate;
    public bool Loopback => _loopback;
    public bool HasStopped { get; private set; }

    public WasapiSink(bool loopback) => _loopback = loopback;

    /// <summary>
    /// 打开并初始化设备（共享模式，绝不独占）。
    /// masterRate=0 表示本设备即主设备（loopback），目标采样率取自身设备速率；
    /// 否则把本设备（麦克风）重采样到 masterRate。
    /// </summary>
    public void Open(int dataFlow, int role, int masterRate)
    {
        IMMDevice dev = AudioApi.GetDefaultDevice(dataFlow, role);
        Guid iidClient = AudioApi.IID_IAudioClient;
        int hr = dev.Activate(ref iidClient, AudioApi.CLSCTX_ALL, IntPtr.Zero, out object obj);
        AudioApi.ThrowHr(hr);
        _client = (IAudioClient)obj;

        hr = _client.GetMixFormat(out IntPtr fmtPtr);
        AudioApi.ThrowHr(hr);
        try
        {
            _fmt = AudioApi.ReadFormat(fmtPtr);
            _targetRate = _loopback || masterRate <= 0 ? _fmt.SampleRate : masterRate;

            int flags = AudioApi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                        | (_loopback ? AudioApi.AUDCLNT_STREAMFLAGS_LOOPBACK : 0);

            // 共享模式：不独占设备，不影响正在进行的直播/会议等其它使用
            hr = _client.Initialize(AudioApi.AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, fmtPtr, IntPtr.Zero);
            if (hr < 0)
            {
                if (hr == AudioApi.AUDCLNT_E_DEVICE_IN_USE)
                    throw new COMException("设备正被独占使用", hr);
                AudioApi.ThrowHr(hr);
            }

            hr = _client.SetEventHandle(_evt.SafeWaitHandle!.DangerousGetHandle());
            AudioApi.ThrowHr(hr);

            Guid iidCap = AudioApi.IID_IAudioCaptureClient;
            hr = _client.GetService(ref iidCap, out IntPtr capPtr);
            AudioApi.ThrowHr(hr);
            try { _cap = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capPtr); }
            finally { Marshal.Release(capPtr); }

            _conv = new PcmResampler(_fmt.SampleRate, _fmt.Channels, _targetRate);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fmtPtr);
        }
    }

    /// <summary>启动采集线程。队列由调用方持有，本对象停止时对其调用 CompleteAdding。</summary>
    public void StartPump(BlockingCollection<float[]> queue)
    {
        _stop = false;
        _thread = new Thread(() => PumpLoop(queue)) { IsBackground = true, Name = _loopback ? "SysAudioPump" : "MicPump" };
        _thread.Start();
    }

    public void StopPump()
    {
        _stop = true;
        try { _evt.Set(); } catch { }
        try { _client?.Stop(); } catch { }
        _thread?.Join(2000);
        HasStopped = true;
    }

    private void PumpLoop(BlockingCollection<float[]> queue)
    {
        try
        {
            int hr = _client!.Start();
            AudioApi.ThrowHr(hr);

            while (!_stop)
            {
                if (!_evt.WaitOne(300)) continue; // 超时继续检查 _stop

                while (true)
                {
                    hr = _cap!.GetNextPacketSize(out uint n);
                    if (hr < 0) break;
                    if (n == 0) break;

                    hr = _cap.GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                        out _, out _);
                    if (hr < 0) break;
                    try
                    {
                        if ((flags & AudioApi.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                            PushSilence(queue, (int)frames);
                        else
                            PushPacket(queue, data, (int)frames);
                    }
                    finally
                    {
                        _ = _cap.ReleaseBuffer(frames);
                    }
                }
            }
        }
        catch { /* 采集结束或失败：静默排空已在队列中的数据 */ }
        finally
        {
            queue.CompleteAdding();
        }
    }

    private void PushPacket(BlockingCollection<float[]> queue, IntPtr data, int frames)
    {
        int samples = frames * _fmt.Channels;
        int bytes = samples * _fmt.BytesPerSample;
        if (bytes > _raw.Length) _raw = new byte[Math.Max(bytes, _raw.Length * 2)];
        Marshal.Copy(data, _raw, 0, bytes);

        var native = new float[samples];
        int off = 0;
        for (int i = 0; i < samples; i++)
        {
            native[i] = RawSample(_raw, off, _fmt.BytesPerSample, _fmt.IsFloat, _fmt.Bits);
            off += _fmt.BytesPerSample;
        }
        float[] outBuf = _conv!.Process(native, frames);
        queue.Add(outBuf);
    }

    private void PushSilence(BlockingCollection<float[]> queue, int frames)
    {
        var native = new float[frames * _fmt.Channels];
        float[] outBuf = _conv!.Process(native, frames);
        queue.Add(outBuf);
    }

    private static float RawSample(byte[] b, int off, int bps, bool isFloat, int bits)
    {
        if (isFloat)
        {
            if (bps == 4) return BitConverter.ToSingle(b, off);
            if (bps == 8) return (float)BitConverter.ToDouble(b, off);
            return 0f;
        }
        long v;
        switch (bps)
        {
            case 1: v = (sbyte)b[off]; break;
            case 2: v = (short)(b[off] | (b[off + 1] << 8)); break;
            case 3:
                v = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);
                if ((v & 0x800000) != 0) v |= ~0xFFFFFF;
                break;
            default:
                v = (int)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
                break;
        }
        double max = bits >= 32 ? 2147483648.0 : (double)(1L << (bits - 1));
        return (float)(v / max);
    }
}
