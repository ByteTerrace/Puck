namespace Puck.Abstractions.Gpu;

/// <summary>
/// A shared fence created on this device for another to open: beyond <see cref="IGpuSharedFence"/>, it exposes the
/// shared handle a producer on another device or API opens to signal it (Direct3D 11 through
/// <c>ID3D11Device5::OpenSharedFence</c>), and that another backend imports to wait on it
/// (<see cref="IGpuSurfaceTransferFactory.TryImportFence"/>). The fence owns the handle and closes it on disposal.
/// </summary>
public interface IGpuExportableFence : IGpuSharedFence {
    /// <summary>Gets the shared handle (a Windows NT handle) another device opens or imports.</summary>
    nint SharedHandle { get; }
}
