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
    public IGpuCommandPool Create(in GpuObjectName name) {
        var pool = new DirectXGpuCommandPool(deviceContext: deviceContext);
        var naming = deviceContext.Services.Naming;

        if (naming.IsEnabled) {
            var state = DirectXCommandBufferState.Decode(commandBufferHandle: pool.CommandBufferHandle);

            naming.Name(
                handle: state.Allocator,
                kind: GpuObjectKind.CommandPool,
                name: in name
            );
            naming.Name(
                handle: state.CommandList,
                kind: GpuObjectKind.CommandBuffer,
                name: in name
            );
        }

        return pool;
    }
}
