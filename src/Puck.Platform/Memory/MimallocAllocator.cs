using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Puck.Memory;

/// <summary>
/// The engine's native allocator over mimalloc. The library is loaded and initialized by a thread of its own, named
/// <c>mimalloc owner</c>, that waits without running until the process exits: mimalloc records the thread that loads
/// it as its main thread, keeps that thread's heap state in static storage and tears it down when that thread exits,
/// and a later thread that reuses the dead thread's TEB address then faults inside <c>mi_malloc</c>. A thread-pool
/// worker, which exits when idle, must therefore never be the one that loads it.
/// </summary>
public sealed partial class MimallocAllocator : IAllocator, IAllocatorStatistics {
    private const string LibraryName = "mimalloc";
    private const string OwnerThreadName = "mimalloc owner";

    // Runs before any import of this class loads the library. The owner thread must not call into this class: its type
    // initializer is still running, so the call would wait on it (Loader holds the import it makes instead).
    static MimallocAllocator() {
        var loaded = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(start: () => {
            try {
                Loader.mi_process_init();
            } catch (Exception exception) {
                _ = loaded.TrySetException(exception: exception);
                return;
            }
            _ = loaded.TrySetResult();
            Thread.Sleep(millisecondsTimeout: Timeout.Infinite);
        }) { IsBackground = true, Name = OwnerThreadName };

        owner.Start();
        loaded.Task.GetAwaiter().GetResult();
    }

    private static partial class Loader {
        [LibraryImport(LibraryName, EntryPoint = "mi_process_init")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void mi_process_init();
    }

    [LibraryImport(LibraryName, EntryPoint = "mi_stats_get_json")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial byte* mi_stats_get_json(nuint size, byte* buffer);
    [LibraryImport(LibraryName, EntryPoint = "mi_collect")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void mi_collect([MarshalAs(UnmanagedType.U1)] bool force);

    /// <inheritdoc/>
    public unsafe (long Committed, long Reserved) ReadStatistics() {
        // v3's process-wide JSON avoids pinning the versioned mi_stats_t ABI. The returned block belongs to mimalloc.
        var json = mi_stats_get_json(buffer: null, size: 0);

        if (json is null) { return default; }
        try {
            using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(ptr: ((nint)json))!);

            return (document.RootElement.GetProperty(propertyName: "committed").GetProperty(propertyName: "current").GetInt64(), document.RootElement.GetProperty(propertyName: "reserved").GetProperty(propertyName: "current").GetInt64());
        } finally { mi_free(p: json); }
    }
    /// <inheritdoc/>
    public void Collect() => mi_collect(force: true);

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
