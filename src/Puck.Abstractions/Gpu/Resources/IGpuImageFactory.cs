namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral images with declared usages.
/// </summary>
public interface IGpuImageFactory {
    /// <summary>Creates an image.</summary>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="usage">The usages the image is created for; <see cref="GpuImageUsages.Validate"/> states which a
    /// format may declare.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <param name="clearDepth">The depth a depth attachment is cleared to, in [0, 1]: the render pass drawing into it clears
    /// to the same <see cref="GpuDepthAttachment.ClearDepth"/>, since a backend that keeps an optimized clear with the
    /// image (Direct3D 12) creates it with this one. Unused for any other usage.</param>
    /// <returns>The created image, owned by the caller.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The extent is zero, the format or usage is undefined, or the clear
    /// depth lies outside [0, 1].</exception>
    /// <exception cref="ArgumentException">The usage does not fit the format.</exception>
    IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name, float clearDepth = 1f);
}
