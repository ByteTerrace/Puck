namespace Puck.World.Server;

/// <summary>Stored, unaged impression state. Numeric belief values are raw Q48.16; UpdatedAt is an engine-time
/// anchor on the current bank's clock, possibly negative when the history predates that authority's clock origin.</summary>
public readonly record struct WorldSocialImpressionCheckpoint(
    WorldSocialImpressionKey Key, long Value, long Weight, long Uncertainty, Int128 UpdatedAt, ulong IndependentEvents, ulong FirstReceiptOrdinal
);

/// <summary>One exact evidence receipt, retained independently of whether its impression is still remembered.
/// OccurredAt is the immutable original event timestamp; LocalOccurredAt is its possibly negative aging anchor
/// on the current bank's clock. Clock rebasing changes the latter, never the former.</summary>
public readonly record struct WorldSocialReceiptCheckpoint(
    WorldSocialImpressionKey Impression, WorldSocialEventKey Event, ulong OccurredAt, Int128 LocalOccurredAt, ulong Ordinal,
    long Value, long Weight, bool Direct, bool ConflictSeen, Puck.World.Protocol.WorldEntityAddress? OriginalSource, long OriginalValue
);

/// <summary>A detached social-memory checkpoint. Restoring requires an exactly matching compiled source policy.
/// The authority checkpoint codec serializes these logical records, source holds, and import reservations; allocation
/// indexes are rebuilt on restore. Null hold collections are equivalent to empty collections for API callers.</summary>
public sealed record WorldSocialMemoryCheckpoint(
    string PolicyIdentity, ulong EngineTick, int EvidenceAttempts, int ReclaimedReceipts, ulong NextOrdinal,
    IReadOnlyList<WorldSocialImpressionCheckpoint> Impressions, IReadOnlyList<WorldSocialReceiptCheckpoint> Receipts,
    IReadOnlyList<WorldSocialImportReservationCheckpoint>? ImportReservations = null,
    IReadOnlyList<WorldSocialFrozenObserverCheckpoint>? FrozenObservers = null
);

