using System.Collections.Concurrent;

namespace Puck.Memory;

public sealed class TrackingAllocator : IAllocator {
    private readonly ConcurrentDictionary<nuint, AllocationInfo> m_allocations = new();
    private readonly IAllocator m_inner;

    public IReadOnlyDictionary<nuint, AllocationInfo> ActiveAllocations => m_allocations;

    public TrackingAllocator(IAllocator inner) {
        ArgumentNullException.ThrowIfNull(inner);

        m_inner = inner;
    }

    public unsafe void* Allocate(nuint size, nuint alignment = 0) {
        var ptr = m_inner.Allocate(
            alignment: alignment,
            size: size
        );

        if (ptr is not null) {
            m_allocations[(nuint)ptr] = new AllocationInfo(
                Size: size,
                StackTrace: Environment.StackTrace
            );
        }
        return ptr;
    }
    public unsafe void Free(void* ptr) {
        if (ptr is not null) {
            m_allocations.TryRemove(
                key: (nuint)ptr,
                value: out _
            );
            m_inner.Free(ptr: ptr);
        }
    }
    public unsafe void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) {
        var newPtr = m_inner.Reallocate(
            alignment: alignment,
            newSize: newSize,
            ptr: ptr
        );

        if (ptr is not null) {
            m_allocations.TryRemove(
                key: (nuint)ptr,
                value: out _
            );
        }
        if (newPtr is not null) {
            m_allocations[(nuint)newPtr] = new AllocationInfo(
                Size: newSize,
                StackTrace: Environment.StackTrace
            );
        }
        return newPtr;
    }

    public record struct AllocationInfo(nuint Size, string StackTrace);
}
