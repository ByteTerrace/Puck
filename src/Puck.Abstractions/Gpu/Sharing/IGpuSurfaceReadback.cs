namespace Puck.Abstractions.Gpu;

/// <summary>
/// Reads back an offscreen image from the GPU to host memory, on the device context the
/// <see cref="IGpuSurfaceTransferFactory"/> that created it is bound to. The readback object owns the host-visible
/// staging resources it copies through, holds them on the device of its first read, and is released before that device
/// goes, a device loss included. Both backends implement it once, as <see cref="GpuSurfaceReadback"/>.
/// </summary>
public interface IGpuSurfaceReadback : IDisposable {
    /// <summary>Copies the source image to a host-visible buffer, blocks until the copy completes, and returns the
    /// pixel data. The source image is restored to <paramref name="sourceLayout"/>.</summary>
    /// <param name="sourceImageHandle">The native image handle to read back from.</param>
    /// <param name="format">The image's pixel format, which sets the bytes per pixel.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="sourceLayout">The source image's current layout/state: <see cref="GpuImageLayout.External"/>,
    /// <see cref="GpuImageLayout.General"/>, or <see cref="GpuImageLayout.ShaderReadOnly"/>.</param>
    /// <returns>The tightly packed pixel data (<see cref="GpuPixelFormats.ImageByteLength"/>), in memory the caller
    /// owns.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceImageHandle"/> or a dimension is zero, or
    /// <paramref name="sourceLayout"/> is not a readable layout.</exception>
    ReadOnlyMemory<byte> Read(nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, GpuImageLayout sourceLayout);
}
