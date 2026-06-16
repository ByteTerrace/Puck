using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for offscreen color render targets — images rendered into without a
/// swapchain (for example, for headless rendering or readback).
/// </summary>
public interface IVulkanOffscreenImageApi {
    /// <summary>Creates an offscreen color image and allocates its backing memory.</summary>
    /// <param name="request">The offscreen image creation parameters.</param>
    /// <returns>The created image and memory handles.</returns>
    VulkanOffscreenImageCreateResult CreateColorImage(VulkanOffscreenImageCreateRequest request);
    /// <summary>Destroys an offscreen color image and frees its backing memory.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the image.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to destroy.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle backing the image, to free.</param>
    void DestroyColorImage(nint deviceHandle, nint imageHandle, nint memoryHandle);
}
