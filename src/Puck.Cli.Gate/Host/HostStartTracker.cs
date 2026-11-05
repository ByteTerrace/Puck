namespace Puck.Cli.Host;

/// <summary>What a start needs of the commit headroom, as <see cref="HostStartTracker.Judge"/> reads it.</summary>
/// <param name="Covered">Whether the headroom covers <paramref name="NeedGb"/>, or is not judged.</param>
/// <param name="HeadroomGb">The commit limit less the charge, in gigabytes; NaN when the platform reports none.</param>
/// <param name="NeedGb">The start's own expected peak plus <paramref name="GrowthGb"/>, in gigabytes.</param>
/// <param name="OwnGb">The start's own expected peak, in gigabytes.</param>
/// <param name="GrowthGb">What the starts already admitted still have to grow to reach their expected peaks, in
/// gigabytes.</param>
/// <param name="Growing">How many admitted starts are still below their expected peak.</param>
/// <param name="Kind">The start's kind.</param>
public readonly record struct HostStartDemand(bool Covered, double HeadroomGb, double NeedGb, double OwnGb, double GrowthGb, int Growing, HostStartKind Kind);

/// <summary>
/// Admits a start by its expected peak rather than by the commit charge at the instant it asks: admitted Worlds grow
/// (a windowed debug World reaches 12 to 18 GB private within minutes), so several starts admitted at a low charge grow
/// into each other. A start is covered when the commit headroom (limit less charge) holds its own expected peak
/// (<see cref="HostLoadThresholds.DefaultPeakGb"/>, raised by the measured peak of a finished start of the same kind
/// this process saw, to at most <see cref="HostLoadThresholds.MeasuredPeakCeilingFactor"/> times the default) plus what
/// every admitted start still has to grow.
/// <para>Admitted starts come from the live processes each reading lists (<see cref="HostSample.Starts"/>, classified by
/// <see cref="HostProcesses.RunningStart"/>) with their current private bytes, so a process that exits stops counting at
/// once and nothing is read from a record that can go stale. The one thing a reading cannot show is a start admitted a
/// moment ago whose process has not appeared: this tracker holds that start as a reservation of its full peak until a
/// new live process of its kind appears or <see cref="ReservationHold"/> passes, which is what keeps two starts asked in
/// a row from both taking the same headroom. Reservations and measured peaks live in this process's memory only, never
/// in a file, so they cover the starts this process admits (an MCP session, the concurrent workers of one verb) and not
/// those another process admits at the same instant, whose processes are counted once they run.</para>
/// </summary>
public sealed class HostStartTracker {
    private sealed record Reservation(HostStartKind Kind, long At, double Gb, HashSet<int> Known);

    /// <summary>How long an admitted start holds its full expected peak before its live process is expected to show.</summary>
    public static readonly TimeSpan ReservationHold = TimeSpan.FromSeconds(seconds: 90);

    private readonly Lock m_gate = new();
    private readonly Dictionary<HostStartKind, double> m_measured = [];
    private readonly List<Reservation> m_reservations = [];
    private readonly Dictionary<int, (HostStartKind Kind, double PeakGb)> m_seen = [];

    /// <summary>Gets the tracker of this process's host session, which the admission of a real machine reading uses.</summary>
    public static HostStartTracker Session { get; } = new();

    private double Expected(HostStartKind kind) {
        var baseline = HostLoadThresholds.DefaultPeakGb(kind: kind);

        return (m_measured.TryGetValue(key: kind, value: out var measured)
            ? Math.Min(val1: Math.Max(val1: baseline, val2: measured), val2: (baseline * HostLoadThresholds.MeasuredPeakCeilingFactor))
            : baseline);
    }

    /// <summary>Returns the commit a start of a kind is expected to reach: its default raised by the measured peak of a
    /// finished start of the same kind this tracker saw.</summary>
    /// <param name="kind">The start's kind.</param>
    /// <returns>The expected peak, in gigabytes.</returns>
    public double ExpectedPeakGb(HostStartKind kind) {
        lock (m_gate) {
            return Expected(kind: kind);
        }
    }
    /// <summary>Reads what a start of <paramref name="kind"/> needs of the headroom <paramref name="reading"/> shows,
    /// folding the reading's processes into the tracker first, and, when <paramref name="reserve"/> is set and the start is
    /// covered, holds its peak as a reservation in the same step so a concurrent start reads it.</summary>
    /// <param name="reading">The machine reading.</param>
    /// <param name="kind">The start's kind.</param>
    /// <param name="thresholds">The thresholds that judge whether commit headroom is judged at all.</param>
    /// <param name="clock">The clock a reservation's hold is measured on.</param>
    /// <param name="reserve">Whether a covered start is admitted by this call and so reserves its peak.</param>
    /// <returns>The start's demand.</returns>
    public HostStartDemand Judge(HostSample reading, HostStartKind kind, HostLoadThresholds thresholds, TimeProvider clock, bool reserve) {
        lock (m_gate) {
            var live = (reading.Starts ?? []);
            var liveIds = new HashSet<int>(collection: live.Select(selector: static start => start.Id));

            foreach (var start in live) {
                m_seen[start.Id] = (start.Kind, Math.Max(val1: start.PrivateGb, val2: (m_seen.TryGetValue(key: start.Id, value: out var seen) ? seen.PeakGb : 0)));
            }
            foreach (var (id, seen) in m_seen.Where(predicate: pair => !liveIds.Contains(item: pair.Key)).ToArray()) {
                m_measured[seen.Kind] = Math.Max(val1: seen.PeakGb, val2: (m_measured.TryGetValue(key: seen.Kind, value: out var measured) ? measured : 0));
                _ = m_seen.Remove(key: id);
            }

            // A reservation ends when its hold passes or a live process of its kind it did not know of appears; each new
            // process answers one reservation.
            var claimed = new HashSet<int>();
            List<Reservation> kept = [];

            foreach (var reservation in m_reservations) {
                if (clock.GetElapsedTime(startingTimestamp: reservation.At) >= ReservationHold) { continue; }

                var answered = false;

                foreach (var start in live) {
                    if ((start.Kind == reservation.Kind) && !reservation.Known.Contains(item: start.Id) && claimed.Add(item: start.Id)) {
                        answered = true;

                        break;
                    }
                }
                if (!answered) { kept.Add(item: reservation); }
            }

            m_reservations.Clear();
            m_reservations.AddRange(collection: kept);

            var growth = 0.0;
            var growing = 0;

            foreach (var start in live) {
                var left = (Expected(kind: start.Kind) - start.PrivateGb);

                if (left > 0) {
                    growth += left;
                    growing++;
                }
            }
            foreach (var reservation in m_reservations) {
                growth += reservation.Gb;
                growing++;
            }

            var own = Expected(kind: kind);

            if (double.IsFinite(d: reading.CommitLimitGb) && (reading.CommitLimitGb > 0)) {
                own = Math.Min(val1: own, val2: (HostLoadThresholds.PeakShareOfCommitLimit * reading.CommitLimitGb));
            }

            var need = (own + growth);
            var covered = thresholds.CommitCovers(reading: reading, needGb: need);

            if (reserve && covered && (kind != HostStartKind.Step)) {
                m_reservations.Add(item: new Reservation(Kind: kind, At: clock.GetTimestamp(), Gb: own, Known: liveIds));
            }

            return new HostStartDemand(
                Covered: covered,
                Growing: growing,
                GrowthGb: growth,
                HeadroomGb: (reading.CommitLimitGb - reading.CommitChargeGb),
                Kind: kind,
                NeedGb: need,
                OwnGb: own
            );
        }
    }
}
