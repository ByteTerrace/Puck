namespace Puck.Vulkan.Messages;

/// <summary>
/// The result of creating an exportable image: the created image, its exportable memory, and the shared NT handle
/// another Vulkan instance imports to sample it zero-copy.
/// </summary>
/// <param name="ImageHandle">The created native <c>VkImage</c> handle.</param>
/// <param name="MemoryHandle">The native <c>VkDeviceMemory</c> handle backing the image.</param>
/// <param name="SharedHandle">The exported Win32 NT handle; the owner must close it once done.</param>
public readonly record struct VulkanExternalImageExportResult(
    nint ImageHandle,
    nint MemoryHandle,
    nint SharedHandle
);
