using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanCommandResources"/> — a command pool and a set of command buffers allocated
/// from it.
/// </summary>
public interface IVulkanCommandResourcesFactory {
    /// <summary>Creates a command pool and allocates the requested number of command buffers from it.</summary>
    /// <param name="logicalDevice">The logical device the resources are created on.</param>
    /// <param name="commandBufferCount">The number of command buffers to allocate (typically one per swapchain image).</param>
    /// <returns>A new, owning <see cref="VulkanCommandResources"/>.</returns>
    VulkanCommandResources Create(VulkanLogicalDevice logicalDevice, uint commandBufferCount);
}
