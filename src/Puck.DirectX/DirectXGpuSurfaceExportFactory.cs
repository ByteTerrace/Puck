using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuSurfaceExportFactory"/> for Direct3D 12 by creating
/// <see cref="DirectXGpuExportableRenderTarget"/> instances, converting <see cref="GpuPixelFormat"/> constants to
/// <c>DXGI_FORMAT</c> values. Registered only on the Direct3D 12 backend, which can hand a shared texture to
/// another backend on the same adapter.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuSurfaceExportFactory : IGpuSurfaceExportFactory {
    /// <inheritdoc/>
    public IGpuExportableRenderTarget CreateExportableTarget(IGpuDeviceContext deviceContext, uint width, uint height, uint format) =>
        new DirectXGpuExportableRenderTarget(
            deviceContext: (IDirectXDeviceContext)deviceContext,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            height: height,
            width: width
        );

    /// <inheritdoc/>
    public IGpuExportableStorageImage CreateExportableStorageImage(IGpuDeviceContext deviceContext, uint width, uint height, uint format) =>
        new DirectXGpuExportableStorageImage(
            deviceContext: (IDirectXDeviceContext)deviceContext,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            height: height,
            width: width
        );
}
