using System.ComponentModel;
using System.Diagnostics;
using Puck.Confinement;
using Puck.Abstractions;

namespace Puck.Cli.Host;

/// <summary>What a start needs of the machine's memory, as <see cref="HostStartTracker.Judge"/> reads it, in two
/// quantities: private bytes (commit) against the commit headroom less its reserve, and private working set (the residency
/// it cannot give back without paging) against free physical memory above the pressure floor. The whole working set is
/// reported beside it, so a refusal shows how much of a start's residency is shareable file pages the system reclaims.</summary>
/// <param name="CommitCovered">Whether the commit headroom covers <paramref name="NeedGb"/> and the commit reserve, or is
/// not judged.</param>
/// <param name="PhysicalCovered">Whether free physical memory less <paramref name="ResidentNeedGb"/> stays at or above the
/// pressure floor (<see cref="HostLoadThresholds.PhysicalCovers"/>), or is not judged.</param>
/// <param name="HeadroomGb">The commit limit less the charge, in gigabytes; NaN when the platform reports none.</param>
/// <param name="FreeRamGb">The free physical memory the reading shows, in gigabytes; NaN when the platform reports none.</param>
/// <param name="NeedGb">The start's own expected commit peak plus <paramref name="GrowthGb"/>, in gigabytes.</param>
/// <param name="OwnGb">The start's own expected commit peak (private bytes), in gigabytes.</param>
/// <param name="GrowthGb">What the starts already admitted still have to grow in private bytes to reach their expected
/// peaks, in gigabytes.</param>
/// <param name="ResidentNeedGb">The start's own expected private working-set peak plus <paramref name="ResidentGrowthGb"/>,
/// in gigabytes.</param>
/// <param name="ResidentOwnGb">The start's own expected private working-set peak, in gigabytes.</param>
/// <param name="ResidentGrowthGb">What the starts already admitted still have to grow in private working set to reach their
/// expected peaks, in gigabytes.</param>
/// <param name="Growing">How many admitted starts are still below their expected commit peak.</param>
/// <param name="Kind">The start's kind.</param>
/// <param name="WorkingSetOwnGb">The start's own expected whole working-set peak, in gigabytes, which admission reports and
/// never judges; NaN before the profile has an observation.</param>
/// <param name="LoneOwnGb">The least commit, in gigabytes, the start is admitted at on an idle machine: for a measured
/// profile the lesser of its expected commit peak and the kind's fallback, which <paramref name="OwnGb"/> is when the
/// expected peak exceeds what the machine could hold with no admitted start running; for a profile this machine has not
/// measured, its expected private working set, which a start alone is admitted at, except a windowed World with a cold store,
/// which is judged as a measured profile is. A start whose lone need the machine
/// could never hold is refused by name (<see cref="HostLoadThresholds.NeverFits"/>).</param>
/// <param name="LoneResidentOwnGb">The lone need in private working set, in gigabytes.</param>
/// <param name="Profile">The profile the start was judged by, or <see langword="null"/> for its kind alone.</param>
/// <param name="Measured">Whether this machine has observed a start of the profile, its document included; an unmeasured
/// start is judged by what any World of its kind reached here, else by the fallback.</param>
/// <param name="ExpectedOwnGb">The start's expected commit peak, in gigabytes, before any lone figure replaces it:
/// <paramref name="OwnGb"/> is less than this when the start is judged by a lone figure.</param>
public readonly record struct HostStartDemand(bool CommitCovered, bool PhysicalCovered, double HeadroomGb, double FreeRamGb, double NeedGb, double OwnGb, double GrowthGb, double ResidentNeedGb, double ResidentOwnGb, double ResidentGrowthGb, int Growing, HostStartKind Kind, double WorkingSetOwnGb = double.NaN, double LoneOwnGb = double.NaN, double LoneResidentOwnGb = double.NaN, HostStartProfile? Profile = null, bool Measured = true, double ExpectedOwnGb = double.NaN) {
    /// <summary>Gets whether the machine can hold the start: both the commit headroom and free physical memory cover it.</summary>
    public bool Covered => (CommitCovered && PhysicalCovered);
}

