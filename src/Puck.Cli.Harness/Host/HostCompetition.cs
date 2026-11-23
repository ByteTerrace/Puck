using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli.Host;

/// <summary>One program's share of the CPU other processes used in a window.</summary>
/// <param name="Name">The process name, without extension; every process of that name is one entry.</param>
/// <param name="Processes">How many processes of the name used CPU in the window.</param>
/// <param name="CpuSeconds">The CPU seconds they used in it.</param>
public sealed record HostCompetitor(string Name, int Processes, double CpuSeconds);

/// <summary>
/// What the rest of the machine did while a measurement ran: the CPU every process outside the measurement's own tree used,
/// the programs that used most, and whether another World or a lease holder was live, with the verdict on it.
/// </summary>
/// <param name="WallSeconds">The window's length.</param>
/// <param name="LogicalProcessors">The machine's logical processors.</param>
/// <param name="OthersCpuSeconds">The CPU seconds the machine spent outside the measurement's own tree.</param>
/// <param name="OthersShare">That time as a share of the machine's CPU capacity over the window, 0 to 1.</param>
/// <param name="OthersCores">That time as logical processors kept busy over the window.</param>
/// <param name="OwnCpuSeconds">The CPU seconds the measurement's own tree used.</param>
/// <param name="AttributedSeconds">The part of <paramref name="OthersCpuSeconds"/> a named process accounts for; the rest is
/// kernel and interrupt time, a process that exited between readings and one the system will not let this process read.</param>
/// <param name="Top">The programs that used most, most first.</param>
/// <param name="Worlds">Every other World seen running, by name and process id.</param>
/// <param name="LeaseHolders">Every other holder of the gpu or heavy lease seen, by holder and lease.</param>
/// <param name="Readings">How many process readings the window took.</param>
/// <param name="Disturbed">Why the window is not a quiet one; empty when it is.</param>
public sealed record HostCompetitionReport(double WallSeconds, int LogicalProcessors, double OthersCpuSeconds, double OthersShare, double OthersCores, double OwnCpuSeconds, double AttributedSeconds, IReadOnlyList<HostCompetitor> Top, IReadOnlyList<string> Worlds, IReadOnlyList<string> LeaseHolders, int Readings, IReadOnlyList<string> Disturbed);

/// <summary>
/// Records what competes with a measurement for the machine, from the operating system's own counters and never from what a
/// lease claims: it reads the machine's total CPU time and, every <see cref="Interval"/>, each process's, and reports as
/// competition everything outside the process tree of the one that started it. The total catches CPU a process spent and
/// exited between two readings, the per-process times name who spent the rest, and the process list shows any other World,
/// whether or not it holds a lease. A reading walks the process list, so the interval stays coarse and the recorder's own
/// cost stays well under one percent of a core.
/// </summary>
public sealed class HostCompetitionRecorder : IDisposable {
    /// <summary>The time between readings.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 3);
    /// <summary>The share of the machine's CPU capacity other processes may use before the window is disturbed: a build or a
    /// second World keeps one or more cores busy for most of a run, which is a share of this size on an eight-thread machine,
    /// while a quiet desktop's background (the system, the compositor, a driver, a scanner) stays well under one core.</summary>
    public const double DisturbedShare = 0.08;
    /// <summary>The cores other processes may keep busy before the window is disturbed whatever the machine's size: on a
    /// machine with many threads a build is a small share of the whole and still takes cores from the World's workers.</summary>
    public const double DisturbedCores = 1.0;
    /// <summary>How many programs the report names.</summary>
    public const int TopCount = 5;

    private sealed class Track(string name, DateTime started, double cpu, bool born, Process? handle) {
        public string Name { get; } = name;
        public DateTime Started { get; } = started;
        // The CPU the process had used when first seen, which counts toward the window unless the process began inside it.
        public double Baseline { get; } = (born ? 0.0 : cpu);
        public double Last { get; set; } = cpu;
        public Process? Handle { get; } = handle;
        public double Used => Math.Max(val1: 0.0, val2: (Last - Baseline));
    }

