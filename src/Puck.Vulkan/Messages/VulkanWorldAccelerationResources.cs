namespace Puck.Vulkan.Messages;

/// <summary>
/// Device resources for the ray-query world path: one static unit-AABB BLAS shared by every instance (instance
/// transforms scale and place it over each instance's world bound), the TLAS rebuilt over the instance buffer
/// every frame, their backing buffers and build scratch, and the persistently mapped instance buffer the world
/// rewrites each frame. A handle bag — it rides the per-frame build request into the recorder.
/// </summary>
/// <param name="AabbBufferHandle">The native <c>VkBuffer</c> holding the single unit-AABB build input.</param>
/// <param name="AabbMemoryHandle">The native <c>VkDeviceMemory</c> backing the AABB buffer.</param>
/// <param name="BlasBufferHandle">The native <c>VkBuffer</c> backing the bottom-level acceleration structure storage.</param>
/// <param name="BlasMemoryHandle">The native <c>VkDeviceMemory</c> backing the BLAS buffer.</param>
/// <param name="BlasHandle">The native <c>VkAccelerationStructureKHR</c> handle of the shared unit-AABB BLAS.</param>
/// <param name="BlasDeviceAddress">The device address of the BLAS, referenced by each TLAS instance.</param>
/// <param name="BlasScratchBufferHandle">The native <c>VkBuffer</c> holding the BLAS build scratch.</param>
/// <param name="BlasScratchMemoryHandle">The native <c>VkDeviceMemory</c> backing the BLAS scratch buffer.</param>
/// <param name="BlasScratchDeviceAddress">The alignment-rounded device address of the BLAS build scratch.</param>
/// <param name="InstanceBufferHandle">The native <c>VkBuffer</c> holding the TLAS instance array.</param>
/// <param name="InstanceMemoryHandle">The native <c>VkDeviceMemory</c> backing the instance buffer.</param>
/// <param name="InstanceBufferDeviceAddress">The device address of the instance buffer, consumed by the TLAS build.</param>
/// <param name="InstanceBufferMappedPointer">The persistently mapped host pointer the world writes instances through.</param>
/// <param name="TlasBufferHandle">The native <c>VkBuffer</c> backing the top-level acceleration structure storage.</param>
/// <param name="TlasMemoryHandle">The native <c>VkDeviceMemory</c> backing the TLAS buffer.</param>
/// <param name="TlasHandle">The native <c>VkAccelerationStructureKHR</c> handle of the per-frame TLAS.</param>
/// <param name="TlasScratchBufferHandle">The native <c>VkBuffer</c> holding the TLAS build scratch.</param>
/// <param name="TlasScratchMemoryHandle">The native <c>VkDeviceMemory</c> backing the TLAS scratch buffer.</param>
/// <param name="TlasScratchDeviceAddress">The alignment-rounded device address of the TLAS build scratch.</param>
/// <param name="AabbDeviceAddress">The device address of the unit-AABB buffer, consumed by the BLAS build.</param>
/// <param name="MaxInstanceCount">The maximum number of instances the instance buffer and TLAS are sized for.</param>
public readonly record struct VulkanWorldAccelerationResources(
    nint AabbBufferHandle,
    nint AabbMemoryHandle,
    nint BlasBufferHandle,
    nint BlasMemoryHandle,
    nint BlasHandle,
    ulong BlasDeviceAddress,
    nint BlasScratchBufferHandle,
    nint BlasScratchMemoryHandle,
    ulong BlasScratchDeviceAddress,
    nint InstanceBufferHandle,
    nint InstanceMemoryHandle,
    ulong InstanceBufferDeviceAddress,
    nint InstanceBufferMappedPointer,
    nint TlasBufferHandle,
    nint TlasMemoryHandle,
    nint TlasHandle,
    nint TlasScratchBufferHandle,
    nint TlasScratchMemoryHandle,
    ulong TlasScratchDeviceAddress,
    ulong AabbDeviceAddress,
    uint MaxInstanceCount
);
