using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral GPU resource pixel formats. Each backend maps these to its native format values.
/// Deliberately distinct from <see cref="SurfaceFormat"/>: that enum is the presentable-surface vocabulary
/// (what a swapchain, window, or capture produces), while this one describes GPU resources (storage images,
/// render targets, sampled textures) and is free to grow GPU-only members with no presentable equivalent. The explicit
/// <see cref="GpuPixelFormats.FromSurfaceFormat"/> bridge marks exactly where a presentable format enters
/// GPU-resource land.
/// </summary>
public enum GpuPixelFormat : uint {
    /// <summary>The R8G8B8A8 unsigned normalized format.</summary>
    R8G8B8A8Unorm = 1,
    /// <summary>The B8G8R8A8 unsigned normalized format.</summary>
    B8G8R8A8Unorm = 2,
    /// <summary>Four 16-bit floating-point channels.</summary>
    R16G16B16A16Float = 3,
    /// <summary>Four 32-bit floating-point channels.</summary>
    R32G32B32A32Float = 4,
    /// <summary>One 32-bit floating-point depth channel, for a depth attachment.</summary>
    D32Float = 5,
    /// <summary>BC4: one unsigned normalized channel in 8-byte blocks of 4x4 texels (Vulkan <c>BC4_UNORM_BLOCK</c>,
    /// <c>DXGI_FORMAT_BC4_UNORM</c>). Sampled only.</summary>
    Bc4Unorm = 6,
    /// <summary>BC5: two unsigned normalized channels in 16-byte blocks of 4x4 texels (Vulkan <c>BC5_UNORM_BLOCK</c>,
    /// <c>DXGI_FORMAT_BC5_UNORM</c>). Sampled only.</summary>
    Bc5Unorm = 7,
    /// <summary>BC6H: three unsigned 16-bit floating-point channels in 16-byte blocks of 4x4 texels (Vulkan
    /// <c>BC6H_UFLOAT_BLOCK</c>, <c>DXGI_FORMAT_BC6H_UF16</c>). Sampled only.</summary>
    Bc6hUfloat = 8,
    /// <summary>BC7: four unsigned normalized channels in 16-byte blocks of 4x4 texels, read without sRGB decode (Vulkan
    /// <c>BC7_UNORM_BLOCK</c>, <c>DXGI_FORMAT_BC7_UNORM</c>). Sampled only.</summary>
    Bc7Unorm = 9,
}
/// <summary>
/// Conversions into the <see cref="GpuPixelFormat"/> vocabulary, and the byte layout of its images.
/// </summary>
public static class GpuPixelFormats {
    /// <summary>The texels along each edge of a block-compressed format's block.</summary>
    public const uint BlockTexels = 4U;

