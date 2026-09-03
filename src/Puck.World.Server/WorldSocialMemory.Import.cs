using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldSocialMemory {
    /// <summary>Atomically adds one previously absent observer's memory, preserving ages, original event identities,
    /// and forgetting boundaries. Validation and temporary storage scale with incoming records, not bank capacity.</summary>
    /// <remarks>The source policy must exactly describe the checkpoint. Destination storage/work budgets may differ,
    /// but dimension declarations and learning/evidence semantics must match; conflicting meanings are refused.
    /// No existing owner is overwritten or merged, even if it retains only receipts. Outstanding import reservations
    /// exclude their observers and held storage from this unreserved door. This single-writer operation
    /// does not reserve future space, freeze a source, authenticate ownership, or implement a transfer handshake.
    /// The caller must establish those conditions. Work counters and the destination clock are preserved. Successful
    /// intake allocates detached validation scratch; ordinary evidence ingestion remains allocation-free.</remarks>
    /// <param name="observer">The exact original mobility incarnation being admitted.</param>
    /// <param name="sourcePolicy">The immutable policy that gave the exported records their meaning.</param>
    /// <param name="checkpoint">Only the selected observer's records, on the source clock.</param>
    /// <param name="reason">Empty on success; a named input, ownership, policy, capacity, or representation refusal otherwise.</param>
    /// <returns>Whether all incoming records were admitted. A refusal changes no destination state.</returns>
    /// <exception cref="ArgumentNullException">The source policy or checkpoint is null.</exception>
    public bool TryImportObserver(WorldEntityAddress observer, CompiledWorldSocialPolicy sourcePolicy,
        WorldSocialMemoryCheckpoint checkpoint, out string reason) {
        ArgumentNullException.ThrowIfNull(sourcePolicy);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!Valid(observer)) { reason = "invalid social observer address"; return false; }
        if (m_reservedObservers.ContainsKey(observer)) { reason = "social observer has an import reservation"; return false; }
        if (!TryPrepareObserverImport(observer, sourcePolicy, checkpoint, m_nextOrdinal,
            Policy.ImpressionCapacity - ImpressionCount - ReservedImpressionCount,
            Policy.ReceiptCapacity - ReceiptCount - ReservedReceiptCount, out var incoming, out reason)) { return false; }
        AddCheckpointRows(incoming!);
        m_nextOrdinal = incoming!.NextOrdinal;
        return true;
    }

    // A prepared subset owns its arrays. No destination writes occur here; a reserved group prepares every member
    // against its own quota and one sequential ordinal cursor before applying any of them.
    private bool TryPrepareObserverImport(WorldEntityAddress observer, CompiledWorldSocialPolicy sourcePolicy,
        WorldSocialMemoryCheckpoint checkpoint, ulong ordinal, int impressionAllowance, int receiptAllowance,
        out WorldSocialMemoryCheckpoint? incoming, out string reason) {
        incoming = null;
        if (!Valid(observer)) { reason = "invalid social observer address"; return false; }
        if (IsObserverFrozen(observer)) { reason = "social observer memory is frozen here"; return false; }
        if (m_observers.ContainsKey(observer)) { reason = "social observer memory is already owned here"; return false; }
        if (!SameMemorySemantics(sourcePolicy, Policy)) { reason = "social memory policy semantics differ"; return false; }
        if (checkpoint.Impressions is not { } impressions || checkpoint.Receipts is not { } receipts ||
            impressions.Count < 0 || receipts.Count < 0) {
            reason = "invalid social memory checkpoint"; return false;
        }
        if (impressions.Count > Policy.ImpressionsPerObserver || impressions.Count > impressionAllowance) {
            reason = "social impression capacity is full for this observer"; return false;
        }
        if (receipts.Count > receiptAllowance) { reason = "social receipt capacity is full"; return false; }

        try {
            incoming = ValidateCheckpoint(sourcePolicy, checkpoint);
            // Validate the detached lengths as well as the cheap caller-side preflight. IReadOnlyList.Count may
            // change while validation copies it; source-policy validity alone does not prove destination quotas.
            if (incoming.Impressions.Count > Policy.ImpressionsPerObserver || incoming.Impressions.Count > impressionAllowance) {
                reason = "social impression capacity is full for this observer"; return false;
            }
            if (incoming.Receipts.Count > receiptAllowance) { reason = "social receipt capacity is full"; return false; }
            if (incoming.ImportReservations is { Count: > 0 }) { reason = "an observer import cannot carry authority reservations"; return false; }
            if (incoming.FrozenObservers is { Count: > 0 }) { reason = "an observer import cannot carry source ownership holds"; return false; }
            if (incoming.Impressions.Any(row => row.Key.Observer != observer) || incoming.Receipts.Any(row => row.Impression.Observer != observer)) {
                reason = "social memory checkpoint contains a different observer"; return false;
            }
            var impressionRows = (WorldSocialImpressionCheckpoint[])incoming.Impressions;
            var receiptRows = (WorldSocialReceiptCheckpoint[])incoming.Receipts;
            Array.Sort(receiptRows, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
            var delta = (Int128)EngineTick - incoming.EngineTick;
            var nextOrdinal = (ulong)receiptRows.Length;
            for (var index = 0; index < impressionRows.Length; index++) {
                var row = impressionRows[index];
                var first = (ulong)ReceiptBoundary(receiptRows, row.FirstReceiptOrdinal);
                nextOrdinal = Math.Max(nextOrdinal, checked(first + row.IndependentEvents));
                impressionRows[index] = row with {
                    UpdatedAt = checked(row.UpdatedAt + delta), FirstReceiptOrdinal = checked(ordinal + first),
                };
            }
            for (var index = 0; index < receiptRows.Length; index++) {
                var row = receiptRows[index];
                receiptRows[index] = row with {
                    LocalOccurredAt = checked(row.LocalOccurredAt + delta), Ordinal = checked(ordinal + (ulong)index),
                };
            }
            incoming = incoming with { EngineTick = EngineTick, NextOrdinal = checked(ordinal + nextOrdinal) };
        } catch (ArgumentException) { reason = "invalid social memory checkpoint"; return false; }
        catch (OverflowException) { reason = "social memory clock or admission ordinal cannot represent the import"; return false; }

        reason = string.Empty;
        return true;
    }

    private static bool SameMemorySemantics(CompiledWorldSocialPolicy left, CompiledWorldSocialPolicy right) =>
        left.Dimensions.SequenceEqual(right.Dimensions) && left.EvidenceLifetimeTicks == right.EvidenceLifetimeTicks &&
        left.ReliabilityDimension == right.ReliabilityDimension && left.UnfamiliarReliability == right.UnfamiliarReliability &&
        left.DirectWeight == right.DirectWeight && left.ReportWeight == right.ReportWeight;
}
