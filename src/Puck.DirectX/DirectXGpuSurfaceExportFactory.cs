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
    public IGpuExportableRenderTarget CreateExportableTarget(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) =>
        new DirectXGpuExportableRenderTarget(
            deviceContext: (IDirectXDeviceContext)deviceContext,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            height: height,
            width: width
        );

    /// <inheritdoc/>
    public IGpuExportableStorageImage CreateExportableStorageImage(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) =>
        new DirectXGpuExportableStorageImage(
            deviceContext: (IDirectXDeviceContext)deviceContext,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            height: height,
            width: width
        );

    /// <summary>Creates an exportable storage image whose shared handle another API family can open — with
    /// <c>ALLOW_SIMULTANEOUS_ACCESS</c>, so a Direct3D 11 device (e.g. Media Foundation's camera decode device) can
    /// open and write it while this device merely owns the allocation. Not part of the neutral interface: the caller is
    /// Windows-specific by construction.</summary>
    /// <param name="deviceContext">The Direct3D 12 device context that allocates the texture.</param>
    /// <param name="format">The neutral <see cref="GpuPixelFormat"/>.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The exportable image (its <see cref="IGpuExportableStorageImage.SharedHandle"/> is the cross-API handle).</returns>
    public IGpuExportableStorageImage CreateSimultaneousAccessStorageImage(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) =>
        new DirectXGpuExportableStorageImage(
            deviceContext: (IDirectXDeviceContext)deviceContext,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            height: height,
            simultaneousAccess: true,
            width: width
        );
}
