using System.Globalization;
using Puck.World.Server;

namespace Puck.World;

/// <summary>What an explicit stop of a running instance did.</summary>
public enum WorldInstanceStop : byte {
    /// <summary>The instance was retired, after its final checkpoint where the host keeps durable images.</summary>
    Retired,
    /// <summary>The instance holds custody: its visiting companions were sent home, and it retires at the end of a
    /// later drain.</summary>
    Stopping,
    /// <summary>Nothing was stopped; the reason names why.</summary>
    Refused,
}
/// <summary>Writes a stopping row's final checkpoint where a later activation recovers it from, together with the row's
/// crossing log.</summary>
/// <param name="row">The row being stopped, still admitted and not yet disposed.</param>
/// <param name="checkpoint">The row's final image, its host slice included.</param>
/// <param name="reason">Why the image is not durable, on failure.</param>
/// <returns><see langword="true"/> once the image is durable.</returns>
public delegate bool WorldFinalCheckpointWriter(WorldInstance row, WorldAuthorityCheckpoint checkpoint, out string reason);

public sealed partial class WorldInstanceHost {
    // Rows an explicit stop is retiring once the drain has settled what it swept (CompleteStops).
    private readonly HashSet<string> m_stopping = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets or sets where an explicit stop writes each row's final checkpoint, or <see langword="null"/> for a
    /// host whose rows keep no durable image. Without one, a stop never retires a row that still holds custody: it waits
    /// for what the drain settles and refuses what only another authority ends.</summary>
    public WorldFinalCheckpointWriter? FinalCheckpoints { get; set; }

