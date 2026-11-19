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
/// never judges; NaN before the kind has an observation.</param>
/// <param name="LoneOwnGb">What the start is judged to need when nothing admitted runs beside it: the lesser of its expected
/// commit peak and the kind's fallback, in gigabytes. <paramref name="OwnGb"/> is this when the start is alone; the machine
/// can never hold a start whose lone need it cannot.</param>
/// <param name="LoneResidentOwnGb">The lone need in private working set, in gigabytes.</param>
public readonly record struct HostStartDemand(bool CommitCovered, bool PhysicalCovered, double HeadroomGb, double FreeRamGb, double NeedGb, double OwnGb, double GrowthGb, double ResidentNeedGb, double ResidentOwnGb, double ResidentGrowthGb, int Growing, HostStartKind Kind, double WorkingSetOwnGb = double.NaN, double LoneOwnGb = double.NaN, double LoneResidentOwnGb = double.NaN) {
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
/// Before a kind has an observation it uses <see cref="HostLoadThresholds.DefaultPeakGb"/> and, for its private working set,
/// <see cref="HostLoadThresholds.ResidentFraction"/> of its expected commit peak. After, a kind's expected peak in each
/// quantity is the high percentile of its recent distribution (<see cref="HostPeakWindow"/>: the last ten processes of the
/// kind, each observed at a plateau or for a full plateau window), so one outlier session raises it only until a few
/// ordinary starts follow, a sustained rise is learned, and the estimate moves down as readily as up (a refused kind
/// observes nothing, see the lone start below). The windows are shared
/// between CLI processes under the lease root (<see cref="ObservationDirectory"/>), where the file decides: a long-lived
/// process adds what it observed since its last write and keeps nothing else of its own. A live process above the
/// estimate is still counted at its own usage, without clipping it. Growth tapers to zero over <see cref="Plateau"/> without
/// a material allocation and returns when growth resumes. A World that reported ready reserves no growth. The system commit
/// charge already includes live usage. A kind whose expected need alone exceeds the machine is named by
/// <see cref="HostLoadThresholds.NeverFits"/>, not waited on. A start with nothing admitted running or reserved beside it
/// is judged by the lesser of that estimate and the kind's fallback (<see cref="HostStartDemand.LoneOwnGb"/>): the estimate
/// arbitrates between starts that run together, and a refused kind runs nothing that could lower it again, so an inflated
/// estimate must never refuse a lone start.
/// <para>Admitted starts come from the live processes each reading lists (<see cref="HostSample.Starts"/>, classified by
/// <see cref="HostProcesses.RunningStart"/>) with their current private bytes, so a process that exits stops counting at
/// once and nothing is read from a record that can go stale. The one thing a reading cannot show is a start admitted a
/// moment ago whose process has not appeared: this tracker holds that start as a reservation of its full peak until a
/// new live process of its kind appears or <see cref="ReservationHold"/> passes, which is what keeps two starts asked in
/// a row from both taking the same headroom. Pre-process reservations live in this process's memory only,
/// so they cover the starts this process admits (an MCP session, the concurrent workers of one verb) and not
/// those another process admits at the same instant, whose processes are counted once they run.</para>
/// </summary>
public sealed class HostStartTracker {
    private sealed record Reservation(HostStartKind Kind, long At, double Gb, double ResidentGb, HashSet<(int Id, long StartTicks)> Known);
    private sealed record Observation(HostStartKind Kind, HostPeaks Peaks, double AnchorGb, long GrewAt, long FirstAt);

    /// <summary>Without material growth for this long, a live process reserves no further growth.</summary>
    public static readonly TimeSpan Plateau = TimeSpan.FromMinutes(1);
    private const double MaterialGrowthGb = 1.0 / 16;
    /// <summary>The directory under the lease root that holds each kind's observation window, one file per
    /// <see cref="HostStartKind"/> name. A change to the files' lines takes a new name and raises <see cref="CliLease.Protocol"/>:
    /// every CLI copy on the machine rewrites these files on each reading, so two copies that disagree on the lines must not
    /// share one.</summary>
    public const string ObservationDirectory = "host-peaks";
    private readonly string? m_root;

    /// <summary>Creates a tracker, optionally sharing measured peaks in the machine's lease root.</summary>
    /// <param name="leaseRoot">The shared lease root; null keeps observations local for injected readings.</param>
    public HostStartTracker(string? leaseRoot = null) { m_root = leaseRoot; }

    /// <summary>How long an admitted start holds its full expected peak before its live process is expected to show.</summary>
    public static readonly TimeSpan ReservationHold = TimeSpan.FromSeconds(seconds: 90);

    private readonly Lock m_gate = new();
    private readonly Dictionary<HostStartKind, HostPeakWindow> m_windows = [];
    private readonly List<Reservation> m_reservations = [];
    // Keyed by process id and start ticks: a recycled id is another start, and carries none of the last one's peak.
    private readonly Dictionary<(int Id, long StartTicks), Observation> m_seen = [];

    /// <summary>Gets the tracker of this process's host session, which the admission of a real machine reading uses.</summary>
    public static HostStartTracker Session { get; } = new(leaseRoot: CliLease.RootAsync(Environment.CurrentDirectory).GetAwaiter().GetResult());

    private HostPeakWindow Window(HostStartKind kind) {
        if (!m_windows.TryGetValue(kind, out var window)) {
            window = new HostPeakWindow();
            m_windows[kind] = window;
        }

        return window;
    }
    // The one expected-peak door: the recent observations' high percentile once the kind has any, else its fallbacks (the
    // kind's default commit peak, a fixed share of it as the private working set, and no whole working set, which is
    // reported only and has nothing to stand in for it).
    private HostPeaks Expected(HostStartKind kind) => ((m_windows.TryGetValue(kind, out var window) && (window.Count > 0))
        ? new HostPeaks(PrivateGb: window.PrivateGb, ResidentGb: window.ResidentGb, WorkingSetGb: window.WorkingSetGb)
        : Fallback(kind: kind));
    private static HostPeaks Fallback(HostStartKind kind) => new(PrivateGb: HostLoadThresholds.DefaultPeakGb(kind: kind), ResidentGb: (HostLoadThresholds.ResidentFraction * HostLoadThresholds.DefaultPeakGb(kind: kind)), WorkingSetGb: double.NaN);
    // What a start asked of an idle host is judged by: the lesser of what the kind's observations expect and the fallback an
    // unmeasured kind gets. The learned expectation arbitrates between starts that run together, and a start that runs alone
    // has nothing to arbitrate with, so one outlier session (its peak in the window) could otherwise refuse every later
    // start of its kind, and a refused kind runs nothing that could teach the window a lower figure.
    private HostPeaks Lone(HostStartKind kind) {
        var learned = Expected(kind: kind);
        var fallback = Fallback(kind: kind);

        return new HostPeaks(PrivateGb: Math.Min(learned.PrivateGb, fallback.PrivateGb), ResidentGb: Math.Min(learned.ResidentGb, fallback.ResidentGb), WorkingSetGb: learned.WorkingSetGb);
    }
    // A live process's peaks as one reading shows them.
    private static HostPeaks Reading(HostRunningStart start) => new(PrivateGb: start.PrivateGb, ResidentGb: start.ResidentGb, WorkingSetGb: start.HeldGb);

    // A process is an observation of its kind once it has been seen for a full plateau (live: its growth stopped for one;
    // exited: it was watched for one). Before that it counts only to raise a kind that already has observations, since a
    // short focused check says nothing of an ordinary start's peak. A process already in the window keeps its entry current.
    private void Record(HostStartKind kind, (int Id, long StartTicks) id, HostPeaks peaks, bool settled) {
        var window = Window(kind: kind);

        // Only the judged quantities raise a kind early: a reported working set alone never brings a short run in.
        if (window.Contains(id: id.Id, startTicks: id.StartTicks) || settled || ((window.Count > 0) && (Expected(kind: kind) is var expected) && ((peaks.PrivateGb > expected.PrivateGb) || (peaks.ResidentGb > expected.ResidentGb)))) {
            window.Observe(id: id.Id, startTicks: id.StartTicks, peaks: peaks);
        }
    }

    // Each kind has one observation file under the lease root, the truth of its window. An exclusive guard merges it with
    // what this process observed since its last write, so what a long-lived process remembers never outvotes the file.
    private static void Synchronize(HostPeakWindow window, string path) {
        using var held = FileLock.TryAcquire(path + ".guard");
        if (held is null) { return; }

        var stored = (File.Exists(path) ? File.ReadAllText(path) : null);
        var text = window.Merge(stored: stored);

        if ((text.Length > 0) && (text != stored)) { AtomicFile.WriteAllText(path, text); }
        window.Written();
    }
    private void Measurements() {
        if (m_root is null) { return; }
        var directory = Path.Combine(m_root, ObservationDirectory);
        Directory.CreateDirectory(directory);
        foreach (var kind in Enum.GetValues<HostStartKind>()) {
            Synchronize(Window(kind: kind), Path.Combine(directory, kind.ToString()));
        }
    }

    /// <summary>Returns the kind's expected peaks, each the high percentile of its recent observations, read from the shared
    /// window first. Before the kind has an observation the private bytes are <see cref="HostLoadThresholds.DefaultPeakGb"/>,
    /// the private working set <see cref="HostLoadThresholds.ResidentFraction"/> of that, and the whole working set, which
    /// admission reports and never judges, NaN.</summary>
    /// <param name="kind">The start's kind.</param>
    /// <returns>The expected peaks, in gigabytes.</returns>
    public HostPeaks ExpectedPeaks(HostStartKind kind) {
        lock (m_gate) {
            Measurements();
            return Expected(kind: kind);
        }
    }
    /// <summary>Reads what a start of <paramref name="kind"/> needs of the headroom <paramref name="reading"/> shows,
    /// folding the reading's processes into the tracker first, and, when <paramref name="reserve"/> is set and the start is
    /// covered, holds its peak as a reservation in the same step so a concurrent start reads it.</summary>
    /// <param name="reading">The machine reading.</param>
    /// <param name="kind">The start's kind.</param>
    /// <param name="thresholds">The thresholds that judge the commit headroom (with its reserve) and free physical memory.</param>
    /// <param name="clock">The clock a reservation's hold is measured on.</param>
    /// <param name="reserve">Whether a covered start is admitted by this call and so reserves its peak.</param>
    /// <returns>The start's demand.</returns>
    public HostStartDemand Judge(HostSample reading, HostStartKind kind, HostLoadThresholds thresholds, TimeProvider clock, bool reserve) {
        lock (m_gate) {
            Measurements();
            var live = (reading.Starts ?? []);
            var liveIds = new HashSet<(int Id, long StartTicks)>(collection: live.Select(selector: static start => (start.Id, start.StartTicks)));

            foreach (var start in live) {
                var id = (start.Id, start.StartTicks);
                var seen = m_seen.GetValueOrDefault(id) ?? new Observation(start.Kind, Reading(start: start), start.PrivateGb, clock.GetTimestamp(), clock.GetTimestamp());
                var grew = start.PrivateGb >= seen.AnchorGb + MaterialGrowthGb;
                seen = seen with { Peaks = seen.Peaks.Max(other: Reading(start: start)),
                    AnchorGb = grew ? start.PrivateGb : Math.Min(start.PrivateGb, seen.AnchorGb), GrewAt = grew ? clock.GetTimestamp() : seen.GrewAt };
                m_seen[id] = seen;
                Record(start.Kind, id, seen.Peaks, settled: (clock.GetElapsedTime(seen.GrewAt) >= Plateau));
            }
            foreach (var (id, seen) in m_seen.Where(predicate: pair => !liveIds.Contains(item: pair.Key)).ToArray()) {
                Record(seen.Kind, id, seen.Peaks, settled: (clock.GetElapsedTime(seen.FirstAt) >= Plateau));
                _ = m_seen.Remove(key: id);
            }
            Measurements();

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

            // The one growth sum, in both quantities: ready or plateaued processes count only their actual usage in the
            // system charge and in free memory.
            foreach (var start in live) {
                var seen = m_seen[(start.Id, start.StartTicks)];
                var remaining = Math.Max(0, 1 - clock.GetElapsedTime(seen.GrewAt).TotalSeconds / Plateau.TotalSeconds);
                var expected = Expected(kind: start.Kind);
                var left = start.Ready ? 0 : (Math.Max(expected.PrivateGb, seen.Peaks.PrivateGb) - start.PrivateGb) * remaining;
                var residentLeft = start.Ready ? 0 : (Math.Max(expected.ResidentGb, seen.Peaks.ResidentGb) - start.ResidentGb) * remaining;

                if (left > 0) {
                    growth += left;
                    growing++;
                }
                if (residentLeft > 0) {
                    residentGrowth += residentLeft;
                }
            }
            foreach (var reservation in m_reservations) {
                growth += reservation.Gb;
                residentGrowth += reservation.ResidentGb;
                growing++;
            }

            // Nothing admitted is running or about to, so no completion can free headroom for a larger figure: only the host's
            // own load can, which the reserve and the pressure floor already judge.
            var alone = ((live.Count == 0) && (m_reservations.Count == 0));
            var lone = Lone(kind: kind);
            var expectedOwn = (alone ? lone : Expected(kind: kind));
            var own = expectedOwn.PrivateGb;
            var residentOwn = expectedOwn.ResidentGb;
            var need = (own + growth);
            var residentNeed = (residentOwn + residentGrowth);
            var commitCovered = thresholds.CommitCovers(reading: reading, needGb: need);
            var physicalCovered = thresholds.PhysicalCovers(reading: reading, needGb: residentNeed);

            if (reserve && commitCovered && physicalCovered && (kind != HostStartKind.Step)) {
                m_reservations.Add(item: new Reservation(Kind: kind, At: clock.GetTimestamp(), Gb: own, ResidentGb: residentOwn, Known: liveIds));
            }

            return new HostStartDemand(
                CommitCovered: commitCovered,
                FreeRamGb: reading.FreeRamGb,
                Growing: growing,
                GrowthGb: growth,
                HeadroomGb: (reading.CommitLimitGb - reading.CommitChargeGb),
                Kind: kind,
                LoneOwnGb: lone.PrivateGb,
                LoneResidentOwnGb: lone.ResidentGb,
                NeedGb: need,
                OwnGb: own,
                PhysicalCovered: physicalCovered,
                ResidentGrowthGb: residentGrowth,
                ResidentNeedGb: residentNeed,
                ResidentOwnGb: residentOwn,
                WorkingSetOwnGb: expectedOwn.WorkingSetGb
            );
        }
    }
}
