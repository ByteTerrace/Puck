using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Platform.Windows.Recording;

// The Media Foundation P/Invoke surface, GUIDs, and COM interfaces the hardware video-encoder ladder drives. Kept
// self-contained beside the camera's MfInterop (which it does not touch): the encoder needs richer media-type/sample
// vtables and the async-MFT event generator that the camera path never used. Each COM interface declares its vtable
// slots in order but gives real signatures only to the methods the encoder calls; unused slots are named placeholders
// (never invoked) that keep the layout intact. Startup/shutdown reuse the camera interop's refcounted MFStartup.
[SupportedOSPlatform("windows")]
internal static class MfEncoder {
    // MFTEnumEx enumeration flags.
    public const uint MftEnumFlagSyncMft = 0x00000001;
    public const uint MftEnumFlagAsyncMft = 0x00000008;
    public const uint MftEnumFlagHardware = 0x00000004;
    public const uint MftEnumFlagSortAndFilter = 0x00000040;

    // IMFTransform stream flags and messages.
    public const uint MftOutputStreamProvidesSamples = 0x00000100;
    public const uint MftMessageNotifyBeginStreaming = 0x10000000;
    public const uint MftMessageNotifyEndStreaming = 0x10000001;
    public const uint MftMessageNotifyEndOfStream = 0x10000002;
    public const uint MftMessageNotifyStartOfStream = 0x10000003;
    public const uint MftMessageCommandDrain = 0x00000001;
    public const uint MftMessageCommandFlush = 0x00000000;

    // IMFMediaEventGenerator / async-MFT event types.
    public const uint MfEventFlagNoWait = 0x00000001;
    public const uint MeTransformNeedInput = 601;
    public const uint MeTransformHaveOutput = 602;
    public const uint MeTransformDrainComplete = 603;

