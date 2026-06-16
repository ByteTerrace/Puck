using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native swapchain entry points (<c>vkCreateSwapchainKHR</c> and <c>vkDestroySwapchainKHR</c>).
/// </summary>
public interface IVulkanSwapchainApi {
    /// <summary>Creates a swapchain for a surface.</summary>
    /// <param name="request">The swapchain creation parameters.</param>
    /// <param name="swapchainHandle">When this method returns, the created native <c>VkSwapchainKHR</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the swapchain was created successfully.</returns>
    VkResult CreateSwapchain(VulkanSwapchainCreateRequest request, out nint swapchainHandle);
    /// <summary>Destroys a swapchain.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the swapchain.</param>
    /// <param name="swapchainHandle">The native <c>VkSwapchainKHR</c> handle to destroy.</param>
    void DestroySwapchain(nint deviceHandle, nint swapchainHandle);
}