/// <summary>
/// The single memory-demand calculation: a start's measured peak plus the running starts' remaining growth, in private
/// bytes for the commit headroom and in private working set for free physical memory (above the pressure floor). Private
/// bytes are commit and overstate what a process holds resident (a driver's mirror of device memory charges commit and holds
/// no page); a whole working set overstates what it needs resident (the clean pages of mapped files and images are shared and
/// reclaimed under pressure); so commit is judged by private bytes and free memory by the private working set
/// (<see cref="HostRunningStart.ResidentGb"/>).
/// A start is judged by its own profile (<see cref="HostStartProfile"/>): its kind and, for a windowed World, the backend it
/// boots on, whether its mesh store is warm or cold and the world document it boots, so the cold bakes' far larger peak never
/// sets what a warm session must find free and a small world is never judged by the debug world. The profile of a running
/// process is the one its launch recorded (<see cref="StartProfiles"/>). Every observation of a document's start is also one
/// of its kind, backend and store with no document (<see cref="HostStartProfile.AnyDocument"/>), which judges a document this
/// machine has not measured and a start that names none.
/// Before a profile has an observation it uses <see cref="HostLoadThresholds.DefaultPeakGb"/> and, for its private working set,
/// <see cref="HostLoadThresholds.ResidentFraction"/> of its expected commit peak. After, a profile's expected peak in each
/// quantity is the high percentile of its recent distribution (<see cref="HostPeakWindow"/>: the last ten processes of the
/// profile, each observed at a plateau or for a full plateau window), so one outlier session raises it only until a few
/// ordinary starts follow, a sustained rise is learned, and the estimate moves down as readily as up. The windows are shared
/// between CLI processes under the lease root (<see cref="ObservationDirectory"/>), where the file decides: a long-lived
/// process adds what it observed since its last write and keeps nothing else of its own. A live process above the
/// estimate is still counted at its own usage, without clipping it. Growth tapers to zero over <see cref="Plateau"/> without
/// a material allocation and returns when growth resumes. A World that reported ready reserves no growth, and neither does an
/// idle start, one that used under <see cref="BusyCpuSeconds"/> of CPU over a <see cref="Plateau"/> (a hung test run, whose
/// private bytes may still move a little): it holds what it holds now and nothing more is expected of it, so one that never
/// finishes cannot hold the machine's queue. Every tracker that sees the window pass marks the start idle for the machine
/// (<see cref="StartIdleness"/>), and the probe stops naming it as another process's heavy test run. The system commit
/// charge already includes live usage. A kind whose expected need alone exceeds the machine is named by
/// <see cref="HostLoadThresholds.NeverFits"/>, not waited on. An estimate the machine could not hold even with every admitted
/// start gone (its commit against the headroom plus the live starts' private bytes, its private working set against free
/// memory plus theirs) judges the start by the lesser of itself and the kind's fallback instead
/// (<see cref="HostStartDemand.LoneOwnGb"/>): no wait for an admitted start would admit it, and a refused kind runs nothing
/// that could lower the estimate, so one outlier session would otherwise lock its kind out for good. A profile with no
/// observation of its own is held to the same rule: its figure is a guess, so its private working set is also held to the
/// free memory the idle machine leaves above the pressure floor (<see cref="HostLoadThresholds.PhysicalRoomGb"/>), and a start
/// of it alone (no other Puck process holding commit, admitted or still growing) is judged in commit by its expected private
/// working set, the least commit it can need, and reserves its whole guess; so the first start of the profile runs and records
/// what it reached when it stops (<see cref="Finish(Process, WorldReadiness)"/>), and only a machine whose commit limit could
/// not hold even that is refused. A windowed World with a cold store is never lowered so: it is judged as a measured profile
/// is, by its cold figure (this machine's cold window of its document, else of any document, else the cold fallback), since
/// a bake of every mesh reaches that figure whatever the first warm session of the profile would have taught.
/// <para>Admitted starts come from the live processes each reading lists (<see cref="HostSample.Starts"/>, classified by
/// <see cref="HostProcesses.RunningStart"/>) with their current private bytes, so a process that exits stops counting at
/// once and nothing is read from a record that can go stale. The one thing a reading cannot show is a start admitted a
/// moment ago whose process has not appeared: this tracker holds that start as a reservation of its full peak until a
/// new live process of its kind appears or <see cref="ReservationHold"/> passes, which is what keeps two starts asked in
/// a row from both taking the same headroom. Pre-process reservations live in this process's memory only,
/// so they cover the starts this process admits (an MCP session, the concurrent workers of one verb). What another process
/// admitted under a lease is read from the lease root instead (<see cref="HostSample.Holds"/>, recorded at the grant under
/// the lease's admission lock): each hold counts its holder's process tree toward the hold's expected peak from the grant
/// on, or the growth of the tracked starts inside the tree when that is more, so two lanes granted one after the other
/// never read the same headroom, and several heavy holders share the machine by what each still has to grow.</para>
/// </summary>
public sealed class HostStartTracker {
    private sealed record Reservation(HostStartKind Kind, long At, double Gb, double ResidentGb, HashSet<(int Id, long StartTicks)> Known);
    // CpuAnchor is the process's CPU time when BusyAt was last set.
    private sealed record Observation(HostStartProfile Profile, HostPeaks Peaks, double AnchorGb, long GrewAt, long FirstAt, double CpuAnchor, long BusyAt);

