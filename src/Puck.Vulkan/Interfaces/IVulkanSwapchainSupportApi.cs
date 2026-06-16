using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Gathers the swapchain support details (surface capabilities, formats, and present modes) needed to
/// configure a swapchain for a physical device and surface.
/// </summary>
public interface IVulkanSwapchainSupportApi {
    /// <summary>Queries the swapchain support details for a physical device and surface.</summary>
    /// <param name="instance">The Vulkan instance.</param>
    /// <param name="physicalDevice">The physical device whose support is queried.</param>
    /// <param name="surface">The surface a swapchain would present to.</param>
    /// <returns>The combined surface capabilities, supported formats, and present modes.</returns>
    VulkanSwapchainSupportDetails Query(VulkanInstance instance, VkPhysicalDevice physicalDevice, VulkanSurface surface);
}
