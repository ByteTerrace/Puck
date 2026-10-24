using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Platform.Windows.Audio;

// The WASAPI P/Invoke surface, GUIDs and COM interfaces the live audio render device drives. Every interface
// preserves its native vtable slots in order, including unused methods; deleting a slot changes every later call.
[SupportedOSPlatform("windows")]
internal static class Wasapi {
    public static Guid CLSID_MMDeviceEnumerator = new(g: "bcde0395-e52f-467c-8e3d-c4579291692e");
    public static Guid IID_IMMDeviceEnumerator = new(g: "a95664d2-9614-4f35-a746-de8db63617e6");
    public static Guid IID_IAudioClient = new(g: "1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    public static Guid IID_IAudioRenderClient = new(g: "f294acfc-3146-4483-a7bf-addca7c260e2");

    public const uint ClsCtxAll = 0x17;
    // EDataFlow / ERole.
    public const int DataFlowRender = 0;
    public const int RoleConsole = 0;
    // AUDCLNT_SHAREMODE + stream flags.
    public const int ShareModeShared = 0;
    public const uint StreamFlagsEventCallback = 0x00040000;
    // The render stream's exotic-endpoint safety net: the engine converts/resamples our s16 PCM
    // whenever the endpoint's shared-mode mix format is not already 48000 Hz — on a native-rate endpoint the
    // conversion is the trivial s16→float widen and the mixer path stays sample-exact.
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    // Format tags.
    public const ushort WaveFormatPcm = 0x0001;
    // HRESULTs surfaced as decline reasons.
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);

    [DllImport("Ole32.dll")]
    public static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid, out nint ppv);
    [DllImport("Ole32.dll")]
    public static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);
    [DllImport("Ole32.dll")]
    public static extern void CoTaskMemFree(nint pv);
    [DllImport("Ole32.dll")]
    public static extern void CoUninitialize();
    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);
    [DllImport("Kernel32.dll", SetLastError = true)]
    public static extern nint CreateEventW(nint lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, [MarshalAs(UnmanagedType.LPWStr)] string? lpName);
    [DllImport("Kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);
    /// <summary>Creates the device enumerator, resolves the default render endpoint and activates its
    /// <see cref="IAudioClient"/>. The enumerator and device are released before
    /// returning; the caller owns the client.</summary>
    public static IAudioClient ActivateAudioClient() {
        var enumeratorClsid = CLSID_MMDeviceEnumerator;
        var enumeratorIid = IID_IMMDeviceEnumerator;

        Check(hr: CoCreateInstance(
            dwClsContext: ClsCtxAll,
            pUnkOuter: 0,
            ppv: out var enumeratorObject,
            rclsid: ref enumeratorClsid,
            riid: ref enumeratorIid
        ));

        var enumerator = ((IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pUnk: enumeratorObject));

        _ = Marshal.Release(pUnk: enumeratorObject);

        try {
            Check(hr: enumerator.GetDefaultAudioEndpoint(
                dataFlow: DataFlowRender,
                ppDevice: out var device,
                role: RoleConsole
            ));

            try {
                var audioClientIid = IID_IAudioClient;

                Check(hr: device.Activate(
                    dwClsCtx: ClsCtxAll,
                    iid: ref audioClientIid,
                    pActivationParams: 0,
                    ppInterface: out var audioClientObject
                ));

                return ((IAudioClient)audioClientObject);
            } finally {
                _ = Marshal.ReleaseComObject(o: device);
            }
        } finally {
            _ = Marshal.ReleaseComObject(o: enumerator);
        }
    }

    // WAVEFORMATEX header for the render stream's PCM format.
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
            throw new COMException(
                errorCode: hr,
                message: "a WASAPI call failed"
            );
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
/// <summary>IAudioClient — the shared-mode render stream lifecycle (initialize, buffer/format queries, start/stop,
/// event handle, and GetService for the render client).</summary>
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
