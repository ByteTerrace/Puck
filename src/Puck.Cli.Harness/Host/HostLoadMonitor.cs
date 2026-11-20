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
/// <param name="Ready">Whether the process has reported steady state (<see cref="WorldReadiness"/>), so it has no growth
/// left to reserve.</param>
/// <param name="StartTicks">The process's UTC start ticks, which with <paramref name="Id"/> identify it: a recycled id is
/// another process.</param>
/// <param name="WorkingSetGb">The process's working set, in gigabytes: every page it holds resident now, the clean shareable
/// pages of mapped files and images included; NaN when the reading does not report it.</param>
/// <param name="PrivateResidentGb">The process's private working set, in gigabytes
/// (<see cref="Puck.Hosting.HostMemory.PrivateResidentBytes"/>): the resident pages of its private regions, a graphics driver's
/// write-combined host views included, without the mapped files and images the system shares and reclaims; NaN when the
/// reading does not report it.</param>
/// <param name="CpuSeconds">The CPU time the process has used in total, in seconds; NaN when the reading does not report it,
/// which never counts the process as idle.</param>
/// <param name="IdleSince">When the process was last seen using CPU, from the idle mark another observer recorded
/// (<see cref="StartIdleness"/>), or <see langword="null"/> when none marks it idle.</param>
public readonly record struct HostRunningStart(int Id, HostStartKind Kind, double PrivateGb, bool Ready = false, long StartTicks = 0, double WorkingSetGb = double.NaN, double PrivateResidentGb = double.NaN, double CpuSeconds = double.NaN, DateTimeOffset? IdleSince = null) {
    /// <summary>Gets a phrase for a reading to append when the process is idle, such as <c> idle since 12:03:00Z</c>; empty
    /// when none marks it idle.</summary>
    public string IdleNote => ((IdleSince is { } since) ? string.Create(provider: CultureInfo.InvariantCulture, handler: $" idle since {since.UtcDateTime:HH:mm:ss}Z") : string.Empty);
    /// <summary>Gets the physical memory the process needs resident now, in gigabytes, the quantity admission judges against
    /// free memory: its private working set; its whole working set when the reading carries no private working set; its
    /// private bytes when it carries neither. Each stand-in is never less than the quantity it replaces, so a reading that
    /// cannot split residency judges conservatively.</summary>
    public double ResidentGb => (double.IsFinite(d: PrivateResidentGb) ? PrivateResidentGb : (double.IsFinite(d: WorkingSetGb) ? WorkingSetGb : PrivateGb));
    /// <summary>Gets the process's whole working set, in gigabytes, for reporting: <see cref="WorkingSetGb"/>, or
    /// <see cref="ResidentGb"/> when the reading carries none.</summary>
    public double HeldGb => (double.IsFinite(d: WorkingSetGb) ? Math.Max(val1: WorkingSetGb, val2: ResidentGb) : ResidentGb);
}
/// <summary>The thresholds that turn readings into admission lines. A threshold left out is never judged, so with
/// none the monitor reports only GPU transitions. A start's need is its own expected peak (<see cref="HostStartTracker"/>),
/// judged in two quantities that are not the same: its private bytes (commit) against the commit headroom less a reserve
/// (<see cref="CommitCovers"/>), and its private working set (the residency it cannot give back without paging) against free
/// memory above the pressure floor (<see cref="PhysicalCovers"/>). No fixed amount of free memory or of commit gates a
/// start.</summary>
/// <param name="CapacityCpuPercent">CAPACITY needs the CPU mean below this.</param>
/// <param name="PressureRamGb">PRESSURE when free memory is below this; a start must also leave this much free once its
/// own private working set and the growth of the starts already running are taken.</param>
/// <param name="PressureDiskGb">PRESSURE when free disk is below this.</param>
/// <param name="CommitReservePercent">The share of the commit limit, in percent, that stays unclaimed after a start's need:
/// the margin for processes admission does not see (a build, a hand-started suite). The commit headroom itself
/// below this share is commit pressure and holds every start back. Not judged when the platform reports no commit limit.</param>
/// <param name="InstalledRamGb">The machine's installed physical memory, in gigabytes, which no start's private working set
/// plus the pressure floor can exceed (<see cref="NeverFits"/>); not judged when null.</param>
public sealed record HostLoadThresholds(double? CapacityCpuPercent, double? PressureRamGb, double? PressureDiskGb, double? CommitReservePercent = null, double? InstalledRamGb = null) {
    /// <summary>The commit reserve, in percent of the commit limit: a start is admitted only if the headroom still holds
    /// this much after its need. Allocation fails at the limit with physical memory still free; builds, compilers and
    /// hand-started suites charge commit that admission does not predict, and a tenth of the limit (6.4 GB of a 64 GB
    /// limit) absorbs a typical build.</summary>
    public const double DefaultCommitReservePercent = 10;
    /// <summary>The share of a kind's expected commit peak assumed privately resident until this machine has measured the
    /// kind's private working set: committed memory a process has not touched holds no physical page, and a driver that
    /// mirrors device memory into the process charges commit for it with no physical page at all (a windowed World read
    /// 14 GB resident of 20.4 GB private).</summary>
    public const double ResidentFraction = 0.7;

