namespace Puck.Abstractions.Gpu;

/// <summary>
/// The one pixel-format vocabulary: the layout of an image's texels wherever they live. A GPU resource (a storage
/// image, a render target, a sampled texture, a swapchain image) is created in one, a presented or captured
/// <see cref="Presentation.Surface"/> declares one. Each backend maps a member to its native format value, and
/// <see cref="GpuPixelFormats"/> states each member's byte layout once.
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
    /// <summary>The R8G8B8A8 format with sRGB encoding: a read decodes and a write encodes (Vulkan <c>R8G8B8A8_SRGB</c>,
    /// <c>DXGI_FORMAT_R8G8B8A8_UNORM_SRGB</c>). A swapchain may be created in it.</summary>
    R8G8B8A8Srgb = 10,
    /// <summary>The B8G8R8A8 format with sRGB encoding (Vulkan <c>B8G8R8A8_SRGB</c>,
    /// <c>DXGI_FORMAT_B8G8R8A8_UNORM_SRGB</c>). A swapchain may be created in it.</summary>
    B8G8R8A8Srgb = 11,
    /// <summary>Three 10-bit unsigned normalized color channels and a 2-bit alpha in one 32-bit word, red in the low bits
    /// (Vulkan <c>A2B10G10R10_UNORM_PACK32</c>, <c>DXGI_FORMAT_R10G10B10A2_UNORM</c>). A swapchain may be created in
    /// it.</summary>
    R10G10B10A2Unorm = 12,
    /// <summary>One 8-bit unsigned normalized channel (Vulkan <c>R8_UNORM</c>, <c>DXGI_FORMAT_R8_UNORM</c>).</summary>
    R8Unorm = 13,
    /// <summary>Two 8-bit unsigned normalized channels, red first (Vulkan <c>R8G8_UNORM</c>,
    /// <c>DXGI_FORMAT_R8G8_UNORM</c>).</summary>
    R8G8Unorm = 14,
}
/// <summary>
/// The byte layout of <see cref="GpuPixelFormat"/> images: the one statement of each format's texel size, and the image
/// length an upload reads from it.
/// </summary>
public static class GpuPixelFormats {
    /// <summary>Gets whether a format holds depth rather than color.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for a depth format.</returns>
    public static bool IsDepth(GpuPixelFormat format) => (format == GpuPixelFormat.D32Float);
    /// <summary>Gets the largest code a color channel of a format stores: the steps a write quantizes the range zero to
    /// one into, 255 for an 8-bit format, sRGB or not, and 1023 for the 10-bit color channels of
    /// <see cref="GpuPixelFormat.R10G10B10A2Unorm"/>; zero for a floating-point format, which a write does not
    /// quantize.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The largest code, or zero for a floating-point format.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format holds depth, which no color write targets, or is not
    /// defined.</exception>
    public static uint ColorCodeMaximum(GpuPixelFormat format) => format switch {
        GpuPixelFormat.R8Unorm or GpuPixelFormat.R8G8Unorm or GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm
            or GpuPixelFormat.R8G8B8A8Srgb or GpuPixelFormat.B8G8R8A8Srgb => 255U,
        GpuPixelFormat.R10G10B10A2Unorm => 1023U,
        GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R32G32B32A32Float => 0U,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The pixel format is not a color format a write targets.",
            paramName: nameof(format)
        ),
    };
    /// <summary>Gets whether a format encodes sRGB on a write and decodes it on a read, so a shader writes it linear
    /// light.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for <see cref="GpuPixelFormat.R8G8B8A8Srgb"/> and
    /// <see cref="GpuPixelFormat.B8G8R8A8Srgb"/>.</returns>
    public static bool EncodesSrgb(GpuPixelFormat format) =>
        (format is GpuPixelFormat.R8G8B8A8Srgb or GpuPixelFormat.B8G8R8A8Srgb);
    /// <summary>Gets the bytes one texel of a format occupies.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bytes per texel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not defined.</exception>
    public static uint TexelBytes(GpuPixelFormat format) => format switch {
        GpuPixelFormat.R8Unorm => 1U,
        GpuPixelFormat.R8G8Unorm => 2U,
        GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm or GpuPixelFormat.D32Float or GpuPixelFormat.R8G8B8A8Srgb
            or GpuPixelFormat.B8G8R8A8Srgb or GpuPixelFormat.R10G10B10A2Unorm => 4U,
        GpuPixelFormat.R16G16B16A16Float => 8U,
        GpuPixelFormat.R32G32B32A32Float => 16U,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The pixel format is not defined.",
            paramName: nameof(format)
        ),
    };
    /// <summary>Gets the bytes a tightly packed image occupies: rows of texels with nothing between them. This is the
    /// layout an image upload reads (<see cref="IGpuSurfaceUpload.Upload"/>).</summary>
    /// <param name="format">The format.</param>
    /// <param name="width">The width, in texels.</param>
    /// <param name="height">The height, in texels.</param>
    /// <returns>The image's byte length.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not defined.</exception>
    /// <exception cref="OverflowException">The length exceeds a 64-bit count.</exception>
    public static ulong ImageByteLength(GpuPixelFormat format, uint width, uint height) =>
        checked(((((ulong)width) * height) * TexelBytes(format: format)));
    /// <summary>Refuses an upload whose extent is empty or whose bytes are not exactly the image's tightly packed length,
    /// so every backend's upload refuses the same requests with the same words.</summary>
    /// <param name="byteLength">The length, in bytes, of the pixels offered.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in texels.</param>
    /// <param name="height">The height, in texels.</param>
    /// <returns>The image's byte length.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is zero, or the format is not defined.</exception>
    /// <exception cref="ArgumentException"><paramref name="byteLength"/> is not the image's length.</exception>
    public static ulong RequireImage(long byteLength, GpuPixelFormat format, uint width, uint height) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        var required = ImageByteLength(
            format: format,
            height: height,
            width: width
        );

        if (((ulong)byteLength) != required) {
            throw new ArgumentException(
                message: $"The upload of a {format} image at {width}x{height} requires exactly {required} tightly packed bytes; it offers {byteLength}.",
                paramName: nameof(byteLength)
            );
        }

        return required;
    }
}
