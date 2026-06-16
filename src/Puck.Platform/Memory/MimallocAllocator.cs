using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Memory;

public sealed partial class MimallocAllocator : IAllocator {
    private const string LibraryName = "mimalloc";

    [LibraryImport(LibraryName, EntryPoint = "mi_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial void mi_free(void* p);
    [LibraryImport(LibraryName, EntryPoint = "mi_malloc")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial void* mi_malloc(nuint size);
    [LibraryImport(LibraryName, EntryPoint = "mi_malloc_aligned")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial void* mi_malloc_aligned(nuint size, nuint alignment);
    [LibraryImport(LibraryName, EntryPoint = "mi_realloc")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial void* mi_realloc(void* p, nuint newsize);
    [LibraryImport(LibraryName, EntryPoint = "mi_realloc_aligned")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial void* mi_realloc_aligned(void* p, nuint newsize, nuint alignment);

    public unsafe void* Allocate(nuint size, nuint alignment = 0) {
        if (alignment == 0) {
            return mi_malloc(size: size);
        }
        return mi_malloc_aligned(
            alignment: alignment,
            size: size
        );
    }
    public unsafe void Free(void* ptr) {
        if (ptr is not null) {
            mi_free(p: ptr);
        }
    }
    public unsafe void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) {
        if (alignment == 0) {
            return mi_realloc(
                newsize: newSize,
                p: ptr
            );
        }
        return mi_realloc_aligned(
            alignment: alignment,
            newsize: newSize,
            p: ptr
        );
    }
}