    // Well-known Media Foundation HRESULTs (as unchecked int).
    public const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);
    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    public const int MfEAttributeNotFound = unchecked((int)0xC00D36E6);
    public const int EAccessDenied = unchecked((int)0x80070005);

    // Interface / category / attribute / format GUIDs.
    public static Guid IID_IMFTransform = new(g: "bf94c121-5b05-4e6f-8000-ba598961414d");
    public static Guid MFT_CATEGORY_VIDEO_ENCODER = new(g: "f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static Guid MFT_FRIENDLY_NAME_Attribute = new(g: "314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static Guid MF_TRANSFORM_ASYNC_UNLOCK = new(g: "e5666d6b-3422-4eb6-a421-da7db1f8e207");

    public static Guid MFMediaType_Video = new(g: "73646976-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_NV12 = new(g: "3231564e-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_H264 = new(g: "34363248-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_AV1 = new(g: "31305641-0000-0010-8000-00aa00389b71");

    public static Guid MF_MT_MAJOR_TYPE = new(g: "48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static Guid MF_MT_SUBTYPE = new(g: "f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static Guid MF_MT_FRAME_SIZE = new(g: "1652c33d-d6b2-4012-b834-72030849a37d");
    public static Guid MF_MT_FRAME_RATE = new(g: "c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static Guid MF_MT_PIXEL_ASPECT_RATIO = new(g: "c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static Guid MF_MT_INTERLACE_MODE = new(g: "e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static Guid MF_MT_AVG_BITRATE = new(g: "20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static Guid MF_MT_MPEG2_PROFILE = new(g: "ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static Guid MF_MT_MPEG_SEQUENCE_HEADER = new(g: "3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static Guid MF_MT_MAX_KEYFRAME_SPACING = new(g: "c16eb52b-73a1-476f-8d62-839d6a020652");
    public static Guid MFSampleExtension_CleanPoint = new(g: "9cc7b232-49dd-4998-8ab3-0f6e6e6db7fc");

    // Codec-API parameter GUIDs (best-effort rate-control/GOP configuration through ICodecAPI).
    public static Guid CODECAPI_AVEncMPVGOPSize = new(g: "95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static Guid CODECAPI_AVEncCommonRateControlMode = new(g: "1c0608e9-370c-4710-8a58-cb6181c42423");
    public static Guid CODECAPI_AVEncCommonMeanBitRate = new(g: "f7222374-2144-4815-b550-a37f8e12ee52");
    public static Guid CODECAPI_AVEncMPVDefaultBPictureCount = new(g: "8d390aac-dc5c-4200-b57f-814d04babab2");
    public static Guid IID_ICodecAPI = new(g: "901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public const uint InterlaceModeProgressive = 2;
    public const ushort VtUi4 = 19;

    [DllImport("Mfplat.dll")]
    public static extern int MFTEnumEx(Guid guidCategory, uint Flags, nint pInputType, nint pOutputType, out nint pppMFTActivate, out uint pnumMFTActivate);
    [DllImport("Mfplat.dll")]
    public static extern int MFCreateMediaType(out IMFMediaType2 ppMFType);
    [DllImport("Mfplat.dll")]
    public static extern int MFCreateSample(out IMFSample2 ppIMFSample);
    [DllImport("Mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer2 ppBuffer);

    // A (major, subtype) pair MFTEnumEx matches an MFT's input/output against.
    [StructLayout(LayoutKind.Sequential)]
    public struct MftRegisterTypeInfo {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    // IMFTransform::GetOutputStreamInfo result — dwFlags carries MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.
    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputStreamInfo {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    // IMFTransform::ProcessOutput buffer descriptor. pSample is an IMFSample* (IUnknown) marshalled by hand: the MFT
    // fills it when the stream provides samples, or reads a caller-allocated one otherwise.
    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputDataBuffer {
        public uint dwStreamID;
        public nint pSample;
        public uint dwStatus;
        public nint pEvents;
    }

    // A VT_UI4 VARIANT for ICodecAPI::SetValue.
    [StructLayout(LayoutKind.Sequential)]
    public struct CodecApiVariant {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public ulong value;
    }

    /// <summary>Throws if a Media Foundation HRESULT indicates failure.</summary>
    public static void Check(int hr) {
        if (hr < 0) {
            throw new COMException(message: "a Media Foundation encoder call failed", errorCode: hr);
        }
    }
}

/// <summary>IMFTransform — the encoder MFT. Real signatures on the slots the encoder drives; the rest are placeholders
/// preserving vtable layout.</summary>
[ComImport]
[Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFTransform {
    [PreserveSig] int GetStreamLimits();
    [PreserveSig] int GetStreamCount();
    [PreserveSig] int GetStreamIDs();
    [PreserveSig] int GetInputStreamInfo();
    [PreserveSig] int GetOutputStreamInfo(uint dwOutputStreamID, out MfEncoder.MftOutputStreamInfo pStreamInfo);
    [PreserveSig] int GetAttributes(out IMFAttributesRich pAttributes);
    [PreserveSig] int GetInputStreamAttributes();
    [PreserveSig] int GetOutputStreamAttributes();
    [PreserveSig] int DeleteInputStream();
    [PreserveSig] int AddInputStreams();
    [PreserveSig] int GetInputAvailableType();
    [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType2 ppType);
    [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType2 pType, uint dwFlags);
    [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType2 pType, uint dwFlags);
    [PreserveSig] int GetInputCurrentType();
    [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType2 ppType);
    [PreserveSig] int GetInputStatus();
    [PreserveSig] int GetOutputStatus();
    [PreserveSig] int SetOutputBounds();
    [PreserveSig] int ProcessEvent();
    [PreserveSig] int ProcessMessage(uint eMessage, nint ulParam);
    [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample2 pSample, uint dwFlags);
    [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount, ref MfEncoder.MftOutputDataBuffer pOutputSamples, out uint pdwStatus);
}

/// <summary>IMFMediaEventGenerator — only GetEvent (slot 1) is called; the async MFT is pumped synchronously.</summary>
[ComImport]
[Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFMediaEventGenerator {
    [PreserveSig] int GetEvent(uint dwFlags, out IMFMediaEvent ppEvent);
    [PreserveSig] int BeginGetEvent();
    [PreserveSig] int EndGetEvent();
    [PreserveSig] int QueueEvent();
}

/// <summary>IMFMediaEvent (IMFAttributes-derived) — only GetType (slot 29) is called.</summary>
[ComImport]
[Guid("df598932-f10c-4e39-bba2-c308f101daa3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFMediaEvent {
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
    [PreserveSig] int GetType(out uint pmet);
}

/// <summary>ICodecAPI — only SetValue (slot 7) is called, for best-effort GOP/rate-control tuning.</summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface ICodecAPI {
    [PreserveSig] int IsSupported();
    [PreserveSig] int IsModifiable();
    [PreserveSig] int GetParameterRange();
    [PreserveSig] int GetParameterValues();
    [PreserveSig] int GetDefaultValue();
    [PreserveSig] int GetValue();
    [PreserveSig] int SetValue(ref Guid Api, ref MfEncoder.CodecApiVariant Value);
}

/// <summary>IMFAttributes (rich) — SetUINT32 (19) is the only method used, to unlock the async MFT.</summary>
[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFAttributesRich {
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
    [PreserveSig] int SetGUID();
}

/// <summary>IMFMediaType (rich) — the attribute getters/setters the encoder configures a type with (major/subtype,
/// frame size/rate, bitrate, keyframe spacing) plus the blob reads for the codec-private sequence header.</summary>
[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFMediaType2 {
    [PreserveSig] int GetItem();
    [PreserveSig] int GetItemType();
    [PreserveSig] int CompareItem();
    [PreserveSig] int Compare();
    [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble();
    [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength();
    [PreserveSig] int GetString();
    [PreserveSig] int GetAllocatedString();
    [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(ref Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob();
    [PreserveSig] int GetUnknown();
    [PreserveSig] int SetItem();
    [PreserveSig] int DeleteItem();
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble();
    [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
}

/// <summary>IMFSample (rich) — input samples are built (AddBuffer, SetSampleTime/Duration) and output samples read
/// (GetSampleTime, ConvertToContiguousBuffer, GetUINT32 for the clean-point/keyframe flag).</summary>
[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFSample2 {
    [PreserveSig] int GetItem();
    [PreserveSig] int GetItemType();
    [PreserveSig] int CompareItem();
    [PreserveSig] int Compare();
    [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
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
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration();
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount();
    [PreserveSig] int GetBufferByIndex();
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer2 ppBuffer);
    [PreserveSig] int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer2 pBuffer);
}

/// <summary>IMFMediaBuffer (rich) — Lock/Unlock plus SetCurrentLength for input buffers, GetCurrentLength for output.</summary>
[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFMediaBuffer2 {
    [PreserveSig] int Lock(out nint ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}