    /// <summary>Gets whether a format holds depth rather than color.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for a depth format.</returns>
    public static bool IsDepth(GpuPixelFormat format) => (format == GpuPixelFormat.D32Float);
    /// <summary>Gets whether a format stores 4x4 texel blocks rather than texels. A block-compressed image is only ever
    /// sampled: no shader writes it and no render pass draws into it.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for BC4, BC5, BC6H and BC7.</returns>
    public static bool IsBlockCompressed(GpuPixelFormat format) =>
        (format is GpuPixelFormat.Bc4Unorm or GpuPixelFormat.Bc5Unorm or GpuPixelFormat.Bc6hUfloat or GpuPixelFormat.Bc7Unorm);
    /// <summary>Gets the bytes one texel of an uncompressed format, or one 4x4 block of a block-compressed format,
    /// occupies.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bytes per texel, or per block for a block-compressed format.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not defined.</exception>
    public static uint UnitBytes(GpuPixelFormat format) => format switch {
        GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm or GpuPixelFormat.D32Float => 4U,
        GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.Bc4Unorm => 8U,
        GpuPixelFormat.R32G32B32A32Float or GpuPixelFormat.Bc5Unorm or GpuPixelFormat.Bc6hUfloat or GpuPixelFormat.Bc7Unorm => 16U,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The pixel format is not defined.",
            paramName: nameof(format)
        ),
    };
    /// <summary>Gets the extent of one mip level: each edge halved per level, and never below one texel.</summary>
    /// <param name="width">The width of level 0, in texels.</param>
    /// <param name="height">The height of level 0, in texels.</param>
    /// <param name="level">The level.</param>
    /// <returns>The level's width and height, in texels.</returns>
    public static (uint Width, uint Height) LevelExtent(uint width, uint height, uint level) => (
        Math.Max(val1: 1U, val2: (width >> ((int)level))),
        Math.Max(val1: 1U, val2: (height >> ((int)level)))
    );
    /// <summary>Gets the bytes one tightly packed level of an image occupies: rows of texels, or for a block-compressed
    /// format rows of blocks, each edge rounded up to whole blocks.</summary>
    /// <param name="format">The format.</param>
    /// <param name="width">The level's width, in texels.</param>
    /// <param name="height">The level's height, in texels.</param>
    /// <returns>The level's byte length.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not defined.</exception>
    /// <exception cref="OverflowException">The length exceeds a 64-bit count.</exception>
    public static ulong LevelByteLength(GpuPixelFormat format, uint width, uint height) {
        var (columns, rows) = (IsBlockCompressed(format: format)
            ? (BlocksAcross(texels: width), BlocksAcross(texels: height))
            : (((ulong)width), ((ulong)height)));

        return checked(((columns * rows) * UnitBytes(format: format)));
    }
    /// <summary>Gets the bytes a tightly packed mip chain occupies: every level from 0, largest first, each at
    /// <see cref="LevelByteLength"/>, with nothing between them. This is the layout an image upload reads
    /// (<see cref="IGpuSurfaceUpload.Upload"/>).</summary>
    /// <param name="format">The format.</param>
    /// <param name="width">The width of level 0, in texels.</param>
    /// <param name="height">The height of level 0, in texels.</param>
    /// <param name="levels">The number of levels.</param>
    /// <returns>The chain's byte length.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not defined.</exception>
    /// <exception cref="OverflowException">The length exceeds a 64-bit count.</exception>
    public static ulong ChainByteLength(GpuPixelFormat format, uint width, uint height, uint levels) {
        var total = 0UL;

        for (var level = 0U; (level < levels); level++) {
            var (levelWidth, levelHeight) = LevelExtent(
                height: height,
                level: level,
                width: width
            );

            total = checked((total + LevelByteLength(
                format: format,
                height: levelHeight,
                width: levelWidth
            )));
        }

        return total;
    }
    /// <summary>Refuses an upload whose extent or level count is empty, whose level count exceeds the extent's full mip
    /// chain, or whose bytes are not exactly the chain's tightly packed length, so every backend's upload refuses the
    /// same requests with the same words.</summary>
    /// <param name="byteLength">The length, in bytes, of the pixels offered.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width of level 0, in texels.</param>
    /// <param name="height">The height of level 0, in texels.</param>
    /// <param name="levels">The number of levels offered.</param>
    /// <returns>The chain's byte length.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension or the level count is zero, the level count exceeds
    /// the full chain, or the format is not defined.</exception>
    /// <exception cref="ArgumentException"><paramref name="byteLength"/> is not the chain's length.</exception>
    public static ulong RequireChain(long byteLength, GpuPixelFormat format, uint width, uint height, uint levels) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);
        ArgumentOutOfRangeException.ThrowIfZero(value: levels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: (32U - ((uint)uint.LeadingZeroCount(value: Math.Max(val1: width, val2: height)))),
            value: levels
        );

        var required = ChainByteLength(
            format: format,
            height: height,
            levels: levels,
            width: width
        );

        if (((ulong)byteLength) != required) {
            throw new ArgumentException(
                message: $"The upload of {levels} {format} level(s) at {width}x{height} requires exactly {required} tightly packed bytes; it offers {byteLength}.",
                paramName: nameof(byteLength)
            );
        }

        return required;
    }
    /// <summary>Converts a <see cref="SurfaceFormat"/> to its <see cref="GpuPixelFormat"/> equivalent.</summary>
    public static GpuPixelFormat FromSurfaceFormat(SurfaceFormat format) {
        return format switch {
            SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
            SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
            _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The surface format has no GPU pixel format mapping.",
            paramName: nameof(format)
        ),
        };
    }

    private static ulong BlocksAcross(uint texels) => (((((ulong)texels) + BlockTexels) - 1UL) / BlockTexels);
}
