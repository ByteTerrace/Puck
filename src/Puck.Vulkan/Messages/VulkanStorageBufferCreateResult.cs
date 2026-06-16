namespace Puck.Vulkan.Messages;

/// <summary>
/// The result of creating a storage buffer: the buffer and its backing memory.
/// </summary>
/// <param name="BufferHandle">The created native <c>VkBuffer</c> handle.</param>
/// <param name="MemoryHandle">The native <c>VkDeviceMemory</c> handle backing the buffer.</param>
public readonly record struct VulkanStorageBufferCreateResult(
    nint BufferHandle,
    nint MemoryHandle
);
