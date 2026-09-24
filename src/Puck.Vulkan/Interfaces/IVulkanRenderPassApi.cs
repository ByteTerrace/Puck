using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

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
    /// <summary>Destroys a render pass; a zero handle is skipped.</summary>
    /// <param name="device">The command table of the logical device that owns the render pass.</param>
    /// <param name="renderPassHandle">The native <c>VkRenderPass</c> handle to destroy.</param>
    void DestroyRenderPass(VulkanDeviceCommands device, nint renderPassHandle);
}
