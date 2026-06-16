using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native Vulkan instance entry points (<c>vkCreateInstance</c> and <c>vkDestroyInstance</c>).
/// </summary>
public interface IVulkanInstanceApi {
    /// <summary>Creates a Vulkan instance.</summary>
    /// <param name="request">The instance creation parameters.</param>
    /// <param name="instanceHandle">When this method returns, the created native <c>VkInstance</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the instance was created successfully.</returns>
    VkResult CreateInstance(VulkanInstanceCreateRequest request, out nint instanceHandle);
    /// <summary>Destroys a Vulkan instance.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle to destroy.</param>
    void DestroyInstance(nint instanceHandle);
}
