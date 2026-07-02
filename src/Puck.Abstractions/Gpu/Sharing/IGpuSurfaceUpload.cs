namespace Puck.Abstractions.Gpu;

/// <summary>
/// Uploads CPU pixels to a GPU image for sampling. The upload object owns the image (and staging resources)
/// behind the handles it returns.
/// </summary>
public interface IGpuSurfaceUpload : IDisposable {
    /// <summary>Uploads the pixel data, blocks until the copy completes, and returns a native image view handle
    /// ready for sampling. The returned handle is owned by this upload object — the caller never destroys it —
    /// and is only guaranteed valid until the next <see cref="Upload"/> on this instance or this object's disposal
    /// (Direct3D 12 replaces the handle on every call; Vulkan reuses the same view while the device and
    /// extent/format are unchanged).</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="pixels">The tightly packed pixel data to upload.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns>The native image view handle, owned by this upload object.</returns>
    nint Upload(IGpuDeviceContext deviceContext, ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height);
}
