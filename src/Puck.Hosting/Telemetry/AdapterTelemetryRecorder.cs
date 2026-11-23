using System.Diagnostics;
using Puck.Maths;

namespace Puck.Hosting.Telemetry;

/// <summary>The lowest, middle and highest value of one reading over a span.</summary>
/// <param name="Min">The lowest sample.</param>
/// <param name="Median">The median sample.</param>
/// <param name="Max">The highest sample.</param>
public readonly record struct AdapterSpread(double Min, double Median, double Max) {
    /// <summary>Summarizes samples.</summary>
    /// <param name="samples">The samples.</param>
    /// <returns>Their spread, or <see langword="null"/> when there are none.</returns>
    public static AdapterSpread? Of(IReadOnlyCollection<double> samples) {
        if (samples.Count == 0) { return null; }

        var ordered = samples.Order().ToArray();

        return new AdapterSpread(Max: ordered[^1], Median: OrderStatistics.Quantile(ascending: ordered, fraction: 0.5), Min: ordered[0]);
    }
}

/// <summary>
/// What a graphics adapter did across a span of a measurement, and whether its clocks were held below what the load earned.
/// The judgement is the one <see cref="Judge"/> makes from the span's loaded samples: the adapter was working hard, its
/// graphics clock sat far under the adapter's own ceiling, and neither power nor temperature explains it.
/// </summary>
/// <param name="Info">The provider and the ceilings the span is judged against.</param>
/// <param name="Samples">How many readings fall in the span.</param>
/// <param name="LoadedSamples">How many of them were taken under load (<see cref="LoadedUtilizationPercent"/>).</param>
/// <param name="GraphicsClockMhz">The graphics clock over every reading.</param>
/// <param name="LoadedGraphicsClockMhz">The graphics clock over the loaded readings.</param>
/// <param name="MemoryClockMhz">The memory clock over every reading.</param>
/// <param name="PowerWatts">The power draw over every reading.</param>
/// <param name="UtilizationPercent">The utilization over every reading.</param>
/// <param name="TemperatureCelsius">The temperature over every reading.</param>
/// <param name="PerformanceStates">The readings spent in each performance state.</param>
/// <param name="ClockEventSamples">The readings in which each clock event was active.</param>
/// <param name="HeldReason">Why the clock was held, or <see langword="null"/> when it was not or the span could not be judged.</param>
/// <param name="NotJudged">Why the span could not be judged, or <see langword="null"/> when it was.</param>
public sealed record AdapterTelemetrySummary(
    AdapterTelemetryInfo Info,
    int Samples,
    int LoadedSamples,
    AdapterSpread? GraphicsClockMhz,
    AdapterSpread? LoadedGraphicsClockMhz,
    AdapterSpread? MemoryClockMhz,
    AdapterSpread? PowerWatts,
    AdapterSpread? UtilizationPercent,
    AdapterSpread? TemperatureCelsius,
    IReadOnlyDictionary<int, int> PerformanceStates,
    IReadOnlyDictionary<AdapterClockEvents, int> ClockEventSamples,
    string? HeldReason,
    string? NotJudged
) {
    /// <summary>The utilization, in percent, from which a reading counts as taken under load: below it the driver may
    /// lawfully idle the clocks, so a low clock proves nothing.</summary>
    public const double LoadedUtilizationPercent = 60.0;
    /// <summary>The fewest loaded readings a span needs to be judged.</summary>
    public const int MinLoadedSamples = 3;
    /// <summary>The share of the adapter's highest graphics clock below which a loaded span's median clock is held: a free
    /// adapter under load boosts to within a few percent of its ceiling, while a clock locked to the base clock sits near
    /// two thirds of it.</summary>
    public const double HeldClockFraction = 0.80;
    /// <summary>The share of loaded readings in which a power or thermal event must be active for the low clock to be the
    /// machine's own limit and not a lock.</summary>
    public const double ConstrainedShare = 0.5;

    private static readonly AdapterClockEvents[] SingleEvents = [AdapterClockEvents.Idle, AdapterClockEvents.ApplicationClocks, AdapterClockEvents.PowerCap, AdapterClockEvents.HardwareSlowdown, AdapterClockEvents.SyncBoost, AdapterClockEvents.ThermalSlowdown, AdapterClockEvents.HardwareThermalSlowdown, AdapterClockEvents.PowerBrakeSlowdown, AdapterClockEvents.DisplayClocks];

    /// <summary>Summarizes readings taken across a span and judges them.</summary>
    /// <param name="info">The provider and the adapter's ceilings.</param>
    /// <param name="readings">The readings in the span, in order.</param>
    /// <returns>The summary.</returns>
    public static AdapterTelemetrySummary Of(AdapterTelemetryInfo info, IReadOnlyList<AdapterTelemetryReading> readings) {
        var loaded = readings.Where(predicate: static reading => (reading.UtilizationPercent >= LoadedUtilizationPercent) && reading.GraphicsClockMhz.HasValue).ToArray();
        var states = new SortedDictionary<int, int>();
        var events = new SortedDictionary<AdapterClockEvents, int>();

        foreach (var reading in readings) {
            if (reading.PerformanceState is { } state) {
                states[state] = (states.GetValueOrDefault(key: state) + 1);
            }
            if (reading.ClockEvents is { } active) {
                foreach (var flag in SingleEvents) {
                    if (active.HasFlag(flag: flag)) {
                        events[flag] = (events.GetValueOrDefault(key: flag) + 1);
                    }
                }
            }
        }

        var (held, notJudged) = Judge(info: info, loaded: loaded);

        return new AdapterTelemetrySummary(
            ClockEventSamples: events,
            GraphicsClockMhz: Spread(readings: readings, select: static reading => reading.GraphicsClockMhz),
            HeldReason: held,
            Info: info,
            LoadedGraphicsClockMhz: Spread(readings: loaded, select: static reading => reading.GraphicsClockMhz),
            LoadedSamples: loaded.Length,
            MemoryClockMhz: Spread(readings: readings, select: static reading => reading.MemoryClockMhz),
            NotJudged: notJudged,
            PerformanceStates: states,
            PowerWatts: Spread(readings: readings, select: static reading => reading.PowerWatts),
            Samples: readings.Count,
            TemperatureCelsius: Spread(readings: readings, select: static reading => reading.TemperatureCelsius),
            UtilizationPercent: Spread(readings: readings, select: static reading => reading.UtilizationPercent)
        );
    }

    private static AdapterSpread? Spread(IEnumerable<AdapterTelemetryReading> readings, Func<AdapterTelemetryReading, double?> select) => AdapterSpread.Of(samples: [.. readings.Select(selector: select).OfType<double>()]);

    // Held when the adapter ran hard, its median graphics clock sat under the held share of the adapter's ceiling, and a
    // power or thermal event was not active in half the loaded readings. A span that cannot be judged says why.
    private static (string? Held, string? NotJudged) Judge(AdapterTelemetryInfo info, AdapterTelemetryReading[] loaded) {
        if (info.Unavailable is { } unavailable) { return (null, $"no adapter telemetry: {unavailable}"); }
        if (info.MaxGraphicsClockMhz is not { } ceiling) { return (null, "the adapter reports no highest graphics clock to judge against"); }
        if (loaded.Length < MinLoadedSamples) { return (null, $"{loaded.Length} loaded reading(s) (utilization of {LoadedUtilizationPercent:0}% or more), {MinLoadedSamples} needed"); }

        var clock = OrderStatistics.Quantile(ascending: loaded.Select(selector: static reading => reading.GraphicsClockMhz!.Value).Order().ToArray(), fraction: 0.5);
        var constrained = loaded.Count(predicate: static reading => ((reading.ClockEvents ?? AdapterClockEvents.None) & AdapterClockEvents.Constrained) != AdapterClockEvents.None);

        if ((clock >= (HeldClockFraction * ceiling)) || ((constrained / (double)loaded.Length) >= ConstrainedShare)) { return (null, null); }

        var load = loaded.Average(selector: static reading => reading.UtilizationPercent!.Value);
        var active = loaded.Select(selector: static reading => (reading.ClockEvents ?? AdapterClockEvents.None)).Aggregate(seed: AdapterClockEvents.None, func: static (all, next) => (all | next));

        return (string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"GPU clock held: the graphics clock sat at a median {clock:0} MHz, {(clock / ceiling):P0} of the adapter's {ceiling:0} MHz ceiling, under {load:0}% utilization with no power or thermal limit active (clock events seen: {active}); a profiler's or a tool's clock lock is the usual cause"), null);
    }
}