    /// <summary>Without material growth for this long, a live process reserves no further growth; without CPU use for this
    /// long it is idle.</summary>
    public static readonly TimeSpan Plateau = TimeSpan.FromMinutes(1);
    private const double MaterialGrowthGb = 1.0 / 16;
    /// <summary>The CPU time, in seconds, a process uses to count as busy: under it for a <see cref="Plateau"/> a process is
    /// idle (<see cref="StartIdleness"/>), whatever its private bytes do. A hung process still wakes for a timer or a
    /// collection now and then, which this tolerates.</summary>
    public const double BusyCpuSeconds = 1.0;
    /// <summary>The directory under the lease root that holds each profile's observation window, one file per
    /// <see cref="HostStartProfile.Name"/>. A change to the files' lines or to what their names mean takes a new name and
    /// raises <see cref="CliLease.Protocol"/>: every CLI copy on the machine rewrites these files on each reading, so two
    /// copies that disagree on them must not share one.</summary>
    public const string ObservationDirectory = "host-profiles";
    private readonly string? m_root;

    /// <summary>Creates a tracker, optionally sharing measured peaks in the machine's lease root.</summary>
    /// <param name="leaseRoot">The shared lease root; null keeps observations local for injected readings.</param>
    public HostStartTracker(string? leaseRoot = null) { m_root = leaseRoot; }

    /// <summary>How long an admitted start holds its full expected peak before its live process is expected to show.</summary>
    public static readonly TimeSpan ReservationHold = TimeSpan.FromSeconds(seconds: 90);

    private readonly Lock m_gate = new();
    private readonly Dictionary<HostStartProfile, HostPeakWindow> m_windows = [];
    private readonly List<Reservation> m_reservations = [];
    // Keyed by process id and start ticks: a recycled id is another start, and carries none of the last one's peak.
    private readonly Dictionary<(int Id, long StartTicks), Observation> m_seen = [];
    // The holds other processes admitted, by holder process: an observation of each one's whole process tree.
    private readonly Dictionary<(int Id, long StartTicks), Observation> m_holds = [];

    /// <summary>Gets the tracker of this process's host session, which the admission of a real machine reading uses.</summary>
    public static HostStartTracker Session { get; } = new(leaseRoot: CliLease.RootAsync(Environment.CurrentDirectory).GetAwaiter().GetResult());

