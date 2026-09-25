namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates exportable images for zero-copy cross-backend surface sharing. This is an <em>optional</em> capability: only
/// a backend that can hand a shared GPU texture to another backend registers it, so a backend that cannot export is
/// never forced to supply an unsupported implementation. A host resolves it (rather than taking a hard dependency) and
/// falls back to the CPU-pixel transport when it is absent.
/// </summary>
public interface IGpuSurfaceExportFactory {
    /// <summary>Creates an exportable image backed by shared GPU memory on the given device.</summary>
    /// <param name="format">The pixel format; a color format, since a depth attachment is never exported.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="usage">The usages the image is created for, as <see cref="IGpuImageFactory.Create"/> takes them.</param>
    /// <returns>A new, owning <see cref="IGpuExportableImage"/>.</returns>
    IGpuExportableImage CreateExportableImage(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage);
}
