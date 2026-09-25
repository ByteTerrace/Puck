using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuCommandPoolFactory"/> for Direct3D 12 by creating
/// <see cref="DirectXGpuCommandPool"/> instances.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuCommandPoolFactory(DirectXDeviceContext deviceContext) : IGpuCommandPoolFactory {
    /// <inheritdoc/>
    public IGpuCommandPool Create() =>
        new DirectXGpuCommandPool(deviceContext: deviceContext);
}