public sealed partial class WorldSocialMemory {
    /// <summary>Copies logical state into canonical observer/subject/dimension order, with receipts in admission order.
    /// Capture deliberately allocates; the returned collections share no mutable storage with the bank.</summary>
    /// <returns>A detached checkpoint.</returns>
    public WorldSocialMemoryCheckpoint Capture() {
        var impressions = new WorldSocialImpressionCheckpoint[m_impressions.Count];
        var index = 0;
        foreach (var (key, state) in m_impressions) {
            impressions[index++] = new(key, state.Value, state.Weight, state.Uncertainty, state.UpdatedAt, state.IndependentEvents, state.FirstReceiptOrdinal);
        }
        Array.Sort(impressions, static (left, right) => Compare(left.Key, right.Key));
        var receipts = new WorldSocialReceiptCheckpoint[m_receipts.Count];
        index = 0;
        foreach (var (key, state) in m_receipts) {
            receipts[index++] = new(key.Impression, key.Event, state.OccurredAt, state.LocalOccurredAt, state.Ordinal, state.Value,
                state.Weight, state.Direct, state.ConflictSeen, state.OriginalSource, state.OriginalValue);
        }
        Array.Sort(receipts, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        return new(Policy.Identity, EngineTick, EvidenceAttempts, ReclaimedReceipts, m_nextOrdinal, impressions, receipts,
            CaptureImportReservations(), CaptureFrozenObservers());
    }

    /// <summary>Validates a complete checkpoint and returns a new independent bank. Failure cannot modify an existing
    /// bank. Expired receipts still awaiting bounded reclamation are preserved rather than silently cleaned.</summary>
    /// <param name="policy">The matching compiled policy.</param>
    /// <param name="checkpoint">The detached state to restore.</param>
    /// <returns>A restored bank with identical future-affecting state.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The checkpoint contains an invalid value, duplicate, capacity, or policy identity.</exception>
    public static WorldSocialMemory Restore(CompiledWorldSocialPolicy policy, WorldSocialMemoryCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint = ValidateCheckpoint(policy, checkpoint);
        var bank = new WorldSocialMemory(policy) {
            EngineTick = checkpoint.EngineTick, EvidenceAttempts = checkpoint.EvidenceAttempts,
            ReclaimedReceipts = checkpoint.ReclaimedReceipts, m_nextOrdinal = checkpoint.NextOrdinal,
        };
        bank.AddCheckpointRows(checkpoint);
        foreach (var row in checkpoint.ImportReservations!) { bank.AddImportReservation(row.Key, (WorldSocialImportAllowance[])row.Members); }
        foreach (var row in checkpoint.FrozenObservers!) { bank.AddFrozenObserver(row); }
        return bank;
    }

    private void AddCheckpointRows(WorldSocialMemoryCheckpoint checkpoint) {
        // All callers supply ValidateCheckpoint's owned arrays. Concrete enumeration also keeps the prepared
        // commit allocation-free before tiered PGO; interface enumeration boxes twice on a cold crossing.
        foreach (var row in (WorldSocialImpressionCheckpoint[])checkpoint.Impressions) {
            Put(row.Key, new(row.Value, row.Weight, row.Uncertainty, row.UpdatedAt, row.IndependentEvents, row.FirstReceiptOrdinal));
        }
        foreach (var row in (WorldSocialReceiptCheckpoint[])checkpoint.Receipts) {
            Put(new ReceiptKey(row.Impression, row.Event), new(row.OccurredAt, row.LocalOccurredAt, row.Ordinal,
                row.Value, row.Weight, row.Direct, row.ConflictSeen, row.OriginalSource, row.OriginalValue));
        }
    }

    // Validation owns detached copies and uses scratch proportional to the supplied records, not the policy's
    // world-wide capacities. Restore and individual intake share exactly the same logical-state admission rules.
    private static WorldSocialMemoryCheckpoint ValidateCheckpoint(CompiledWorldSocialPolicy policy, WorldSocialMemoryCheckpoint checkpoint) {
        Require(checkpoint.PolicyIdentity == policy.Identity && checkpoint.Impressions is not null && checkpoint.Receipts is not null &&
            checkpoint.Impressions.Count >= 0 && checkpoint.Impressions.Count <= policy.ImpressionCapacity &&
            checkpoint.Receipts.Count >= 0 && checkpoint.Receipts.Count <= policy.ReceiptCapacity &&
            checkpoint.EvidenceAttempts >= 0 && checkpoint.EvidenceAttempts <= policy.EvidenceAttemptsPerTick &&
            checkpoint.ReclaimedReceipts >= 0 && checkpoint.ReclaimedReceipts <= policy.ExpiredReceiptsPerTick &&
            (checkpoint.EngineTick != 0 || checkpoint.ReclaimedReceipts == 0));
        checkpoint = checkpoint with {
            Impressions = CopyRows(checkpoint.Impressions!, policy.ImpressionCapacity),
            Receipts = CopyRows(checkpoint.Receipts!, policy.ReceiptCapacity),
        };
        var impressions = new HashSet<WorldSocialImpressionKey>(checkpoint.Impressions.Count);
        var owners = new Dictionary<Puck.World.Protocol.WorldEntityAddress, int>();
        foreach (var row in checkpoint.Impressions) {
            Require(Valid(row.Key, policy) && impressions.Add(row.Key) && row.UpdatedAt <= checkpoint.EngineTick &&
                row.IndependentEvents > 0 && row.Uncertainty >= 0 && row.Uncertainty <= One && row.FirstReceiptOrdinal < checkpoint.NextOrdinal &&
                row.IndependentEvents <= checkpoint.NextOrdinal - row.FirstReceiptOrdinal);
            var d = policy.Dimensions[row.Key.Dimension];
            var count = owners.GetValueOrDefault(row.Key.Observer);
            Require(row.Value >= d.Minimum && row.Value <= d.Maximum && row.Weight >= 0 && row.Weight <= d.WeightCapacity &&
                count < policy.ImpressionsPerObserver);
            owners[row.Key.Observer] = count + 1;
        }
        var ordinals = new HashSet<ulong>(checkpoint.Receipts.Count);
        var receipts = new HashSet<ReceiptKey>(checkpoint.Receipts.Count);
        foreach (var row in checkpoint.Receipts) {
            Require(Valid(row.Impression, policy) && Valid(row.Event.Origin) && !string.IsNullOrWhiteSpace(row.Event.Aspect) &&
                row.Event.Aspect.Length <= 64 && row.LocalOccurredAt <= checkpoint.EngineTick && row.Ordinal < checkpoint.NextOrdinal && ordinals.Add(row.Ordinal) &&
                (row.OriginalSource is null || Valid(row.OriginalSource.Value)) && (row.Direct || row.OriginalSource is not null));
            var d = policy.Dimensions[row.Impression.Dimension];
            var maximumWeight = !row.Direct ? policy.ReportWeight : row.OriginalSource is null
                ? Multiply(policy.DirectWeight, One + d.FollowUpBoost) : Math.Max(policy.ReportWeight, policy.DirectWeight);
            Require(row.Value >= d.Minimum && row.Value <= d.Maximum && row.OriginalValue >= d.Minimum && row.OriginalValue <= d.Maximum &&
                row.Weight > 0 && row.Weight <= maximumWeight &&
                (row.Direct ? policy.DirectWeight > 0 : row.Value == row.OriginalValue) &&
                (row.OriginalSource is not null || (row.Direct && !row.ConflictSeen && row.Value == row.OriginalValue)));
            Require(receipts.Add(new ReceiptKey(row.Impression, row.Event)));
            owners.TryAdd(row.Impression.Observer, 0);
        }
        checkpoint = checkpoint with { ImportReservations = ValidateImportReservations(policy, checkpoint.ImportReservations,
            owners.Keys, checkpoint.Impressions.Count, checkpoint.Receipts.Count) };
        return checkpoint with { FrozenObservers = ValidateFrozenObservers(checkpoint) };
    }

    private static T[] CopyRows<T>(IReadOnlyList<T> rows, int maximum, int minimum = 0) {
        // Count is caller code too. Recheck the exact value used for allocation, even if an earlier guard read it.
        var count = rows.Count;
        Require(count >= minimum && count <= maximum);
        var copy = new T[count];
        var index = 0;
        foreach (var row in rows) {
            Require(index < copy.Length);
            copy[index++] = row;
        }
        Require(index == copy.Length);
        return copy;
    }

    private static void Require(bool condition) {
        if (!condition) { throw new ArgumentException("invalid social memory checkpoint", "checkpoint"); }
    }

    private static int Compare(WorldSocialImpressionKey left, WorldSocialImpressionKey right) {
        var comparison = Compare(left.Observer, right.Observer);
        if (comparison == 0) { comparison = Compare(left.Subject, right.Subject); }
        return comparison != 0 ? comparison : left.Dimension.CompareTo(right.Dimension);
    }
    private static int Compare(Puck.World.Protocol.WorldEntityAddress left, Puck.World.Protocol.WorldEntityAddress right) {
        var comparison = StringComparer.Ordinal.Compare(left.Authority, right.Authority);
        if (comparison == 0) { comparison = left.Index.CompareTo(right.Index); }
        return comparison != 0 ? comparison : left.Generation.CompareTo(right.Generation);
    }
}
