using System.Globalization;
using Puck.World.Server;

namespace Puck.World;

/// <summary>What an explicit stop of a running instance did.</summary>
public enum WorldInstanceStop : byte {
    /// <summary>The instance was retired, after its final image where the host keeps durable images.</summary>
    Retired,
    /// <summary>The instance holds custody or an accepted arrival: it sends its visiting companions home and retires at
    /// the end of a later drain.</summary>
    Stopping,
    /// <summary>Nothing was stopped; the reason names why.</summary>
    Refused,
}
/// <summary>Writes a row's image where a later activation recovers it from, together with the row's crossing log.</summary>
/// <param name="row">The row, still admitted and not yet disposed.</param>
/// <param name="checkpoint">The row's image, its host slice included.</param>
/// <param name="reason">Why the image is not durable, on failure.</param>
/// <returns><see langword="true"/> once the image is durable.</returns>
public delegate bool WorldDurableImageWriter(WorldInstance row, WorldAuthorityCheckpoint checkpoint, out string reason);

public sealed partial class WorldInstanceHost {
    // Rows an explicit stop is retiring. Captured in each row's host slice, so a restart resumes the stop.
    private readonly HashSet<string> m_stopping = new(comparer: StringComparer.Ordinal);
    // The last wait or failure narrated for each stopping row, so a stop that waits across drains narrates once.
    private readonly Dictionary<string, string> m_stopNarrated = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets or sets where an explicit stop writes a row's image: once when the stop is accepted, so a restart
    /// resumes it, and once more as the final image before the row retires. <see langword="null"/> for a host whose rows
    /// keep no durable image: a stop there never retires a row that still holds custody. It refuses what only another
    /// authority ends and waits for what its drains settle.</summary>
    public WorldDurableImageWriter? DurableImages { get; set; }

