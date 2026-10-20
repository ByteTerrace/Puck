using System.Runtime.Versioning;
using Puck.Abstractions.Sources;
using Windows.Media.Capture.Frames;

namespace Puck.Platform.Windows;

/// <summary>Preserves the Media Foundation color attributes attached to one native camera format until the GPU
/// converter can either resolve them exactly or refuse the GPU tier.</summary>
public readonly record struct Win32CameraColorimetry(uint Matrix, uint NominalRange, uint ChromaSiting) {
    private const uint InvalidAttribute = uint.MaxValue;

    private static readonly Guid YuvMatrixAttribute = new(g: "3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRangeAttribute = new(g: "c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid ChromaSitingAttribute = new(g: "65df2370-c773-4c33-aa64-843e068efb0c");

    [SupportedOSPlatform("windows10.0.14393")]
    private static uint ReadUInt32(MediaFrameFormat format, Guid key) {
        try {
            if (!format.Properties.TryGetValue(
                key: key,
                value: out var value
            )) {
                return 0u;
            }

            return (value switch {
                byte byteValue => byteValue,
                ushort ushortValue => ushortValue,
                uint uintValue => uintValue,
                int intValue when (intValue >= 0) => checked((uint)intValue),
                _ => InvalidAttribute,
            });
        } catch {
            // Malformed projected metadata must not prevent the CPU graph from opening. The GPU converter rejects the
            // sentinel later, which routes the pair through the existing CPU fallback.
            return InvalidAttribute;
        }
    }

    [SupportedOSPlatform("windows10.0.14393")]
    public static Win32CameraColorimetry From(MediaFrameFormat format) => new(
        ChromaSiting: ReadUInt32(
            format: format,
            key: ChromaSitingAttribute
        ),
        Matrix: ReadUInt32(
            format: format,
            key: YuvMatrixAttribute
        ),
        NominalRange: ReadUInt32(
            format: format,
            key: NominalRangeAttribute
        )
    );
    public Win32YuvConversion Resolve() {
        var matrix = (Matrix switch {
            0u or 1u => ImageYuvMatrix.Bt709,
            2u => ImageYuvMatrix.Bt601,
            _ => throw new NotSupportedException(message: $"the camera reports unsupported YUV transfer matrix {Matrix}"),
        });
        var range = (NominalRange switch {
            0u or 2u => ImageYuvRange.Limited,
            1u => ImageYuvRange.Full,
            _ => throw new NotSupportedException(message: $"the camera reports unsupported YUV nominal range {NominalRange}"),
        });

        if ((ChromaSiting & ~0xFu) != 0u) {
            throw new NotSupportedException(message: $"the camera reports unsupported chroma-siting flags 0x{ChromaSiting:X}");
        }

        return new Win32YuvConversion(
            ChromaHorizontallyCosited: ((ChromaSiting & 0x4u) != 0u),
            ChromaVerticallyCosited: ((ChromaSiting & 0x2u) != 0u),
            Matrix: matrix,
            Range: range
        );
    }
}
public readonly record struct Win32YuvConversion(
    ImageYuvMatrix Matrix,
    ImageYuvRange Range,
    bool ChromaHorizontallyCosited,
    bool ChromaVerticallyCosited
);
