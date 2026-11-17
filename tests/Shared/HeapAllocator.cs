using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;

namespace Puck.Testing;

/// <summary>A native heap for test fixtures: every allocation is aligned to at least 16 bytes, and a request for a
/// stricter alignment gets it.</summary>
public sealed unsafe class HeapAllocator : IAllocator {
    private const nuint MinimumAlignment = 16;

    public void* Allocate(nuint size, nuint alignment = 0) => NativeMemory.AlignedAlloc(
        alignment: Math.Max(val1: alignment, val2: MinimumAlignment),
        byteCount: Math.Max(val1: size, val2: 1)
    );
    public void Free(void* ptr) => NativeMemory.AlignedFree(ptr: ptr);
    public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => NativeMemory.AlignedRealloc(
        alignment: Math.Max(val1: alignment, val2: MinimumAlignment),
        byteCount: Math.Max(val1: newSize, val2: 1),
        ptr: ptr
    );
}
