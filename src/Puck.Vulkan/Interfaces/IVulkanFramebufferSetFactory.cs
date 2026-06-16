using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanFramebufferSet"/> — the per-swapchain-image image views and framebuffers
/// bound to a render pass.
/// </summary>
public interface IVulkanFramebufferSetFactory {
    /// <summary>Creates an image view and framebuffer for each image of the swapchain, all compatible with the render pass.</summary>
    /// <param name="logicalDevice">The logical device the resources are created on.</param>
    /// <param name="renderPass">The render pass the framebuffers are compatible with.</param>
    /// <param name="swapchain">The swapchain whose images the framebuffers wrap.</param>
    /// <returns>A new, owning <see cref="VulkanFramebufferSet"/>.</returns>
    VulkanFramebufferSet Create(VulkanLogicalDevice logicalDevice, VulkanRenderPass renderPass, VulkanSwapchain swapchain);
}
