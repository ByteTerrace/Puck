using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuComputeCommandPoolFactory"/> for Direct3D 12 by creating
/// <see cref="DirectXGpuComputeCommandPool"/> instances.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuComputeCommandPoolFactory(DirectXDeviceContext deviceContext) : IGpuComputeCommandPoolFactory {
    /// <inheritdoc/>
    public IGpuComputeCommandPool Create() =>
        new DirectXGpuComputeCommandPool(deviceContext: deviceContext);
}
