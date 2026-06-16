using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for creating and destroying vertex buffers.
/// </summary>
public interface IVulkanVertexBufferApi {
    /// <summary>Creates a vertex buffer and uploads the supplied vertex data into it.</summary>
    /// <param name="request">The vertex buffer creation parameters.</param>
    /// <param name="vertexData">The raw vertex data to upload into the buffer.</param>
    /// <returns>The created buffer and memory handles.</returns>
    VulkanVertexBufferCreateResult CreateVertexBuffer(VulkanVertexBufferCreateRequest request, byte[] vertexData);
    /// <summary>Destroys a vertex buffer and frees its backing memory.</summary>
    /// <param name="request">The destroy parameters identifying the buffer to release.</param>
    void DestroyVertexBuffer(VulkanVertexBufferDestroyRequest request);
}