    /// <summary>Gets the commit, in gigabytes, the reserve keeps unclaimed in a reading.</summary>
    /// <param name="reading">The reading, whose commit limit the reserve is a share of.</param>
    /// <returns>The reserve, or zero when none is judged or the platform reports no commit limit.</returns>
    public double CommitReserveGb(HostSample reading) => (((CommitReservePercent is { } percent) && double.IsFinite(d: reading.CommitLimitGb)) ? (reading.CommitLimitGb * percent / 100.0) : 0);

    /// <summary>Gets whether a reading's commit headroom is at or above the reserve: below it every start is held back (commit
    /// pressure). A threshold left out, or a platform that reports no commit limit, never holds a reading back.</summary>
    /// <param name="reading">The reading.</param>
    /// <returns><see langword="true"/> when the headroom holds the reserve or is not judged.</returns>
    public bool CommitHasHeadroom(HostSample reading) => ((CommitReservePercent is null) || !double.IsFinite(d: reading.CommitLimitGb) || !double.IsFinite(d: reading.CommitChargeGb) || ((reading.CommitLimitGb - reading.CommitChargeGb) >= CommitReserveGb(reading: reading)));
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

    /// <summary>Returns the fallback peak before this machine has measured a plateaued or finished start of the kind: the
    /// kind's reading on a large machine, held to the host budget a Puck process's account derives on a machine of
    /// <paramref name="installedRamGb"/> (<see cref="Puck.Hosting.Jobs.MemoryAccount.DefaultBudgetOf"/>). A World sizes its
    /// caches and pools from that budget, so on a 16 GB machine it plans for 12 GB and ran at 8 to 10 GB private where a
    /// 64 GB machine reads 12 to 18 GB; judging the small machine by the large one's figure refuses a start that fits.</summary>
    /// <param name="kind">The start's kind.</param>
    /// <param name="installedRamGb">The machine's installed memory, in gigabytes, or <see langword="null"/> to judge by the
    /// kind's figure alone.</param>
    /// <returns>The default expected peak, in gigabytes.</returns>
    public static double DefaultPeakGb(HostStartKind kind, double? installedRamGb) {
        var peak = kind switch {
            HostStartKind.WindowedWorld => WindowedWorldPeakGb,
            HostStartKind.HeadlessWorld => HeadlessWorldPeakGb,
            HostStartKind.HeavySuite => HeavySuitePeakGb,
            HostStartKind.ReproduceReplica => ReproduceReplicaPeakGb,
            _ => StepPeakGb,
        };

        return ((installedRamGb is { } installed && double.IsFinite(d: installed) && (installed > 0))
            ? Math.Min(val1: peak, val2: (Puck.Hosting.Jobs.MemoryAccount.DefaultBudgetOf(installedPhysicalBytes: (long)(installed * 1073741824.0)) / 1073741824.0))
            : peak);
    }
    /// <summary>Gets whether a reading's commit headroom (the limit less the charge) covers <paramref name="needGb"/> and
    /// still holds the commit reserve afterwards. A threshold left out, or a platform that reports no commit limit, never
    /// holds a reading back.</summary>
    /// <param name="reading">The reading.</param>
    /// <param name="needGb">The commit, in gigabytes, the start and the growth still to come need (private bytes).</param>
    /// <returns><see langword="true"/> when the headroom covers the need and the reserve, or is not judged.</returns>
    public bool CommitCovers(HostSample reading, double needGb) => ((CommitReservePercent is null) || !double.IsFinite(d: reading.CommitLimitGb) || !double.IsFinite(d: reading.CommitChargeGb) || ((reading.CommitLimitGb - reading.CommitChargeGb - needGb) >= CommitReserveGb(reading: reading)));
    /// <summary>Gets whether a start that needs <paramref name="needGb"/> of physical memory (its own expected private
    /// working set plus the private working-set growth the running starts have left) still leaves free memory at or above
    /// <see cref="PressureRamGb"/>. Free memory
    /// here is what the operating system can hand out without writing anything to disk (Windows
    /// <c>ullAvailPhys</c>, the sum of the standby, free and zero lists; Linux <c>MemAvailable</c>), so reclaimable cache
    /// already counts as free. Commit is the hard limit (allocation fails at it); physical memory short of the start
    /// makes every process page, which is why this leaves the pressure floor instead of demanding the start's whole
    /// need in a fixed free amount. The need is a private working set, neither private bytes nor the whole working set:
    /// committed memory a process never touches holds no physical page, so private bytes overstate residency, and the clean
    /// pages of mapped files and images in a working set are shared and handed back to the standby list under pressure,
    /// where free memory already counts them. A threshold left out, or a platform that reports no free memory, never holds
    /// a reading back.</summary>
    /// <param name="reading">The reading.</param>
    /// <param name="needGb">The physical memory, in gigabytes, the start and the growth still to come take from free memory.</param>
    /// <returns><see langword="true"/> when the start leaves the pressure floor free or is not judged.</returns>
    public bool PhysicalCovers(HostSample reading, double needGb) => ((PressureRamGb is not { } floor) || !double.IsFinite(d: reading.FreeRamGb) || ((reading.FreeRamGb - needGb) >= floor));
    /// <summary>Names what keeps a start from ever being admitted on this machine however idle it is: its own expected
    /// private bytes plus the commit reserve exceed the commit limit, or its own expected private working set plus the
    /// pressure floor exceed the installed memory. Waiting cannot help such a start.</summary>
    /// <param name="reading">The reading, whose commit limit is the machine's.</param>
    /// <param name="ownGb">The start's own expected commit peak, in gigabytes.</param>
    /// <param name="residentOwnGb">The start's own expected private working-set peak, in gigabytes.</param>
    /// <returns>The shortfall as a phrase, or <see langword="null"/> when the machine could hold the start alone or does
    /// not report its totals.</returns>
    public string? NeverFits(HostSample reading, double ownGb, double residentOwnGb) {
        if ((CommitReservePercent is not null) && double.IsFinite(d: reading.CommitLimitGb) && ((ownGb + CommitReserveGb(reading: reading)) > reading.CommitLimitGb)) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"it expects {ownGb:0.0}GB of commit and the commit reserve is {CommitReserveGb(reading: reading):0.0}GB, over the machine's {reading.CommitLimitGb:0.0}GB commit limit");
        }
        if ((PressureRamGb is { } floor) && (InstalledRamGb is { } installed) && ((residentOwnGb + floor) > installed)) {
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"it expects {residentOwnGb:0.0}GB of private working set and the pressure floor is {floor:0.0}GB, over the machine's {installed:0.0}GB of memory");
        }

        return null;
    }
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

    /// <summary>Returns the thresholds for a machine of a given size. The memory threshold is a fraction of the installed
    /// memory, rounded up to whole gigabytes because firmware reservations make the reported size slightly less than
    /// the nominal one: PRESSURE begins when free memory is below one eighth of it, and a start must leave that much
    /// free (<see cref="PhysicalCovers"/>). The CPU threshold leaves idle logical processors for the work being
    /// admitted: three, but never fewer than 40% of the machine nor more than half of it. Free disk under
    /// <see cref="DiskPressureGb"/> is pressure on every machine. A 16 GB, six-thread machine gets CPU below 50% and
    /// pressure under 2 GB; a 32 GB, 16-thread machine gets CPU below 60% and pressure under 4 GB. Every machine keeps
    /// <see cref="DefaultCommitReservePercent"/> of its commit limit unclaimed.</summary>
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
            CapacityCpuPercent: Math.Round(digits: 1, value: (100.0 * (1.0 - (idle / logicalProcessors)))),
            CommitReservePercent: DefaultCommitReservePercent,
            InstalledRamGb: installed,
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
/// threshold and the commit headroom holds its reserve, once that many readings exist (what a start
/// itself needs of memory is judged per start by <see cref="HostStartTracker"/>, not here);</item>
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
    /// <summary>Gets the latest reading's admission state (<c>PRESSURE</c> with its reasons, <c>CAPACITY</c> or
    /// <c>LOADED</c>), or <see langword="null"/> before capacity can be judged.</summary>
    public string? Admission => m_admission;
    /// <summary>Gets the CPU mean, in percent, over the readings the window holds.</summary>
    public double CpuMean => ((m_cpu.Count == 0) ? double.NaN : m_cpu.Average());

    /// <summary>What capacity is waiting on, including an incomplete CPU window; null only with capacity.</summary>
    public string? CapacityWait { get; private set; } = "collecting CPU samples";

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

        // Capacity and its explanation come from the same decision, so quiet cannot call a cold window loaded
        // or blame an instantaneous CPU reading when it is the rolling mean that fails.
        var waits = new List<string>(pressure);
        if (thresholds.CapacityCpuPercent is not { } cpu) {
            waits.Add("capacity thresholds unavailable");
        } else {
            if (m_cpu.Count < cpuSamples) { waits.Add($"collecting CPU samples: {m_cpu.Count} of {cpuSamples}"); }
            else if (!(mean < cpu)) { waits.Add($"cpu mean={Number(mean)}% (needs under {Number(cpu)}%)"); }
            if (!thresholds.CommitHasHeadroom(sample)) { waits.Add($"commit={Number(sample.CommitPercent)}% (needs {thresholds.CommitReservePercent}% of the limit unclaimed)"); }
        }
        HasCapacity = waits.Count == 0;
        CapacityWait = HasCapacity ? null : string.Join("; ", waits);

        // Capacity is judged only with its CPU threshold and a full window; until then a reading without pressure has
        // no admission state, and the next one that does prints.
        var judged = ((thresholds.CapacityCpuPercent is not null) && (m_cpu.Count == cpuSamples));
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
