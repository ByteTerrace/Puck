using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanVertexBuffer"/> and uploads vertex data into it.
/// </summary>
public interface IVulkanVertexBufferFactory {
    /// <summary>Creates a vertex buffer sized to the supplied data and uploads that data into it.</summary>
    /// <param name="vulkanInstance">The Vulkan instance, used to resolve memory support.</param>
    /// <param name="logicalDevice">The logical device the buffer is created on.</param>
    /// <param name="vertexData">The raw vertex data to upload into the buffer.</param>
    /// <returns>A new, owning <see cref="VulkanVertexBuffer"/>.</returns>
    VulkanVertexBuffer Create(
        VulkanInstance vulkanInstance,
        VulkanLogicalDevice logicalDevice,
        byte[] vertexData
    );
}
