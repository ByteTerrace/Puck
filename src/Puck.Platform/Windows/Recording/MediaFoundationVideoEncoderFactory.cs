using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Recording;
using static Puck.Platform.Windows.Recording.MfEncoder;

namespace Puck.Platform.Windows.Recording;

/// <summary>
/// The Windows <see cref="IVideoEncoderFactory"/>: walks the codec ladder and returns the first entry a Media
/// Foundation encoder MFT can produce on this machine. For each ladder token it enumerates hardware encoder MFTs
/// (<c>MFTEnumEx</c>, <c>MFT_CATEGORY_VIDEO_ENCODER</c>, hardware flag) and, only if no hardware MFT initializes, the
/// software MFTs — every candidate is fully configured (NV12 in, codec out) before it is accepted, so an MFT that
/// advertises support but rejects the extent or CPU input is skipped rather than returned broken. Declines with a
/// reason (never throws) when nothing on the ladder is encodable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationVideoEncoderFactory : IVideoEncoderFactory {
    /// <inheritdoc/>
    public IVideoEncoder? Create(IReadOnlyList<string> codecLadder, int width, int height, int frameRate, int bitrateKilobitsPerSecond, out string reason) {
        ArgumentNullException.ThrowIfNull(codecLadder);

        if (!OperatingSystem.IsWindows()) {
            reason = "video encoding requires Windows Media Foundation";

            return null;
        }

        if ((width <= 0) || (height <= 0) || ((width & 1) != 0) || ((height & 1) != 0)) {
            reason = $"the frame extent {width}x{height} must be positive and even for NV12 encoding";

            return null;
        }

        var attempts = new List<string>();

        MfInterop.Check(hr: MfInterop.MFStartup(Version: MfInterop.MfVersion, dwFlags: 0));

        try {
            foreach (var token in codecLadder) {
                if (!TryMapCodec(token: token, codec: out var codec, subtype: out var subtype)) {
                    attempts.Add(item: $"'{token}': unknown ladder token");

                    continue;
                }

                // Hardware first (the brief's ladder), then software only if no hardware MFT initializes.
                var encoder = TryCreateForCategory(codec: codec, subtype: subtype, hardware: true, width: width, height: height, frameRate: frameRate, bitrateKilobitsPerSecond: bitrateKilobitsPerSecond, attempts: attempts)
                    ?? TryCreateForCategory(codec: codec, subtype: subtype, hardware: false, width: width, height: height, frameRate: frameRate, bitrateKilobitsPerSecond: bitrateKilobitsPerSecond, attempts: attempts);

                if (encoder is not null) {
                    reason = "";

                    return encoder;
                }
            }
        } finally {
            _ = MfInterop.MFShutdown();
        }

        reason = ((attempts.Count == 0)
            ? "the codec ladder was empty"
            : $"no ladder codec was encodable ({string.Join(separator: "; ", values: attempts)})");

        return null;
    }

    private static MediaFoundationVideoEncoder? TryCreateForCategory(EncoderCodec codec, Guid subtype, bool hardware, int width, int height, int frameRate, int bitrateKilobitsPerSecond, List<string> attempts) {
        var flags = (MftEnumFlagSortAndFilter | (hardware ? (MftEnumFlagHardware | MftEnumFlagAsyncMft) : MftEnumFlagSyncMft));
        var outputInfo = new MftRegisterTypeInfo {
            guidMajorType = MFMediaType_Video,
            guidSubtype = subtype,
        };
        var outputInfoPointer = Marshal.AllocHGlobal(cb: Marshal.SizeOf<MftRegisterTypeInfo>());

        try {
            Marshal.StructureToPtr(structure: outputInfo, ptr: outputInfoPointer, fDeleteOld: false);

            var category = MFT_CATEGORY_VIDEO_ENCODER;
            var hr = MFTEnumEx(guidCategory: category, Flags: flags, pInputType: 0, pOutputType: outputInfoPointer, pppMFTActivate: out var activateArray, pnumMFTActivate: out var count);

            if ((hr < 0) || (count == 0) || (activateArray == 0)) {
                return null;
            }

            try {
                for (var index = 0; (index < count); index++) {
                    var activatePointer = Marshal.ReadIntPtr(ptr: activateArray, ofs: (index * IntPtr.Size));

                    if (activatePointer == 0) {
                        continue;
                    }

                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pUnk: activatePointer);
                    var mftName = ReadFriendlyName(activate: activate);

                    if (TryActivateEncoder(activate: activate, codec: codec, mftName: mftName, width: width, height: height, frameRate: frameRate, bitrateKilobitsPerSecond: bitrateKilobitsPerSecond, attempts: attempts, encoder: out var encoder)) {
                        return encoder;
                    }
                }
            } finally {
                for (var index = 0; (index < count); index++) {
                    var activatePointer = Marshal.ReadIntPtr(ptr: activateArray, ofs: (index * IntPtr.Size));

                    if (activatePointer != 0) {
                        _ = Marshal.Release(pUnk: activatePointer);
                    }
                }

                Marshal.FreeCoTaskMem(ptr: activateArray);
            }
        } finally {
            Marshal.FreeHGlobal(hglobal: outputInfoPointer);
        }

        return null;
    }

    private static bool TryActivateEncoder(IMFActivate activate, EncoderCodec codec, string mftName, int width, int height, int frameRate, int bitrateKilobitsPerSecond, List<string> attempts, out MediaFoundationVideoEncoder? encoder) {
        encoder = null;

        object? transformObject = null;

        try {
            var transformIid = IID_IMFTransform;

            MfInterop.Check(hr: activate.ActivateObject(riid: ref transformIid, ppv: out transformObject));

            var transform = (IMFTransform)transformObject;

            encoder = new MediaFoundationVideoEncoder(transform: transform, codec: codec, mftName: mftName, width: width, height: height, frameRate: frameRate, bitrateKilobitsPerSecond: bitrateKilobitsPerSecond);

            return true;
        } catch (Exception exception) {
            attempts.Add(item: $"'{mftName}' ({codec}) failed to initialize: 0x{exception.HResult:X8} {exception.Message}");

            if (transformObject is not null) {
                _ = Marshal.ReleaseComObject(o: transformObject);
            }

            return false;
        }
    }

    private static string ReadFriendlyName(IMFActivate activate) {
        var nameKey = MFT_FRIENDLY_NAME_Attribute;

        return ((activate.GetAllocatedString(guidKey: ref nameKey, ppwszValue: out var name, pcchLength: out _) >= 0)
            ? name
            : "unnamed encoder MFT");
    }

    private static bool TryMapCodec(string token, out EncoderCodec codec, out Guid subtype) {
        switch (token?.Trim().ToLowerInvariant()) {
            case "av1": {
                codec = EncoderCodec.Av1;
                subtype = MFVideoFormat_AV1;

                return true;
            }
            case "h264":
            case "avc": {
                codec = EncoderCodec.H264;
                subtype = MFVideoFormat_H264;

                return true;
            }
            default: {
                codec = EncoderCodec.H264;
                subtype = default;

                return false;
            }
        }
    }
}