    private HostPeakWindow Window(HostStartProfile profile) {
        profile = profile.Class;
        if (!m_windows.TryGetValue(profile, out var window)) {
            window = new HostPeakWindow();
            m_windows[profile] = window;
        }

        return window;
    }
    // Whether this machine has observed a start of the profile itself (its own class, document and all).
    private bool Measured(HostStartProfile kind) => (m_windows.TryGetValue(kind.Class, out var window) && (window.Count > 0));
    // The one expected-peak door: the recent observations' high percentile once the profile has any; for a document this
    // machine has not measured, what any World of its kind, backend and store reached here (AnyDocument); else its fallbacks
    // (the profile's default commit peak, a fixed share of it as the private working set, and no whole working set, which is
    // reported only and has nothing to stand in for it).
    // The fallbacks are held to the machine's size (installedRamGb, the judging thresholds' InstalledRamGb).
    private HostPeaks Expected(HostStartProfile kind, double? installedRamGb) {
        if ((m_windows.TryGetValue(kind.Class, out var window) && (window.Count > 0)) || ((kind.Class.Document is not null) && m_windows.TryGetValue(kind.AnyDocument, out window) && (window.Count > 0))) {
            return new HostPeaks(PrivateGb: window.PrivateGb, ResidentGb: window.ResidentGb, WorkingSetGb: window.WorkingSetGb);
        }

        return Fallback(installedRamGb: installedRamGb, kind: kind);
    }
    private static HostPeaks Fallback(HostStartProfile kind, double? installedRamGb) {
        var peak = HostLoadThresholds.DefaultPeakGb(installedRamGb: installedRamGb, kind: kind);

        return new(PrivateGb: peak, ResidentGb: (HostLoadThresholds.ResidentFraction * peak), WorkingSetGb: double.NaN);
    }
    // What a start is judged by when its expectation exceeds what the machine could hold with no admitted start running: the
    // lesser of that expectation and the fallback an unmeasured profile gets. Such an expectation could otherwise refuse every
    // start of its profile however long it waits, and a refused profile runs nothing that could teach the window a lower
    // figure.
    // A profile this machine has never measured is a guess, and a guess refused with nothing else running would never run, so
    // never be measured. Its private working set is held to what the idle reading leaves above the pressure floor. Alone
    // (no other Puck process holding commit or still growing), its commit is held to its least possible figure, its expected
    // private working set: every privately resident page is committed, so no start of it can need less, and the first start
    // runs and records what it reached. Beside other work commit judges the whole guess, so admitted work never grows into a
    // start that guessed low. A step is never tracked once it runs, so no start of it teaches a lower figure, and commit
    // judges its whole guess alone too. A measured profile is never clamped: its own record says what it needs. Nor is a
    // windowed World with a cold store: its figure (a cold window of this document, else of any document, else the cold
    // fallback) is what a bake of every mesh reaches, which is no guess at a play session, and a cold bake let in at a warm
    // session's least commit exhausts the machine's memory before it can teach the window anything.
    private HostPeaks Lone(HostStartProfile kind, HostLoadThresholds thresholds, HostSample idle, bool alone) {
        var learned = Expected(installedRamGb: thresholds.InstalledRamGb, kind: kind);
        var fallback = Fallback(installedRamGb: thresholds.InstalledRamGb, kind: kind);
        var resident = Math.Min(learned.ResidentGb, fallback.ResidentGb);
        var commit = Math.Min(learned.PrivateGb, fallback.PrivateGb);

        if (Measured(kind: kind) || (kind.Class is { Kind: HostStartKind.WindowedWorld, Store: StoreWarmth.Cold })) { return new HostPeaks(PrivateGb: commit, ResidentGb: resident, WorkingSetGb: learned.WorkingSetGb); }

        return new HostPeaks(PrivateGb: ((alone && (kind.Kind != HostStartKind.Step)) ? Math.Min(commit, resident) : commit), ResidentGb: Math.Min(resident, thresholds.PhysicalRoomGb(reading: idle)), WorkingSetGb: learned.WorkingSetGb);
    }
    // A live process's peaks as one reading shows them.
    private static HostPeaks Reading(HostRunningStart start) => new(PrivateGb: start.PrivateGb, ResidentGb: start.ResidentGb, WorkingSetGb: start.HeldGb);

    // Folds one reading of a start, or of a hold's process tree, into its observation: its profile (which a launch can make
    // cold while the process runs), its peaks, when its private bytes last grew materially, and when it last used CPU. A
    // tree's CPU time falls when a busy child exits; the anchor follows it down without counting the fall as use, so the
    // next child's work is measured from there.
    private static (Observation Seen, bool Busy) Follow(Observation? seen, HostStartProfile kind, HostPeaks peaks, double cpuSeconds, TimeProvider clock) {
        var now = clock.GetTimestamp();

        seen = ((seen ?? new Observation(kind, peaks, peaks.PrivateGb, now, now, cpuSeconds, now)) with { Profile = kind });
        var grew = (peaks.PrivateGb >= (seen.AnchorGb + MaterialGrowthGb));
        var busy = (!double.IsFinite(cpuSeconds) || !double.IsFinite(seen.CpuAnchor) || (cpuSeconds >= (seen.CpuAnchor + BusyCpuSeconds)));

        return (seen with {
            Peaks = seen.Peaks.Max(other: peaks),
            AnchorGb = (grew ? peaks.PrivateGb : Math.Min(peaks.PrivateGb, seen.AnchorGb)),
            GrewAt = (grew ? now : seen.GrewAt),
            CpuAnchor = ((double.IsFinite(cpuSeconds) && (busy || (cpuSeconds < seen.CpuAnchor))) ? cpuSeconds : seen.CpuAnchor),
            BusyAt = (busy ? now : seen.BusyAt),
        }, busy);
    }
    // What an observed start or hold still has to grow, in private bytes and in private working set: toward the greater of
    // its kind's expected peak and its own observed peak, tapering to nothing over a plateau without material growth, and
    // nothing at all once it is idle.
    private static (double Commit, double Resident) Left(Observation seen, HostPeaks expected, double privateGb, double residentGb, bool idle, TimeProvider clock) {
        var remaining = (idle ? 0 : Math.Max(0, (1 - (clock.GetElapsedTime(seen.GrewAt).TotalSeconds / Plateau.TotalSeconds))));

        return (((Math.Max(expected.PrivateGb, seen.Peaks.PrivateGb) - privateGb) * remaining), ((Math.Max(expected.ResidentGb, seen.Peaks.ResidentGb) - residentGb) * remaining));
    }

