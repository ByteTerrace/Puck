using System.Globalization;
using System.Runtime.InteropServices;

namespace Puck.Hosting;

/// <summary>
/// Reads how much physical memory the machine has free now: on Windows the memory status's available physical bytes,
/// on Linux <c>/proc/meminfo</c>'s <c>MemAvailable</c>. A cheap operating-system query, so a scheduler may ask before
/// each piece of work it starts.
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

    /// <summary>Returns the physical memory free now, in bytes.</summary>
    /// <returns>The bytes, or <see langword="null"/> when this platform or this moment cannot say.</returns>
    public static long? AvailablePhysicalBytes() {
        if (OperatingSystem.IsWindows()) {
            var status = new MemoryStatusEx { Length = ((uint)Marshal.SizeOf<MemoryStatusEx>()) };

            return (GlobalMemoryStatusEx(status: ref status) ? ((long)Math.Min(val1: status.AvailablePhysical, val2: long.MaxValue)) : null);
        }
        if (OperatingSystem.IsLinux()) {
            try {
                var available = File.ReadLines(path: "/proc/meminfo").FirstOrDefault(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "MemAvailable:"));

                return (((available is not null) && long.TryParse(provider: CultureInfo.InvariantCulture, result: out var kilobytes, s: available.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ')[1], style: NumberStyles.None))
                    ? (kilobytes * 1024)
                    : null);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                return null;
            }
        }

        return null;
    }
}
