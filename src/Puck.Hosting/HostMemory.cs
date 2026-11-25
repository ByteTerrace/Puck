using System.Globalization;
using System.Runtime.InteropServices;

namespace Puck.Hosting;

/// <summary>
/// Reads how much physical memory the machine has free now and how much it has installed, and how much of its commit
/// limit is charged: on Windows the memory status, on Linux <c>/proc/meminfo</c>. A cheap operating-system query, so a
/// scheduler may ask before each piece of work it starts.
/// </summary>
public static partial class HostMemory {
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct MemoryStatusEx {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport(libraryName: "kernel32.dll")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    // A /proc/meminfo field in bytes, or null when the file or the field cannot be read.
    private static long? MemInfoBytes(string field, string path = "/proc/meminfo") {
        try {
            var line = File.ReadLines(path: path).FirstOrDefault(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: field));

            return (((line is not null) && long.TryParse(provider: CultureInfo.InvariantCulture, result: out var kilobytes, s: line.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ')[1], style: NumberStyles.None))
                ? (kilobytes * 1024)
                : null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }
    // The memory status, or null when it cannot be read.
    private static MemoryStatusEx? Status() {
        var status = new MemoryStatusEx { Length = ((uint)Marshal.SizeOf<MemoryStatusEx>()) };

        return (GlobalMemoryStatusEx(status: ref status) ? status : null);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport(libraryName: "kernel32.dll")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport(libraryName: "kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(unmanagedType: UnmanagedType.Bool)] bool inherit, uint processId);
    [LibraryImport(libraryName: "kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx2 counters, uint size);

    // PROCESS_MEMORY_COUNTERS_EX2 (psapi.h): the documented counters through PrivateUsage, then the private working set and
    // the shared commit. A system that predates the structure refuses its size, which reads as no answer.
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2 {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    /// <summary>Reads how much of a process's working set is its own private memory: the pages no other process shares
    /// and the system cannot drop without writing them out. On Windows it is the private working set
    /// (<c>PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize</c>), which counts every resident page of a private region, a
    /// graphics driver's write-combined host views included, and leaves out commit no page backs yet (a driver's
    /// device-memory mirror, a collector's or allocator's committed reserve) and mapped files and images, whose clean pages
    /// the system shares between processes and reclaims under pressure (NVIDIA's Direct3D 12 shader disk cache is about a
    /// gibibyte of a windowed World's working set). On Linux it is <c>/proc/&lt;pid&gt;/status</c>'s <c>RssAnon</c> plus
    /// <c>RssShmem</c>: shared memory is swap-backed, not a clean file page. The host memory account's bar
    /// (<see cref="Jobs.MemoryAccount.SetHostBar"/>) and the tools' admission of a start both read it.</summary>
    /// <param name="processId">The process to inspect.</param>
    /// <returns>The bytes, or <see langword="null"/> when they cannot be read (the process exited, belongs to another user,
    /// the system predates the counter, or the host is neither Windows nor Linux).</returns>
    public static long? PrivateResidentBytes(int processId) {
        if (OperatingSystem.IsLinux()) {
            var status = $"/proc/{processId.ToString(provider: CultureInfo.InvariantCulture)}/status";

            return ((MemInfoBytes(field: "RssAnon:", path: status) is { } anonymous) ? (anonymous + (MemInfoBytes(field: "RssShmem:", path: status) ?? 0L)) : null);
        }
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var process = OpenProcess(access: ProcessQueryLimitedInformation, inherit: false, processId: ((uint)processId));

        if (process == 0) {
            return null;
        }

        try {
            var counters = new ProcessMemoryCountersEx2 { Size = ((uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()) };

            return (GetProcessMemoryInfo(counters: ref counters, process: process, size: counters.Size)
                ? ((long)Math.Min(val1: ((ulong)counters.PrivateWorkingSetSize), val2: ((ulong)long.MaxValue)))
                : null);
        } finally {
            _ = CloseHandle(handle: process);
        }
    }
    /// <summary>Returns the physical memory free now, in bytes.</summary>
    /// <returns>The bytes, or <see langword="null"/> when this platform or this moment cannot say.</returns>
    public static long? AvailablePhysicalBytes() {
        if (OperatingSystem.IsWindows()) {
            return ((Status() is { } status) ? ((long)Math.Min(val1: status.AvailablePhysical, val2: long.MaxValue)) : null);
        }
        if (OperatingSystem.IsLinux()) {
            return MemInfoBytes(field: "MemAvailable:");
        }

        return null;
    }
    /// <summary>Returns how much more memory the process could take now, in bytes: the free physical memory
    /// (<see cref="AvailablePhysicalBytes"/>), or what is left of the commit limit (<see cref="CommitBytes"/>) where the
    /// platform enforces one and that is smaller, since an allocation fails at the limit whatever physical memory is
    /// free.</summary>
    /// <returns>The bytes, or <see langword="null"/> when this platform or this moment cannot say how much is free.</returns>
    public static long? AdmissibleBytes() {
        if (AvailablePhysicalBytes() is not { } available) { return null; }

        return ((CommitBytes() is { } commit) ? Math.Min(val1: available, val2: Math.Max(val1: 0L, val2: (commit.Limit - commit.Charge))) : available);
    }
    [LibraryImport(libraryName: "kernel32.dll")]
    private static partial nint CreateMemoryResourceNotification(int notificationType);
    [LibraryImport(libraryName: "kernel32.dll")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool QueryMemoryResourceNotification(nint handle, [MarshalAs(unmanagedType: UnmanagedType.Bool)] out bool state);

    // The system's low-memory notification (LowMemoryResourceNotification, 0), opened once and kept for the process's
    // life; zero off Windows or where the system refused it.
    private static readonly Lazy<nint> LowMemoryNotification = new(valueFactory: static () => (OperatingSystem.IsWindows() ? CreateMemoryResourceNotification(notificationType: 0) : 0));

    /// <summary>Returns whether the operating system says its available physical memory is running low: on Windows the
    /// system-wide low-memory resource notification, which every process sees at once. Work that can wait gives its room
    /// back while it stands. Linux publishes no such signal, and reads as not low.</summary>
    /// <returns>Whether the signal is set.</returns>
    public static bool LowMemorySignaled() => ((LowMemoryNotification.Value != 0) && QueryMemoryResourceNotification(handle: LowMemoryNotification.Value, state: out var low) && low);
    /// <summary>Returns the physical memory the machine has installed and the operating system can use, in bytes: on
    /// Windows the memory status's total physical bytes, on Linux <c>/proc/meminfo</c>'s <c>MemTotal</c>. Firmware and
    /// device reservations make it slightly less than the nominal size.</summary>
    /// <returns>The bytes, or <see langword="null"/> when this platform cannot say.</returns>
    public static long? InstalledPhysicalBytes() {
        if (OperatingSystem.IsWindows()) {
            return ((Status() is { } status) ? ((long)Math.Min(val1: status.TotalPhysical, val2: long.MaxValue)) : null);
        }
        if (OperatingSystem.IsLinux()) {
            return MemInfoBytes(field: "MemTotal:");
        }

        return null;
    }
    /// <summary>Returns the system's commit charge and commit limit, in bytes. Allocations fail at the limit whatever
    /// physical memory is free, so this is the reading that says whether another process can start. On Windows it is the
    /// memory status's page-file total (the commit limit) and what is left of it; on Linux <c>/proc/meminfo</c>'s
    /// <c>Committed_AS</c> and <c>CommitLimit</c>, and only under strict overcommit (<c>vm.overcommit_memory</c> 2),
    /// since the kernel enforces no limit otherwise and a charge over it is ordinary there.</summary>
    /// <returns>The charge and the limit, or <see langword="null"/> when this platform or this moment cannot say, or the
    /// platform enforces no commit limit.</returns>
    public static (long Charge, long Limit)? CommitBytes() {
        if (OperatingSystem.IsWindows()) {
            return (((Status() is { } status) && (status.TotalPageFile > 0) && (status.AvailablePageFile <= status.TotalPageFile))
                ? (((long)Math.Min(val1: (status.TotalPageFile - status.AvailablePageFile), val2: long.MaxValue)), ((long)Math.Min(val1: status.TotalPageFile, val2: long.MaxValue)))
                : null);
        }
        if (OperatingSystem.IsLinux()) {
            try {
                if (File.ReadAllText(path: "/proc/sys/vm/overcommit_memory").Trim() != "2") { return null; }
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                return null;
            }

            return (((MemInfoBytes(field: "Committed_AS:") is { } charge) && (MemInfoBytes(field: "CommitLimit:") is { } limit) && (limit > 0)) ? (charge, limit) : null);
        }

        return null;
    }

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct MemoryBasicInformation {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MemCommit = 0x1000;
    private const uint MemFree = 0x10000;
    // The most private allocations a walk names, by resident bytes.
    private const int LargestPrivateCount = 8;

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct HeapSummaryInformation {
        public uint Size;
        public nuint Allocated;
        public nuint Committed;
        public nuint Reserved;
        public nuint MaximumReserve;
    }

    [LibraryImport(libraryName: "kernel32.dll")]
    private static unsafe partial uint GetProcessHeaps(uint count, nint* heaps);
    [LibraryImport(libraryName: "kernel32.dll")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool HeapSummary(nint heap, uint flags, ref HeapSummaryInformation summary);

    // The process's NT heaps (the CRT's, the loader's, every native library's that allocates through HeapAlloc) summed:
    // how many, and the bytes they hold allocated and committed.
    private static unsafe NativeHeapReading ReadNativeHeaps() {
        var count = GetProcessHeaps(count: 0, heaps: null);
        var heaps = new nint[(count + 16)];
        long allocated = 0, committed = 0;

        fixed (nint* first = heaps) { count = Math.Min(val1: GetProcessHeaps(count: ((uint)heaps.Length), heaps: first), val2: ((uint)heaps.Length)); }
        for (var index = 0; (index < count); index++) {
            var summary = new HeapSummaryInformation { Size = ((uint)sizeof(HeapSummaryInformation)) };

            if (!HeapSummary(flags: 0, heap: heaps[index], summary: ref summary)) { continue; }
            allocated += ((long)summary.Allocated);
            committed += ((long)summary.Committed);
        }

        return new NativeHeapReading(Allocated: allocated, Committed: committed, Count: ((int)count));
    }
    private const uint MemImage = 0x1000000;
    private const uint MemMapped = 0x40000;
    private const uint MemPrivate = 0x20000;
    // The protection modifiers a graphics driver maps its allocations with for the host to write: uncached and
    // write-combined. Ordinary heaps, stacks and the collector's regions carry neither.
    private const uint PageNoCache = 0x200;
    private const uint PageWriteCombine = 0x400;

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct WorkingSetExInformation {
        public nint VirtualAddress;
        public nuint VirtualAttributes;
    }

    [LibraryImport(libraryName: "kernel32.dll")]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);
    [LibraryImport(libraryName: "kernel32.dll", EntryPoint = "K32QueryWorkingSetEx")]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static unsafe partial bool QueryWorkingSetEx(nint process, WorkingSetExInformation* buffer, uint length);

    // The pages QueryWorkingSetEx answers in one call: a quarter of a gibibyte of address space, in a 1 MiB buffer.
    private const int ResidencyBatch = 65536;

    // How many bytes of a committed region are in the process's working set, asked of QueryWorkingSetEx a batch of pages
    // at a time; null once the system refuses the query, so a reading never passes a guess for a count.
    private static unsafe long? ResidentBytes(nint start, long length, WorkingSetExInformation* batch, long pageSize) {
        var pages = (length / pageSize);
        var resident = 0L;

        for (var first = 0L; (first < pages); first += ResidencyBatch) {
            var count = ((int)Math.Min(val1: ResidencyBatch, val2: (pages - first)));

            for (var index = 0; (index < count); index++) {
                batch[index] = new WorkingSetExInformation { VirtualAddress = (start + ((nint)((first + index) * pageSize))) };
            }
            if (!QueryWorkingSetEx(buffer: batch, length: ((uint)(count * sizeof(WorkingSetExInformation))), process: -1)) { return null; }
            for (var index = 0; (index < count); index++) {
                // Bit 0 is Valid: the page is in the working set.
                if ((batch[index].VirtualAttributes & 1) != 0) { resident += pageSize; }
            }
        }

        return resident;
    }

    /// <summary>Walks this process's address space and sums its memory by kind: on Windows every committed region's bytes
    /// by its type (private, a mapped section, an image), with the private regions a driver mapped write-combined or
    /// uncached apart, and how much of each kind is in the working set, read with <c>VirtualQuery</c> and
    /// <c>QueryWorkingSetEx</c> over the whole address space; on Linux <c>/proc/self/smaps_rollup</c>'s resident anonymous
    /// memory and swap as the private bytes and its other resident bytes as the mapped ones, since Linux commits anonymous
    /// memory as it is touched. The walk reads the address space as each region is reached while the process runs on, so
    /// regions that change during it are read at different instants. It costs a system call per region (thousands) and one
    /// per quarter gibibyte committed, so it is taken on request, never per frame.</summary>
    /// <param name="allocatorOwns">Whether the engine's native allocator manages an address (its arenas), which names
    /// the private allocations it holds by their pages; <see langword="null"/> names none.</param>
    /// <returns>The walk, or <see langword="null"/> when this platform cannot be walked.</returns>
    public static unsafe ProcessMemoryWalk? WalkProcess(Func<nint, bool>? allocatorOwns = null) {
        if (OperatingSystem.IsWindows()) {
            long privateBytes = 0, driverBytes = 0, mappedBytes = 0, imageBytes = 0;
            long? privateResident = 0, driverResident = 0, mappedResident = 0, imageResident = 0;
            var regions = 0;
            var address = ((nint)0);
            var size = ((nuint)Marshal.SizeOf<MemoryBasicInformation>());
            var pageSize = ((long)Environment.SystemPageSize);
            var batch = ((WorkingSetExInformation*)NativeMemory.Alloc(byteCount: ((nuint)(ResidencyBatch * sizeof(WorkingSetExInformation)))));
            // The ordinary private memory (not a driver's view) of each allocation, which VirtualQuery reads as consecutive
            // regions sharing an allocation base, and the address range each reserves.
            var allocations = new List<PrivateAllocation>();
            var current = default(PrivateAllocation);

            try {
                while (VirtualQuery(address: address, buffer: out var region, length: size) != 0) {
                    regions++;
                    if ((region.Type == MemPrivate) && (region.State != MemFree)) {
                        if (current.Base != region.AllocationBase) {
                            if (current.Committed > 0) { allocations.Add(item: current); }
                            current = new PrivateAllocation(Base: region.AllocationBase, Committed: 0, Reserved: 0, Resident: 0) { Allocator = (allocatorOwns?.Invoke(arg: region.AllocationBase) ?? false) };
                        }
                        current = (current with { Reserved = (current.Reserved + ((long)region.RegionSize)) });
                    }
                    if (region.State == MemCommit) {
                        var bytes = ((long)region.RegionSize);
                        var resident = ResidentBytes(batch: batch, length: bytes, pageSize: pageSize, start: region.BaseAddress);

                        switch (region.Type) {
                            case MemPrivate when ((region.Protect & (PageNoCache | PageWriteCombine)) != 0):
                                driverBytes += bytes;
                                driverResident += resident;
                                break;
                            case MemPrivate:
                                privateBytes += bytes;
                                privateResident += resident;
                                current = (current with { Committed = (current.Committed + bytes), Resident = (current.Resident + (resident ?? 0L)) });
                                break;
                            case MemMapped: mappedBytes += bytes; mappedResident += resident; break;
                            case MemImage: imageBytes += bytes; imageResident += resident; break;
                        }
                    }

                    var next = (region.BaseAddress + ((nint)region.RegionSize));

                    if (next <= address) { break; }
                    address = next;
                }
                if (current.Committed > 0) { allocations.Add(item: current); }
            } finally {
                NativeMemory.Free(ptr: batch);
            }

            return new(ImageBytes: imageBytes, MappedBytes: mappedBytes, PrivateBytes: (privateBytes + driverBytes), PrivateCommitted: true, Regions: regions) {
                DriverMappedBytes = driverBytes,
                DriverMappedResident = driverResident,
                ImageResident = imageResident,
                MappedResident = mappedResident,
                PrivateResident = (privateResident + driverResident),
                PrivateBySize = PrivateSizeClass.Of(allocations: allocations),
                LargestPrivate = [.. allocations.OrderByDescending(keySelector: static allocation => allocation.Resident).Take(count: LargestPrivateCount)],
                NativeHeaps = ReadNativeHeaps(),
                AllocatorCommitted = ((allocatorOwns is null) ? null : allocations.Where(predicate: static allocation => allocation.Allocator).Sum(selector: static allocation => allocation.Committed)),
                AllocatorResident = ((allocatorOwns is null) ? null : allocations.Where(predicate: static allocation => allocation.Allocator).Sum(selector: static allocation => allocation.Resident)),
            };
        }
        if (OperatingSystem.IsLinux()) {
            try {
                long rss = 0, anonymous = 0, swap = 0;

                foreach (var line in File.ReadLines(path: "/proc/self/smaps_rollup")) {
                    var parts = line.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ');

                    if ((parts.Length < 2) || !long.TryParse(provider: CultureInfo.InvariantCulture, result: out var kilobytes, s: parts[1], style: NumberStyles.None)) { continue; }
                    switch (parts[0]) {
                        case "Rss:": rss = (kilobytes * 1024); break;
                        case "Anonymous:": anonymous = (kilobytes * 1024); break;
                        case "Swap:": swap = (kilobytes * 1024); break;
                    }
                }

                return new(ImageBytes: 0, MappedBytes: Math.Max(val1: 0, val2: (rss - anonymous)), PrivateBytes: (anonymous + swap), PrivateCommitted: false, Regions: 0) {
                    MappedResident = Math.Max(val1: 0, val2: (rss - anonymous)),
                    PrivateResident = anonymous,
                };
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                return null;
            }
        }

        return null;
    }
}
/// <summary>One walk of a process's address space (<see cref="HostMemory.WalkProcess"/>), in bytes.</summary>
/// <param name="PrivateBytes">The process's private memory: on Windows its committed private regions (the managed heap,
/// every native heap, stacks, the runtime's code and loader heaps, and the driver's private allocations); on Linux its
/// resident anonymous memory and swap.</param>
/// <param name="MappedBytes">Committed mapped sections on Windows; resident file-backed and shared memory on
/// Linux.</param>
/// <param name="ImageBytes">Committed image regions (loaded modules) on Windows; zero on Linux, whose images are part of
/// <paramref name="MappedBytes"/>.</param>
/// <param name="PrivateCommitted">Whether <paramref name="PrivateBytes"/> is committed memory (Windows) rather than
/// resident memory (Linux).</param>
/// <param name="Regions">The regions the walk read; zero on Linux, whose kernel sums them.</param>
public sealed record ProcessMemoryWalk(long PrivateBytes, long MappedBytes, long ImageBytes, bool PrivateCommitted, int Regions) {
    /// <summary>Gets the part of <see cref="PrivateBytes"/> mapped write-combined or uncached: the host's views of
    /// graphics allocations (upload and readback heaps, and on a driver that maps device memory into the process, its
    /// device-local blocks), which the backend's ledgers already count as device blocks. Zero where the platform cannot
    /// tell (Linux).</summary>
    public long DriverMappedBytes { get; init; }
    /// <summary>Gets how much of <see cref="PrivateBytes"/> is in the working set, or <see langword="null"/> when the
    /// system refused the query.</summary>
    public long? PrivateResident { get; init; }
    /// <summary>Gets how much of <see cref="DriverMappedBytes"/> is in the working set, or <see langword="null"/> when
    /// the system refused the query. Device memory a driver maps is never in it.</summary>
    public long? DriverMappedResident { get; init; }
    /// <summary>Gets how much of <see cref="MappedBytes"/> is in the working set, or <see langword="null"/> when the
    /// system refused the query.</summary>
    public long? MappedResident { get; init; }
    /// <summary>Gets how much of <see cref="ImageBytes"/> is in the working set, or <see langword="null"/> when the
    /// system refused the query.</summary>
    public long? ImageResident { get; init; }
    /// <summary>Gets the ordinary private memory (not a driver's view) summed by the size of the address range each
    /// allocation reserves, smallest class first: the shape of what holds it (the collector's and the allocator's arenas
    /// reserve gibibytes, heap segments and thread stacks a few mebibytes, a driver's own blocks their own sizes). Empty
    /// where the platform cannot tell (Linux).</summary>
    public IReadOnlyList<PrivateSizeClass> PrivateBySize { get; init; } = [];
    /// <summary>Gets the private allocations (not a driver's view) holding the most resident bytes, most first. Empty
    /// where the platform cannot tell (Linux).</summary>
    public IReadOnlyList<PrivateAllocation> LargestPrivate { get; init; } = [];
    /// <summary>Gets the process's NT heaps summed, or <see langword="null"/> where the platform has none.</summary>
    public NativeHeapReading? NativeHeaps { get; init; }
    /// <summary>Gets the committed private bytes of the allocations the engine's native allocator manages, read from its
    /// arenas' pages, or <see langword="null"/> when the walk was given no allocator or the platform cannot tell.</summary>
    public long? AllocatorCommitted { get; init; }
    /// <summary>Gets how much of <see cref="AllocatorCommitted"/> is in the working set, or <see langword="null"/>.</summary>
    public long? AllocatorResident { get; init; }
}
/// <summary>One private allocation of a process walk, in bytes: the regions sharing one allocation base.</summary>
/// <param name="Base">The allocation's base address.</param>
/// <param name="Reserved">The address range it reserves, committed or not.</param>
/// <param name="Committed">Its committed ordinary private bytes.</param>
/// <param name="Resident">How much of <paramref name="Committed"/> is in the working set.</param>
public readonly record struct PrivateAllocation(nint Base, long Reserved, long Committed, long Resident) {
    /// <summary>Gets whether the engine's native allocator manages the allocation (one of its arenas).</summary>
    public bool Allocator { get; init; }
}
/// <summary>The private allocations of one reservation-size class, in bytes.</summary>
/// <param name="ReservedAtMost">The largest reservation the class holds: a power of four, or <see cref="long.MaxValue"/>
/// for the last.</param>
/// <param name="Allocations">The allocations in the class.</param>
/// <param name="Committed">Their committed bytes.</param>
/// <param name="Resident">How much of <paramref name="Committed"/> is in the working set.</param>
public readonly record struct PrivateSizeClass(long ReservedAtMost, int Allocations, long Committed, long Resident) {
    // The classes' upper bounds: 64 KiB to 1 GiB by fours, then everything larger.
    private static readonly long[] Bounds = [(64L << 10), (256L << 10), (1L << 20), (4L << 20), (16L << 20), (64L << 20), (256L << 20), (1L << 30), long.MaxValue];

    /// <summary>Sums allocations into the classes, dropping the empty ones.</summary>
    /// <param name="allocations">The allocations.</param>
    /// <returns>The classes holding any allocation, smallest first.</returns>
    public static IReadOnlyList<PrivateSizeClass> Of(IReadOnlyList<PrivateAllocation> allocations) {
        var classes = new PrivateSizeClass[Bounds.Length];

        for (var index = 0; (index < Bounds.Length); index++) { classes[index] = new PrivateSizeClass(Allocations: 0, Committed: 0, ReservedAtMost: Bounds[index], Resident: 0); }
        foreach (var allocation in allocations) {
            var index = Array.FindIndex(array: Bounds, match: bound => (allocation.Reserved <= bound));
            var held = classes[index];

            classes[index] = (held with { Allocations = (held.Allocations + 1), Committed = (held.Committed + allocation.Committed), Resident = (held.Resident + allocation.Resident) });
        }

        return [.. classes.Where(predicate: static each => (each.Allocations > 0))];
    }
}
/// <summary>A process's NT heaps summed, in bytes.</summary>
/// <param name="Count">The heaps.</param>
/// <param name="Allocated">The bytes their live blocks hold.</param>
/// <param name="Committed">The bytes they hold committed.</param>
public readonly record struct NativeHeapReading(int Count, long Allocated, long Committed);
