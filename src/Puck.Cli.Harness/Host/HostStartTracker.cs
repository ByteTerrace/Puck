using Puck.Confinement;
using System.Globalization;
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
/// Before a kind has a measurement it uses <see cref="HostLoadThresholds.DefaultPeakGb"/> and, for its working set,
/// <see cref="HostLoadThresholds.ResidentFraction"/> of its expected commit peak. Observed private-byte and working-set
/// peaks of plateaued processes, or finished processes observed for a full plateau window, replace those fallbacks,
/// including downward, and are shared between CLI
/// processes under the lease root (<c>host-peaks/&lt;kind&gt;</c> for private bytes, <c>host-peaks/&lt;kind&gt;.resident</c>
/// for working set). A known measurement rises whenever a live process exceeds it, without clipping
/// real usage. Growth tapers to zero over <see cref="Plateau"/> without a material allocation and returns when growth
/// resumes. A World that reported ready reserves no growth. The system commit charge already includes live usage.
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
    private readonly Dictionary<HostStartKind, double> m_measured = [];
    private readonly Dictionary<HostStartKind, double> m_measuredResident = [];
    private readonly List<Reservation> m_reservations = [];
    // Keyed by process id and start ticks: a recycled id is another start, and carries none of the last one's peak.
    private readonly Dictionary<(int Id, long StartTicks), Observation> m_seen = [];

    /// <summary>Gets the tracker of this process's host session, which the admission of a real machine reading uses.</summary>
    public static HostStartTracker Session { get; } = new(leaseRoot: CliLease.RootAsync(Environment.CurrentDirectory).GetAwaiter().GetResult());

    private double Expected(HostStartKind kind) => (m_measured.TryGetValue(kind, out var measured) ? measured : HostLoadThresholds.DefaultPeakGb(kind: kind));
    // Until the kind's working set is measured, a fixed share of its expected commit peak (itself measured when it can be).
    private double ExpectedResident(HostStartKind kind) => (m_measuredResident.TryGetValue(kind, out var measured) ? measured : (HostLoadThresholds.ResidentFraction * Expected(kind: kind)));

    // Each kind has one private-bytes measurement file and one working-set file under the existing lease root. An
    // exclusive guard merges concurrent observations before replacement; a lower observed peak replaces the fallback,
    // never a measured larger peak.
    private static void Learn(Dictionary<HostStartKind, double> store, HostStartKind kind, double peak) {
        if (double.IsFinite(peak) && peak > 0) {
            store[kind] = Math.Max(peak, store.GetValueOrDefault(kind));
        }
    }
    private void Learn(HostStartKind kind, double peak, double resident) {
        Learn(m_measured, kind, peak);
        Learn(m_measuredResident, kind, resident);
    }
    private static void Synchronize(Dictionary<HostStartKind, double> store, HostStartKind kind, string path) {
        using var held = FileLock.TryAcquire(path + ".guard");
        if (held is null) { return; }
        if (File.Exists(path) && double.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, out var peak)) { Learn(store, kind, peak); }
        if (store.TryGetValue(kind, out var measured)) {
            var text = measured.ToString("R", CultureInfo.InvariantCulture);
            if (!File.Exists(path) || File.ReadAllText(path) != text) { AtomicFile.WriteAllText(path, text); }
        }
    }
    private void Measurements() {
        if (m_root is null) { return; }
        var directory = Path.Combine(m_root, "host-peaks");
        Directory.CreateDirectory(directory);
        foreach (var kind in Enum.GetValues<HostStartKind>()) {
            Synchronize(m_measured, kind, Path.Combine(directory, kind.ToString()));
            Synchronize(m_measuredResident, kind, Path.Combine(directory, kind.ToString() + ".resident"));
        }
    }

    /// <summary>Returns the kind's measured commit peak (private bytes), or its fallback before a measurement exists.</summary>
    /// <param name="kind">The start's kind.</param>
    /// <returns>The expected peak, in gigabytes.</returns>
    public double ExpectedPeakGb(HostStartKind kind) {
        lock (m_gate) {
            Measurements();
            return Expected(kind: kind);
        }
    }
    /// <summary>Returns the kind's measured working-set peak, or its fallback before a measurement exists.</summary>
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
                if (m_measured.ContainsKey(start.Kind) || clock.GetElapsedTime(seen.GrewAt) >= Plateau) { Learn(start.Kind, seen.PeakGb, seen.ResidentPeakGb); }
            }
            foreach (var (id, seen) in m_seen.Where(predicate: pair => !liveIds.Contains(item: pair.Key)).ToArray()) {
                // A short focused check is not a measurement of a long run's peak. It can raise an existing
                // measurement, but replacing the fallback needs a full observation window.
                if (m_measured.ContainsKey(seen.Kind) || clock.GetElapsedTime(seen.FirstAt) >= Plateau) { Learn(seen.Kind, seen.PeakGb, seen.ResidentPeakGb); }
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
