using Puck.Maths;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// How a single-level color image's texels lie in a buffer a buffer-image copy reads or writes
/// (<see cref="IGpuRecorder.CopyBufferToImage"/>, <see cref="IGpuRecorder.CopyImageToBuffer"/>): row after row from the
/// buffer's first byte, each row <see cref="RowPitch"/> bytes after the one before it, its first
/// <see cref="PackedRowBytes"/> bytes the row's texels. The device decides the pitch (<see cref="Of"/> takes its
/// <see cref="IGpuRecorder.CopyRowAlignment"/>), so a host that writes or reads the buffer walks rows by the footprint
/// rather than assuming tight packing.
/// </summary>
public readonly record struct GpuCopyFootprint {
    private GpuCopyFootprint(GpuPixelFormat format, uint width, uint height, uint rowPitch) {
        Format = format;
        Height = height;
        RowPitch = rowPitch;
        Width = width;
    }

    /// <summary>Gets the image's format.</summary>
    public GpuPixelFormat Format { get; }
    /// <summary>Gets the image's height, in texels: the rows the buffer holds.</summary>
    public uint Height { get; }
    /// <summary>Gets the bytes from the start of one row to the start of the next; a multiple of the texel size and of
    /// the device's row alignment.</summary>
    public uint RowPitch { get; }
    /// <summary>Gets the image's width, in texels.</summary>
    public uint Width { get; }
    /// <summary>Gets the bytes of one row's texels, with nothing after them.</summary>
    public uint PackedRowBytes => (Width * GpuPixelFormats.TexelBytes(format: Format));
    /// <summary>Gets the bytes the copy reaches: every row's pitch but the last, then the last row's texels. A buffer at
    /// least this long holds the image.</summary>
    public ulong ByteLength => ((((ulong)RowPitch) * (Height - 1U)) + PackedRowBytes);
    /// <summary>Gets whether each row's texels follow the last row's with nothing between them, so the buffer holds the
    /// tightly packed image (<see cref="GpuPixelFormats.ImageByteLength"/>) and a host moves it in one copy.</summary>
    public bool IsPacked => (RowPitch == PackedRowBytes);

    /// <summary>Refuses a footprint a device's buffer-image copy does not admit: one never made (zero extent), or one
    /// whose pitch is not a multiple of the device's row alignment.</summary>
    /// <param name="rowAlignment">The device's <see cref="IGpuRecorder.CopyRowAlignment"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The footprint is empty, or its pitch is not a multiple of
    /// <paramref name="rowAlignment"/>.</exception>
    public void RequireAdmitted(uint rowAlignment) {
        if ((Width == 0U) || (Height == 0U)) {
            throw new ArgumentOutOfRangeException(
                actualValue: this,
                message: "A buffer-image copy's footprint comes from GpuCopyFootprint.Of; this one is empty.",
                paramName: "footprint"
            );
        }

        if ((RowPitch % rowAlignment) != 0U) {
            throw new ArgumentOutOfRangeException(
                actualValue: RowPitch,
                message: $"The device aligns a buffer-image copy's rows to {rowAlignment} bytes; this footprint's pitch is {RowPitch}.",
                paramName: "footprint"
            );
        }
    }
    /// <summary>Returns the footprint of an image whose rows start on a multiple of <paramref name="rowAlignment"/>.</summary>
    /// <param name="format">The image's format; a color format.</param>
    /// <param name="width">The width, in texels; positive.</param>
    /// <param name="height">The height, in texels; positive.</param>
    /// <param name="rowAlignment">The device's row alignment (<see cref="IGpuRecorder.CopyRowAlignment"/>); a positive
    /// power of two.</param>
    /// <returns>The footprint.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is zero, the format is undefined or a depth format, or
    /// <paramref name="rowAlignment"/> is not a positive power of two.</exception>
    /// <exception cref="OverflowException">A row's pitch exceeds 32 bits.</exception>
    public static GpuCopyFootprint Of(GpuPixelFormat format, uint width, uint height, uint rowAlignment) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        if (GpuPixelFormats.IsDepth(format: format)) {
            throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: "A buffer-image copy moves a color image's texels; a depth image has no copy footprint here.",
                paramName: nameof(format)
            );
        }

        var packed = checked((width * GpuPixelFormats.TexelBytes(format: format)));
        // A texel's size is a power of two, so rounding a whole number of texels up to a power of two keeps it whole.
        var pitch = packed.AlignUp(alignment: rowAlignment);

        if (pitch < packed) {
            throw new OverflowException(message: $"A {width}-texel row of {format} aligned to {rowAlignment} bytes exceeds 32 bits.");
        }

        return new GpuCopyFootprint(
            format: format,
            height: height,
            rowPitch: pitch,
            width: width
        );
    }
}
