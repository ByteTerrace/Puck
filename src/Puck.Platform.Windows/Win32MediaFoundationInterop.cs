using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Platform.Windows;

// The Media Foundation P/Invoke surface + GUIDs used by the camera session. Kept in one place; the COM interfaces
// below declare their vtable slots in order but give real signatures only to the methods the session calls — unused
// slots are named placeholders (never invoked), which keeps the interop small without breaking vtable layout.
[SupportedOSPlatform("windows")]
internal static class MfInterop {
    public const uint MfVersion = 0x00020070; // (MF_SDK_VERSION << 16) | MF_API_VERSION
    public const uint FirstVideoStream = 0xFFFFFFFC; // MF_SOURCE_READER_FIRST_VIDEO_STREAM
    public const uint AllStreams = 0xFFFFFFFE; // MF_SOURCE_READER_ALL_STREAMS
    public const uint AnyStream = 0xFFFFFFFE; // MF_SOURCE_READER_ANY_STREAM (same value; ReadSample context)
    public const uint EndOfStream = 0x00000002; // MF_SOURCE_READERF_ENDOFSTREAM

    public static Guid IID_IMFMediaSource = new(g: "279a808d-aec7-40c8-9c6b-a6b492c78a66");
    public static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new(g: "c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP = new(g: "8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new(g: "60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    public static Guid MF_MT_MAJOR_TYPE = new(g: "48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static Guid MF_MT_SUBTYPE = new(g: "f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static Guid MF_MT_FRAME_SIZE = new(g: "1652c33d-d6b2-4012-b834-72030849a37d");
    public static Guid MF_MT_FRAME_RATE = new(g: "c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    // Narrows a VIDCAP enumeration to one KS category — some devices expose their infrared sensor as its own
    // KSCATEGORY_SENSOR_CAMERA device. Others (the BRIO) expose it as a SECOND STREAM on the color device instead,
    // identified per stream by MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES carrying the Infrared flag — the session
    // probes streams first and falls back to the sensor category only when no stream carries the flag.
    public static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY = new(g: "77f0ae69-c3bd-4509-941d-467e4d24899e");
    public static Guid KSCATEGORY_SENSOR_CAMERA = new(g: "24e552d7-6523-47f7-a647-d3465bf1f5ca");
    // Per-stream frame-source classification (readable through IMFSourceReader.GetPresentationAttribute with a stream
    // index): a VT_UI4 flag set — Color = 0x1, Infrared = 0x2, Depth = 0x4.
    public static Guid MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES = new(g: "17145fd1-1b2b-423c-8001-2b6833ed3588");
    public const uint FrameSourceTypeInfrared = 0x2;
    // 8-bit luminance — the format IR streams commonly deliver; the video processor cannot always convert it to
    // RGB32, so the session may accept it natively and expand host-side.
    public static Guid MFVideoFormat_L8 = new(g: "00000032-0000-0010-8000-00aa00389b71");
    // Logitech's video vendor extension unit (hardware-verified on the BRIO at topology node 6): selector 2 is the
    // discrete field-of-view control (byte 0/1/2 = 90/78/65 degrees, applied MID-STREAM only — a set on an idle
    // filter is accepted and ignored at the next stream start). Other selectors are device-specific and undocumented.
    public static Guid LOGITECH_VIDEO_XU = new(g: "49e40215-f434-47fe-b158-0e885023e51b");
    public static Guid MF_MT_DEFAULT_STRIDE = new(g: "644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static Guid MFMediaType_Video = new(g: "73646976-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_ARGB32 = new(g: "00000015-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_RGB32 = new(g: "00000016-0000-0010-8000-00aa00389b71");
    public static Guid MF_SOURCE_READER_D3D_MANAGER = new(g: "ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new(g: "0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new(g: "fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    [DllImport("Mfplat.dll")]
    public static extern int MFStartup(uint Version, uint dwFlags);
    [DllImport("Mfplat.dll")]
    public static extern int MFShutdown();
    [DllImport("Mfplat.dll")]
    public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);
    [DllImport("Mfplat.dll")]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);
    [DllImport("Mf.dll")]
    public static extern int MFEnumDeviceSources(IMFAttributes pAttributes, out nint pppSourceActivate, out uint pcSourceActivate);
    [DllImport("Mfreadwrite.dll")]
    public static extern int MFCreateSourceReaderFromMediaSource([MarshalAs(UnmanagedType.IUnknown)] object pMediaSource, IMFAttributes pAttributes, out IMFSourceReader ppSourceReader);
    [DllImport("Mfplat.dll")]
    public static extern int MFCreateDXGIDeviceManager(out uint pResetToken, out IMFDXGIDeviceManager ppDeviceManager);
    /// <summary>Throws if a Media Foundation HRESULT indicates failure.</summary>
    /// <param name="hr">The HRESULT a Media Foundation call returned.</param>
    /// <exception cref="COMException"><paramref name="hr"/> is negative.</exception>
    public static void Check(int hr) {
        if (hr < 0) {
            var systemMessage = Marshal.GetExceptionForHR(errorCode: hr)?.Message;

            throw new COMException(
                errorCode: hr,
                message: $"Media Foundation call failed (0x{unchecked((uint)hr):X8}){((systemMessage is null) ? "" : $": {systemMessage}")}"
            );
        }
    }
    /// <summary>Enumerates video capture devices, activates the first one as a media source, and reports its friendly
    /// name. Shared by both the CPU and GPU-tier camera sessions — the enumeration/activation shape is identical; only
    /// what each does with the resulting source (reader configuration) differs.</summary>
    /// <param name="infrared">Whether to enumerate the sensor-camera category (the infrared stream a Windows Hello
    /// capable device exposes as its own capture device) instead of the default color-camera category.</param>
    /// <returns>The activated media source (as <see cref="object"/>, the way <c>ActivateObject</c> yields it) and the
    /// device's friendly name (or <see langword="null"/> if the driver did not report one).</returns>
    /// <exception cref="InvalidOperationException">No matching video capture device was found.</exception>
    public static (object MediaSource, string? Name) ActivateDefaultVideoSource(bool infrared = false) {
        Check(hr: MFCreateAttributes(cInitialSize: 2, ppMFAttributes: out var enumConfig));

        var sourceTypeKey = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
        var vidcap = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP;

        Check(hr: enumConfig.SetGUID(guidKey: ref sourceTypeKey, guidValue: ref vidcap));

        if (infrared) {
            var categoryKey = MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY;
            var sensorCategory = KSCATEGORY_SENSOR_CAMERA;

            Check(hr: enumConfig.SetGUID(guidKey: ref categoryKey, guidValue: ref sensorCategory));
        }

        Check(hr: MFEnumDeviceSources(pAttributes: enumConfig, pcSourceActivate: out var count, pppSourceActivate: out var devices));

        if ((0 == count) || (0 == devices)) {
            throw new InvalidOperationException(message: (infrared
                ? "no infrared capture device was found"
                : "no video capture devices were found"
            ));
        }

        var activate = ((IMFActivate)Marshal.GetObjectForIUnknown(pUnk: Marshal.ReadIntPtr(ptr: devices)));

        // Release every raw device pointer the array owns (the RCW above holds its own ref) and free the array.
        for (var index = 0; (index < count); index++) {
            _ = Marshal.Release(pUnk: Marshal.ReadIntPtr(ptr: devices, ofs: (index * IntPtr.Size)));
        }

        Marshal.FreeCoTaskMem(ptr: devices);

        var nameKey = MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME;
        var name = ((activate.GetAllocatedString(guidKey: ref nameKey, pcchLength: out _, ppwszValue: out var deviceName) >= 0)
            ? deviceName
            : null);

        var sourceIid = IID_IMFMediaSource;

        Check(hr: activate.ActivateObject(ppv: out var mediaSource, riid: ref sourceIid));

        return (mediaSource, name);
    }
}
/// <summary>IMFAttributes — only SetUINT32 (slot 19) and SetGUID (slot 22) are called; earlier slots are placeholders.</summary>
[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFAttributes {
    [PreserveSig] int GetItem();
    [PreserveSig] int GetItemType();
    [PreserveSig] int CompareItem();
    [PreserveSig] int Compare();
    [PreserveSig] int GetUINT32();
    [PreserveSig] int GetUINT64();
    [PreserveSig] int GetDouble();
    [PreserveSig] int GetGUID();
    [PreserveSig] int GetStringLength();
    [PreserveSig] int GetString();
    [PreserveSig] int GetAllocatedString();
    [PreserveSig] int GetBlobSize();
    [PreserveSig] int GetBlob();
    [PreserveSig] int GetAllocatedBlob();
    [PreserveSig] int GetUnknown();
    [PreserveSig] int SetItem();
    [PreserveSig] int DeleteItem();
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64();
    [PreserveSig] int SetDouble();
    [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
    [PreserveSig] int SetString();
    [PreserveSig] int SetBlob();
    [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object punkValue);
}
/// <summary>IMFActivate — GetAllocatedString (slot 11) + ActivateObject (slot 31) are called.</summary>
[ComImport]
[Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFActivate {
    [PreserveSig] int GetItem();
    [PreserveSig] int GetItemType();
    [PreserveSig] int CompareItem();
    [PreserveSig] int Compare();
    [PreserveSig] int GetUINT32();
    [PreserveSig] int GetUINT64();
    [PreserveSig] int GetDouble();
    [PreserveSig] int GetGUID();
    [PreserveSig] int GetStringLength();
    [PreserveSig] int GetString();
    [PreserveSig] int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize();
    [PreserveSig] int GetBlob();
    [PreserveSig] int GetAllocatedBlob();
    [PreserveSig] int GetUnknown();
    [PreserveSig] int SetItem();
    [PreserveSig] int DeleteItem();
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32();
    [PreserveSig] int SetUINT64();
    [PreserveSig] int SetDouble();
    [PreserveSig] int SetGUID();
    [PreserveSig] int SetString();
    [PreserveSig] int SetBlob();
    [PreserveSig] int SetUnknown();
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount();
    [PreserveSig] int GetItemByIndex();
    [PreserveSig] int CopyAllItems();
    [PreserveSig] int ActivateObject(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}
/// <summary>IMFMediaType — GetUINT32/GetUINT64, SetUINT64 (frame size), and SetGUID are called (all IMFAttributes-prefix slots).</summary>
[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFMediaType {
    [PreserveSig] int GetItem();
    [PreserveSig] int GetItemType();
    [PreserveSig] int CompareItem();
    [PreserveSig] int Compare();
    [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble();
    [PreserveSig] int GetGUID(ref Guid guidKey, out Guid guidValue);
    [PreserveSig] int GetStringLength();
    [PreserveSig] int GetString();
    [PreserveSig] int GetAllocatedString();
    [PreserveSig] int GetBlobSize();
    [PreserveSig] int GetBlob();
    [PreserveSig] int GetAllocatedBlob();
    [PreserveSig] int GetUnknown();
    [PreserveSig] int SetItem();
    [PreserveSig] int DeleteItem();
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32();
    [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble();
    [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
}
/// <summary>IMFSample — only ConvertToContiguousBuffer (slot 39) is called.</summary>
[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFSample {
    [PreserveSig] int GetItem();
    [PreserveSig] int GetItemType();
    [PreserveSig] int CompareItem();
    [PreserveSig] int Compare();
    [PreserveSig] int GetUINT32();
    [PreserveSig] int GetUINT64();
    [PreserveSig] int GetDouble();
    [PreserveSig] int GetGUID();
    [PreserveSig] int GetStringLength();
    [PreserveSig] int GetString();
    [PreserveSig] int GetAllocatedString();
    [PreserveSig] int GetBlobSize();
    [PreserveSig] int GetBlob();
    [PreserveSig] int GetAllocatedBlob();
    [PreserveSig] int GetUnknown();
    [PreserveSig] int SetItem();
    [PreserveSig] int DeleteItem();
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32();
    [PreserveSig] int SetUINT64();
    [PreserveSig] int SetDouble();
    [PreserveSig] int SetGUID();
    [PreserveSig] int SetString();
    [PreserveSig] int SetBlob();
    [PreserveSig] int SetUnknown();
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount();
    [PreserveSig] int GetItemByIndex();
    [PreserveSig] int CopyAllItems();
    [PreserveSig] int GetSampleFlags();
    [PreserveSig] int SetSampleFlags();
    [PreserveSig] int GetSampleTime();
    [PreserveSig] int SetSampleTime();
    [PreserveSig] int GetSampleDuration();
    [PreserveSig] int SetSampleDuration();
    [PreserveSig] int GetBufferCount();
    [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
}
/// <summary>IMFDXGIBuffer — the DXGI (GPU-texture) view of a media buffer produced under a D3D manager: GetResource
/// yields the sample's <c>ID3D11Texture2D</c> and GetSubresourceIndex the array slice a DXVA component wrote (decoders
/// output texture arrays). Obtained by casting an <see cref="IMFMediaBuffer"/> (a runtime QI).</summary>
[ComImport]
[Guid("e7174cfa-1c9e-48b1-8866-626226bfc258")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFDXGIBuffer {
    [PreserveSig] int GetResource(ref Guid riid, out nint ppvObject);
    [PreserveSig] int GetSubresourceIndex(out uint puSubresource);
    [PreserveSig] int GetUnknown();
    [PreserveSig] int SetUnknown();
}
/// <summary>IMFDXGIDeviceManager — the shared-D3D-device broker Media Foundation's DXVA components lock the device
/// through. Only ResetDevice (associating the D3D11 device) is called; the source reader receives the manager via the
/// <c>MF_SOURCE_READER_D3D_MANAGER</c> attribute.</summary>
[ComImport]
[Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFDXGIDeviceManager {
    [PreserveSig] int CloseDeviceHandle();
    [PreserveSig] int GetVideoService();
    [PreserveSig] int LockDevice();
    [PreserveSig] int OpenDeviceHandle();
    [PreserveSig] int ResetDevice(nint pUnkDevice, uint resetToken);
    [PreserveSig] int TestDevice();
    [PreserveSig] int UnlockDevice();
}
/// <summary>IMFMediaBuffer — Lock, Unlock, GetCurrentLength.</summary>
[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFMediaBuffer {
    [PreserveSig] int Lock(out nint ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
}
/// <summary>IMFSourceReader — SetStreamSelection (2), GetCurrentMediaType (4), SetCurrentMediaType (5), ReadSample (7).</summary>
/// <summary>Kernel Streaming's raw property door, answered by Media Foundation video capture sources — the route to a
/// vendor extension unit's controls (a <c>KSP_NODE</c>-shaped property keyed by the XU's GUID + selector + topology
/// node id, with <c>KSPROPERTY_TYPE_TOPOLOGY</c> OR'd into the flags).</summary>
[ComImport]
[Guid("28f54685-06fd-11d2-b27a-00a0c9223196")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IKsControl {
    [PreserveSig] int KsProperty(nint Property, uint PropertyLength, nint PropertyData, uint DataLength, out uint BytesReturned);
    [PreserveSig] int KsMethod(nint Method, uint MethodLength, nint MethodData, uint DataLength, out uint BytesReturned);
    [PreserveSig] int KsEvent(nint Event, uint EventLength, nint EventData, uint DataLength, out uint BytesReturned);
}
/// <summary>DirectShow's classic camera-control interface (pan/tilt/zoom/exposure/focus by KSPROPERTY ordinal) —
/// implemented by Media Foundation video capture sources, so the session's media source answers it directly.</summary>
[ComImport]
[Guid("c6e13370-30ac-11d0-a18c-00a0c9118956")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IAMCameraControl {
    [PreserveSig] int GetRange(int Property, out int pMin, out int pMax, out int pSteppingDelta, out int pDefault, out int pCapsFlags);
    [PreserveSig] int Set(int Property, int lValue, int Flags);
    [PreserveSig] int Get(int Property, out int lValue, out int pFlags);
}
/// <summary>DirectShow's classic image-quality interface (brightness/contrast/saturation/… by ordinal) — implemented by
/// Media Foundation video capture sources, the sibling of <see cref="IAMCameraControl"/>.</summary>
[ComImport]
[Guid("c6e13360-30ac-11d0-a18c-00a0c9118956")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IAMVideoProcAmp {
    [PreserveSig] int GetRange(int Property, out int pMin, out int pMax, out int pSteppingDelta, out int pDefault, out int pCapsFlags);
    [PreserveSig] int Set(int Property, int lValue, int Flags);
    [PreserveSig] int Get(int Property, out int lValue, out int pFlags);
}
[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFSourceReader {
    [PreserveSig] int GetStreamSelection();
    [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, nint pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition();
    [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex, out uint pdwStreamFlags, out long pllTimestamp, out IMFSample? ppSample);
    [PreserveSig] int Flush();
    [PreserveSig] int GetServiceForStream();
    [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, ref Guid guidAttribute, out MfPropVariant pvarAttribute);
}
/// <summary>A minimal PROPVARIANT for the VT_UI4-shaped attributes <see cref="IMFSourceReader.GetPresentationAttribute"/>
/// answers (the per-stream frame-source flags). No PropVariantClear is needed for the inline value types read through
/// it — nothing is allocated behind a VT_UI4.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MfPropVariant {
    public const ushort VtUInt32 = 19;

    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public uint UInt32Value;
    public uint Padding1;
    public ulong Padding2;
}
