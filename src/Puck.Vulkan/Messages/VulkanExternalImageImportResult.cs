namespace Puck.Vulkan.Messages;

/// <summary>
/// The result of importing an external image: the created image and the memory imported from the shared handle.
/// </summary>
/// <param name="ImageHandle">The created native <c>VkImage</c> handle.</param>
/// <param name="MemoryHandle">The native <c>VkDeviceMemory</c> handle imported from the shared resource.</param>
public readonly record struct VulkanExternalImageImportResult(
    nint ImageHandle,
    nint MemoryHandle
);
