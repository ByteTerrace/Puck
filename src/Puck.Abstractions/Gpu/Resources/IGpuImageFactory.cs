namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral images with declared usages. A depth attachment is created from the render pass attachment it
/// serves (<see cref="CreateDepth"/>), which states the one depth it is cleared to.
/// </summary>
public interface IGpuImageFactory {
    /// <summary>Creates an image.</summary>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="usage">The usages the image is created for, any but <see cref="GpuImageUsage.DepthAttachment"/>;
    /// <see cref="GpuImageUsages.Validate"/> states which a format may declare.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>The created image, owned by the caller.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The extent is zero, or the format or usage is undefined.</exception>
    /// <exception cref="ArgumentException">The usage does not fit the format, or is a depth attachment's.</exception>
    IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name);
    /// <summary>Creates the image a render pass's depth attachment draws into, in the attachment's format and cleared to its
    /// <see cref="GpuDepthAttachment.ClearDepth"/>: a backend that keeps an optimized clear with the image (Direct3D 12)
    /// creates it with that depth, so the render pass's clear takes the fast path and the debug layer reports no
    /// mismatched clear. The image is only a depth attachment.</summary>
    /// <param name="attachment">The depth attachment the image is created for.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>).</param>
    /// <returns>The created image, owned by the caller.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The extent is zero, the format is undefined, or the clear depth lies
    /// outside [0, 1].</exception>
    /// <exception cref="ArgumentException">The format is not a depth format.</exception>
    IGpuImage CreateDepth(in GpuDepthAttachment attachment, uint width, uint height, in GpuObjectName name);
}
