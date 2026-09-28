namespace Puck.Abstractions.Gpu;

/// <summary>
/// Imports a shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) from
/// another GPU backend as a sampleable image, without a host-memory round trip, on the device context the
/// <see cref="IGpuSurfaceTransferFactory"/> that created it is bound to. The import object owns every native resource
/// behind the handles it returns, holds them on the device of its first import, and is released before that device
/// goes, a device loss included.
/// </summary>
public interface IGpuSurfaceImport : IDisposable {
    /// <summary>Imports a shared handle and returns native image and image-view handles ready for transfer and sampling. Idempotent for a
    /// repeated handle: both backends cache the opened image, so a producer can pass the same stable handle every
    /// frame without re-importing. The returned handle is owned by this import object — the caller never destroys
    /// it — and stays valid until this object is disposed (on Vulkan, a later call with a different
    /// handle/extent/format replaces the single cached image, invalidating the previous handle).</summary>
    /// <param name="sharedHandle">The shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms).</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns>The native image and image-view handles, owned by this import object.</returns>
    GpuImportedSurface Import(nint sharedHandle, GpuPixelFormat format, uint width, uint height);
}
