namespace Puck.Abstractions;

/// <summary>
/// Reads back an offscreen image from the GPU to host memory. The readback object owns the host-visible staging
/// resources behind the memory it returns.
/// </summary>
public interface IGpuSurfaceReadback : IDisposable {
    /// <summary>Copies the source image to a host-visible buffer, blocks until the copy completes, and returns the
    /// pixel data. The source image must be shader-readable and is left shader-readable. The returned memory is
    /// only guaranteed valid until the next <see cref="Read"/> on this instance (the Direct3D 12 implementation
    /// reuses one output buffer across calls) — copy it if it must live longer.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sourceImageHandle">The native image handle to read back from.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="format">The pixel format (a <see cref="GpuPixelFormat"/> constant).</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel.</param>
    /// <returns>The tightly packed pixel data; valid until the next <see cref="Read"/> or this object's disposal.</returns>
    ReadOnlyMemory<byte> Read(IGpuDeviceContext deviceContext, nint sourceImageHandle, uint width, uint height, uint format, uint bytesPerPixel);
}
