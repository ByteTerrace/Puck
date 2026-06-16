using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for storage buffers and the mapping of their backing device memory.
/// </summary>
public interface IVulkanStorageBufferApi {
    /// <summary>Creates a storage buffer and allocates its backing memory.</summary>
    /// <param name="request">The storage buffer creation parameters.</param>
    /// <returns>The created buffer and memory handles.</returns>
    VulkanStorageBufferCreateResult CreateStorageBuffer(VulkanStorageBufferCreateRequest request);
    /// <summary>Destroys a storage buffer and frees its backing memory.</summary>
    /// <param name="request">The destroy parameters identifying the buffer to release.</param>
    void DestroyStorageBuffer(VulkanStorageBufferDestroyRequest request);
    /// <summary>Maps a region of device memory into the host address space.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to map.</param>
    /// <param name="size">The size, in bytes, of the region to map.</param>
    /// <returns>A pointer to the mapped host memory.</returns>
    nint MapMemory(nint deviceHandle, nint memoryHandle, ulong size);
    /// <summary>Unmaps previously mapped device memory.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to unmap.</param>
    void UnmapMemory(nint deviceHandle, nint memoryHandle);
}
