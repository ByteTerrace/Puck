using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuRenderTargetFactory"/> for Direct3D 12 by creating
/// <see cref="DirectXGpuRenderTarget"/> instances, converting <see cref="GpuPixelFormat"/> constants to
/// <c>DXGI_FORMAT</c> values.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuRenderTargetFactory : IGpuRenderTargetFactory {
    /// <inheritdoc/>
    public IGpuRenderTarget Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) =>
        new DirectXGpuRenderTarget(
            deviceContext: (IDirectXDeviceContext)deviceContext,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            height: height,
            width: width
        );
}
