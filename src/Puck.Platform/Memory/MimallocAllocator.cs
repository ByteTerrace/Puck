using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Puck.Memory;

/// <summary>
/// The engine's native allocator over mimalloc. The library is loaded and initialized by a thread of its own, named
/// <c>mimalloc owner</c>, that lives until the process exits: mimalloc records the thread that loads
/// it as its main thread, keeps that thread's heap state in static storage and tears it down when that thread exits,
/// and a later thread that reuses the dead thread's TEB address then faults inside <c>mi_malloc</c>. A thread-pool
/// worker, which exits when idle, must therefore never be the one that loads it.
/// <para>
/// The owner thread also gives freed memory back. mimalloc schedules a freed range's decommit a delay after the free
/// and carries it out only when some later free or collection finds the delay passed, so the frees that end a burst
/// of work (a bake's workspace, a residency's stores) stay committed and resident for as long as nothing else frees.
/// The owner thread wakes once every <see cref="PurgePeriod"/> and runs an unforced collection, which decommits every
/// range whose delay has passed and touches nothing in use; the decommit calls run there, never on a thread that
/// renders or steps. A page for objects past a slice (64 KiB) is committed as its blocks are first used
/// (<c>page_commit_on_demand</c>) rather than whole, so a thread holding one block of a size class holds that block's
/// pages and not the 512 KiB or 4 MiB page mimalloc sets aside for the class.
/// </para>
/// </summary>
public sealed partial class MimallocAllocator : IAllocator, IAllocatorStatistics {
    private const string LibraryName = "mimalloc";
    private const string OwnerThreadName = "mimalloc owner";

    /// <summary>How often the owner thread decommits the freed ranges whose purge delay has passed. mimalloc's own
    /// delay (a second by default) is what keeps a buffer freed and taken again at once from being decommitted between;
    /// this period only bounds how long an expired range waits for some thread to carry its purge out.</summary>
    public static readonly TimeSpan PurgePeriod = TimeSpan.FromSeconds(value: 1d);

    // Runs before any import of this class loads the library. The owner thread must not call into this class: its type
    // initializer is still running, so the call would wait on it (Loader holds the import it makes instead).
    static MimallocAllocator() {
        var loaded = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(start: () => {
            try {
                Loader.mi_process_init();
                Loader.Configure();
            } catch (Exception exception) {
                _ = loaded.TrySetException(exception: exception);
                return;
            }
            _ = loaded.TrySetResult();
            while (true) {
                Thread.Sleep(timeout: PurgePeriod);
                Loader.mi_collect(force: false);
            }
        }) { IsBackground = true, Name = OwnerThreadName };

        owner.Start();
        loaded.Task.GetAwaiter().GetResult();
    }

    private static partial class Loader {
        // mi_option_t values of mimalloc 3.3 (its include/mimalloc.h). The enum is no part of the library's stable ABI,
        // so Configure sets them only on the minor version they were read from.
        private const int OptionPageCommitOnDemand = 40;
        private const int OptionsVersion = 303;

        [LibraryImport(LibraryName, EntryPoint = "mi_process_init")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void mi_process_init();
        [LibraryImport(LibraryName, EntryPoint = "mi_collect")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        internal static partial void mi_collect([MarshalAs(UnmanagedType.U1)] bool force);
        [LibraryImport(LibraryName, EntryPoint = "mi_version")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial int mi_version();
        [LibraryImport(LibraryName, EntryPoint = "mi_option_set")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void mi_option_set(int option, CLong value);

        // Sets the options the process runs under, on the owner thread before any other thread allocates. A page for
        // objects past a slice is committed as its blocks are first used: mimalloc's default commits a size class's
        // whole page for its first block, on every thread that allocates the class.
        internal static void Configure() {
            if ((mi_version() / 100) != OptionsVersion) { return; }
            mi_option_set(option: OptionPageCommitOnDemand, value: new CLong(value: 1));
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "mi_stats_get_json")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial byte* mi_stats_get_json(nuint size, byte* buffer);

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
    public void Collect() => Loader.mi_collect(force: true);
    /// <inheritdoc/>
    /// <remarks>mimalloc answers for an address inside one of its pages; an arena's own header at its base reads
    /// false.</remarks>
    public unsafe bool Owns(nint address) => mi_is_in_heap_region(p: ((void*)address));

    [LibraryImport(LibraryName, EntryPoint = "mi_is_in_heap_region")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool mi_is_in_heap_region(void* p);

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
