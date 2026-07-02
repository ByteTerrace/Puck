namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral storage images (compute-writable, then sampleable).
/// </summary>
public interface IGpuStorageImageFactory {
    /// <summary>Creates a storage image.</summary>
    /// <param name="deviceContext">The device to create the image on.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <returns>The created storage image.</returns>
    IGpuStorageImage Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height);
}
