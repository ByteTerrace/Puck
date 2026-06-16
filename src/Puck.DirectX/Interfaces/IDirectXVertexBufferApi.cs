using System.Runtime.Versioning;
using Puck.DirectX.Messages;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Wraps the native entry points for vertex buffers: creating an upload-heap resource, uploading vertex data
/// into it, and releasing it. The peer of <c>IVulkanVertexBufferApi</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public interface IDirectXVertexBufferApi {
    /// <summary>Creates a vertex buffer in an upload heap and copies the vertex data into it.</summary>
    /// <param name="request">The vertex buffer creation parameters.</param>
    /// <returns>The created resource handle and its GPU virtual address.</returns>
    /// <exception cref="DirectXException">A Direct3D 12 call failed.</exception>
    DirectXVertexBufferCreateResult CreateVertexBuffer(DirectXVertexBufferCreateRequest request);
    /// <summary>Releases a vertex buffer resource.</summary>
    /// <param name="bufferHandle">The native <c>ID3D12Resource</c> handle to release.</param>
    void DestroyVertexBuffer(nint bufferHandle);
}
