namespace Puck.DirectX.Messages;

/// <summary>
/// Device resources for the Direct3D 12 ray-query world path: one static unit-AABB BLAS shared by every instance
/// (instance transforms scale and place it over each primitive's world bound), the TLAS rebuilt over the instance
/// buffer every frame, their backing and scratch buffers, and the persistently mapped upload buffers the world
/// writes the AABB and instances through. The D3D12 peer of <c>VulkanWorldAccelerationResources</c>: an acceleration
/// structure IS its buffer (a default-heap UAV resource left in the <c>RAYTRACING_ACCELERATION_STRUCTURE</c> state),
/// referenced from the shader and from instance descriptors by its GPU virtual address.
/// </summary>
/// <param name="AabbBufferHandle">The native <c>ID3D12Resource</c> upload buffer holding the single unit-AABB build input.</param>
/// <param name="AabbBufferGpuAddress">The GPU virtual address of the AABB buffer, consumed by the BLAS build.</param>
/// <param name="BlasBufferHandle">The native <c>ID3D12Resource</c> backing the bottom-level acceleration structure.</param>
/// <param name="BlasGpuAddress">The GPU virtual address of the BLAS, referenced by each TLAS instance.</param>
/// <param name="BlasScratchBufferHandle">The native <c>ID3D12Resource</c> holding the BLAS build scratch.</param>
/// <param name="InstanceBufferHandle">The native <c>ID3D12Resource</c> upload buffer holding the TLAS instance array.</param>
/// <param name="InstanceBufferGpuAddress">The GPU virtual address of the instance buffer, consumed by the TLAS build.</param>
/// <param name="InstanceBufferMappedPointer">The persistently mapped host pointer the world writes instance descriptors through.</param>
/// <param name="TlasBufferHandle">The native <c>ID3D12Resource</c> backing the top-level acceleration structure.</param>
/// <param name="TlasGpuAddress">The GPU virtual address of the TLAS, bound to the ray-query shader as an SRV.</param>
/// <param name="TlasScratchBufferHandle">The native <c>ID3D12Resource</c> holding the TLAS build scratch.</param>
/// <param name="MaxInstanceCount">The maximum number of instances the instance buffer and TLAS are sized for.</param>
public readonly record struct DirectXWorldAccelerationResources(
    nint AabbBufferHandle,
    ulong AabbBufferGpuAddress,
    nint BlasBufferHandle,
    ulong BlasGpuAddress,
    nint BlasScratchBufferHandle,
    nint InstanceBufferHandle,
    ulong InstanceBufferGpuAddress,
    nint InstanceBufferMappedPointer,
    nint TlasBufferHandle,
    ulong TlasGpuAddress,
    nint TlasScratchBufferHandle,
    uint MaxInstanceCount
);
