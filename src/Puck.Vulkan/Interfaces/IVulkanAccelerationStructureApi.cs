using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the optional ray-tracing acceleration-structure entry points (<c>VK_KHR_acceleration_structure</c>
/// and its buffer-device-address dependency): buffer and structure creation, device-address queries, build
/// sizing, and recording build and barrier commands.
/// </summary>
public interface IVulkanAccelerationStructureApi {
    /// <summary>Determines whether acceleration-structure functionality is usable on the given device.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <returns><see langword="true"/> if the device supports acceleration structures; otherwise, <see langword="false"/>.</returns>
    bool SupportsDevice(nint deviceHandle);
    /// <summary>Queries the minimum required alignment of an acceleration structure build's scratch buffer offset.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle.</param>
    /// <param name="physicalDeviceHandle">The native <c>VkPhysicalDevice</c> handle.</param>
    /// <returns>The minimum scratch offset alignment, in bytes.</returns>
    uint QueryScratchAlignment(nint instanceHandle, nint physicalDeviceHandle);
    /// <summary>Creates a buffer suitable for acceleration structure storage or scratch use, and allocates its backing memory.</summary>
    /// <param name="request">The buffer creation parameters.</param>
    /// <returns>The created native <c>VkBuffer</c> and <c>VkDeviceMemory</c> handles.</returns>
    (nint BufferHandle, nint MemoryHandle) CreateBuffer(VulkanAccelerationBufferCreateRequest request);
    /// <summary>Destroys an acceleration-structure buffer and frees its backing memory.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to destroy.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to free.</param>
    void DestroyBuffer(nint deviceHandle, nint bufferHandle, nint memoryHandle);
    /// <summary>Maps a region of device memory into the host address space.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to map.</param>
    /// <param name="sizeBytes">The size, in bytes, of the region to map.</param>
    /// <returns>A pointer to the mapped host memory.</returns>
    nint MapMemory(nint deviceHandle, nint memoryHandle, ulong sizeBytes);
    /// <summary>Unmaps previously mapped device memory.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to unmap.</param>
    void UnmapMemory(nint deviceHandle, nint memoryHandle);
    /// <summary>Gets the device address of a buffer.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle.</param>
    /// <returns>The buffer's device address.</returns>
    ulong GetBufferDeviceAddress(nint deviceHandle, nint bufferHandle);
    /// <summary>Creates an acceleration structure over a region of an existing buffer.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle backing the structure.</param>
    /// <param name="sizeBytes">The size, in bytes, of the structure within the buffer.</param>
    /// <param name="accelerationStructureType">The level of the structure (top or bottom), as a <c>VkAccelerationStructureTypeKHR</c> value.</param>
    /// <returns>The created native <c>VkAccelerationStructureKHR</c> handle.</returns>
    nint CreateAccelerationStructure(nint deviceHandle, nint bufferHandle, ulong sizeBytes, uint accelerationStructureType);
    /// <summary>Destroys an acceleration structure.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="accelerationStructureHandle">The native <c>VkAccelerationStructureKHR</c> handle to destroy.</param>
    void DestroyAccelerationStructure(nint deviceHandle, nint accelerationStructureHandle);
    /// <summary>Gets the device address of an acceleration structure, for referencing it from shaders or instance data.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="accelerationStructureHandle">The native <c>VkAccelerationStructureKHR</c> handle.</param>
    /// <returns>The structure's device address.</returns>
    ulong GetDeviceAddress(nint deviceHandle, nint accelerationStructureHandle);
    /// <summary>Computes the memory sizes required to build an acceleration structure for the given geometry.</summary>
    /// <typeparam name="TGeometry">The unmanaged geometry structure type (for example a flattened <see cref="VkAccelerationStructureGeometryInstancesKhr"/> or <see cref="VkAccelerationStructureGeometryAabbsKhr"/>).</typeparam>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="accelerationStructureType">The level of the structure (top or bottom), as a <c>VkAccelerationStructureTypeKHR</c> value.</param>
    /// <param name="buildFlags">A bitmask of <c>VkBuildAccelerationStructureFlagBitsKHR</c> describing the build.</param>
    /// <param name="geometry">The geometry the structure is built from.</param>
    /// <param name="maxPrimitiveCount">The maximum number of primitives in the geometry.</param>
    /// <returns>The required acceleration structure, build scratch, and update scratch sizes.</returns>
    VkAccelerationStructureBuildSizesInfoKhr GetBuildSizes<TGeometry>(
        nint deviceHandle,
        uint accelerationStructureType,
        uint buildFlags,
        in TGeometry geometry,
        uint maxPrimitiveCount
    ) where TGeometry : unmanaged;
    /// <summary>Records a command that builds an acceleration structure from the given geometry into a destination structure.</summary>
    /// <typeparam name="TGeometry">The unmanaged geometry structure type (for example a flattened <see cref="VkAccelerationStructureGeometryInstancesKhr"/> or <see cref="VkAccelerationStructureGeometryAabbsKhr"/>).</typeparam>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="accelerationStructureType">The level of the structure (top or bottom), as a <c>VkAccelerationStructureTypeKHR</c> value.</param>
    /// <param name="buildFlags">A bitmask of <c>VkBuildAccelerationStructureFlagBitsKHR</c> describing the build.</param>
    /// <param name="destinationAccelerationStructure">The native <c>VkAccelerationStructureKHR</c> handle built into.</param>
    /// <param name="scratchDeviceAddress">The device address of the scratch buffer used by the build.</param>
    /// <param name="geometry">The geometry the structure is built from.</param>
    /// <param name="primitiveCount">The number of primitives built into the structure.</param>
    void CmdBuildAccelerationStructure<TGeometry>(
        nint deviceHandle,
        nint commandBufferHandle,
        uint accelerationStructureType,
        uint buildFlags,
        nint destinationAccelerationStructure,
        ulong scratchDeviceAddress,
        in TGeometry geometry,
        uint primitiveCount
    ) where TGeometry : unmanaged;
    /// <summary>Records a global memory barrier over the given access and stage scopes, used to order acceleration structure builds against their consumers.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle the command is recorded into.</param>
    /// <param name="sourceAccessMask">A bitmask of <c>VkAccessFlagBits</c> giving the source access scope.</param>
    /// <param name="destinationAccessMask">A bitmask of <c>VkAccessFlagBits</c> giving the destination access scope.</param>
    /// <param name="sourceStageMask">A bitmask of <c>VkPipelineStageFlagBits</c> giving the source stage scope.</param>
    /// <param name="destinationStageMask">A bitmask of <c>VkPipelineStageFlagBits</c> giving the destination stage scope.</param>
    void CmdMemoryBarrier(
        nint deviceHandle,
        nint commandBufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    );
}
