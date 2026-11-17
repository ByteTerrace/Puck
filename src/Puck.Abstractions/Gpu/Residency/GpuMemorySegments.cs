using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One segment: what a graphics API budgets on its own, a DXGI segment group or a Vulkan heap, and the pool it lies in.
/// Several segments may lie in one pool (an integrated adapter's two Vulkan heaps), and each keeps its own budget.
/// </summary>
/// <param name="Index">The segment's index as its API numbers it: <c>DXGI_MEMORY_SEGMENT_GROUP</c>'s value
/// (<see cref="GpuMemorySegments.DirectXLocal"/>, <see cref="GpuMemorySegments.DirectXNonLocal"/>) or the Vulkan heap
/// index, below <see cref="GpuMemorySegments.MaxSegments"/>.</param>
/// <param name="Name">The segment's name as a report prints it: <c>local</c>, <c>non-local</c>, or <c>heap&lt;index&gt;</c>.</param>
/// <param name="Pool">The physical memory the segment lies in.</param>
/// <param name="Bytes">The segment's size, in bytes, as its API reports it: a Vulkan heap's size, or zero where the API
/// reports none (a DXGI segment group).</param>
public readonly record struct GpuMemorySegment(int Index, string Name, PhysicalMemory Pool, ulong Bytes);
/// <summary>
/// The segments one device budgets, each with the pool it lies in, as its backend reported them when the device was
/// created. This is the one place a segment's pool is decided: on unified memory every segment lies in
/// <see cref="PhysicalMemory.Host"/>; on a discrete adapter Direct3D 12's local segment group and every Vulkan heap the
/// device reports as device-local lie in <see cref="PhysicalMemory.Device"/>, and the rest in
/// <see cref="PhysicalMemory.Host"/>. <see cref="GpuDeviceMemoryWork"/> charges a counted allocation to the pool of the
/// segment its backend says it was placed in, through the segments registered for its device.
/// </summary>
public sealed class GpuMemorySegments {
    /// <summary>The index of Direct3D 12's local segment group (<c>DXGI_MEMORY_SEGMENT_GROUP_LOCAL</c>): a discrete
    /// adapter's own memory (<c>D3D12_MEMORY_POOL_L1</c>), or on a unified adapter its one memory
    /// (<c>D3D12_MEMORY_POOL_L0</c>).</summary>
    public const int DirectXLocal = 0;
    /// <summary>The index of Direct3D 12's non-local segment group (<c>DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL</c>): the
    /// system memory a discrete adapter reaches (<c>D3D12_MEMORY_POOL_L0</c>). A unified adapter has no such
    /// segment.</summary>
    public const int DirectXNonLocal = 1;
    /// <summary>The most segments a device reports: Vulkan's <c>VK_MAX_MEMORY_HEAPS</c>.</summary>
    public const int MaxSegments = 16;

    private readonly GpuMemorySegment?[] m_byIndex;
    private readonly int[] m_memoryTypeSegments;

    private GpuMemorySegments(GpuMemorySegment[] segments, int[] memoryTypeSegments, bool unifiedMemory) {
        m_byIndex = new GpuMemorySegment?[MaxSegments];
        foreach (var segment in segments) {
            m_byIndex[segment.Index] = segment;
        }
        m_memoryTypeSegments = memoryTypeSegments;
        Segments = segments;
        UnifiedMemory = unifiedMemory;
    }

    /// <summary>Gets the segments, in index order.</summary>
    public IReadOnlyList<GpuMemorySegment> Segments { get; }
    /// <summary>Gets whether the device's memory is the host's, so every segment lies in
    /// <see cref="PhysicalMemory.Host"/>.</summary>
    public bool UnifiedMemory { get; }

    /// <summary>Returns the segment at an index.</summary>
    /// <param name="index">The segment's index, as its API numbers it.</param>
    /// <returns>The segment.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The device has no segment at <paramref name="index"/>.</exception>
    public GpuMemorySegment this[int index] => (((((uint)index) < MaxSegments) && (m_byIndex[index] is { } segment))
        ? segment
        : throw new ArgumentOutOfRangeException(
            actualValue: index,
            message: $"The device has no memory segment {index}; it has {string.Join(separator: ", ", values: Segments.Select(selector: static segment => $"{segment.Index} ({segment.Name})"))}.",
            paramName: nameof(index)
        ));

