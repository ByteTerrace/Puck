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
    private static long? MemInfoBytes(string field) {
        try {
            var line = File.ReadLines(path: "/proc/meminfo").FirstOrDefault(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: field));

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
    private const uint MemImage = 0x1000000;
    private const uint MemMapped = 0x40000;
    private const uint MemPrivate = 0x20000;

    [LibraryImport(libraryName: "kernel32.dll")]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    /// <summary>Walks this process's address space and sums its memory by kind: on Windows every committed region's bytes
    /// by its type (private, a mapped section, an image), read with <c>VirtualQuery</c> over the whole address space; on
    /// Linux <c>/proc/self/smaps_rollup</c>'s resident anonymous memory and swap as the private bytes and its other resident
    /// bytes as the mapped ones, since Linux commits anonymous memory as it is touched. The walk reads the address space
    /// as each region is reached while the process runs on, so regions that change during it are read at different
    /// instants. It costs a system call per region (thousands), so it is taken on request, never per frame.</summary>
    /// <returns>The walk, or <see langword="null"/> when this platform cannot be walked.</returns>
    public static ProcessMemoryWalk? WalkProcess() {
        if (OperatingSystem.IsWindows()) {
            long privateBytes = 0, mappedBytes = 0, imageBytes = 0;
            var regions = 0;
            var address = ((nint)0);
            var size = ((nuint)Marshal.SizeOf<MemoryBasicInformation>());

            while (VirtualQuery(address: address, buffer: out var region, length: size) != 0) {
                regions++;
                if (region.State == MemCommit) {
                    var bytes = ((long)region.RegionSize);

                    switch (region.Type) {
                        case MemPrivate: privateBytes += bytes; break;
                        case MemMapped: mappedBytes += bytes; break;
                        case MemImage: imageBytes += bytes; break;
                    }
                }

                var next = (region.BaseAddress + ((nint)region.RegionSize));

                if (next <= address) { break; }
                address = next;
            }

            return new(ImageBytes: imageBytes, MappedBytes: mappedBytes, PrivateBytes: privateBytes, PrivateCommitted: true, Regions: regions);
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

                return new(ImageBytes: 0, MappedBytes: Math.Max(val1: 0, val2: (rss - anonymous)), PrivateBytes: (anonymous + swap), PrivateCommitted: false, Regions: 0);
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
public sealed record ProcessMemoryWalk(long PrivateBytes, long MappedBytes, long ImageBytes, bool PrivateCommitted, int Regions);
