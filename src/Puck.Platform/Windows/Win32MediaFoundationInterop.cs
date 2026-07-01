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
    public const uint EndOfStream = 0x00000002; // MF_SOURCE_READERF_ENDOFSTREAM

    public static Guid IID_IMFMediaSource = new(g: "279a808d-aec7-40c8-9c6b-a6b492c78a66");
    public static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new(g: "c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP = new(g: "8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new(g: "60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    public static Guid MF_MT_MAJOR_TYPE = new(g: "48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static Guid MF_MT_SUBTYPE = new(g: "f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static Guid MF_MT_FRAME_SIZE = new(g: "1652c33d-d6b2-4012-b834-72030849a37d");
    public static Guid MFMediaType_Video = new(g: "73646976-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_RGB32 = new(g: "00000016-0000-0010-8000-00aa00389b71");
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

/// <summary>IMFMediaType — GetUINT64 (slot 6) + SetGUID (slot 22) are called (both IMFAttributes-prefix slots).</summary>
[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFMediaType {
    [PreserveSig] int GetItem();
    [PreserveSig] int GetItemType();
    [PreserveSig] int CompareItem();
    [PreserveSig] int Compare();
    [PreserveSig] int GetUINT32();
    [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
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
    [PreserveSig] int GetBufferByIndex();
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
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
[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IMFSourceReader {
    [PreserveSig] int GetStreamSelection();
    [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    [PreserveSig] int GetNativeMediaType();
    [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, nint pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition();
    [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex, out uint pdwStreamFlags, out long pllTimestamp, out IMFSample? ppSample);
}
