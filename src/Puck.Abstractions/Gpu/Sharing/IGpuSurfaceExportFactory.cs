namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates exportable render targets for zero-copy cross-backend surface sharing. This is an <em>optional</em>
/// capability: only a backend that can hand a shared GPU texture to another backend registers it, so a backend
/// that cannot export is never forced to supply an unsupported implementation. A host resolves it (rather than
/// taking a hard dependency) and falls back to the CPU-pixel transport when it is absent.
/// </summary>
public interface IGpuSurfaceExportFactory {
    /// <summary>Creates an exportable render target backed by shared GPU memory on the given device.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="format">The pixel format of the render target.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns>A new, owning <see cref="IGpuExportableRenderTarget"/>.</returns>
    IGpuExportableRenderTarget CreateExportableTarget(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height);

    /// <summary>Creates an exportable compute storage image backed by shared GPU memory on the given device — the
    /// compute-dispatch counterpart of <see cref="CreateExportableTarget"/>, for handing a compute result to another
    /// backend zero-copy.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="format">The pixel format of the storage image.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns>A new, owning <see cref="IGpuExportableStorageImage"/>.</returns>
    IGpuExportableStorageImage CreateExportableStorageImage(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height);
}
