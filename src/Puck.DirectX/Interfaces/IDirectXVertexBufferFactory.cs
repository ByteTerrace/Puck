using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Creates an owning <see cref="DirectXVertexBuffer"/> from vertex bytes, against a device context. The peer
/// of <c>IVulkanVertexBufferFactory</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public interface IDirectXVertexBufferFactory {
    /// <summary>Creates a vertex buffer and uploads the vertex data.</summary>
    /// <param name="deviceContext">The device the buffer is created on.</param>
    /// <param name="vertexData">The tightly packed vertex bytes.</param>
    /// <param name="strideBytes">The size, in bytes, of one vertex.</param>
    /// <returns>A new, owning <see cref="DirectXVertexBuffer"/>.</returns>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    DirectXVertexBuffer Create(
        IDirectXDeviceContext deviceContext,
        ReadOnlyMemory<byte> vertexData,
        uint strideBytes
    );
}
