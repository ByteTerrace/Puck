using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuVertexBufferFactory"/> for Direct3D 12 by forwarding to
/// <see cref="IDirectXVertexBufferFactory"/> and wrapping the result in a
/// <see cref="DirectXGpuVertexBuffer"/> whose <c>BufferHandle</c> is a GCHandle token carrying the full
/// vertex buffer view data.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuVertexBufferFactory(IDirectXVertexBufferFactory vertexBufferFactory) : IGpuVertexBufferFactory {
    /// <inheritdoc/>
    public IGpuVertexBuffer Create(IGpuDeviceContext deviceContext, byte[] vertexData, uint strideBytes) {
        var dxContext = (IDirectXDeviceContext)deviceContext;
        var inner = vertexBufferFactory.Create(
            deviceContext: dxContext,
            strideBytes: strideBytes,
            vertexData: vertexData
        );

        return new DirectXGpuVertexBuffer(inner: inner);
    }
}