    private readonly Dictionary<(int Id, long Started), Track> m_others = [];
    private readonly Dictionary<(int Id, long Started), Track> m_own = [];
    private readonly HashSet<string> m_worlds = new(comparer: StringComparer.Ordinal);
    // The processes whose command line has been read, so a dotnet process that is not a World is read once.
    private readonly HashSet<(int Id, long Started)> m_classified = [];
    private readonly HashSet<string> m_leases = new(comparer: StringComparer.Ordinal);
    private readonly HostProbe m_probe = new(checkoutRoot: Environment.CurrentDirectory);
    private readonly Timer m_timer;
    private readonly Lock m_gate = new();
    private readonly long m_begun = Stopwatch.GetTimestamp();
    private readonly DateTime m_began = DateTime.UtcNow;
    private (ulong Idle, ulong Total)? m_first;
    private (ulong Idle, ulong Total)? m_last;
    private int m_readings;
    private bool m_finished;

    /// <summary>Initializes a new instance of the <see cref="HostCompetitionRecorder"/> class, takes the window's first
    /// reading and starts reading every <see cref="Interval"/>.</summary>
    public HostCompetitionRecorder() {
        Read();
        m_timer = new Timer(callback: _ => Read(), dueTime: Interval, period: Interval, state: null);
    }

    // Reads every process once. The own tree is this process and what descends from it (the World a verb starts), whose
    // handles are kept so its CPU time can still be read after it exits.
    private void Read() {
        lock (m_gate) {
            if (m_finished) { return; }

            var times = HostProbe.CpuTimes();

            m_first ??= times;
            m_last = times ?? m_last;
            m_readings++;

            var processes = Process.GetProcesses();
            var kept = new HashSet<Process>(comparer: ReferenceEqualityComparer.Instance);

            try {
                var tree = new Dictionary<int, (int Parent, DateTime Started)>();
                var started = new Dictionary<int, DateTime>();

                foreach (var process in processes) {
                    var begun = HostProbe.StartOf(process: process);

                    started[process.Id] = begun;
                    tree[process.Id] = (ProcessInspection.ParentId(processId: process.Id), begun);
                }

                var self = Environment.ProcessId;
                var own = new HashSet<int>(collection: HostProcesses.Descendants(processes: tree, root: self)) { self };

                foreach (var process in processes) {
                    var begun = started[process.Id];
                    var key = (process.Id, begun.ToUniversalTime().Ticks);

                    if (own.Contains(item: process.Id)) {
                        if (Account(tracks: m_own, key: key, process: process, begun: begun, keep: true)) { _ = kept.Add(item: process); }
                    } else {
                        _ = Account(tracks: m_others, key: key, process: process, begun: begun, keep: false);
                        NoteWorld(process: process, key: key);
                    }
                }

                NoteLeases(own: own);
            } finally {
                foreach (var process in processes.Where(predicate: process => !kept.Contains(item: process))) { process.Dispose(); }
            }
        }
    }

    // Folds one process's CPU time into its track. A process the system will not let this one read has no track, and what it
    // used is part of the unattributed remainder. An own process's handle is kept: the World a run starts has exited by the
    // time the window closes.
    // Returns whether the track took the process object as its handle.
    private bool Account(Dictionary<(int Id, long Started), Track> tracks, (int Id, long Started) key, Process process, DateTime begun, bool keep) {
        var cpu = HostProbe.CpuSeconds(process: process);

        if (!double.IsFinite(d: cpu)) { return false; }
        if (tracks.TryGetValue(key: key, value: out var track)) {
            track.Last = cpu;

            return false;
        }

        tracks[key] = new Track(name: process.ProcessName, started: begun, cpu: cpu, born: (begun.ToUniversalTime() >= m_began), handle: (keep ? process : null));

        return keep;
    }

