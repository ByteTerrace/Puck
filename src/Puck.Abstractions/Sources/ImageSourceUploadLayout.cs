using System.Buffers.Binary;

namespace Puck.Abstractions.Sources;

/// <summary>
/// The header an uploaded source's region leads with: the image's extent, format and color encoding, and where each of
/// its planes lies in the region. Eight little-endian uints, in order: width, height, format code, color word, plane 0
/// byte offset, plane 0 row stride in bytes, plane 1 byte offset, plane 1 row stride in bytes. The color word packs the
/// matrix in bits 0–7, the range in 8–15, the transfer function in 16–23 and the primaries in 24–31. A single-plane
/// format carries zero for plane 1. <c>image-source.hlsli</c> in <c>Puck.Shaders</c> reads the same words.
/// </summary>
/// <param name="Width">The image's width in pixels.</param>
/// <param name="Height">The image's height in pixels.</param>
/// <param name="Format">The pixel format.</param>
/// <param name="Color">The color encoding.</param>
/// <param name="Plane0Offset">Plane 0's byte offset in the region: the pixels, the Y plane, or an indexed image's
/// palette.</param>
/// <param name="Plane0Stride">Plane 0's row stride in bytes; a palette's is its whole length.</param>
/// <param name="Plane1Offset">Plane 1's byte offset: the interleaved CbCr plane or an indexed image's index rows; zero for
/// a single-plane format.</param>
/// <param name="Plane1Stride">Plane 1's row stride in bytes; zero for a single-plane format.</param>
public readonly record struct ImageSourceUploadHeader(uint Width, uint Height, ImagePixelFormat Format, ImageColorEncoding Color, uint Plane0Offset, uint Plane0Stride, uint Plane1Offset, uint Plane1Stride) {
    /// <summary>Gets the color word the header carries for <see cref="Color"/>.</summary>
    public uint ColorWord => ((uint)Color.Matrix) | (((uint)Color.Range) << 8) | (((uint)Color.Transfer) << 16) | (((uint)Color.Primaries) << 24);
}
/// <summary>
/// Lays out an uploaded source's region: a <see cref="HeaderBytes"/>-byte <see cref="ImageSourceUploadHeader"/>, then its
/// planes, each row starting on a four-byte boundary so a kernel reads it by word. The region is what a producer writes
/// through a <c>GpuRegion</c> and what a conversion pass (<see cref="ImageSourceConversion"/>) reads.
/// <list type="bullet">
/// <item><description><see cref="ImagePixelFormat.R8G8B8A8Unorm"/>, <see cref="ImagePixelFormat.B8G8R8A8Unorm"/> and
/// <see cref="ImagePixelFormat.R10G10B10A2Unorm"/>: one plane of four-byte pixels.</description></item>
/// <item><description><see cref="ImagePixelFormat.Indexed8"/>: plane 0 is the <see cref="PaletteEntries"/>-entry RGBA8
/// palette and plane 1 the index rows, one byte per pixel.</description></item>
/// <item><description><see cref="ImagePixelFormat.Nv12"/>: plane 0 is the Y rows and plane 1 the half-height rows of
/// interleaved Cb, Cr pairs, one pair per two columns; an odd extent rounds the chroma plane up.</description></item>
/// </list>
/// </summary>
public static class ImageSourceUploadLayout {
    /// <summary>The header's length in bytes: eight uints.</summary>
    public const int HeaderBytes = 32;
    /// <summary>The largest extent either axis may declare, so every offset fits a uint with room to spare.</summary>
    public const uint MaxExtent = 16_384U;
    /// <summary>An indexed image's palette length in entries.</summary>
    public const int PaletteEntries = 256;

    private static uint AlignWord(uint bytes) => (bytes + 3U) & ~3U;
    // The chroma plane's extent along an axis: half the luma extent, rounded up.
    private static uint Half(uint extent) => ((extent + 1U) / 2U);

