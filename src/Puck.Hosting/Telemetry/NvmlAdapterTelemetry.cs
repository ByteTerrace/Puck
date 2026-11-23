using System.Runtime.InteropServices;

namespace Puck.Hosting.Telemetry;

/// <summary>
/// Reads an NVIDIA adapter through NVML, the management library the driver installs (<c>nvml.dll</c> on Windows,
/// <c>libnvidia-ml.so.1</c> on Linux). The library is loaded at run time and its entry points bound by name, so a machine
/// without it, or an older driver without a newer entry point, loses only the fields that entry point would have read.
/// Nothing of the library is redistributed. Clock events are read through <c>nvmlDeviceGetCurrentClocksEventReasons</c>,
/// or its earlier name <c>nvmlDeviceGetCurrentClocksThrottleReasons</c>; the bit values of
/// <see cref="AdapterClockEvents"/> are NVML's.
/// </summary>
internal sealed unsafe class NvmlAdapterTelemetry : IAdapterTelemetry {
    private const uint ClockGraphics = 0;
    private const uint ClockMemory = 2;
    private const uint TemperatureGpu = 0;
    private const uint NameLength = 96;
    private const int Success = 0;

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    private struct Utilization {
        public uint Gpu;
        public uint Memory;
    }

    private readonly nint m_library;
    private readonly nint m_device;
    private readonly delegate* unmanaged<int> m_shutdown;
    private readonly delegate* unmanaged<nint, uint, uint*, int> m_clock;
    private readonly delegate* unmanaged<nint, uint*, int> m_power;
    private readonly delegate* unmanaged<nint, Utilization*, int> m_utilization;
    private readonly delegate* unmanaged<nint, int*, int> m_performanceState;
    private readonly delegate* unmanaged<nint, uint, uint*, int> m_temperature;
    private readonly delegate* unmanaged<nint, ulong*, int> m_events;
    private readonly Lock m_gate = new();
    private bool m_disposed;

    private NvmlAdapterTelemetry(nint library, nint device, AdapterTelemetryInfo info, delegate* unmanaged<int> shutdown, delegate* unmanaged<nint, uint, uint*, int> clock, delegate* unmanaged<nint, uint*, int> power, delegate* unmanaged<nint, Utilization*, int> utilization, delegate* unmanaged<nint, int*, int> performanceState, delegate* unmanaged<nint, uint, uint*, int> temperature, delegate* unmanaged<nint, ulong*, int> events) {
        Info = info;
        m_clock = clock;
        m_device = device;
        m_events = events;
        m_library = library;
        m_performanceState = performanceState;
        m_power = power;
        m_shutdown = shutdown;
        m_temperature = temperature;
        m_utilization = utilization;
    }

    /// <inheritdoc/>
    public AdapterTelemetryInfo Info { get; }

