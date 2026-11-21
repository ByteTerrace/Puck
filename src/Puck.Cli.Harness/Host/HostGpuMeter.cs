using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Puck.Cli.Host;

/// <summary>What the machine's GPU holds in dedicated (video) memory at one reading.</summary>
/// <param name="TotalGb">The dedicated video memory the adapter has, in gigabytes.</param>
/// <param name="UsedGb">The dedicated video memory every process holds on the busiest adapter, in gigabytes; more than
/// <paramref name="TotalGb"/> when the driver has oversubscribed the device and pages video memory to system memory.</param>
public readonly record struct HostDeviceMemory(double TotalGb, double UsedGb);

/// <summary>
/// Reads how much dedicated video memory the machine's GPU has and how much every process holds, for the device headroom
/// admission judges (<see cref="HostLoadThresholds.DeviceHasHeadroom"/>). On Windows the holdings are the counters the Task
/// Manager's GPU memory column reads (<c>GPU Process Memory</c>, <c>Dedicated Usage</c> of every process) through the
/// Performance Data Helper, and the capacity is the adapter's memory size the display driver records in the registry; neither
/// needs a device, a driver call or a lease. Several adapters are read as one: the busiest holds the usage and the largest
/// gives the capacity. A platform with no such counters, or a machine whose counters cannot be read, reads
/// <see langword="null"/>, and admission then treats the device as unmeasured.
/// </summary>
public sealed partial class HostGpuMeter {
    private const string CounterPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const string AdapterClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint FormatLarge = 0x00000400;
    private const uint MoreData = 0x800007D2;
    private const uint Success = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem {
        public nint Name;
        public uint Status;
        public long Value;
    }

    [LibraryImport(libraryName: "pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);
    [LibraryImport(libraryName: "pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nint userData, out nint counter);
    [LibraryImport(libraryName: "pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);
    [LibraryImport(libraryName: "pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);
    [LibraryImport(libraryName: "pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    private nint m_query;
    private nint m_counter;
    private bool m_failed;
    private double m_totalGb = double.NaN;

    ~HostGpuMeter() {
        if (m_query != 0) { _ = PdhCloseQuery(query: m_query); }
    }

    private bool Open() {
        if (m_query != 0) { return true; }
        if (!OperatingSystem.IsWindows() || m_failed) { return false; }
        if ((PdhOpenQueryW(dataSource: null, userData: 0, query: out var query) != Success)) { m_failed = true; return false; }
        if ((PdhAddEnglishCounterW(query: query, fullCounterPath: CounterPath, userData: 0, counter: out var counter) != Success)) {
            _ = PdhCloseQuery(query: query);
            m_failed = true;

            return false;
        }
        m_query = query;
        m_counter = counter;

        return true;
    }

    // The dedicated video memory the largest adapter has, from the display driver's registry record; NaN when the machine
    // records none.
    private static double Capacity() {
        if (!OperatingSystem.IsWindows()) { return double.NaN; }

        var largest = 0L;

        try {
            using var adapters = Registry.LocalMachine.OpenSubKey(name: AdapterClass);

            // The class holds one key per adapter beside keys the system keeps to itself, which refuse a read.
            foreach (var name in (adapters?.GetSubKeyNames() ?? [])) {
                try {
                    using var adapter = adapters!.OpenSubKey(name: name);

                    if (adapter?.GetValue(name: "HardwareInformation.qwMemorySize") is { } value) {
                        largest = Math.Max(val1: largest, val2: (value is long qword ? qword : (value is byte[] { Length: 8 } bytes ? BitConverter.ToInt64(value: bytes) : 0L)));
                    }
                } catch (Exception exception) when ((exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)) { }
            }
        } catch (Exception exception) when ((exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)) {
            return double.NaN;
        }

        return ((largest > 0) ? (largest / 1073741824.0) : double.NaN);
    }

    /// <summary>Reads the dedicated video memory now.</summary>
    /// <returns>The reading, or <see langword="null"/> when the platform reports none.</returns>
    public HostDeviceMemory? Read() {
        if (!Open() || (PdhCollectQueryData(query: m_query) != Success)) { return null; }
        if (double.IsNaN(d: m_totalGb)) { m_totalGb = Capacity(); }
        if (!double.IsFinite(d: m_totalGb)) { return null; }

        uint size = 0;

        if (PdhGetFormattedCounterArrayW(counter: m_counter, format: FormatLarge, bufferSize: ref size, itemCount: out var count, itemBuffer: 0) != MoreData) { return null; }

        var buffer = Marshal.AllocHGlobal(cb: checked((int)size));

        try {
            if (PdhGetFormattedCounterArrayW(counter: m_counter, format: FormatLarge, bufferSize: ref size, itemCount: out count, itemBuffer: buffer) != Success) { return null; }

            var readings = new List<(string? Instance, long Bytes)>();

            for (var index = 0; (index < count); index++) {
                var item = Marshal.PtrToStructure<CounterItem>(ptr: (buffer + (index * Marshal.SizeOf<CounterItem>())));

                if (item.Status is (0 or 1)) { readings.Add(item: (Marshal.PtrToStringUni(ptr: item.Name), item.Value)); }
            }

            return new HostDeviceMemory(TotalGb: m_totalGb, UsedGb: Used(readings: readings));
        } finally {
            Marshal.FreeHGlobal(hglobal: buffer);
        }
    }

    /// <summary>Returns the dedicated video memory the busiest adapter has in use. A counter instance is named
    /// <c>pid_&lt;pid&gt;_luid_&lt;high&gt;_&lt;low&gt;_phys_&lt;n&gt;</c>, so the instances that share a luid are one
    /// adapter's and add up.</summary>
    /// <param name="readings">Each counter instance's name and the bytes it holds.</param>
    /// <returns>The memory in use on the busiest adapter, in gigabytes; zero when no instance reports.</returns>
    public static double Used(IEnumerable<(string? Instance, long Bytes)> readings) {
        var adapters = new Dictionary<string, long>(comparer: StringComparer.Ordinal);

        foreach (var (instance, bytes) in readings) {
            var text = (instance ?? string.Empty);
            var luid = text.IndexOf(value: "_luid_", comparisonType: StringComparison.Ordinal);
            var adapter = ((luid >= 0) ? text[(luid + "_luid_".Length)..] : string.Empty);
            var phys = adapter.IndexOf(value: "_phys_", comparisonType: StringComparison.Ordinal);

            adapter = ((phys >= 0) ? adapter[..phys] : adapter);
            adapters[adapter] = (adapters.GetValueOrDefault(key: adapter) + bytes);
        }

        return (adapters.Values.DefaultIfEmpty(defaultValue: 0L).Max() / 1073741824.0);
    }
}