    /// <summary>Returns the index of the segment a Vulkan memory type allocates from: the heap its
    /// <c>VkMemoryType.heapIndex</c> names.</summary>
    /// <param name="memoryTypeIndex">The memory type's index, as <c>VkMemoryAllocateInfo.memoryTypeIndex</c> names it.</param>
    /// <returns>The segment's index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The device has no memory type at <paramref name="memoryTypeIndex"/>,
    /// or these segments were not read from Vulkan.</exception>
    public int OfMemoryType(uint memoryTypeIndex) => ((memoryTypeIndex < ((uint)m_memoryTypeSegments.Length))
        ? m_memoryTypeSegments[memoryTypeIndex]
        : throw new ArgumentOutOfRangeException(
            actualValue: memoryTypeIndex,
            message: $"The device reports {m_memoryTypeSegments.Length} memory types.",
            paramName: nameof(memoryTypeIndex)
        ));
    /// <summary>Returns the pool an allocation of a role is expected in: host memory on unified memory and for
    /// <see cref="GpuMemoryRole.HostVisible"/>, the adapter's own memory for the device-local roles on a discrete
    /// adapter (<see cref="GpuDeviceMemoryWork.IsDeviceLocal"/>). The placement decides the pool an allocation is charged
    /// to; this is the pool a reservation made before any placement names, and the one a placement disagrees with when
    /// the device placed it elsewhere.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The expected pool.</returns>
    public PhysicalMemory ExpectedPool(GpuMemoryRole role) => ExpectedPool(role: role, unifiedMemory: UnifiedMemory);
    /// <summary>Returns the pool an allocation of a role is expected in on a device whose memory is, or is not, the
    /// host's (<see cref="GpuMemoryProfile.UnifiedMemory"/>).</summary>
    /// <param name="unifiedMemory">Whether the device's memory is the host's.</param>
    /// <param name="role">The role.</param>
    /// <returns>The expected pool.</returns>
    public static PhysicalMemory ExpectedPool(bool unifiedMemory, GpuMemoryRole role) => ((!unifiedMemory && GpuDeviceMemoryWork.IsDeviceLocal(role: role))
        ? PhysicalMemory.Device
        : PhysicalMemory.Host);
    /// <summary>Returns Direct3D 12's segments for a device: the local segment group, and on a discrete adapter the
    /// non-local one. A unified adapter's non-local group names no memory of its own (its system pool and its local
    /// group are one memory), so it is not a segment.</summary>
    /// <param name="profile">The device's memory profile.</param>
    /// <returns>The segments.</returns>
    public static GpuMemorySegments FromDirectX(GpuMemoryProfile profile) => (profile.UnifiedMemory
        ? new GpuMemorySegments(
            memoryTypeSegments: [],
            segments: [new GpuMemorySegment(Bytes: 0UL, Index: DirectXLocal, Name: "local", Pool: PhysicalMemory.Host)],
            unifiedMemory: true
        )
        : new GpuMemorySegments(
            memoryTypeSegments: [],
            segments: [
                new GpuMemorySegment(Bytes: 0UL, Index: DirectXLocal, Name: "local", Pool: PhysicalMemory.Device),
                new GpuMemorySegment(Bytes: 0UL, Index: DirectXNonLocal, Name: "non-local", Pool: PhysicalMemory.Host),
            ],
            unifiedMemory: false
        ));
    /// <summary>Returns Vulkan's segments for a device: one per heap, a device-local heap
    /// (<c>VK_MEMORY_HEAP_DEVICE_LOCAL_BIT</c>, the rule <see cref="GpuMemoryProfile.FromVulkan"/> counts by) in
    /// <see cref="PhysicalMemory.Device"/> and every other heap in <see cref="PhysicalMemory.Host"/>, or every heap in
    /// <see cref="PhysicalMemory.Host"/> on unified memory, with each memory type's heap.</summary>
    /// <param name="profile">The device's memory profile, read from the same report.</param>
    /// <param name="memoryTypes">The valid memory types as <c>{ propertyFlags, heapIndex }</c> pairs
    /// (<c>VkMemoryType</c>).</param>
    /// <param name="memoryHeaps">The valid memory heaps as <c>{ size, flags }</c> pairs (<c>VkMemoryHeap</c>).</param>
    /// <returns>The segments.</returns>
    /// <exception cref="ArgumentException"><paramref name="memoryTypes"/> or <paramref name="memoryHeaps"/> holds an odd
    /// count, the device reports more than <see cref="MaxSegments"/> heaps, or a memory type names a heap past
    /// <paramref name="memoryHeaps"/>.</exception>
    public static GpuMemorySegments FromVulkan(GpuMemoryProfile profile, ReadOnlySpan<uint> memoryTypes, ReadOnlySpan<ulong> memoryHeaps) {
        if (((memoryTypes.Length % 2) != 0) || ((memoryHeaps.Length % 2) != 0)) {
            throw new ArgumentException(message: "Vulkan memory types are {propertyFlags, heapIndex} pairs and heaps {size, flags} pairs; an odd count is not a native layout.");
        }

        var heapCount = (memoryHeaps.Length / 2);

        if (heapCount > MaxSegments) {
            throw new ArgumentException(message: $"The device reports {heapCount} memory heaps, past VK_MAX_MEMORY_HEAPS ({MaxSegments}).");
        }

        var segments = new GpuMemorySegment[heapCount];

        for (var heap = 0; (heap < heapCount); heap++) {
            segments[heap] = new GpuMemorySegment(
                Bytes: memoryHeaps[(heap * 2)],
                Index: heap,
                Name: $"heap{heap}",
                Pool: ((!profile.UnifiedMemory && GpuMemoryProfile.IsDeviceLocalHeap(heap: heap, memoryHeaps: memoryHeaps))
                    ? PhysicalMemory.Device
                    : PhysicalMemory.Host)
            );
        }

        var typeSegments = new int[(memoryTypes.Length / 2)];

        for (var type = 0; (type < typeSegments.Length); type++) {
            var heap = memoryTypes[((type * 2) + 1)];

            if (heap >= ((uint)heapCount)) {
                throw new ArgumentException(message: $"Vulkan memory type {type} names heap {heap}, past the {heapCount} reported.");
            }

            typeSegments[type] = ((int)heap);
        }

        return new GpuMemorySegments(
            memoryTypeSegments: typeSegments,
            segments: segments,
            unifiedMemory: profile.UnifiedMemory
        );
    }
}
