using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Platform.Windows.Recording;

// The WASAPI P/Invoke surface, GUIDs, and COM interfaces the loopback/microphone capture sources AND the render
// device drive. Hand-rolled beside the camera's Media Foundation interop (the established pattern): each interface
// declares its vtable slots in order, real signatures only on the methods used. Loopback and microphone share
// IMMDeviceEnumerator/IMMDevice/IAudioClient/IAudioCaptureClient; the only difference is the endpoint data-flow and
// the loopback stream flag. The render path shares everything up through IAudioClient and swaps the capture client
// for IAudioRenderClient.
[SupportedOSPlatform("windows")]
internal static class Wasapi {
    public static Guid CLSID_MMDeviceEnumerator = new(g: "bcde0395-e52f-467c-8e3d-c4579291692e");
    public static Guid IID_IMMDeviceEnumerator = new(g: "a95664d2-9614-4f35-a746-de8db63617e6");
    public static Guid IID_IAudioClient = new(g: "1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    public static Guid IID_IAudioCaptureClient = new(g: "c8adbd64-e71e-48a0-a4de-185c395cd317");
    public static Guid IID_IAudioRenderClient = new(g: "f294acfc-3146-4483-a7bf-addca7c260e2");

    public const uint ClsCtxAll = 0x17;

    // EDataFlow / ERole.
    public const int DataFlowRender = 0;
    public const int DataFlowCapture = 1;
    public const int RoleConsole = 0;

    // AUDCLNT_SHAREMODE + stream flags.
    public const int ShareModeShared = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    // The render stream's exotic-endpoint safety net: the engine converts/resamples our s16 PCM
    // whenever the endpoint's shared-mode mix format is not already 48000 Hz — on a native-rate endpoint the
    // conversion is the trivial s16→float widen and the mixer path stays sample-exact.
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    // IAudioCaptureClient::GetBuffer flags.
    public const uint BufferFlagsSilent = 0x00000002;

    // Format tags.
    public const ushort WaveFormatPcm = 0x0001;
    public const ushort WaveFormatIeeeFloat = 0x0003;
    public const ushort WaveFormatExtensible = 0xFFFE;

    public static Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new(g: "00000003-0000-0010-8000-00aa00389b71");
    public static Guid KSDATAFORMAT_SUBTYPE_PCM = new(g: "00000001-0000-0010-8000-00aa00389b71");

    // HRESULTs surfaced as decline reasons.
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const int EAccessDenied = unchecked((int)0x80070005);

    [DllImport("Ole32.dll")]
    public static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid, out nint ppv);
    [DllImport("Ole32.dll")]
    public static extern void CoTaskMemFree(nint pv);

    // WAVEFORMATEX header; a WAVEFORMATEXTENSIBLE follows it in memory when wFormatTag is 0xFFFE.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WaveFormatEx {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    /// <summary>Throws if a WASAPI HRESULT indicates failure.</summary>
    public static void Check(int hr) {
        if (hr < 0) {
            throw new COMException(message: "a WASAPI call failed", errorCode: hr);
        }
    }
}

/// <summary>IMMDeviceEnumerator — GetDefaultAudioEndpoint (slot 2) and GetDevice (slot 3).</summary>
[ComImport]
[Guid("a95664d2-9614-4f35-a746-de8db63617e6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints();
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback();
    [PreserveSig] int UnregisterEndpointNotificationCallback();
}

/// <summary>IMMDevice — Activate (slot 1) and GetId (slot 3).</summary>
[ComImport]
[Guid("d666063f-1587-4e43-81f1-b948e807363f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int OpenPropertyStore();
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    [PreserveSig] int GetState();
}

/// <summary>IAudioClient — the shared-mode capture stream lifecycle (initialize, buffer/format queries, start/stop,
/// event handle, and GetService for the capture client).</summary>
[ComImport]
[Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IAudioClient {
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, nint pFormat, nint audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
    [PreserveSig] int GetStreamLatency(out long phnsLatency);
    [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, nint pFormat, out nint ppClosestMatch);
    [PreserveSig] int GetMixFormat(out nint ppDeviceFormat);
    [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint eventHandle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

/// <summary>IAudioCaptureClient — GetBuffer / ReleaseBuffer / GetNextPacketSize.</summary>
[ComImport]
[Guid("c8adbd64-e71e-48a0-a4de-185c395cd317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IAudioCaptureClient {
    [PreserveSig] int GetBuffer(out nint ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

/// <summary>IAudioRenderClient — the two-method render vtable (GetBuffer / ReleaseBuffer) the world speaker device
/// fills through.</summary>
[ComImport]
[Guid("f294acfc-3146-4483-a7bf-addca7c260e2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IAudioRenderClient {
    [PreserveSig] int GetBuffer(uint numFramesRequested, out nint ppData);
    [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
}
