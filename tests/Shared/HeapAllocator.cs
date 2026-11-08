using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;

namespace Puck.Testing;

/// <summary>A native heap for test fixtures that do not require aligned allocations.</summary>
public sealed unsafe class HeapAllocator : IAllocator {
    public void* Allocate(nuint size, nuint alignment = 0) => NativeMemory.Alloc(byteCount: Math.Max(val1: size, val2: 1));
    public void Free(void* ptr) => NativeMemory.Free(ptr: ptr);
    public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => NativeMemory.Realloc(
        byteCount: Math.Max(val1: newSize, val2: 1),
        ptr: ptr
    );
}
