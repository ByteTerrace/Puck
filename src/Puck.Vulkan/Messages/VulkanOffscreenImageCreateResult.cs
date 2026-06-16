namespace Puck.Vulkan.Messages;

/// <summary>
/// The result of creating an offscreen color image: the image and its backing memory.
/// </summary>
/// <param name="ImageHandle">The created native <c>VkImage</c> handle.</param>
/// <param name="MemoryHandle">The native <c>VkDeviceMemory</c> handle backing the image.</param>
public readonly record struct VulkanOffscreenImageCreateResult(
    nint ImageHandle,
    nint MemoryHandle
);