/// <summary>
/// Reads an <see cref="IAdapterTelemetry"/> on a timer for a measurement's life and answers a summary of any span of it, so
/// a run can attribute the adapter's state to each chapter without a read on its own frame thread. It owns the telemetry
/// and disposes it.
/// </summary>
public sealed class AdapterTelemetryRecorder : IDisposable {
    /// <summary>The most readings kept; a measurement that outlasts them keeps its first.</summary>
    public const int MaxSamples = 32768;

    private readonly IAdapterTelemetry m_telemetry;
    private readonly List<(long Timestamp, AdapterTelemetryReading Reading)> m_samples = [];
    private readonly Timer m_timer;
    private readonly Lock m_gate = new();

    /// <summary>Initializes a new instance of the <see cref="AdapterTelemetryRecorder"/> class, takes a first reading and
    /// starts the timer.</summary>
    /// <param name="telemetry">The telemetry to read; the recorder takes ownership.</param>
    /// <param name="interval">The time between readings.</param>
    public AdapterTelemetryRecorder(IAdapterTelemetry telemetry, TimeSpan interval) {
        m_telemetry = telemetry;
        Take();
        m_timer = new Timer(callback: _ => Take(), dueTime: interval, period: interval, state: null);
    }

    /// <summary>Gets the provider and the ceilings the recorder's spans are judged against.</summary>
    public AdapterTelemetryInfo Info => m_telemetry.Info;

    private void Take() {
        var reading = m_telemetry.Read();

        lock (m_gate) {
            if (m_samples.Count < MaxSamples) { m_samples.Add(item: (Stopwatch.GetTimestamp(), reading)); }
        }
    }

    /// <summary>Summarizes the readings taken in a span.</summary>
    /// <param name="from">The span's first <see cref="Stopwatch.GetTimestamp"/> tick.</param>
    /// <param name="to">The span's last tick; zero for now.</param>
    /// <returns>The summary of those readings.</returns>
    public AdapterTelemetrySummary Summarize(long from, long to) {
        AdapterTelemetryReading[] readings;

        lock (m_gate) {
            var last = ((to == 0) ? long.MaxValue : to);

            readings = [.. m_samples.Where(predicate: sample => (sample.Timestamp >= from) && (sample.Timestamp <= last)).Select(selector: static sample => sample.Reading)];
        }

        return AdapterTelemetrySummary.Of(info: m_telemetry.Info, readings: readings);
    }

    /// <summary>Stops the timer and disposes the telemetry.</summary>
    public void Dispose() {
        m_timer.Dispose();
        m_telemetry.Dispose();
    }
}
