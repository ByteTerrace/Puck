namespace Puck.Hosting.Telemetry;

/// <summary>
/// Why a graphics adapter is running below the clocks its load would otherwise earn, named by the adapter vendor's driver
/// and shared by every provider. The bit values are the ones NVIDIA's NVML reports; another provider maps its own reasons
/// onto these names or reports <see cref="None"/>.
/// </summary>
[Flags]
public enum AdapterClockEvents : ulong {
    /// <summary>No reason is active, or the provider reports none.</summary>
    None = 0,
    /// <summary>The adapter is idle, so its clocks are down because nothing asks for more.</summary>
    Idle = 0x1,
    /// <summary>Clocks are set by something other than the driver's own governor: an application clock request, a profiler's
    /// or an overclocking tool's lock.</summary>
    ApplicationClocks = 0x2,
    /// <summary>The driver lowered the clocks to stay under its power limit.</summary>
    PowerCap = 0x4,
    /// <summary>The hardware slowed the clocks (a temperature, power or power-supply trip).</summary>
    HardwareSlowdown = 0x8,
    /// <summary>Several adapters are boosting together and this one follows the group.</summary>
    SyncBoost = 0x10,
    /// <summary>The driver lowered the clocks to stay under its temperature limit.</summary>
    ThermalSlowdown = 0x20,
    /// <summary>The hardware lowered the clocks because of temperature.</summary>
    HardwareThermalSlowdown = 0x40,
    /// <summary>The power brake pin asked for a slowdown.</summary>
    PowerBrakeSlowdown = 0x80,
    /// <summary>The display's needs set the clocks.</summary>
    DisplayClocks = 0x100,
    /// <summary>The reasons a loaded adapter's low clock is the machine's own limit and not a lock: power and thermal.</summary>
    Constrained = (PowerCap | HardwareSlowdown | ThermalSlowdown | HardwareThermalSlowdown | PowerBrakeSlowdown),
}

/// <summary>One reading of a graphics adapter's state; every field is <see langword="null"/> when the provider cannot say.</summary>
/// <param name="GraphicsClockMhz">The graphics (shader) clock, in megahertz.</param>
/// <param name="MemoryClockMhz">The memory clock, in megahertz.</param>
/// <param name="PowerWatts">The board's power draw, in watts.</param>
/// <param name="UtilizationPercent">The share of the last sample period in which the adapter ran any work, 0 to 100.</param>
/// <param name="MemoryUtilizationPercent">The share of the last sample period in which its memory was read or written, 0 to 100.</param>
/// <param name="TemperatureCelsius">The die's temperature, in degrees Celsius.</param>
/// <param name="PerformanceState">The performance state: 0 is the highest-performance state and larger numbers are lower.</param>
/// <param name="ClockEvents">Why the clocks are below their ceiling right now.</param>
public readonly record struct AdapterTelemetryReading(
    double? GraphicsClockMhz,
    double? MemoryClockMhz,
    double? PowerWatts,
    double? UtilizationPercent,
    double? MemoryUtilizationPercent,
    double? TemperatureCelsius,
    int? PerformanceState,
    AdapterClockEvents? ClockEvents
);

/// <summary>What an adapter telemetry provider is, and the ceilings its readings are judged against.</summary>
/// <param name="Provider">The provider's name (<c>nvml</c>), or <c>none</c> when no provider could open.</param>
/// <param name="Adapter">The adapter's name, or <see langword="null"/> when none was opened.</param>
/// <param name="MaxGraphicsClockMhz">The highest graphics clock the adapter supports, which its boost approaches.</param>
/// <param name="MaxMemoryClockMhz">The highest memory clock the adapter supports.</param>
/// <param name="PowerLimitWatts">The board's enforced power limit.</param>
/// <param name="Unavailable">Why no provider opened, or <see langword="null"/> when one did.</param>
public sealed record AdapterTelemetryInfo(string Provider, string? Adapter, double? MaxGraphicsClockMhz, double? MaxMemoryClockMhz, double? PowerLimitWatts, string? Unavailable);

/// <summary>
/// Reads a graphics adapter's live state (clocks, power, utilization, performance state and the reasons its clocks are
/// held down) through a provider the machine has, so a measurement can say what the hardware was doing while it ran. The
/// contract is provider-independent: <see cref="AdapterTelemetry.Open"/> picks the provider, and an absent library, an
/// absent adapter or a field a driver does not report reads as unknown and never as a failure.
/// </summary>
public interface IAdapterTelemetry : IDisposable {
    /// <summary>Gets what the provider is and the ceilings its readings are judged against.</summary>
    AdapterTelemetryInfo Info { get; }
    /// <summary>Takes one reading; safe to call from any thread.</summary>
    /// <returns>The reading, with every field unknown when the provider is unavailable or fails.</returns>
    AdapterTelemetryReading Read();
}

/// <summary>Opens the adapter telemetry the machine offers.</summary>
public static class AdapterTelemetry {
    private delegate bool Provider(string? adapter, out IAdapterTelemetry? telemetry, out string reason);

    // Each provider the engine knows, tried in order. A provider for another vendor (a DXGI or sysfs reader) joins this list
    // and speaks the same IAdapterTelemetry; nothing above it names a vendor.
    private static readonly (string Name, Provider Open)[] Providers = [("nvml", NvmlAdapterTelemetry.TryOpen)];

    /// <summary>Opens the first provider that can read the adapter.</summary>
    /// <param name="adapter">The adapter's name as the renderer reports it, matched by containment against each provider's
    /// adapters; when it is <see langword="null"/> or matches none, the busiest adapter is read.</param>
    /// <returns>The telemetry; when no provider opened, one whose readings are all unknown and whose
    /// <see cref="AdapterTelemetryInfo.Unavailable"/> says why.</returns>
    public static IAdapterTelemetry Open(string? adapter = null) {
        var reasons = new List<string>();

        foreach (var (name, open) in Providers) {
            try {
                if (open(adapter, out var telemetry, out var reason) && (telemetry is not null)) {
                    return telemetry;
                }

                reasons.Add(item: $"{name}: {reason}");
            } catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or NotSupportedException) {
                reasons.Add(item: $"{name}: {exception.Message}");
            }
        }

        return new UnavailableAdapterTelemetry(reason: string.Join(separator: "; ", values: reasons));
    }

    private sealed class UnavailableAdapterTelemetry(string reason) : IAdapterTelemetry {
        public AdapterTelemetryInfo Info { get; } = new("none", null, null, null, null, reason);

        public void Dispose() { }
        public AdapterTelemetryReading Read() => default;
    }
}