    // A process is an observation of its profile once it has been seen for a full plateau (live: its growth stopped for one;
    // exited: it was watched for one). Before that it counts only to raise a profile that already has observations, since a
    // short focused check says nothing of an ordinary start's peak. A process already in the window keeps its entry current.
    // A start of a named document is also an observation of any World of its kind, backend and store (AnyDocument).
    private void Record(HostStartProfile kind, (int Id, long StartTicks) id, HostPeaks peaks, bool settled) {
        foreach (var profile in ((kind.Class.Document is null) ? [kind.Class] : (HostStartProfile[])[kind.Class, kind.AnyDocument])) {
            var window = Window(profile: profile);

            // Only the judged quantities raise a kind early: a reported working set alone never brings a short run in.
            if (window.Contains(id: id.Id, startTicks: id.StartTicks) || settled || ((window.Count > 0) && ((peaks.PrivateGb > window.PrivateGb) || (peaks.ResidentGb > window.ResidentGb)))) {
                window.Observe(id: id.Id, startTicks: id.StartTicks, peaks: peaks);
            }
        }
    }

    // Each profile has one observation file under the lease root, the truth of its window. An exclusive guard merges it with
    // what this process observed since its last write, so what a long-lived process remembers never outvotes the file.
    private static void Synchronize(HostPeakWindow window, string path) {
        using var held = FileLock.TryAcquire(path + GuardSuffix);
        if (held is null) { return; }

        var stored = (File.Exists(path) ? File.ReadAllText(path) : null);
        var text = window.Merge(stored: stored);

        if ((text.Length > 0) && (text != stored)) { AtomicFile.WriteAllText(path, text); }
        window.Written();
    }
    // Every profile asked about or this process has observed, each with the profile of any document it falls back to, and
    // every bare kind: the files are the machine's windows, so a profile another CLI measured is read before this process has
    // met one of its own. Only those files are read, so a window per document costs a reading nothing until it is asked for.
    private void Measurements(params IEnumerable<HostStartProfile> asked) {
        if (m_root is null) { return; }
        var directory = Path.Combine(m_root, ObservationDirectory);
        Directory.CreateDirectory(directory);

        var known = new HashSet<HostStartProfile>(collection: m_windows.Keys);

        foreach (var kind in Enum.GetValues<HostStartKind>()) { _ = known.Add(item: new HostStartProfile(Kind: kind)); }
        foreach (var profile in asked) {
            _ = known.Add(item: profile.Class);
            _ = known.Add(item: profile.AnyDocument);
        }
        foreach (var profile in known) {
            Synchronize(Window(profile: profile), Path.Combine(directory, profile.Name));
        }
    }
    private const string GuardSuffix = ".guard";

    /// <summary>Returns every profile this machine has a window of observed peaks for, read from the lease root: each kind,
    /// backend, store and document a start of which has been measured here.</summary>
    /// <returns>The profiles, by name; empty for a tracker that keeps its observations local.</returns>
    public IReadOnlyList<HostStartProfile> MeasuredProfiles() {
        if ((m_root is null) || !Directory.Exists(Path.Combine(m_root, ObservationDirectory))) { return []; }

        return [.. Directory.EnumerateFiles(Path.Combine(m_root, ObservationDirectory))
            .Select(static file => Path.GetFileName(file))
            .Where(static name => !name.EndsWith(GuardSuffix, StringComparison.Ordinal))
            .Select(static name => (HostStartProfile.TryParse(name: name, profile: out var profile) ? profile : (HostStartProfile?)null))
            .OfType<HostStartProfile>()
            .OrderBy(static profile => profile.Name, StringComparer.Ordinal)];
    }