    // What keeps a row from retiring without a durable image of it: the titles it holds abroad, which only their
    // custodian's return, a recall or a hop's resolution ends; the custody steps its drains still owe, its own departures
    // in doubt among them; the visiting companions it embodies, whose title is another authority's; and the arrivals it
    // accepted that have not landed. Reaping and an explicit stop read this one guard, on a row not frozen for retirement.
    private readonly record struct CustodyHold(IReadOnlyList<WorldCustodyTitle> Abroad, bool Owes, int[] Visiting, int Arriving) {
        public bool Holds => ((Abroad.Count > 0) || Owes || (Visiting.Length > 0) || (Arriving > 0));

        public string Describe() {
            var parts = new List<string>();

            if (Abroad.Count > 0) {
                parts.Add(item: $"holds {string.Join(separator: ", ", values: Abroad.Select(selector: static title => ((title.Kind == WorldCustodyKind.Away)
                    ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"the title of {title.Incarnation} away at '{title.Custodian}' epoch {title.Epoch}")
                    : $"the title of {title.Incarnation} pending hop {title.PendingKey}")))}");
            }
            if (Owes) {
                parts.Add(item: "owes custody steps its drain makes");
            }
            if (Visiting.Length > 0) {
                parts.Add(item: $"embodies visiting companions at {string.Join(separator: ", ", values: Visiting.Select(selector: static slot => string.Create(provider: CultureInfo.InvariantCulture, handler: $"body:{slot}")))}");
            }
            if (Arriving > 0) {
                parts.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"has accepted {Arriving} arrival(s) that have not landed"));
            }
            return ((parts.Count == 0)
                ? "holds no custody"
                : string.Join(separator: "; ", values: parts));
        }
    }

    private CustodyHold CustodyHeldBy(WorldInstance instance) {
        var (abroad, owes, arriving) = instance.Server.ExecuteAuthorityOperation(operation: () => (
            instance.Server.TransferEscrow.Custody.TitlesAbroad(),
            instance.Server.TransferEscrow.Custody.OwesCustodySteps,
            instance.Server.TransferEscrow.AcceptedArrivals
        ));
        var inDoubt = m_inDoubtTransfers.Any(predicate: pending => string.Equals(a: pending.Transfer.SourceInstance, b: instance.Name, comparisonType: StringComparison.Ordinal));

        return new CustodyHold(
            Abroad: abroad,
            Arriving: arriving,
            Owes: (owes || inDoubt),
            Visiting: VisitingCompanions(instance: instance)
        );
    }
    // The body indices of every visiting companion the row embodies: the bodies whose return home its sweep queues.
    private static int[] VisitingCompanions(WorldInstance instance) => [.. Enumerable.Range(start: 0, count: instance.Server.Population.Capacity)
        .Where(predicate: slot => (ReturnLeaf(slot: slot, source: instance) is not null))];
    // Every admitted row not frozen for retirement. A frozen row answers no authority operation (it throws), and a silo
    // row stays admitted while its final image uploads, so the drain's custody passes walk only these.
    private WorldInstance[] UnfrozenRows() => [.. m_instances.Values.Where(predicate: static row => !row.Server.IsRetiring)];
    // The running, unfrozen row that holds a title authority's titles, or null when this host runs none.
    private WorldInstance? RunningAuthority(string authority) => m_instances.Values.FirstOrDefault(predicate: row => (
        !row.Server.IsRetiring &&
        string.Equals(a: row.Server.AuthorityIdentity, b: authority, comparisonType: StringComparison.Ordinal)
    ));

    /// <summary>Stops a running instance without discarding the custody it holds. A stop that is accepted is part of
    /// the row's image, written through <see cref="DurableImages"/> before it is acknowledged, so a restart resumes it.
    /// The stop sends every visiting companion home, again at each drain until none is embodied here, and the row retires
    /// at the end of the first drain at which, under the row's authority gate, it embodies no visiting companion and
    /// holds no accepted arrival that has not landed: admission closes in that same hold of the gate
    /// (<see cref="WorldServer.TryFreezeForRetirement"/>), so no arrival lands between the check and the retirement.
    /// The final image then carries whatever is still abroad or in doubt; a failed final write keeps the row frozen and
    /// is tried again at the next drain. A host without durable images refuses the stop of a row holding a title abroad,
    /// waits while the row owes steps its drains make, and abandons the stop when a title goes abroad while it waits.
    /// A stop is refused while a visiting companion's home is no row this host runs, since nothing could send it home.
    /// Every later outcome is narrated on <c>world.instance.stop</c>.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="reason">What the stop is waiting for, or why it was refused.</param>
    /// <returns>Whether the instance was retired, is stopping, or was refused.</returns>
    public WorldInstanceStop Stop(string name, out string reason) {
        if (!TryRetirable(instance: out var instance, name: name, reason: out reason)) {
            return WorldInstanceStop.Refused;
        }
        if (m_stopping.Contains(item: name)) {
            reason = $"'{name}' is already stopping";
            return WorldInstanceStop.Stopping;
        }
        if (instance.Server.IsRetiring) {
            reason = $"'{name}' is already retiring";
            return WorldInstanceStop.Refused;
        }

        var hold = CustodyHeldBy(instance: instance);

        if ((DurableImages is null) && (hold.Abroad.Count > 0)) {
            reason = $"'{name}' {hold.Describe()}, and this host keeps no durable image to carry a title abroad until its custodian returns it";
            return WorldInstanceStop.Refused;
        }
        foreach (var slot in hold.Visiting) {
            if ((ReturnLeaf(slot: slot, source: instance) is { } leaf) && (RunningAuthority(authority: leaf.Home) is null)) {
                reason = string.Create(provider: CultureInfo.InvariantCulture, handler: $"'{name}' embodies the visiting companion body:{slot}, whose home '{leaf.Home}' is no row this host runs, so the stop could not send it home");
                return WorldInstanceStop.Refused;
            }
        }
        _ = m_stopping.Add(item: name);
        if (!TryWriteImage(instance: instance, reason: out reason)) {
            _ = m_stopping.Remove(item: name);
            return WorldInstanceStop.Refused;
        }
        switch (AdvanceStop(instance: instance, waiting: out reason)) {
            case StopProgress.Retired:
                return WorldInstanceStop.Retired;
            case StopProgress.Abandoned:
                return WorldInstanceStop.Refused;
            default:
                m_stopNarrated[name] = reason;
                return WorldInstanceStop.Stopping;
        }
    }
    /// <summary>Reads what custody a running instance holds and whether an explicit stop is retiring it.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="custody">The custody it holds, in words ("holds no custody" when none; "retiring" once frozen).</param>
    /// <param name="stopping">Whether an explicit stop is retiring it.</param>
    /// <returns><see langword="false"/> when no instance runs under <paramref name="name"/>.</returns>
    public bool TryDescribeCustody(string name, out string custody, out bool stopping) {
        if (!m_instances.TryGetValue(key: name, value: out var instance)) {
            custody = string.Empty;
            stopping = false;
            return false;
        }
        custody = (instance.Server.IsRetiring
            ? "retiring"
            : CustodyHeldBy(instance: instance).Describe());
        stopping = m_stopping.Contains(item: name);
        return true;
    }

    private enum StopProgress : byte { Retired, Waiting, Abandoned }

    // One step of a stopping row: send home what still visits, freeze under the final check, write the final image,
    // retire. Waiting leaves the row stopping; Abandoned drops the stop.
    private StopProgress AdvanceStop(WorldInstance instance, out string waiting) {
        var name = instance.Name;

        if (!instance.Server.IsRetiring) {
            if (m_pendingTransfers.Any(predicate: pending => string.Equals(a: pending.SourceInstance, b: name, comparisonType: StringComparison.Ordinal))) {
                waiting = $"'{name}' waits for its queued departures";
                return StopProgress.Waiting;
            }

            var sent = 0;

            foreach (var slot in VisitingCompanions(instance: instance)) {
                if (
                    (ReturnLeaf(slot: slot, source: instance) is { } leaf) &&
                    (RunningAuthority(authority: leaf.Home) is not null) &&
                    TryEnqueueCompanionReturn(reason: out _, slot: slot, sourceInstance: name, transferId: out _)
                ) {
                    sent++;
                }
            }
            if (sent > 0) {
                waiting = string.Create(provider: CultureInfo.InvariantCulture, handler: $"'{name}' sent {sent} visiting companion(s) home and retires after the drain that settles them");
                return StopProgress.Waiting;
            }

            var hold = default(CustodyHold);
            var abandon = false;
            var frozen = instance.Server.TryFreezeForRetirement(admits: () => {
                hold = CustodyHeldBy(instance: instance);
                if ((hold.Visiting.Length > 0) || (hold.Arriving > 0)) {
                    return false;
                }
                if (DurableImages is null) {
                    abandon = (hold.Abroad.Count > 0);
                    return !hold.Holds;
                }
                return true;
            });

            if (!frozen) {
                waiting = (abandon
                    ? $"'{name}' {hold.Describe()}, and this host keeps no durable image to carry it"
                    : $"'{name}' waits: it {hold.Describe()}");
                if (abandon) {
                    _ = m_stopping.Remove(item: name);
                    _ = m_stopNarrated.Remove(key: name);
                    return StopProgress.Abandoned;
                }
                return StopProgress.Waiting;
            }
        }
        if (!TryWriteImage(instance: instance, reason: out waiting)) {
            return StopProgress.Waiting;
        }
        Retire(instance: instance);
        waiting = string.Empty;
        return StopProgress.Retired;
    }
    // Writes the row's image where the host keeps durable images; a host without them has nothing to write. A frozen row
    // is retiring, so its image is the final one, which a later activation starts from without resuming the stop.
    private bool TryWriteImage(WorldInstance instance, out string reason) {
        if (DurableImages is not { } writer) {
            reason = string.Empty;
            return true;
        }

        var final = instance.Server.IsRetiring;
        var hostRow = (CaptureRow(row: instance) with { Stopping = !final });

        if (!instance.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: hostRow, reason: out var captured)) {
            reason = $"'{instance.Name}' could not capture its {(final ? "final " : string.Empty)}image ({captured})";
            return false;
        }
        if (!writer(checkpoint: checkpoint!, reason: out var written, row: instance)) {
            reason = $"'{instance.Name}''s {(final ? "final " : string.Empty)}image is not durable ({written})";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    // Advances every stopping row at the end of the drain, after the custody steps the drain makes.
    private void CompleteStops() {
        if (m_stopping.Count == 0) {
            return;
        }
        foreach (var name in m_stopping.Order(comparer: StringComparer.Ordinal).ToArray()) {
            if (!m_instances.TryGetValue(key: name, value: out var instance)) {
                _ = m_stopping.Remove(item: name);
                _ = m_stopNarrated.Remove(key: name);
                continue;
            }
            switch (AdvanceStop(instance: instance, waiting: out var waiting)) {
                case StopProgress.Retired:
                    _ = m_stopNarrated.Remove(key: name);
                    NarrateStop(text: $"'{name}' retired");
                    break;
                case StopProgress.Abandoned:
                    NarrateStop(text: $"refused ({waiting})");
                    break;
                default:
                    if (!string.Equals(a: m_stopNarrated.GetValueOrDefault(key: name), b: waiting, comparisonType: StringComparison.Ordinal)) {
                        m_stopNarrated[name] = waiting;
                        NarrateStop(text: $"stopping ({waiting})");
                    }
                    break;
            }
        }
    }
    private void NarrateStop(string text) {
        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(channel: "world.instance.stop", text: $"[world.instance.stop: {text}]");
        }
    }
}
