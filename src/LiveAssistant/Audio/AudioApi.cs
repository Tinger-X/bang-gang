using System.Runtime.InteropServices;

namespace LiveAssistant;

#region COM 接口（均以标准顺序对应 vtable，前三个槽位为 IUnknown）

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
    [PreserveSig] int GetStreamLatency(out long phnsLatency);
    [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, IntPtr ppClosestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
    [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, out IntPtr ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags,
        out ulong pu64DevicePosition, out ulong pu64QPCPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

#endregion

/// <summary>WASAPI 共享模式（非独占）核心音频 API。</summary>
internal static class AudioApi
{
    public const int eRender = 0;
    public const int eCapture = 1;
    public const int eConsole = 0;
    public const int eMultimedia = 1;
    public const int eCommunications = 2;
    public const int CLSCTX_ALL = 0x17;

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;

    // 设备正被其他程序以独占方式占用
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>取默认输入/输出设备。</summary>
    public static IMMDevice GetDefaultDevice(int dataFlow, int role)
    {
        Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(t)!;
        int hr = enumerator.GetDefaultAudioEndpoint(dataFlow, role, out IMMDevice dev);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return dev;
    }

    /// <summary>从 GetMixFormat 返回的指针读取采样格式（逐字段读取，兼容 WAVEFORMATEX / EXTENSIBLE）。</summary>
    public static SampleFormat ReadFormat(IntPtr p)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int channels = Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int blockAlign = Marshal.ReadInt16(p, 12);
        int bits = Marshal.ReadInt16(p, 14);
        int cbSize = Marshal.ReadInt16(p, 16);

        bool isExt = tag == 0xFFFE;
        bool isFloat = tag == 3;
        if (isExt)
        {
            // WAVEFORMATEXTENSIBLE: 基结构 18 字节 + wValidBits(2)@18 + wSamplesPerBlock(2)@20 + wReserved(2)@22 + SubFormat GUID @24
            var gb = new byte[16];
            Marshal.Copy(p + 24, gb, 0, 16);
            Guid sf = new(gb);
            isFloat = sf == new Guid("00000003-0000-0010-8000-00AA00389B71");
        }
        int bps = blockAlign / Math.Max(1, channels); // 每采样容器字节数
        if (bps == 0) bps = (bits + 7) / 8;
        return new SampleFormat(isFloat, channels, rate, bits, bps);
    }

    public static void ThrowHr(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}

/// <summary>一个音频流的采样格式。</summary>
internal readonly struct SampleFormat
{
    public readonly bool IsFloat;
    public readonly int Channels;
    public readonly int SampleRate;
    public readonly int Bits;
    public readonly int BytesPerSample;

    public SampleFormat(bool isFloat, int channels, int rate, int bits, int bps)
    {
        IsFloat = isFloat; Channels = channels; SampleRate = rate; Bits = bits; BytesPerSample = bps;
    }
}
