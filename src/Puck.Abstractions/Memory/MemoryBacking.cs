namespace Puck.Abstractions.Memory;

/// <summary>
/// What a charged byte's storage is made of: the physical row of the process's memory it is part of. The rows are
/// disjoint, so a byte lies in exactly one, and the storage that makes a charge names its row with it
/// (<see cref="IAllocationObserver.Change"/>), since only that storage knows: an <see cref="OwnedBuffer{T}"/> is native
/// when it was handed an allocator and managed when it was not.
/// <para>
/// A charge only names part of its row; the row itself is read elsewhere (the allocator's committed bytes, the
/// collector's, the device memory ledger's blocks), and what the row holds beyond its charges is its residual: slack for
/// the allocator, unnamed tenants for the managed heap, unused room for the device blocks. What the process holds beyond
/// every row (the runtime's code and loader heaps, stacks, the driver's private memory) no owner can charge, so it has
/// no member here: it is the residual of a page walk of the process.
/// </para>
/// </summary>
public enum MemoryBacking {
    /// <summary>Storage from the engine's native allocator (<see cref="IAllocator"/>), whose committed bytes are the row
    /// (<see cref="IAllocatorStatistics"/>). Host memory.</summary>
    NativeAllocator,
    /// <summary>Native storage allocated outside the engine's allocator (<c>NativeMemory</c>), such as a GPU region's
    /// host copy. Nothing else measures it, so its charges are the row. Host memory.</summary>
    NativeDirect,
    /// <summary>Managed arrays and objects, part of what the collector has committed. Host memory.</summary>
    Managed,
    /// <summary>Device memory blocks a backend holds from the device, counted by its ledger
    /// (<c>GpuDeviceMemoryWork</c>): the pool of the segment they were placed in.</summary>
    DeviceBlocks,
}
