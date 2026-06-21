using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuStorageImageFactory"/> for Direct3D 12 by creating <see cref="DirectXGpuStorageImage"/>
/// instances, converting <see cref="GpuPixelFormat"/> constants to <c>DXGI_FORMAT</c> values.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuStorageImageFactory : IGpuStorageImageFactory {
    /// <inheritdoc/>
    public IGpuStorageImage Create(IGpuDeviceContext deviceContext, uint format, uint width, uint height) =>
        new DirectXGpuStorageImage(
            deviceContext: (IDirectXDeviceContext)deviceContext,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            height: height,
            width: width
        );
}