    // The library, loaded from the places NVIDIA installs it: the system directory (or the loader's own search on Linux)
    // first, then the toolkit directory older Windows drivers use.
    private static bool TryLoad(out nint library) {
        if (OperatingSystem.IsWindows()) {
            if (NativeLibrary.TryLoad(libraryName: "nvml.dll", assembly: typeof(NvmlAdapterTelemetry).Assembly, searchPath: DllImportSearchPath.System32, handle: out library)) { return true; }

            return NativeLibrary.TryLoad(libraryPath: Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.ProgramFiles), path2: "NVIDIA Corporation", path3: "NVSMI", path4: "nvml.dll"), handle: out library);
        }
        if (OperatingSystem.IsLinux()) {
            return (NativeLibrary.TryLoad(libraryPath: "libnvidia-ml.so.1", handle: out library) || NativeLibrary.TryLoad(libraryPath: "libnvidia-ml.so", handle: out library));
        }

        library = 0;

        return false;
    }

    private static nint Export(nint library, string name) => (NativeLibrary.TryGetExport(handle: library, name: name, address: out var address) ? address : 0);

    // Opens the adapter the renderer names, or the busiest one when it names none NVML knows.
    internal static bool TryOpen(string? adapter, out IAdapterTelemetry? telemetry, out string reason) {
        telemetry = null;
        reason = string.Empty;

        if (!TryLoad(library: out var library)) {
            reason = "the NVML library is not installed";

            return false;
        }

        var init = (delegate* unmanaged<int>)Export(library: library, name: "nvmlInit_v2");
        var shutdown = (delegate* unmanaged<int>)Export(library: library, name: "nvmlShutdown");
        var count = (delegate* unmanaged<uint*, int>)Export(library: library, name: "nvmlDeviceGetCount_v2");
        var handleByIndex = (delegate* unmanaged<uint, nint*, int>)Export(library: library, name: "nvmlDeviceGetHandleByIndex_v2");
        var name = (delegate* unmanaged<nint, byte*, uint, int>)Export(library: library, name: "nvmlDeviceGetName");
        var clock = (delegate* unmanaged<nint, uint, uint*, int>)Export(library: library, name: "nvmlDeviceGetClockInfo");
        var maxClock = (delegate* unmanaged<nint, uint, uint*, int>)Export(library: library, name: "nvmlDeviceGetMaxClockInfo");
        var power = (delegate* unmanaged<nint, uint*, int>)Export(library: library, name: "nvmlDeviceGetPowerUsage");
        var powerLimit = (delegate* unmanaged<nint, uint*, int>)Export(library: library, name: "nvmlDeviceGetEnforcedPowerLimit");
        var utilization = (delegate* unmanaged<nint, Utilization*, int>)Export(library: library, name: "nvmlDeviceGetUtilizationRates");
        var performanceState = (delegate* unmanaged<nint, int*, int>)Export(library: library, name: "nvmlDeviceGetPerformanceState");
        var temperature = (delegate* unmanaged<nint, uint, uint*, int>)Export(library: library, name: "nvmlDeviceGetTemperature");
        var events = (delegate* unmanaged<nint, ulong*, int>)Export(library: library, name: "nvmlDeviceGetCurrentClocksEventReasons");

        if (events == null) {
            events = (delegate* unmanaged<nint, ulong*, int>)Export(library: library, name: "nvmlDeviceGetCurrentClocksThrottleReasons");
        }
        if ((init == null) || (shutdown == null) || (count == null) || (handleByIndex == null) || (name == null) || (clock == null) || (utilization == null)) {
            NativeLibrary.Free(handle: library);
            reason = "the NVML library lacks an entry point the reader needs";

            return false;
        }

        var status = init();

        if (status != Success) {
            NativeLibrary.Free(handle: library);
            reason = $"nvmlInit failed with code {status} (no NVIDIA driver is loaded)";

            return false;
        }

        uint devices = 0;

        if ((count(&devices) != Success) || (devices == 0)) {
            _ = shutdown();
            NativeLibrary.Free(handle: library);
            reason = "NVML reports no adapter";

            return false;
        }

        var chosen = default(nint);
        var chosenName = string.Empty;
        var busiest = -1.0;

        var buffer = stackalloc byte[(int)NameLength];

        for (uint index = 0; (index < devices); index++) {
            nint device = 0;

            if (handleByIndex(index, &device) != Success) { continue; }

            var deviceName = ((name(device, buffer, NameLength) == Success) ? (Marshal.PtrToStringUTF8(ptr: ((nint)buffer)) ?? string.Empty) : string.Empty);

            if ((adapter is { Length: > 0 }) && (deviceName.Length > 0) && (deviceName.Contains(value: adapter, comparisonType: StringComparison.OrdinalIgnoreCase) || adapter.Contains(value: deviceName, comparisonType: StringComparison.OrdinalIgnoreCase))) {
                chosen = device;
                chosenName = deviceName;
                busiest = double.PositiveInfinity;

                break;
            }

            var rates = default(Utilization);
            var load = ((utilization(device, &rates) == Success) ? rates.Gpu : 0.0);

            if ((chosen == 0) || (load > busiest)) {
                chosen = device;
                chosenName = deviceName;
                busiest = load;
            }
        }
        if (chosen == 0) {
            _ = shutdown();
            NativeLibrary.Free(handle: library);
            reason = "NVML could not open an adapter";

            return false;
        }

        double? Megahertz(delegate* unmanaged<nint, uint, uint*, int> read, uint type) {
            uint value = 0;

            return ((read != null) && (read(chosen, type, &value) == Success) ? value : null);
        }

        double? MilliwattsAsWatts(delegate* unmanaged<nint, uint*, int> read) {
            uint value = 0;

            return ((read != null) && (read(chosen, &value) == Success) ? (value / 1000.0) : null);
        }

        telemetry = new NvmlAdapterTelemetry(
            clock: clock,
            device: chosen,
            events: events,
            info: new AdapterTelemetryInfo(Provider: "nvml", Adapter: chosenName, MaxGraphicsClockMhz: Megahertz(read: maxClock, type: ClockGraphics), MaxMemoryClockMhz: Megahertz(read: maxClock, type: ClockMemory), PowerLimitWatts: MilliwattsAsWatts(read: powerLimit), Unavailable: null),
            library: library,
            performanceState: performanceState,
            power: power,
            shutdown: shutdown,
            temperature: temperature,
            utilization: utilization
        );

        return true;
    }

    /// <inheritdoc/>
    public AdapterTelemetryReading Read() {
        lock (m_gate) {
            if (m_disposed) { return default; }

            double? graphics = null, memory = null, watts = null, load = null, memoryLoad = null, celsius = null;
            int? state = null;
            AdapterClockEvents? reasons = null;
            uint value = 0;
            var pstate = 0;
            ulong bits = 0;
            var rates = default(Utilization);

            if (m_clock(m_device, ClockGraphics, &value) == Success) { graphics = value; }
            if (m_clock(m_device, ClockMemory, &value) == Success) { memory = value; }
            if ((m_power != null) && (m_power(m_device, &value) == Success)) { watts = (value / 1000.0); }
            if (m_utilization(m_device, &rates) == Success) {
                load = rates.Gpu;
                memoryLoad = rates.Memory;
            }
            if ((m_temperature != null) && (m_temperature(m_device, TemperatureGpu, &value) == Success)) { celsius = value; }
            if ((m_performanceState != null) && (m_performanceState(m_device, &pstate) == Success) && (pstate is >= 0 and <= 15)) { state = pstate; }
            if ((m_events != null) && (m_events(m_device, &bits) == Success)) { reasons = ((AdapterClockEvents)bits); }

            return new AdapterTelemetryReading(ClockEvents: reasons, GraphicsClockMhz: graphics, MemoryClockMhz: memory, MemoryUtilizationPercent: memoryLoad, PerformanceState: state, PowerWatts: watts, TemperatureCelsius: celsius, UtilizationPercent: load);
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) { return; }

            m_disposed = true;
            _ = m_shutdown();
            NativeLibrary.Free(handle: m_library);
        }
    }
}
