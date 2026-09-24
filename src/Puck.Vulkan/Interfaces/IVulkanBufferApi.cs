using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// The one way the backend creates, maps, and destroys a buffer and its memory. A caller states the buffer's usage and
/// the memory it needs; <see cref="Interop.VulkanBuffer"/> owns the result when a caller wants an owner.
/// </summary>
public interface IVulkanBufferApi {
    /// <summary>Creates an exclusive buffer, allocates memory of a type <paramref name="memory"/> selects, and binds the
    /// two. Anything created before a failure is released before the exception propagates.</summary>
    /// <param name="device">The device context the buffer is created on.</param>
    /// <param name="usage">A bitmask of <see cref="VulkanBufferUsageFlags"/>.</param>
    /// <param name="memory">The memory the buffer is allocated from.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer; must be non-zero.</param>
    /// <returns>The buffer and its memory, owned by the caller and released with <see cref="Destroy"/>.</returns>
    VulkanBufferHandles Create(IVulkanDeviceContext device, uint usage, VulkanBufferMemory memory, ulong sizeBytes);
    /// <summary>Destroys a buffer and frees its memory; a zero handle is skipped.</summary>
    /// <param name="handles">The buffer and memory to release.</param>
    void Destroy(VulkanBufferHandles handles);
    /// <summary>Maps a <see cref="VulkanBufferMemory.HostCoherent"/> buffer's memory from offset zero.</summary>
    /// <param name="handles">The buffer whose memory is mapped; it must not already be mapped.</param>
    /// <param name="sizeBytes">The size, in bytes, of the mapped range.</param>
    /// <returns>The host pointer to the mapped range, valid until <see cref="Unmap"/>.</returns>
    nint Map(VulkanBufferHandles handles, ulong sizeBytes);
    /// <summary>Unmaps a buffer's memory mapped by <see cref="Map"/>.</summary>
    /// <param name="handles">The buffer whose memory is unmapped.</param>
    void Unmap(VulkanBufferHandles handles);
}
