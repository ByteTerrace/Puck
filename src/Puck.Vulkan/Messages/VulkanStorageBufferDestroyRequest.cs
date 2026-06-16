namespace Puck.Vulkan.Messages;

/// <summary>
/// Identifies a storage buffer to destroy along with its backing memory.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="BufferHandle">The native <c>VkBuffer</c> handle to destroy.</param>
/// <param name="MemoryHandle">The native <c>VkDeviceMemory</c> handle to free.</param>
public readonly record struct VulkanStorageBufferDestroyRequest(
    nint DeviceHandle,
    nint BufferHandle,
    nint MemoryHandle
);
