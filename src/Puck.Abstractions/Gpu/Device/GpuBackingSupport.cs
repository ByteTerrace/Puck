namespace Puck.Abstractions.Gpu;

/// <summary>
/// How a device can back a large buffer and give its memory back, and how a kernel may index an array of buffers: the
/// capability read the streaming plan's choice between sparse backing and slabs rests on
/// (<see cref="GpuDeviceCapabilities.Backing"/>). Each flag means one thing on both backends; the record keeps the native
/// figures beside it (<see cref="GpuDeviceCapabilities.ReservedResourcesTier"/>,
/// <see cref="GpuDeviceCapabilities.SparseAddressSpaceBytes"/>).
/// </summary>
[Flags]
public enum GpuBackingSupport : uint {
    /// <summary>Nothing is reported.</summary>
    None = 0U,
    /// <summary>A buffer can be created without memory and bound to memory afterwards, in pieces: Vulkan's
    /// <c>sparseBinding</c>, Direct3D 12's reserved resources at tiled resources tier 1 or above.</summary>
    SparseBinding = (1U << 0),
    /// <summary>Such a buffer may be partly bound while a kernel reads it: Vulkan's <c>sparseResidencyBuffer</c>,
    /// Direct3D 12's reserved buffers at tier 1 or above.</summary>
    SparseResidencyBuffer = (1U << 1),
    /// <summary>One piece of memory may back several places at once: Vulkan's <c>sparseResidencyAliased</c>.</summary>
    SparseResidencyAliased = (1U << 2),
    /// <summary>A queue binds sparse memory: a Vulkan queue family with <c>VK_QUEUE_SPARSE_BINDING_BIT</c>; any
    /// Direct3D 12 queue's <c>UpdateTileMappings</c> at tier 1 or above.</summary>
    SparseQueue = (1U << 3),
    /// <summary>A read of an unbound piece returns zero: Vulkan's <c>residencyNonResidentStrict</c>, Direct3D 12's tier 2
    /// or above.</summary>
    SparseNonResidentStrict = (1U << 4),
    /// <summary>A priority can be passed to the memory manager per allocation: Vulkan's <c>VK_EXT_memory_priority</c>
    /// with <c>memoryPriority</c>, Direct3D 12's <c>ID3D12Device1::SetResidencyPriority</c>.</summary>
    MemoryPriority = (1U << 5),
    /// <summary>Device-local memory may be paged out under pressure instead of failing allocation: Vulkan's
    /// <c>VK_EXT_pageable_device_local_memory</c> with <c>pageableDeviceLocalMemory</c>; always on Direct3D 12, whose heaps
    /// the operating system's video memory manager pages.</summary>
    PageableDeviceLocal = (1U << 6),
    /// <summary>Memory can be made resident without blocking, with a refusal past the budget: Direct3D 12's
    /// <c>ID3D12Device3::EnqueueMakeResident</c>.</summary>
    EnqueueMakeResident = (1U << 7),
    /// <summary>A kernel may index an array of storage buffers by a dynamically uniform value: Vulkan's
    /// <c>shaderStorageBufferArrayDynamicIndexing</c>; always on Direct3D 12 at shader model 5.1 and above.</summary>
    BufferArrayDynamicIndexing = (1U << 8),
    /// <summary>A kernel may index an array of storage buffers by a value that differs across a wave's lanes: Vulkan's
    /// <c>shaderStorageBufferArrayNonUniformIndexing</c>; always on Direct3D 12 through
    /// <c>NonUniformResourceIndex</c>.</summary>
    BufferArrayNonUniformIndexing = (1U << 9),
}
