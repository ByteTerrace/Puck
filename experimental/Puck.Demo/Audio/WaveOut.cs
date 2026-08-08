using System.Runtime.InteropServices;

namespace Puck.Demo.Audio;

/// <summary>The PCM stream description passed to <see cref="WaveOut.Open"/> (the OS <c>WAVEFORMATEX</c> layout).</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
internal struct WaveFormat {
    public ushort FormatTag;
    public ushort ChannelCount;
    public uint SamplesPerSecond;
    public uint AverageBytesPerSecond;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraByteCount;
}

/// <summary>A queued output buffer's bookkeeping block (the OS <c>WAVEHDR</c> layout). Lives in native memory for as
/// long as it can be in flight, so the device driver's pointer to it never moves.</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
internal struct WaveHeader {
    public nint Data;
    public uint BufferByteLength;
    public uint BytesRecorded;
    public nint UserData;
    public uint Flags;
    public uint LoopCount;
    public nint Next;
    public nint Reserved;
}

/// <summary>P/Invoke surface for the OS waveform-audio output API (winmm.dll) — the host side of cabinet audio.</summary>
internal static partial class WaveOut {
    /// <summary>The "pick a suitable device" id (<c>WAVE_MAPPER</c>).</summary>
    public const uint DeviceMapper = 0xFFFFFFFF;
    /// <summary>The header flag the driver sets when a queued buffer has finished playing (<c>WHDR_DONE</c>).</summary>
    public const uint FlagDone = 0x00000001;
    /// <summary>The success return code (<c>MMSYSERR_NOERROR</c>).</summary>
    public const uint NoError = 0;
    /// <summary>The PCM format tag (<c>WAVE_FORMAT_PCM</c>).</summary>
    public const ushort FormatPcm = 1;

    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "waveOutOpen")]
    public static partial uint Open(out nint handle, uint deviceId, in WaveFormat format, nint callback, nint instance, uint flags);
    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    public static partial uint PrepareHeader(nint handle, nint header, uint headerByteLength);
    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    public static partial uint UnprepareHeader(nint handle, nint header, uint headerByteLength);
    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "waveOutWrite")]
    public static partial uint Write(nint handle, nint header, uint headerByteLength);
    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "waveOutReset")]
    public static partial uint Reset(nint handle);
    [LibraryImport(libraryName: "winmm.dll", EntryPoint = "waveOutClose")]
    public static partial uint Close(nint handle);
}