    // Names a process that is another World: the apphost, or dotnet running Puck.World.dll, by the same classification the
    // host admission uses. Only those two names pay the command-line read.
    private void NoteWorld(Process process, (int Id, long Started) key) {
        var name = process.ProcessName;

        if (!name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.World") && !name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "dotnet")) { return; }
        if (!m_classified.Add(item: key)) { return; }

        var kind = HostProcesses.RunningStart(commandLine: ProcessInspection.CommandLine(processId: process.Id), name: name);

        if (kind is { } started && (started != HostStartKind.HeavySuite)) {
            _ = m_worlds.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{name} {(started == HostStartKind.WindowedWorld ? "(windowed World)" : "(World)")} {key.Id}"));
        }
    }

    // Names the other holders of the gpu and heavy leases whose guard the operating system shows held. A hold inside the
    // measurement's own tree is its own.
    private void NoteLeases(HashSet<int> own) {
        if (m_probe.LeaseRoot is not { } root) { return; }

        try {
            foreach (var hold in CliLease.AdmittedHolds(now: DateTimeOffset.UtcNow, root: root)) {
                if (!own.Contains(item: hold.ProcessId) && !ProcessInspection.IsAncestor(pid: hold.ProcessId, ticks: hold.StartTicks)) {
                    _ = m_leases.Add(item: $"{hold.Holder} ({hold.Lease} lease, process {hold.ProcessId.ToString(provider: CultureInfo.InvariantCulture)})");
                }
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Takes a last reading, stops reading and judges the window.</summary>
    /// <returns>What the rest of the machine did across the window.</returns>
    public HostCompetitionReport Finish() {
        Read();
        m_timer.Dispose();

        lock (m_gate) {
            m_finished = true;

            var wall = Stopwatch.GetElapsedTime(startingTimestamp: m_begun).TotalSeconds;
            var cpus = Environment.ProcessorCount;
            var own = 0.0;

            foreach (var track in m_own.Values) {
                if ((track.Handle is { } handle) && (HostProbe.CpuSeconds(process: handle) is var read) && double.IsFinite(d: read)) { track.Last = Math.Max(val1: track.Last, val2: read); }

                own += track.Used;
                track.Handle?.Dispose();
            }

            var busy = 0.0;
            var capacity = 0.0;

            if ((m_first is { } first) && (m_last is { } last) && (last.Total > first.Total) && (last.Idle >= first.Idle)) {
                busy = ((last.Total - first.Total) - (last.Idle - first.Idle)) / HostProbe.CpuTicksPerSecond;
                capacity = ((last.Total - first.Total) / HostProbe.CpuTicksPerSecond);
            }

            // The machine's busy time less the measurement's own is the competition, with the kernel and interrupt time the
            // measurement's work causes, which no process owns.
            var others = Math.Max(val1: 0.0, val2: (busy - own));
            var attributed = m_others.Values.Sum(selector: static track => track.Used);
            var top = m_others.Values.Where(predicate: static track => (track.Used > 0.0))
                .GroupBy(keySelector: static track => track.Name, comparer: StringComparer.OrdinalIgnoreCase)
                .Select(selector: static group => new HostCompetitor(Name: group.Key, Processes: group.Count(), CpuSeconds: group.Sum(selector: static track => track.Used)))
                .OrderByDescending(keySelector: static competitor => competitor.CpuSeconds).Take(count: TopCount).ToArray();
            var share = ((capacity > 0.0) ? (others / capacity) : double.NaN);
            var cores = ((wall > 0.0) ? (others / wall) : double.NaN);
            var worlds = m_worlds.Order(comparer: StringComparer.Ordinal).ToArray();
            var disturbed = new List<string>();

            if ((share > DisturbedShare) || (cores > DisturbedCores)) {
                disturbed.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"competing load: other processes used {others:0.0} CPU s in {wall:0} s ({share:P0} of the machine, {cores:0.0} of {cpus} cores), led by {(top.Length == 0 ? "no named process" : string.Join(separator: ", ", values: top.Take(count: 3).Select(selector: static competitor => $"{competitor.Name} x{competitor.Processes} {competitor.CpuSeconds:0.0} s")))}"));
            }
            if (worlds.Length > 0) {
                disturbed.Add(item: $"another World was live: {string.Join(separator: ", ", values: worlds)}");
            }

            return new HostCompetitionReport(AttributedSeconds: attributed, Disturbed: disturbed, LeaseHolders: [.. m_leases.Order(comparer: StringComparer.Ordinal)], LogicalProcessors: cpus, OthersCores: cores, OthersCpuSeconds: others, OthersShare: share, OwnCpuSeconds: own, Readings: m_readings, Top: top, WallSeconds: wall, Worlds: worlds);
        }
    }

    /// <summary>Stops reading.</summary>
    public void Dispose() {
        m_timer.Dispose();
        lock (m_gate) {
            m_finished = true;
            foreach (var track in m_own.Values) { track.Handle?.Dispose(); }
            m_own.Clear();
        }
    }
}
