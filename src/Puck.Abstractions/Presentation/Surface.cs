using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>Identifies the single payload carried by a <see cref="Surface"/>.</summary>
public enum SurfaceKind : byte {
    /// <summary>No content is present.</summary>
    Empty,
    /// <summary>A shader-readable image view owned by the consumer's GPU device chain.</summary>
    SameDeviceImage,
    /// <summary>Tightly packed pixels in host memory.</summary>
    CpuPixels,
    /// <summary>An external texture handle that the consumer must import.</summary>
    SharedHandle,
}
/// <summary>
/// The rendered pixels a node hands its host to composite. Factory methods enforce that exactly one payload is
/// populated, that the format is one its payload carries (<see cref="IsSurfaceFormat"/> for a shared texture,
/// <see cref="IsImageFormat"/> for CPU pixels and a same-device image), and that CPU storage exactly matches the declared
/// extent. The default value is the valid empty surface.
/// </summary>
public readonly record struct Surface {
    private Surface(
        SurfaceKind kind,
        nint imageHandle,
        nint imageViewHandle,
        uint width,
        uint height,
        GpuPixelFormat format,
        ReadOnlyMemory<byte> pixels,
        nint sharedHandle
    ) {
        Kind = kind;
        ImageHandle = imageHandle;
        ImageViewHandle = imageViewHandle;
        Width = width;
        Height = height;
        Format = format;
        Pixels = pixels;
        SharedHandle = sharedHandle;
    }

    /// <summary>Gets the texel format: a surface format (<see cref="IsSurfaceFormat"/>) for a shared texture, or any image
    /// format (<see cref="IsImageFormat"/>) for CPU pixels and a same-device image; zero for the empty surface.</summary>
    public GpuPixelFormat Format { get; }
    /// <summary>Gets the surface height in pixels.</summary>
    public uint Height { get; }
    /// <summary>Gets the same-device native image/resource handle used for transfer operations, or zero for another variant.</summary>
    public nint ImageHandle { get; }
    /// <summary>Gets the same-device image-view handle, or zero for another variant.</summary>
    public nint ImageViewHandle { get; }
    /// <summary>Gets whether this is the CPU-pixel variant.</summary>
    public bool IsCpuPixels => (SurfaceKind.CpuPixels == Kind);
    /// <summary>Gets whether this is the empty variant.</summary>
    public bool IsEmpty => (SurfaceKind.Empty == Kind);
    /// <summary>Gets whether this is the same-device image variant.</summary>
    public bool IsSameDeviceImage => (SurfaceKind.SameDeviceImage == Kind);
    /// <summary>Gets whether this is the external shared-handle variant.</summary>
    public bool IsSharedHandle => (SurfaceKind.SharedHandle == Kind);
    /// <summary>Gets the payload variant.</summary>
    public SurfaceKind Kind { get; }
    /// <summary>Gets the tightly packed CPU pixels, or empty memory for another variant.</summary>
    public ReadOnlyMemory<byte> Pixels { get; }
    /// <summary>Gets the external shared texture handle, or zero for another variant.</summary>
    public nint SharedHandle { get; }
    /// <summary>Gets the surface width in pixels.</summary>
    public uint Width { get; }

    private static void ValidateCommon(uint width, uint height, GpuPixelFormat format, bool image) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        if (!(image
            ? IsImageFormat(format: format)
            : IsSurfaceFormat(format: format))) {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                (image
                    ? "CPU pixels and a same-device image are R8G8B8A8Unorm, B8G8R8A8Unorm, R16G16B16A16Float or R32G32B32A32Float."
                    : "A surface's shared texture is R8G8B8A8Unorm or B8G8R8A8Unorm.")
            );
        }

        _ = RequiredByteLength(
            format: format,
            height: height,
            width: width
        );
    }

    /// <summary>Gets whether a surface's shared texture may carry a format, and whether a consumer that reads only 8-bit
    /// pixels reads it: the two 8-bit four-channel unsigned normalized orders, which every capture sink, video encoder
    /// and cross-device transfer reads.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for <see cref="GpuPixelFormat.R8G8B8A8Unorm"/> and
    /// <see cref="GpuPixelFormat.B8G8R8A8Unorm"/>.</returns>
    public static bool IsSurfaceFormat(GpuPixelFormat format) =>
        (format is GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm);
    /// <summary>Gets whether CPU pixels or a same-device image may carry a format: a surface format, or a four-channel
    /// float format, which a consumer samples or converts as it is and a display or a capture encodes; a capture of an
    /// HDR display hands its pixels over in <see cref="GpuPixelFormat.R16G16B16A16Float"/>.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for a surface format (<see cref="IsSurfaceFormat"/>),
    /// <see cref="GpuPixelFormat.R16G16B16A16Float"/> and <see cref="GpuPixelFormat.R32G32B32A32Float"/>.</returns>
    public static bool IsImageFormat(GpuPixelFormat format) => (
        IsSurfaceFormat(format: format) ||
        (format is GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R32G32B32A32Float)
    );
    /// <summary>Creates a surface backed by exactly one tightly packed texel of its format for every declared pixel, in
    /// any image format (<see cref="IsImageFormat"/>).</summary>
    public static Surface CpuPixels(ReadOnlyMemory<byte> pixels, uint width, uint height, GpuPixelFormat format) {
        ValidateCommon(
            format: format,
            height: height,
            image: true,
            width: width
        );
        var requiredByteLength = RequiredByteLength(
            format: format,
            height: height,
            width: width
        );

        if (pixels.Length != requiredByteLength) {
            throw new ArgumentException(
                message: $"The CPU surface requires exactly {requiredByteLength} tightly packed bytes for its declared extent.",
                paramName: nameof(pixels)
            );
        }

        return new Surface(
            format: format,
            height: height,
            imageHandle: 0,
            imageViewHandle: 0,
            kind: SurfaceKind.CpuPixels,
            pixels: pixels,
            sharedHandle: 0,
            width: width
        );
    }
    /// <summary>Returns the byte length a tightly packed extent of a format occupies: one texel of
    /// <see cref="GpuPixelFormats.UnitBytes"/> per pixel.</summary>
    public static int RequiredByteLength(uint width, uint height, GpuPixelFormat format) => checked((int)(checked((((ulong)width) * height)) * GpuPixelFormats.UnitBytes(format: format)));
    /// <summary>Creates a surface whose image view belongs to the consumer's device chain, in any image format
    /// (<see cref="IsImageFormat"/>).</summary>
    public static Surface SameDeviceImage(nint imageHandle, nint imageViewHandle, uint width, uint height, GpuPixelFormat format) {
        ValidateCommon(
            format: format,
            height: height,
            image: true,
            width: width
        );
        ArgumentOutOfRangeException.ThrowIfZero(value: imageHandle);
        ArgumentOutOfRangeException.ThrowIfZero(value: imageViewHandle);

        return new Surface(
            format: format,
            height: height,
            imageHandle: imageHandle,
            imageViewHandle: imageViewHandle,
            kind: SurfaceKind.SameDeviceImage,
            pixels: default,
            sharedHandle: 0,
            width: width
        );
    }
    /// <summary>Creates a surface backed by an external shareable texture handle.</summary>
    public static Surface SharedTexture(nint sharedHandle, uint width, uint height, GpuPixelFormat format) {
        ValidateCommon(
            format: format,
            height: height,
            image: false,
            width: width
        );
        ArgumentOutOfRangeException.ThrowIfZero(value: sharedHandle);

        return new Surface(
            format: format,
            height: height,
            imageHandle: 0,
            imageViewHandle: 0,
            kind: SurfaceKind.SharedHandle,
            pixels: default,
            sharedHandle: sharedHandle,
            width: width
        );
    }
}
