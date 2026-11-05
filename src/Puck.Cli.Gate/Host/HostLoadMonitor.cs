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
/// <param name="HeavyTestHolder">A heavy test run (<see cref="HostProcesses.IsHeavyTest"/>) the reading process did not
/// start, its name and id, or <see langword="null"/> when none runs.</param>
/// <param name="CommitChargeGb">The system's commit charge, in gigabytes; NaN when the platform reports none.</param>
/// <param name="CommitLimitGb">The system's commit limit, in gigabytes; NaN when the platform reports none.</param>
/// <param name="Starts">The running processes the host tracks for admission (<see cref="HostProcesses.RunningStart"/>) with
/// their private bytes, or <see langword="null"/> when the reading lists none.</param>
public readonly record struct HostSample(DateTimeOffset At, double CpuPercent, double FreeRamGb, double FreeDiskGb, string? GpuHolder, int ReuseNodes, string? HeavyTestHolder = null, double CommitChargeGb = double.NaN, double CommitLimitGb = double.NaN, IReadOnlyList<HostRunningStart>? Starts = null) {
    /// <summary>Gets the commit charge as a percentage of the commit limit: allocations fail at 100 whatever physical memory
    /// is free. NaN when the charge or the limit is unknown.</summary>
    public double CommitPercent => ((double.IsFinite(d: CommitChargeGb) && double.IsFinite(d: CommitLimitGb) && (CommitLimitGb > 0)) ? (100.0 * CommitChargeGb / CommitLimitGb) : double.NaN);
}
/// <summary>A running process the host admitted: a World, a test-batch worker or a heavy test run.</summary>
/// <param name="Id">The process id.</param>
/// <param name="Kind">What the process is (<see cref="HostProcesses.RunningStart"/>).</param>
/// <param name="PrivateGb">The process's private bytes, in gigabytes: the commit it holds now.</param>
public readonly record struct HostRunningStart(int Id, HostStartKind Kind, double PrivateGb);
/// <summary>The thresholds that turn readings into admission lines. A threshold left out is never judged, so with
/// none the monitor reports only GPU transitions.</summary>
/// <param name="CapacityCpuPercent">CAPACITY needs the CPU mean below this.</param>
/// <param name="CapacityRamGb">CAPACITY needs free memory above this.</param>
/// <param name="PressureRamGb">PRESSURE when free memory is below this.</param>
/// <param name="PressureDiskGb">PRESSURE when free disk is below this.</param>
/// <param name="CapacityCommitPercent">CAPACITY needs the commit charge below this share of the commit limit, when the
/// platform reports one.</param>
public sealed record HostLoadThresholds(double? CapacityCpuPercent, double? CapacityRamGb, double? PressureRamGb, double? PressureDiskGb, double? CapacityCommitPercent = null) {
    /// <summary>The share of the commit limit, in percent, a charge must stay under for the machine to take more work.
    /// Allocation fails at the limit with physical memory still free, and one windowed World or heavy suite charges
    /// ten gigabytes, so the margin is what keeps two of them from meeting there.</summary>
    public const double CommitCapacityPercent = 75;

    /// <summary>Gets whether a reading's commit charge is under <see cref="CapacityCommitPercent"/> of its limit. A
    /// threshold left out, or a platform that reports no commit limit, never holds a reading back.</summary>
    /// <param name="reading">The reading.</param>
    /// <returns><see langword="true"/> when the commit charge leaves room for more work or is not judged.</returns>
    public bool CommitHasHeadroom(HostSample reading) => ((CapacityCommitPercent is not { } limit) || double.IsNaN(d: reading.CommitPercent) || (reading.CommitPercent < limit));
    /// <summary>The commit, in gigabytes, a windowed World is expected to reach in a session of minutes (readings of 12 to
    /// 18 GB private on the debug world).</summary>
    public const double WindowedWorldPeakGb = 18;
    /// <summary>The commit, in gigabytes, a headless World is expected to reach (readings of 4 to 6 GB).</summary>
    public const double HeadlessWorldPeakGb = 6;
    /// <summary>The commit, in gigabytes, a heavy test suite is expected to reach (the mesh-irradiance laws reached 9 GB).</summary>
    public const double HeavySuitePeakGb = 10;
    /// <summary>The commit, in gigabytes, a replica of a <c>puck test</c> batch is expected to reach (a reproduce run read
    /// about 8 GB).</summary>
    public const double ReproduceReplicaPeakGb = 9;
    /// <summary>The commit, in gigabytes, an ordinary step (a build, a verb that boots no World) is expected to reach.</summary>
    public const double StepPeakGb = 2;
    /// <summary>The most a finished start's measured peak raises its kind's expected peak, as a multiple of the default, so
    /// one outlier cannot make every later start of the kind wait for headroom that never comes.</summary>
    public const double MeasuredPeakCeilingFactor = 2;
    /// <summary>The most one start's expected peak counts, as a share of the commit limit, so a machine whose limit is
    /// near one World's peak still admits that World once nothing else is growing.</summary>
    public const double PeakShareOfCommitLimit = 0.5;

