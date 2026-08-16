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
/// populated and that CPU storage exactly matches the declared extent and four-byte pixel format. The default value is
/// the valid empty surface.
/// </summary>
public readonly record struct Surface {
    private Surface(
        SurfaceKind kind,
        nint imageHandle,
        nint imageViewHandle,
        uint width,
        uint height,
        SurfaceFormat format,
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

    /// <summary>Gets the texel format.</summary>
    public SurfaceFormat Format { get; }
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

    private static void ValidateCommon(uint width, uint height, SurfaceFormat format) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        if (
            !Enum.IsDefined(value: format) ||
            (SurfaceFormat.Unknown == format)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "The surface format must be a supported four-byte texel format."
            );
        }

        _ = RequiredByteLength(
            height: height,
            width: width
        );
    }

    /// <summary>Creates a surface backed by exactly one tightly packed four-byte texel for every declared pixel.</summary>
    public static Surface CpuPixels(ReadOnlyMemory<byte> pixels, uint width, uint height, SurfaceFormat format) {
        ValidateCommon(
            format: format,
            height: height,
            width: width
        );
        var requiredByteLength = RequiredByteLength(
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
    /// <summary>Returns the byte length required by a tightly packed supported surface extent.</summary>
    public static int RequiredByteLength(uint width, uint height) => checked((int)(checked((((ulong)width) * height)) * 4UL));
    /// <summary>Creates a surface whose image view belongs to the consumer's device chain.</summary>
    public static Surface SameDeviceImage(nint imageHandle, nint imageViewHandle, uint width, uint height, SurfaceFormat format) {
        ValidateCommon(
            format: format,
            height: height,
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
    public static Surface SharedTexture(nint sharedHandle, uint width, uint height, SurfaceFormat format) {
        ValidateCommon(
            format: format,
            height: height,
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
