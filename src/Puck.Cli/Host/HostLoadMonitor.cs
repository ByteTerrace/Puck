using System.Globalization;

namespace Puck.Cli.Host;

/// <summary>One reading of the machine an agent runs on.</summary>
/// <param name="At">When the reading was taken.</param>
/// <param name="CpuPercent">CPU busy percentage over the interval that ended with this reading.</param>
/// <param name="FreeRamGb">Free physical memory, in gigabytes.</param>
/// <param name="FreeDiskGb">Free space on the drive holding the checkout, in gigabytes.</param>
/// <param name="GpuHolder">The process holding the GPU (its name and id), or <see langword="null"/> when none does.</param>
/// <param name="ReuseNodes">MSBuild nodes left running for reuse (a build or restore run without
/// <c>-nodeReuse:false</c>).</param>
internal readonly record struct HostSample(DateTimeOffset At, double CpuPercent, double FreeRamGb, double FreeDiskGb, string? GpuHolder, int ReuseNodes);
/// <summary>The thresholds that turn readings into admission lines. A threshold left out is never judged, so with
/// none the monitor reports only GPU transitions.</summary>
/// <param name="CapacityCpuPercent">CAPACITY needs the CPU mean below this.</param>
/// <param name="CapacityRamGb">CAPACITY needs free memory above this.</param>
/// <param name="PressureRamGb">PRESSURE when free memory is below this.</param>
/// <param name="PressureDiskGb">PRESSURE when free disk is below this.</param>
internal sealed record HostLoadThresholds(double? CapacityCpuPercent, double? CapacityRamGb, double? PressureRamGb, double? PressureDiskGb) {
    public static readonly HostLoadThresholds Default = new(CapacityCpuPercent: 50, CapacityRamGb: 5, PressureDiskGb: 10, PressureRamGb: 2);
}
/// <summary>
/// Turns a stream of <see cref="HostSample"/>s into the lines an agent admits or holds work by. It reads nothing itself,
/// so every rule is a law over injected samples:
/// <list type="bullet">
/// <item><c>GPU busy (&lt;holder&gt;) …</c> or <c>GPU idle …</c> once at the first sample and on every change after;</item>
/// <item><c>PRESSURE &lt;why&gt; …</c> while free memory or disk is under its pressure threshold, at most once per
/// <see cref="PressureEvery"/>;</item>
/// <item>otherwise <c>CAPACITY …</c> while the CPU mean over the last <c>cpuSamples</c> readings is under its threshold
/// and free memory is over its own, at most once per <see cref="CapacityEvery"/>, and only once that many readings
/// exist. PRESSURE wins over CAPACITY within one reading.</item>
/// </list>
/// </summary>
internal sealed class HostLoadMonitor(HostLoadThresholds thresholds, int cpuSamples) {
    /// <summary>The shortest gap between two PRESSURE lines.</summary>
    public static readonly TimeSpan PressureEvery = TimeSpan.FromMinutes(minutes: 5);
    /// <summary>The shortest gap between two CAPACITY lines.</summary>
    public static readonly TimeSpan CapacityEvery = TimeSpan.FromMinutes(minutes: 10);

    private readonly Queue<double> m_cpu = new();

    private bool? m_gpuBusy;
    private DateTimeOffset? m_lastCapacity;
    private DateTimeOffset? m_lastPressure;

    /// <summary>Whether the latest reading has capacity, even when its CAPACITY line is throttled.</summary>
    public bool HasCapacity { get; private set; }

    private static string Number(double value) => value.ToString(format: "0.0", provider: CultureInfo.InvariantCulture);

    /// <summary>Takes one reading and returns the lines it produces, in order: the GPU line, then PRESSURE or
    /// CAPACITY.</summary>
    /// <param name="sample">The reading.</param>
    /// <returns>Zero or more lines.</returns>
    public IReadOnlyList<string> Observe(HostSample sample) {
        List<string> lines = [];

        m_cpu.Enqueue(item: sample.CpuPercent);

        while (m_cpu.Count > cpuSamples) {
            _ = m_cpu.Dequeue();
        }

        var mean = m_cpu.Average();
        var values = $"cpu={mean.ToString(format: "0", provider: CultureInfo.InvariantCulture)}% freeRAM={Number(value: sample.FreeRamGb)}GB freeDisk={Number(value: sample.FreeDiskGb)}GB reuseNodes={sample.ReuseNodes.ToString(provider: CultureInfo.InvariantCulture)}";
        var busy = (sample.GpuHolder is not null);

        if (m_gpuBusy != busy) {
            lines.Add(item: (busy
                ? $"GPU busy ({sample.GpuHolder}) {values}"
                : $"GPU idle {values}"
            ));
            m_gpuBusy = busy;
        }

        List<string> pressure = [];

        if ((thresholds.PressureRamGb is { } ram) && (sample.FreeRamGb < ram)) {
            pressure.Add(item: $"freeRAM<{Number(value: ram)}GB");
        }
        if ((thresholds.PressureDiskGb is { } disk) && (sample.FreeDiskGb < disk)) {
            pressure.Add(item: $"freeDisk<{Number(value: disk)}GB");
        }

        HasCapacity = ((pressure.Count == 0) &&
            (thresholds.CapacityCpuPercent is { } cpu) &&
            (thresholds.CapacityRamGb is { } free) &&
            (m_cpu.Count == cpuSamples) && (mean < cpu) && (sample.FreeRamGb > free));

        if (pressure.Count != 0) {
            if ((m_lastPressure is not { } lastPressure) || ((sample.At - lastPressure) >= PressureEvery)) {
                lines.Add(item: $"PRESSURE {string.Join(separator: ',', values: pressure)} {values}");
                m_lastPressure = sample.At;
            }
        } else if (HasCapacity) {
            if ((m_lastCapacity is not { } lastCapacity) || ((sample.At - lastCapacity) >= CapacityEvery)) {
                lines.Add(item: $"CAPACITY {values}");
                m_lastCapacity = sample.At;
            }
        }

        return lines;
    }
}
