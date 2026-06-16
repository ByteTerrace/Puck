using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanRenderPass"/> compatible with a swapchain's image format.
/// </summary>
public interface IVulkanRenderPassFactory {
    /// <summary>Creates a render pass with a single color attachment matching the swapchain's format.</summary>
    /// <param name="logicalDevice">The logical device the render pass is created on.</param>
    /// <param name="swapchain">The swapchain whose image format the render pass is compatible with.</param>
    /// <returns>A new, owning <see cref="VulkanRenderPass"/>.</returns>
    VulkanRenderPass Create(VulkanLogicalDevice logicalDevice, VulkanSwapchain swapchain);
}
