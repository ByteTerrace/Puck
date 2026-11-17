using Puck.Confinement;
using Puck.Abstractions;

namespace Puck.Cli.Host;

/// <summary>What a start needs of the machine's memory, as <see cref="HostStartTracker.Judge"/> reads it, in two
/// quantities: private bytes (commit) against the commit headroom less its reserve, and working set (physical residency)
/// against free physical memory above the pressure floor.</summary>
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
/// <param name="ResidentNeedGb">The start's own expected working-set peak plus <paramref name="ResidentGrowthGb"/>, in
/// gigabytes.</param>
/// <param name="ResidentOwnGb">The start's own expected working-set peak, in gigabytes.</param>
/// <param name="ResidentGrowthGb">What the starts already admitted still have to grow in working set to reach their
/// expected peaks, in gigabytes.</param>
/// <param name="Growing">How many admitted starts are still below their expected commit peak.</param>
/// <param name="Kind">The start's kind.</param>
public readonly record struct HostStartDemand(bool CommitCovered, bool PhysicalCovered, double HeadroomGb, double FreeRamGb, double NeedGb, double OwnGb, double GrowthGb, double ResidentNeedGb, double ResidentOwnGb, double ResidentGrowthGb, int Growing, HostStartKind Kind) {
    /// <summary>Gets whether the machine can hold the start: both the commit headroom and free physical memory cover it.</summary>
    public bool Covered => (CommitCovered && PhysicalCovered);
}

/// <summary>
/// The single memory-demand calculation: a start's measured peak plus the running starts' remaining growth, in private
/// bytes for the commit headroom and in working set for free physical memory (above the pressure floor); private bytes
/// are commit and overstate what a process holds resident, so each is judged against its own quantity.
/// Before a kind has an observation it uses <see cref="HostLoadThresholds.DefaultPeakGb"/> and, for its working set,
/// <see cref="HostLoadThresholds.ResidentFraction"/> of its expected commit peak. After, a kind's expected peak in each
/// quantity is the high percentile of its recent distribution (<see cref="HostPeakWindow"/>: the last ten processes of the
/// kind, each observed at a plateau or for a full plateau window), so one outlier session raises it only until a few
/// ordinary starts follow, a sustained rise is learned, and the estimate moves down as readily as up. The windows are shared
/// between CLI processes under the lease root (<c>host-observations/&lt;kind&gt;</c>), where the file decides: a long-lived
/// process adds what it observed since its last write and keeps nothing else of its own. A live process above the
/// estimate is still counted at its own usage, without clipping it. Growth tapers to zero over <see cref="Plateau"/> without
/// a material allocation and returns when growth resumes. A World that reported ready reserves no growth. The system commit
/// charge already includes live usage. A kind whose expected need alone exceeds the machine is named by
/// <see cref="HostLoadThresholds.NeverFits"/>, not waited on.
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
    private sealed record Observation(HostStartKind Kind, double PeakGb, double ResidentPeakGb, double AnchorGb, long GrewAt, long FirstAt);

    /// <summary>Without material growth for this long, a live process reserves no further growth.</summary>
    public static readonly TimeSpan Plateau = TimeSpan.FromMinutes(1);
    private const double MaterialGrowthGb = 1.0 / 16;
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
    // The one expected-peak door: the recent observations' high percentile once the kind has any, else its fallback.
    private double Expected(HostStartKind kind) => ((m_windows.TryGetValue(kind, out var window) && (window.Count > 0)) ? window.PrivateGb : HostLoadThresholds.DefaultPeakGb(kind: kind));
    // Until the kind's working set is observed, a fixed share of its expected commit peak.
    private double ExpectedResident(HostStartKind kind) => ((m_windows.TryGetValue(kind, out var window) && (window.Count > 0)) ? window.ResidentGb : (HostLoadThresholds.ResidentFraction * Expected(kind: kind)));

    // A process is an observation of its kind once it has been seen for a full plateau (live: its growth stopped for one;
    // exited: it was watched for one). Before that it counts only to raise a kind that already has observations, since a
    // short focused check says nothing of an ordinary start's peak. A process already in the window keeps its entry current.
    private void Record(HostStartKind kind, (int Id, long StartTicks) id, double peakGb, double residentPeakGb, bool settled) {
        var window = Window(kind: kind);

        if (window.Contains(id: id.Id, startTicks: id.StartTicks) || settled || ((window.Count > 0) && (peakGb > Expected(kind: kind)))) {
            window.Observe(id: id.Id, startTicks: id.StartTicks, privateGb: peakGb, residentGb: residentPeakGb);
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
        var directory = Path.Combine(m_root, "host-observations");
        Directory.CreateDirectory(directory);
        foreach (var kind in Enum.GetValues<HostStartKind>()) {
            Synchronize(Window(kind: kind), Path.Combine(directory, kind.ToString()));
        }
    }

    /// <summary>Returns the kind's expected commit peak (private bytes), the high percentile of its recent observations, or
    /// its fallback before one exists.</summary>
    /// <param name="kind">The start's kind.</param>
    /// <returns>The expected peak, in gigabytes.</returns>
    public double ExpectedPeakGb(HostStartKind kind) {
        lock (m_gate) {
            Measurements();
            return Expected(kind: kind);
        }
    }
    /// <summary>Returns the kind's expected working-set peak, the high percentile of its recent observations, or its
    /// fallback before one exists.</summary>
    /// <param name="kind">The start's kind.</param>
    /// <returns>The expected working-set peak, in gigabytes.</returns>
    public double ExpectedResidentPeakGb(HostStartKind kind) {
        lock (m_gate) {
            Measurements();
            return ExpectedResident(kind: kind);
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
                var seen = m_seen.GetValueOrDefault(id) ?? new Observation(start.Kind, start.PrivateGb, start.ResidentGb, start.PrivateGb, clock.GetTimestamp(), clock.GetTimestamp());
                var grew = start.PrivateGb >= seen.AnchorGb + MaterialGrowthGb;
                seen = seen with { PeakGb = Math.Max(start.PrivateGb, seen.PeakGb), ResidentPeakGb = Math.Max(start.ResidentGb, seen.ResidentPeakGb),
                    AnchorGb = grew ? start.PrivateGb : Math.Min(start.PrivateGb, seen.AnchorGb), GrewAt = grew ? clock.GetTimestamp() : seen.GrewAt };
                m_seen[id] = seen;
                Record(start.Kind, id, seen.PeakGb, seen.ResidentPeakGb, settled: (clock.GetElapsedTime(seen.GrewAt) >= Plateau));
            }
            foreach (var (id, seen) in m_seen.Where(predicate: pair => !liveIds.Contains(item: pair.Key)).ToArray()) {
                Record(seen.Kind, id, seen.PeakGb, seen.ResidentPeakGb, settled: (clock.GetElapsedTime(seen.FirstAt) >= Plateau));
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
                var left = start.Ready ? 0 : (Math.Max(Expected(start.Kind), seen.PeakGb) - start.PrivateGb) * remaining;
                var residentLeft = start.Ready ? 0 : (Math.Max(ExpectedResident(start.Kind), seen.ResidentPeakGb) - start.ResidentGb) * remaining;

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

            var own = Expected(kind: kind);
            var residentOwn = ExpectedResident(kind: kind);
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
                NeedGb: need,
                OwnGb: own,
                PhysicalCovered: physicalCovered,
                ResidentGrowthGb: residentGrowth,
                ResidentNeedGb: residentNeed,
                ResidentOwnGb: residentOwn
            );
        }
    }
}
