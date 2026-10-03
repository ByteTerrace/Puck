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
/// <item><c>PRESSURE &lt;why&gt; …</c> when free memory or disk falls under its pressure threshold, and again when the
/// reasons change;</item>
/// <item><c>CAPACITY …</c> when, without pressure, the CPU mean over the last <c>cpuSamples</c> readings falls under its
/// threshold and free memory is over its own, once that many readings exist;</item>
/// <item><c>LOADED …</c> when, without pressure, that window shows no capacity, so a watcher sees capacity end.</item>
/// </list>
/// One line per transition: a state that holds prints nothing more. PRESSURE wins over CAPACITY within one reading.
/// </summary>
internal sealed class HostLoadMonitor(HostLoadThresholds thresholds, int cpuSamples) {
    private readonly Queue<double> m_cpu = new();

    private bool? m_gpuBusy;
    // The admission state last printed (PRESSURE with its reasons, CAPACITY or LOADED), or null before the first.
    private string? m_admission;

    /// <summary>Whether the latest reading has capacity, whether or not this reading printed its CAPACITY line.</summary>
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

        // Capacity is judged only with both its thresholds and a full window; until then a reading without pressure has
        // no admission state, and the next one that does prints.
        var judged = ((thresholds.CapacityCpuPercent is not null) && (thresholds.CapacityRamGb is not null) && (m_cpu.Count == cpuSamples));
        var admission = ((pressure.Count != 0)
            ? $"PRESSURE {string.Join(separator: ',', values: pressure)}"
            : (HasCapacity
                ? "CAPACITY"
                : (judged ? "LOADED" : null)));

        if (admission != m_admission) {
            if (admission is not null) {
                lines.Add(item: $"{admission} {values}");
            }

            m_admission = admission;
        }

        return lines;
    }
}
