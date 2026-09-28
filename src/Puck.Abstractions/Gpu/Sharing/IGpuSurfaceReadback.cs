namespace Puck.Abstractions.Gpu;

/// <summary>
/// Reads back an offscreen image from the GPU to host memory, on the device context the
/// <see cref="IGpuSurfaceTransferFactory"/> that created it is bound to. The readback object owns the host-visible
/// staging resources behind the memory it returns, holds them on the device of its first read, and is released before
/// that device goes, a device loss included.
/// </summary>
public interface IGpuSurfaceReadback : IDisposable {
    /// <summary>Copies the source image to a host-visible buffer, blocks until the copy completes, and returns the
    /// pixel data. The source image is restored to <paramref name="sourceLayout"/>. The returned memory is
    /// only guaranteed valid until the next <see cref="Read"/> on this instance (the Direct3D 12 implementation
    /// reuses one output buffer across calls) — copy it if it must live longer.</summary>
    /// <param name="sourceImageHandle">The native image handle to read back from.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel.</param>
    /// <param name="sourceLayout">The source image's current layout/state: <see cref="GpuImageLayout.External"/>,
    /// <see cref="GpuImageLayout.General"/>, or <see cref="GpuImageLayout.ShaderReadOnly"/>.</param>
    /// <returns>The tightly packed pixel data; valid until the next <see cref="Read"/> or this object's disposal.</returns>
    ReadOnlyMemory<byte> Read(nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout);
}
