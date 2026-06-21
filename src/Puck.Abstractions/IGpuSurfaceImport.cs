namespace Puck.Abstractions;

/// <summary>
/// Imports a shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) from
/// another GPU backend as a sampleable image, without a host-memory round trip.
/// </summary>
public interface IGpuSurfaceImport : IDisposable {
    /// <summary>Imports a shared handle and returns a native image view handle ready for sampling.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sharedHandle">The shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms).</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="format">The pixel format (a <see cref="GpuPixelFormat"/> constant).</param>
    /// <returns>The native image view handle.</returns>
    nint Import(IGpuDeviceContext deviceContext, nint sharedHandle, uint width, uint height, uint format);
}
