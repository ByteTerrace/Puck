using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanSwapchain"/> for a surface, choosing a format, present mode, and extent from
/// the surface's reported support.
/// </summary>
public interface IVulkanSwapchainFactory {
    /// <summary>Creates a swapchain for the surface, clamping the requested extent to the surface's supported range.</summary>
    /// <param name="logicalDevice">The logical device the swapchain is created on.</param>
    /// <param name="surface">The surface the swapchain presents to.</param>
    /// <param name="supportDetails">The surface's capabilities, formats, and present modes used to configure the swapchain.</param>
    /// <param name="desiredWidth">The requested swapchain width, in pixels.</param>
    /// <param name="desiredHeight">The requested swapchain height, in pixels.</param>
    /// <param name="preferredPresentMode">The desired present mode, as a <c>VkPresentModeKHR</c> value; used when the surface supports it, otherwise the default mailbox-then-immediate-then-FIFO preference applies. Pass <see langword="null"/> for the default.</param>
    /// <param name="preferredSurfaceFormat">The desired format/color-space pair; used when the surface supports it, otherwise the surface's first reported format applies. Pass <see langword="null"/> for the default.</param>
    /// <param name="imageUsage">The swapchain image usage, as a bitmask of <c>VkImageUsageFlagBits</c>; overrides the default of color-attachment plus transfer-source. Pass <see langword="null"/> for the default.</param>
    /// <returns>A new, owning <see cref="VulkanSwapchain"/>.</returns>
    VulkanSwapchain Create(
        VulkanLogicalDevice logicalDevice,
        VulkanSurface surface,
        VulkanSwapchainSupportDetails supportDetails,
        uint desiredWidth,
        uint desiredHeight,
        uint? preferredPresentMode = null,
        VulkanSurfaceFormat? preferredSurfaceFormat = null,
        uint? imageUsage = null
    );
}
