using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for the per-swapchain-image resources of a framebuffer set: image views,
/// framebuffers, and the swapchain image enumeration they are built from.
/// </summary>
public interface IVulkanFramebufferSetApi {
    /// <summary>Creates a framebuffer.</summary>
    /// <param name="request">The framebuffer creation parameters.</param>
    /// <param name="framebufferHandle">When this method returns, the created native <c>VkFramebuffer</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the framebuffer was created successfully.</returns>
    VkResult CreateFramebuffer(VulkanFramebufferCreateRequest request, out nint framebufferHandle);
    /// <summary>Creates an image view.</summary>
    /// <param name="request">The image view creation parameters.</param>
    /// <param name="imageViewHandle">When this method returns, the created native <c>VkImageView</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the image view was created successfully.</returns>
    VkResult CreateImageView(VulkanImageViewCreateRequest request, out nint imageViewHandle);
    /// <summary>Destroys a framebuffer.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the framebuffer.</param>
    /// <param name="framebufferHandle">The native <c>VkFramebuffer</c> handle to destroy.</param>
    void DestroyFramebuffer(nint deviceHandle, nint framebufferHandle);
    /// <summary>Destroys an image view.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the image view.</param>
    /// <param name="imageViewHandle">The native <c>VkImageView</c> handle to destroy.</param>
    void DestroyImageView(nint deviceHandle, nint imageViewHandle);
    /// <summary>Retrieves the presentable images owned by a swapchain.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="swapchainHandle">The native <c>VkSwapchainKHR</c> handle.</param>
    /// <returns>The native <c>VkImage</c> handles of the swapchain's images.</returns>
    IReadOnlyList<nint> GetSwapchainImages(nint deviceHandle, nint swapchainHandle);
}
