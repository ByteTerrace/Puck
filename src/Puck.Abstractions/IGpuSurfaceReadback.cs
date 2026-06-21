namespace Puck.Abstractions;

/// <summary>
/// Reads back an offscreen image from the GPU to host memory.
/// </summary>
public interface IGpuSurfaceReadback : IDisposable {
    /// <summary>Copies the source image to a host-visible buffer and returns the pixel data.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sourceImageHandle">The native image handle to read back from.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="format">The pixel format (a <see cref="GpuPixelFormat"/> constant).</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel.</param>
    /// <returns>The tightly packed pixel data.</returns>
    ReadOnlyMemory<byte> Read(IGpuDeviceContext deviceContext, nint sourceImageHandle, uint width, uint height, uint format, uint bytesPerPixel);
}