    /// <summary>Returns the header for an image of the given shape, with its planes packed in order after it.</summary>
    /// <param name="format">The pixel format.</param>
    /// <param name="color">The color encoding.</param>
    /// <param name="width">The width in pixels; 1 to <see cref="MaxExtent"/>.</param>
    /// <param name="height">The height in pixels; 1 to <see cref="MaxExtent"/>.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined format, or an extent is
    /// zero or above <see cref="MaxExtent"/>.</exception>
    public static ImageSourceUploadHeader HeaderOf(ImagePixelFormat format, ImageColorEncoding color, uint width, uint height) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxExtent,
            value: width
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxExtent,
            value: height
        );

        const uint First = HeaderBytes;

        return format switch {
            ImagePixelFormat.R8G8B8A8Unorm or ImagePixelFormat.B8G8R8A8Unorm or ImagePixelFormat.R10G10B10A2Unorm => new ImageSourceUploadHeader(
                Color: color,
                Format: format,
                Height: height,
                Plane0Offset: First,
                Plane0Stride: (width * 4U),
                Plane1Offset: 0U,
                Plane1Stride: 0U,
                Width: width
            ),
            ImagePixelFormat.Indexed8 => new ImageSourceUploadHeader(
                Color: color,
                Format: format,
                Height: height,
                Plane0Offset: First,
                Plane0Stride: (PaletteEntries * 4U),
                Plane1Offset: (First + (PaletteEntries * 4U)),
                Plane1Stride: AlignWord(bytes: width),
                Width: width
            ),
            ImagePixelFormat.Nv12 => new ImageSourceUploadHeader(
                Color: color,
                Format: format,
                Height: height,
                Plane0Offset: First,
                Plane0Stride: AlignWord(bytes: width),
                Plane1Offset: (First + (AlignWord(bytes: width) * height)),
                Plane1Stride: AlignWord(bytes: (2U * Half(extent: width))),
                Width: width
            ),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: "The pixel format is not defined.",
                paramName: nameof(format)
            ),
        };
    }
    /// <summary>Returns the region's length in bytes for a header: the header, then every plane, rounded to whole
    /// uints.</summary>
    /// <param name="header">The header, as <see cref="HeaderOf"/> returns it.</param>
    /// <returns>The length in bytes; a multiple of four.</returns>
    public static int ByteCount(in ImageSourceUploadHeader header) {
        var end = header.Format switch {
            ImagePixelFormat.Indexed8 => (((ulong)header.Plane1Offset) + (((ulong)header.Plane1Stride) * header.Height)),
            ImagePixelFormat.Nv12 => (((ulong)header.Plane1Offset) + (((ulong)header.Plane1Stride) * Half(extent: header.Height))),
            _ => (((ulong)header.Plane0Offset) + (((ulong)header.Plane0Stride) * header.Height)),
        };

        return checked((int)((end + 3UL) & ~3UL));
    }
    /// <summary>Returns one plane of a region: its rows at their stride, from the plane's offset.</summary>
    /// <param name="region">The region, at least <see cref="ByteCount"/> bytes of <paramref name="header"/>.</param>
    /// <param name="header">The region's header.</param>
    /// <param name="plane">The plane: 0, or 1 for an indexed or planar format.</param>
    /// <returns>The plane's bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plane"/> is not a plane of the header's format.</exception>
    /// <exception cref="ArgumentException"><paramref name="region"/> is shorter than the header declares.</exception>
    public static Span<byte> PlaneOf(Span<byte> region, in ImageSourceUploadHeader header, int plane) {
        var planar = (header.Format is ImagePixelFormat.Indexed8 or ImagePixelFormat.Nv12);

        if (
            (plane < 0) ||
            (plane > (planar ? 1 : 0))
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: plane,
                message: $"{header.Format} has no plane {plane}.",
                paramName: nameof(plane)
            );
        }

        if (region.Length < ByteCount(header: in header)) {
            throw new ArgumentException(
                message: $"The header declares {ByteCount(header: in header)} bytes; the region holds {region.Length}.",
                paramName: nameof(region)
            );
        }

        var (offset, length) = (plane, header.Format) switch {
            (0, ImagePixelFormat.Indexed8) => (header.Plane0Offset, header.Plane0Stride),
            (0, _) => (header.Plane0Offset, (header.Plane0Stride * header.Height)),
            (_, ImagePixelFormat.Nv12) => (header.Plane1Offset, (header.Plane1Stride * Half(extent: header.Height))),
            _ => (header.Plane1Offset, (header.Plane1Stride * header.Height)),
        };

        return region.Slice(
            length: ((int)length),
            start: ((int)offset)
        );
    }
    /// <summary>Reads a region's header.</summary>
    /// <param name="region">The region; at least <see cref="HeaderBytes"/> long.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ArgumentException"><paramref name="region"/> is shorter than its header or than the planes its
    /// header declares, or the header names no defined format, an extent of zero or above <see cref="MaxExtent"/>, or
    /// planes other than the ones <see cref="HeaderOf"/> lays out.</exception>
    public static ImageSourceUploadHeader Read(ReadOnlySpan<byte> region) {
        if (region.Length < HeaderBytes) {
            throw new ArgumentException(
                message: $"A source region holds a {HeaderBytes}-byte header; this one holds {region.Length} bytes.",
                paramName: nameof(region)
            );
        }

        var colorWord = BinaryPrimitives.ReadUInt32LittleEndian(source: region[12..]);
        var format = ((ImagePixelFormat)BinaryPrimitives.ReadUInt32LittleEndian(source: region[8..]));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(source: region);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(source: region[4..]);
        var color = new ImageColorEncoding(
            Matrix: ((ImageYuvMatrix)(colorWord & 0xFFU)),
            Primaries: ((ImageColorPrimaries)(colorWord >> 24)),
            Range: ((ImageYuvRange)((colorWord >> 8) & 0xFFU)),
            Transfer: ((ImageTransferFunction)((colorWord >> 16) & 0xFFU))
        );

        if (
            !Enum.IsDefined(value: format) ||
            (width == 0U) ||
            (height == 0U) ||
            (width > MaxExtent) ||
            (height > MaxExtent)
        ) {
            throw new ArgumentException(
                message: $"The source region's header declares format {((uint)format)} at {width}x{height}, which is not an uploadable image.",
                paramName: nameof(region)
            );
        }

        var header = new ImageSourceUploadHeader(
            Color: color,
            Format: format,
            Height: height,
            Plane0Offset: BinaryPrimitives.ReadUInt32LittleEndian(source: region[16..]),
            Plane0Stride: BinaryPrimitives.ReadUInt32LittleEndian(source: region[20..]),
            Plane1Offset: BinaryPrimitives.ReadUInt32LittleEndian(source: region[24..]),
            Plane1Stride: BinaryPrimitives.ReadUInt32LittleEndian(source: region[28..]),
            Width: width
        );
        var expected = HeaderOf(
            color: color,
            format: format,
            height: height,
            width: width
        );

        if (header != expected) {
            throw new ArgumentException(
                message: "The source region's plane offsets or strides are not the layout its format declares.",
                paramName: nameof(region)
            );
        }

        if (region.Length < ByteCount(header: in header)) {
            throw new ArgumentException(
                message: $"The source region's header declares {ByteCount(header: in header)} bytes; the region holds {region.Length}.",
                paramName: nameof(region)
            );
        }

        return header;
    }
    /// <summary>Writes a header into the first <see cref="HeaderBytes"/> bytes of a region.</summary>
    /// <param name="region">The region; at least <see cref="HeaderBytes"/> long.</param>
    /// <param name="header">The header.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/> is shorter than the header.</exception>
    public static void Write(Span<byte> region, in ImageSourceUploadHeader header) {
        if (region.Length < HeaderBytes) {
            throw new ArgumentException(
                message: $"A source region holds a {HeaderBytes}-byte header; this one holds {region.Length} bytes.",
                paramName: nameof(region)
            );
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination: region, value: header.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: region[4..], value: header.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: region[8..], value: ((uint)header.Format));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: region[12..], value: header.ColorWord);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: region[16..], value: header.Plane0Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: region[20..], value: header.Plane0Stride);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: region[24..], value: header.Plane1Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: region[28..], value: header.Plane1Stride);
    }
}
