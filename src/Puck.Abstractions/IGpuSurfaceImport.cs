namespace Puck.Abstractions;

/// <summary>
/// Imports a shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) from
/// another GPU backend as a sampleable image, without a host-memory round trip. The import object owns every
/// native resource behind the handles it returns.
/// </summary>
public interface IGpuSurfaceImport : IDisposable {
    /// <summary>Imports a shared handle and returns a native image view handle ready for sampling. Idempotent for a
    /// repeated handle: both backends cache the opened image, so a producer can pass the same stable handle every
    /// frame without re-importing. The returned handle is owned by this import object — the caller never destroys
    /// it — and stays valid until this object is disposed (on Vulkan, a later call with a different
    /// handle/extent/format replaces the single cached image, invalidating the previous handle).</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="sharedHandle">The shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms).</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="format">The pixel format (a <see cref="GpuPixelFormat"/> constant).</param>
    /// <returns>The native image view handle, owned by this import object.</returns>
    nint Import(IGpuDeviceContext deviceContext, nint sharedHandle, uint width, uint height, uint format);
}
