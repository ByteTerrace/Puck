namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral images with declared usages.
/// </summary>
public interface IGpuImageFactory {
    /// <summary>Creates an image.</summary>
    /// <param name="deviceContext">The device to create the image on.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="usage">The usages the image is created for; <see cref="GpuImageUsages.Validate"/> states which a
    /// format may declare.</param>
    /// <returns>The created image, owned by the caller.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The extent is zero, or the format or usage is undefined.</exception>
    /// <exception cref="ArgumentException">The usage does not fit the format.</exception>
    IGpuImage Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage);
}
