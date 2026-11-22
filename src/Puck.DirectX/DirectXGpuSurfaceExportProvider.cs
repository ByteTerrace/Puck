using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>Exports shared surfaces through Direct3D 12: over the Direct3D 12 host's own render device, and on a headless
/// Direct3D 12 device pinned to the render adapter for a Vulkan host, which imports what it allocates.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuSurfaceExportProvider : IGpuSurfaceExportProvider {
    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not a Direct3D 12 device.</exception>
    public IGpuSurfaceExporter ExporterOn(IGpuDeviceContext device) => new DirectXGpuSurfaceExportFactory(
        deviceContext: ((device as DirectXDeviceContext) ?? throw new ArgumentException(message: "Shared surfaces export only through a Direct3D 12 device.", paramName: nameof(device)))
    );
    /// <inheritdoc/>
    public IDisposable OpenExportDevice(long adapterLuid, out IGpuSurfaceExporter exporter) {
        var device = new DirectXDeviceContext(
            adapterLuid: adapterLuid,
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        );

        exporter = new DirectXGpuSurfaceExportFactory(deviceContext: device);

        return device;
    }
}
