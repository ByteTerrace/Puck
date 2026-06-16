using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native render pass entry points (<c>vkCreateRenderPass</c> and <c>vkDestroyRenderPass</c>).
/// </summary>
public interface IVulkanRenderPassApi {
    /// <summary>Creates a render pass.</summary>
    /// <param name="request">The render pass creation parameters.</param>
    /// <param name="renderPassHandle">When this method returns, the created native <c>VkRenderPass</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the render pass was created successfully.</returns>
    VkResult CreateRenderPass(VulkanRenderPassCreateRequest request, out nint renderPassHandle);
    /// <summary>Destroys a render pass.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the render pass.</param>
    /// <param name="renderPassHandle">The native <c>VkRenderPass</c> handle to destroy.</param>
    void DestroyRenderPass(nint deviceHandle, nint renderPassHandle);
}
