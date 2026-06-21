namespace Puck.Abstractions;

/// <summary>
/// Uploads CPU pixels to a GPU image for sampling.
/// </summary>
public interface IGpuSurfaceUpload : IDisposable {
    /// <summary>Uploads the pixel data and returns a native image view handle ready for sampling.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="pixels">The tightly packed pixel data to upload.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="format">The pixel format (a <see cref="GpuPixelFormat"/> constant).</param>
    /// <returns>The native image view handle.</returns>
    nint Upload(IGpuDeviceContext deviceContext, ReadOnlyMemory<byte> pixels, uint width, uint height, uint format);
}
