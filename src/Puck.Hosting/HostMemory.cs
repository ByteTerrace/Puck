using System.Globalization;
using System.Runtime.InteropServices;

namespace Puck.Hosting;

/// <summary>
/// Reads how much physical memory the machine has free now and how much it has installed: on Windows the memory
/// status, on Linux <c>/proc/meminfo</c>. A cheap operating-system query, so a scheduler may ask before each piece of
/// work it starts.
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
}
