namespace Puck.Abstractions.Gpu;

/// <summary>Allocates shared images and fences on one device, whose external handles another device on the same adapter
/// imports: the surfaces a camera, a capture or a view export writes and the render device samples.</summary>
public interface IGpuSurfaceExporter {
    /// <summary>Creates an image another device writes and this device's consumers sample at once.</summary>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns>The image, owned by the caller.</returns>
    IGpuExportableImage CreateSimultaneousAccessImage(GpuPixelFormat format, uint width, uint height);
    /// <summary>Creates an image a compute pass of another device writes.</summary>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns>The image, owned by the caller.</returns>
    IGpuExportableImage CreateSharedComputeImage(GpuPixelFormat format, uint width, uint height);
    /// <summary>Creates a fence another device imports to signal or wait on.</summary>
    /// <returns>The fence, owned by the caller.</returns>
    IGpuExportableFence CreateExportableFence();
}

/// <summary>The host's source of shared-surface exporters: one over a render device that exports surfaces itself, and a
/// headless device on the render adapter for a render backend that only imports them. Registered by a host whose platform
/// has shared surfaces; absent elsewhere.</summary>
public interface IGpuSurfaceExportProvider {
    /// <summary>Creates an exporter over a device of the exporting backend.</summary>
    /// <param name="device">The device, which must belong to the exporting backend.</param>
    /// <returns>The exporter.</returns>
    IGpuSurfaceExporter ExporterOn(IGpuDeviceContext device);
    /// <summary>Opens a headless device of the exporting backend on an adapter, for a render backend that imports the
    /// surfaces it allocates.</summary>
    /// <param name="adapterLuid">The render adapter's locally unique identifier.</param>
    /// <param name="exporter">The exporter over the device.</param>
    /// <returns>The device's lifetime, owned by the caller; the exporter's surfaces outlive no disposal of it.</returns>
    IDisposable OpenExportDevice(long adapterLuid, out IGpuSurfaceExporter exporter);
}