    /// <summary>Returns the profile's expected peaks, each the high percentile of its recent observations, read from the
    /// shared window first. A document this machine has not measured takes the window of its kind, backend and store
    /// (<see cref="HostStartProfile.AnyDocument"/>). Before either has an observation the private bytes are
    /// <see cref="HostLoadThresholds.DefaultPeakGb"/>, the private working set <see cref="HostLoadThresholds.ResidentFraction"/>
    /// of that, and the whole working set, which admission reports and never judges, NaN.</summary>
    /// <param name="kind">The start's profile.</param>
    /// <param name="thresholds">The thresholds of the machine the start runs on, whose installed memory holds the fallbacks
    /// (<see cref="HostLoadThresholds.DefaultPeakGb"/>); <see langword="null"/> judges by the profiles' figures alone.</param>
    /// <returns>The expected peaks, in gigabytes.</returns>
    public HostPeaks ExpectedPeaks(HostStartProfile kind, HostLoadThresholds? thresholds = null) {
        lock (m_gate) {
            Measurements(kind);
            return Expected(installedRamGb: thresholds?.InstalledRamGb, kind: kind);
        }
    }
    /// <summary>Records the peaks a start has reached as it stops, as one settled observation of its profile: the observation
    /// no later reading of a process that is gone can supply, and the one a session that nothing sampled while it ran would
    /// otherwise never teach. Peaks this tracker already followed for the process are kept where they are higher.</summary>
    /// <param name="profile">The profile the start ran as.</param>
    /// <param name="process">The process's id and UTC start ticks.</param>
    /// <param name="peaks">What the process reached (<see cref="HostProbe.PeaksOf"/>); an invalid reading records nothing.</param>
    public void Finish(HostStartProfile profile, (int Id, long StartTicks) process, HostPeaks peaks) {
        lock (m_gate) {
            Measurements(profile);
            if (m_seen.Remove(key: process, value: out var seen) && seen.Peaks.Valid) { peaks = seen.Peaks.Max(other: peaks); }
            Record(profile, process, peaks, settled: true);
            Measurements(profile);
        }
    }
    /// <summary>Records what a World that reported ready has reached, as it is about to stop, read from its own process. A
    /// World that never reported ready has not reached steady state and teaches nothing; a process that has already exited
    /// cannot be read.</summary>
    /// <param name="process">The World's process, still running.</param>
    /// <param name="readiness">The World's readiness, which holds the profile its launch recorded.</param>
    public void Finish(Process process, WorldReadiness readiness) {
        if (!readiness.Ready || (readiness.Profile is not { } profile)) { return; }

        try {
            process.Refresh();
            if (process.HasExited) { return; }

            Finish(profile: profile, process: (process.Id, process.StartTime.ToUniversalTime().Ticks), peaks: HostProbe.PeaksOf(process: process));
        } catch (Exception exception) when ((exception is InvalidOperationException or NotSupportedException or Win32Exception or IOException or UnauthorizedAccessException)) { }
    }
    /// <summary>Reads what a start of <paramref name="kind"/> needs of the headroom <paramref name="reading"/> shows,
    /// folding the reading's processes into the tracker first, and, when <paramref name="reserve"/> is set and the start is
    /// covered, holds its peak as a reservation in the same step so a concurrent start reads it.</summary>
    /// <param name="reading">The machine reading.</param>
    /// <param name="kind">The start's profile.</param>
    /// <param name="thresholds">The thresholds that judge the commit headroom (with its reserve) and free physical memory.</param>
    /// <param name="clock">The clock a reservation's hold is measured on.</param>
    /// <param name="reserve">Whether a covered start is admitted by this call and so reserves its peak.</param>
    /// <returns>The start's demand.</returns>
    public HostStartDemand Judge(HostSample reading, HostStartProfile kind, HostLoadThresholds thresholds, TimeProvider clock, bool reserve) {
        lock (m_gate) {
            var live = (reading.Starts ?? []);
            HostStartProfile[] asked = [kind, .. live.Select(static start => start.Profile), .. (reading.Holds ?? []).Select(static hold => hold.Profile)];

            Measurements(asked);
            var liveIds = new HashSet<(int Id, long StartTicks)>(collection: live.Select(selector: static start => (start.Id, start.StartTicks)));

            // Records, machine-wide, that a start or a hold's tree has gone idle, or has used CPU again after another observer
            // marked it idle; the marks are how every other process's admission sees that a holder gave its place back.
            void Mark(bool busy, bool quiet, DateTimeOffset? markedSince, HostStartKind markKind, int id, long startTicks, Observation seen) {
                if (m_root is not { } root) { return; }
                if (busy && (markedSince is not null)) { StartIdleness.Clear(root: root, processId: id, startTicks: startTicks); }
                else if (quiet && (markedSince is null)) { StartIdleness.Mark(root: root, start: new IdleStart(Kind: markKind, ProcessId: id, Since: (clock.GetUtcNow() - clock.GetElapsedTime(seen.BusyAt)), StartTicks: startTicks)); }
            }

            // The idle starts: no CPU use for a plateau, which settles a start whatever its private bytes do.
            HashSet<(int Id, long StartTicks)> idleStarts = [];

            foreach (var start in live) {
                var id = (start.Id, start.StartTicks);
                var (seen, busy) = Follow(clock: clock, cpuSeconds: start.CpuSeconds, kind: start.Profile, peaks: Reading(start: start), seen: m_seen.GetValueOrDefault(id));

                m_seen[id] = seen;

                // Another observer's mark counts until this tracker sees the process use CPU itself.
                var quiet = (clock.GetElapsedTime(seen.BusyAt) >= Plateau) || ((start.IdleSince is not null) && (seen.BusyAt == seen.FirstAt));

                if (quiet) { _ = idleStarts.Add(item: id); }
                Mark(busy: busy, quiet: quiet, markedSince: start.IdleSince, markKind: start.Kind, id: start.Id, startTicks: start.StartTicks, seen: seen);
                Record(seen.Profile, id, seen.Peaks, settled: (quiet || (clock.GetElapsedTime(seen.GrewAt) >= Plateau)));
            }
            foreach (var (id, seen) in m_seen.Where(predicate: pair => !liveIds.Contains(item: pair.Key)).ToArray()) {
                Record(seen.Profile, id, seen.Peaks, settled: (clock.GetElapsedTime(seen.FirstAt) >= Plateau));
                _ = m_seen.Remove(key: id);
            }
            Measurements(asked);

            // A reservation ends when its hold passes or a live process of its kind it did not know of appears; each new
            // process answers one reservation.
            var claimed = new HashSet<(int Id, long StartTicks)>();
            List<Reservation> kept = [];

            foreach (var reservation in m_reservations) {
                if (clock.GetElapsedTime(startingTimestamp: reservation.At) >= ReservationHold) { continue; }

                var answered = false;

                foreach (var start in live) {
                    if ((start.Kind == reservation.Kind) && !reservation.Known.Contains(item: (start.Id, start.StartTicks)) && claimed.Add(item: (start.Id, start.StartTicks))) {
                        answered = true;

                        break;
                    }
                }
                if (!answered) { kept.Add(item: reservation); }
            }

            m_reservations.Clear();
            m_reservations.AddRange(collection: kept);

            var growth = 0.0;
            var residentGrowth = 0.0;
            var growing = 0;

            void Add((double Commit, double Resident) left) {
                if (left.Commit > 0) {
                    growth += left.Commit;
                    growing++;
                }
                if (left.Resident > 0) { residentGrowth += left.Resident; }
            }

            // The one growth sum, in both quantities: ready or plateaued processes count only their actual usage in the
            // system charge and in free memory.
            var startsLeft = new Dictionary<int, (double Commit, double Resident)>();

            foreach (var start in live) {
                startsLeft[start.Id] = (start.Ready ? (0, 0) : Left(clock: clock, expected: Expected(installedRamGb: thresholds.InstalledRamGb, kind: start.Profile), idle: idleStarts.Contains(item: (start.Id, start.StartTicks)), privateGb: start.PrivateGb, residentGb: start.ResidentGb, seen: m_seen[(start.Id, start.StartTicks)]));
            }
            // A hold another process admitted covers its holder's whole process tree from the grant on, before any start in it
            // appears: the tree still has to grow toward the hold's expected peak, or the tracked starts inside it still have
            // to grow, whichever is more, so a start inside a hold is never counted twice. It tapers and goes idle as a start
            // does, by the tree's private bytes and CPU time.
            HashSet<int> covered = [];
            HashSet<(int Id, long StartTicks)> holdIds = [];

            foreach (var hold in (reading.Holds ?? [])) {
                var id = (hold.Id, hold.StartTicks);
                var (seen, holdBusy) = Follow(clock: clock, cpuSeconds: hold.CpuSeconds, kind: hold.Profile, peaks: new HostPeaks(PrivateGb: hold.PrivateGb, ResidentGb: hold.ResidentGb, WorkingSetGb: double.NaN), seen: m_holds.GetValueOrDefault(id));
                var holdQuiet = (clock.GetElapsedTime(seen.BusyAt) >= Plateau) || ((hold.IdleSince is not null) && (seen.BusyAt == seen.FirstAt));
                var tree = Left(clock: clock, expected: Expected(installedRamGb: thresholds.InstalledRamGb, kind: hold.Profile), idle: holdQuiet, privateGb: hold.PrivateGb, residentGb: hold.ResidentGb, seen: seen);
                var inner = (Commit: 0.0, Resident: 0.0);

                m_holds[id] = seen;
                Mark(busy: holdBusy, quiet: holdQuiet, markedSince: hold.IdleSince, markKind: hold.Kind, id: hold.Id, startTicks: hold.StartTicks, seen: seen);
                _ = holdIds.Add(item: id);
                foreach (var member in hold.Members) {
                    if (covered.Add(item: member) && startsLeft.TryGetValue(key: member, value: out var left)) { inner = ((inner.Commit + left.Commit), (inner.Resident + left.Resident)); }
                }
                Add(left: (Math.Max(tree.Commit, inner.Commit), Math.Max(tree.Resident, inner.Resident)));
            }
            foreach (var gone in m_holds.Keys.Where(predicate: id => !holdIds.Contains(item: id)).ToArray()) { _ = m_holds.Remove(key: gone); }
            foreach (var (id, left) in startsLeft) {
                if (!covered.Contains(item: id)) { Add(left: left); }
            }
            foreach (var reservation in m_reservations) {
                growth += reservation.Gb;
                residentGrowth += reservation.ResidentGb;
                growing++;
            }

            // Each quantity is judged by the learned estimate unless the machine with every admitted start gone could not hold
            // it either: then no wait for an admitted start changes the answer, and the lone figure judges instead. A start is
            // alone when no other Puck process holds commit or is admitted and still to appear (a live start, another
            // process's hold, a reservation of this one's).
            var learned = Expected(installedRamGb: thresholds.InstalledRamGb, kind: kind);
            var idle = reading with {
                CommitChargeGb = (reading.CommitChargeGb - live.Sum(selector: static start => start.PrivateGb)),
                FreeRamGb = (reading.FreeRamGb + live.Sum(selector: static start => start.ResidentGb)),
            };
            var alone = ((live.Count == 0) && ((reading.Holds ?? []).Count == 0) && (m_reservations.Count == 0));
            var lone = Lone(alone: alone, idle: idle, kind: kind, thresholds: thresholds);
            // What the idle machine is asked to hold of the start at least, which NeverFits refuses against: the idle machine
            // is alone.
            var least = (alone ? lone : Lone(alone: true, idle: idle, kind: kind, thresholds: thresholds));
            var expectedOwn = new HostPeaks(
                PrivateGb: (thresholds.CommitCovers(reading: idle, needGb: learned.PrivateGb) ? learned.PrivateGb : lone.PrivateGb),
                ResidentGb: (thresholds.PhysicalCovers(reading: idle, needGb: learned.ResidentGb) ? learned.ResidentGb : lone.ResidentGb),
                WorkingSetGb: learned.WorkingSetGb
            );
            var own = expectedOwn.PrivateGb;
            var residentOwn = expectedOwn.ResidentGb;
            var need = (own + growth);
            var residentNeed = (residentOwn + residentGrowth);
            var commitCovered = thresholds.CommitCovers(reading: reading, needGb: need);
            var physicalCovered = thresholds.PhysicalCovers(reading: reading, needGb: residentNeed);

            // A start admitted below its expectation reserves the whole expectation: a start asked after it in this process
            // is judged beside what it may yet reach, not what it was let in at.
            if (reserve && commitCovered && physicalCovered && (kind.Kind != HostStartKind.Step)) {
                m_reservations.Add(item: new Reservation(Kind: kind.Kind, At: clock.GetTimestamp(), Gb: Math.Max(own, learned.PrivateGb), ResidentGb: Math.Max(residentOwn, learned.ResidentGb), Known: liveIds));
            }

            return new HostStartDemand(
                CommitCovered: commitCovered,
                FreeRamGb: reading.FreeRamGb,
                Growing: growing,
                GrowthGb: growth,
                HeadroomGb: (reading.CommitLimitGb - reading.CommitChargeGb),
                Kind: kind.Kind,
                LoneOwnGb: least.PrivateGb,
                LoneResidentOwnGb: least.ResidentGb,
                ExpectedOwnGb: learned.PrivateGb,
                Measured: Measured(kind: kind),
                NeedGb: need,
                OwnGb: own,
                PhysicalCovered: physicalCovered,
                Profile: kind.Class,
                ResidentGrowthGb: residentGrowth,
                ResidentNeedGb: residentNeed,
                ResidentOwnGb: residentOwn,
                WorkingSetOwnGb: expectedOwn.WorkingSetGb
            );
        }
    }
}