    /// <summary>Returns the commit, in gigabytes, a start of a kind is expected to reach before a measurement of a finished
    /// start of the same kind raises it.</summary>
    /// <param name="kind">The start's kind.</param>
    /// <returns>The default expected peak.</returns>
    public static double DefaultPeakGb(HostStartKind kind) => kind switch {
        HostStartKind.WindowedWorld => WindowedWorldPeakGb,
        HostStartKind.HeadlessWorld => HeadlessWorldPeakGb,
        HostStartKind.HeavySuite => HeavySuitePeakGb,
        HostStartKind.ReproduceReplica => ReproduceReplicaPeakGb,
        _ => StepPeakGb,
    };
    /// <summary>Gets whether a reading's commit headroom (the limit less the charge) covers <paramref name="needGb"/>. A
    /// threshold left out, or a platform that reports no commit limit, never holds a reading back.</summary>
    /// <param name="reading">The reading.</param>
    /// <param name="needGb">The commit, in gigabytes, the start and the growth still to come need.</param>
    /// <returns><see langword="true"/> when the headroom covers the need or is not judged.</returns>
    public bool CommitCovers(HostSample reading, double needGb) => ((CapacityCommitPercent is null) || !double.IsFinite(d: reading.CommitLimitGb) || !double.IsFinite(d: reading.CommitChargeGb) || ((reading.CommitLimitGb - reading.CommitChargeGb) >= needGb));
    /// <summary>The free disk, in gigabytes, below which every machine is under pressure. A build and a suite write the
    /// same outputs whatever the machine's size, so this one threshold does not scale.</summary>
    public const double DiskPressureGb = 10;
    /// <summary>The installed memory, in gigabytes, a machine is judged by when it cannot report its own: the smallest
    /// developer machine the thresholds are tuned for.</summary>
    public const double UnreportedInstalledRamGb = 16;

    private static readonly Lazy<HostLoadThresholds> MachineThresholds = new(valueFactory: static () => For(
        installedRamGb: ((Puck.Hosting.HostMemory.InstalledPhysicalBytes() is { } installed) ? (installed / 1073741824.0) : UnreportedInstalledRamGb),
        logicalProcessors: Environment.ProcessorCount
    ));

    /// <summary>The thresholds for the machine this process runs on (<see cref="For"/> of its installed memory and
    /// logical processors), read once.</summary>
    public static HostLoadThresholds ThisMachine => MachineThresholds.Value;

    /// <summary>Returns the thresholds for a machine of a given size. Memory thresholds are fractions of the installed
    /// memory, rounded up to whole gigabytes because firmware reservations make the reported size slightly less than
    /// the nominal one: CAPACITY needs more than five sixteenths of it free, and PRESSURE begins below one eighth. The
    /// CPU threshold leaves idle logical processors for the work being admitted: three, but never fewer than 40% of the
    /// machine nor more than half of it. Free disk under <see cref="DiskPressureGb"/> is pressure on every machine. A
    /// 16 GB, six-thread machine gets CPU below 50%, more than 5 GB free and pressure under 2 GB; a 32 GB, 16-thread
    /// machine gets CPU below 60%, more than 10 GB free and pressure under 4 GB. Capacity also needs the commit charge
    /// under <see cref="CommitCapacityPercent"/> of the commit limit on every machine.</summary>
    /// <param name="installedRamGb">The installed physical memory, in gigabytes.</param>
    /// <param name="logicalProcessors">The logical processors.</param>
    /// <returns>The thresholds.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="installedRamGb"/> is not a positive finite number,
    /// or <paramref name="logicalProcessors"/> is less than one.</exception>
    public static HostLoadThresholds For(double installedRamGb, int logicalProcessors) {
        if (!double.IsFinite(d: installedRamGb) || (installedRamGb <= 0)) {
            throw new ArgumentOutOfRangeException(actualValue: installedRamGb, message: "The installed memory must be a positive finite number of gigabytes.", paramName: nameof(installedRamGb));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(other: 1, value: logicalProcessors);

        var installed = Math.Ceiling(a: installedRamGb);
        var idle = Math.Clamp(max: (0.5 * logicalProcessors), min: (0.4 * logicalProcessors), value: 3.0);

        return new HostLoadThresholds(
            CapacityCommitPercent: CommitCapacityPercent,
            CapacityCpuPercent: Math.Round(digits: 1, value: (100.0 * (1.0 - (idle / logicalProcessors)))),
            CapacityRamGb: ((installed * 5.0) / 16.0),
            PressureDiskGb: DiskPressureGb,
            PressureRamGb: (installed / 8.0)
        );
    }
}
/// <summary>
/// Turns a stream of <see cref="HostSample"/>s into the lines an agent admits or holds work by. It reads nothing itself,
/// so every rule is a law over injected samples:
/// <list type="bullet">
/// <item><c>GPU busy (&lt;holder&gt;) …</c> or <c>GPU idle …</c> once at the first sample and on every change after;</item>
/// <item><c>PRESSURE &lt;why&gt; …</c> when free memory or disk falls under its pressure threshold, and again when the
/// reasons change;</item>
/// <item><c>CAPACITY …</c> when, without pressure, the CPU mean over the last <c>cpuSamples</c> readings falls under its
/// threshold, free memory is over its own and the commit charge is under its share of the commit limit, once that many
/// readings exist;</item>
/// <item><c>LOADED …</c> when, without pressure, that window shows no capacity, so a watcher sees capacity end.</item>
/// </list>
/// One line per transition: a state that holds prints nothing more. PRESSURE wins over CAPACITY within one reading.
/// </summary>
public sealed class HostLoadMonitor(HostLoadThresholds thresholds, int cpuSamples) {
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
        var commit = (double.IsNaN(d: sample.CommitPercent) ? string.Empty : $" commit={sample.CommitPercent.ToString(format: "0", provider: CultureInfo.InvariantCulture)}%");
        var values = $"cpu={mean.ToString(format: "0", provider: CultureInfo.InvariantCulture)}% freeRAM={Number(value: sample.FreeRamGb)}GB freeDisk={Number(value: sample.FreeDiskGb)}GB{commit} reuseNodes={sample.ReuseNodes.ToString(provider: CultureInfo.InvariantCulture)}";
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
            (m_cpu.Count == cpuSamples) && (mean < cpu) && (sample.FreeRamGb > free) && thresholds.CommitHasHeadroom(reading: sample));

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