    // What keeps a row from retiring without a durable image of it: the titles it holds abroad, which only their
    // custodian's return, a recall or a hop's resolution ends; the custody steps its drains still owe, its own departures
    // in doubt among them; and the visiting companions it embodies, whose title is another authority's. Reaping and an
    // explicit stop read this one guard.
    private readonly record struct CustodyHold(IReadOnlyList<WorldCustodyTitle> Abroad, bool Owes, int[] Visiting) {
        public bool Holds => ((Abroad.Count > 0) || Owes || (Visiting.Length > 0));

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
            return ((parts.Count == 0)
                ? "holds no custody"
                : string.Join(separator: "; ", values: parts));
        }
    }

    private CustodyHold CustodyHeldBy(WorldInstance instance) {
        var (abroad, owes) = instance.Server.ExecuteAuthorityOperation(operation: () => (
            instance.Server.TransferEscrow.Custody.TitlesAbroad(),
            instance.Server.TransferEscrow.Custody.OwesCustodySteps
        ));
        var inDoubt = m_inDoubtTransfers.Any(predicate: pending => string.Equals(a: pending.Transfer.SourceInstance, b: instance.Name, comparisonType: StringComparison.Ordinal));

        return new CustodyHold(
            Abroad: abroad,
            Owes: (owes || inDoubt),
            Visiting: VisitingCompanions(instance: instance)
        );
    }
    // The body indices of every visiting companion the row embodies: the bodies whose return home its sweep queues.
    private static int[] VisitingCompanions(WorldInstance instance) => [.. Enumerable.Range(start: 0, count: instance.Server.Population.Capacity)
        .Where(predicate: slot => (ReturnLeaf(slot: slot, source: instance) is not null))];

    /// <summary>Stops a running instance without discarding the custody it holds. A row holding none writes its final
    /// checkpoint through <see cref="FinalCheckpoints"/> (when the host keeps durable images) and retires now. A row
    /// holding custody first sweeps it: every visiting companion whose home this host runs is sent home, and the row
    /// retires at the end of the first drain that leaves nothing of the sweep queued, with a final checkpoint that
    /// carries whatever is still abroad or in doubt then. A host without durable images refuses the stop of a row that
    /// holds titles abroad, waits while the row owes steps its drains make, and abandons the stop, narrating why, when
    /// the drain leaves a title abroad or a visiting companion it could not send home. The completion and any later
    /// refusal are narrated on <c>world.instance.stop</c>.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="reason">What the stop did or is waiting for, or why it was refused.</param>
    /// <returns>Whether the instance was retired, is stopping, or was refused.</returns>
    public WorldInstanceStop Stop(string name, out string reason) {
        if (!TryRetirable(instance: out var instance, name: name, reason: out reason)) {
            return WorldInstanceStop.Refused;
        }

        var hold = CustodyHeldBy(instance: instance);

        if (m_stopping.Contains(item: name)) {
            reason = $"'{name}' is already stopping: it {hold.Describe()}";
            return WorldInstanceStop.Stopping;
        }
        if (!hold.Holds) {
            if (!TryWriteFinalCheckpoint(instance: instance, reason: out reason)) {
                return WorldInstanceStop.Refused;
            }
            Retire(instance: instance);
            return WorldInstanceStop.Retired;
        }
        if ((FinalCheckpoints is null) && (hold.Abroad.Count > 0)) {
            reason = $"'{name}' {hold.Describe()}, and this host keeps no durable image to carry a title abroad until its custodian returns it";
            return WorldInstanceStop.Refused;
        }

        var sent = 0;

        foreach (var slot in hold.Visiting) {
            if (TryEnqueueCompanionReturn(reason: out _, slot: slot, sourceInstance: name, transferId: out _)) {
                sent++;
            }
        }
        _ = m_stopping.Add(item: name);
        reason = string.Create(provider: CultureInfo.InvariantCulture, handler: $"'{name}' {hold.Describe()}; {sent} visiting companion(s) sent home; it retires after the drain");
        return WorldInstanceStop.Stopping;
    }
    /// <summary>Reads what custody a running instance holds and whether an explicit stop is retiring it.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="custody">The custody it holds, in words ("holds no custody" when none).</param>
    /// <param name="stopping">Whether an explicit stop waits for the drain to retire it.</param>
    /// <returns><see langword="false"/> when no instance runs under <paramref name="name"/>.</returns>
    public bool TryDescribeCustody(string name, out string custody, out bool stopping) {
        if (!m_instances.TryGetValue(key: name, value: out var instance)) {
            custody = string.Empty;
            stopping = false;
            return false;
        }
        custody = CustodyHeldBy(instance: instance).Describe();
        stopping = m_stopping.Contains(item: name);
        return true;
    }

    // Writes the row's final image where the host keeps durable images; a host without them has nothing to write.
    private bool TryWriteFinalCheckpoint(WorldInstance instance, out string reason) {
        if (FinalCheckpoints is not { } writer) {
            reason = string.Empty;
            return true;
        }
        if (!instance.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: CaptureRow(row: instance), reason: out var captured)) {
            reason = $"'{instance.Name}' could not capture its final checkpoint ({captured})";
            return false;
        }
        if (!writer(checkpoint: checkpoint!, reason: out var written, row: instance)) {
            reason = $"'{instance.Name}''s final checkpoint is not durable ({written})";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    // Retires every stopping row whose sweep the drain has settled: nothing it sent home is still queued. Runs at the end
    // of the drain, after the custody steps the drain makes.
    private void CompleteStops() {
        if (m_stopping.Count == 0) {
            return;
        }
        foreach (var name in m_stopping.Order(comparer: StringComparer.Ordinal).ToArray()) {
            if (!m_instances.TryGetValue(key: name, value: out var instance)) {
                _ = m_stopping.Remove(item: name);
                continue;
            }
            if (m_pendingTransfers.Any(predicate: pending => string.Equals(a: pending.SourceInstance, b: name, comparisonType: StringComparison.Ordinal))) {
                continue;
            }

            var hold = CustodyHeldBy(instance: instance);

            if (hold.Holds && (FinalCheckpoints is null)) {
                if ((hold.Abroad.Count == 0) && (hold.Visiting.Length == 0)) {
                    continue;
                }
                _ = m_stopping.Remove(item: name);
                NarrateStop(text: $"refused ('{name}' {hold.Describe()}, and this host keeps no durable image to carry it)");
                continue;
            }
            if (
                !TryRetirable(instance: out _, name: name, reason: out var reason) ||
                !TryWriteFinalCheckpoint(instance: instance, reason: out reason)
            ) {
                _ = m_stopping.Remove(item: name);
                NarrateStop(text: $"refused ({reason})");
                continue;
            }
            Retire(instance: instance);
            NarrateStop(text: (hold.Holds
                ? $"'{name}' retired; its final checkpoint carries what it {hold.Describe()}"
                : $"'{name}' retired"));
        }
    }
    private void NarrateStop(string text) {
        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(channel: "world.instance.stop", text: $"[world.instance.stop: {text}]");
        }
    }
}
