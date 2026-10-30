namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // A key that holds no live lease, retained commit or uncertain arrival here any more: committed (it answers
    // Committed) or dead (it answers Missing). No reservation leases either again and no commit lands under either.
    // Durable is whether a recovery from this authority's checkpoint and crossing log finds the key dead too; Missing is
    // answered only for a durable death, so a crash can never hand a resent commit a lease its source was told was gone.
    private readonly record struct SpentKey(bool Committed, bool Durable);

    // Written only through SetSpent and RemoveSpent, which keep m_sourceRows in step.
    private readonly Dictionary<WorldTransferKey, SpentKey> m_spent = new();
    // Each source namespace's count of rows in m_spent.
    private readonly Dictionary<string, int> m_sourceRows = new(comparer: StringComparer.Ordinal);
    // Per authenticated source namespace, its highest settled floor (WorldTransferReservationRequest.SourceFloor): every
    // id below it is dead here without a row.
    private readonly Dictionary<string, ulong> m_floors = new(comparer: StringComparer.Ordinal);
    // Keys a recovering activation found leased in its crossing log past its checkpoint and has not yet seen arrive.
    private readonly HashSet<WorldTransferKey> m_recoveredLeases = [];

    // Every ledger row, plus the live leases and retained commits that each become one when they end.
    private int LedgerRows => (((m_spent.Count + m_floors.Count) + m_leases.Count) + m_committed.Count);

    // One source's own ledger rows, its floor included.
    private int SourceRows(string sourceAuthority) => (
        m_sourceRows.GetValueOrDefault(key: sourceAuthority) +
        (m_floors.ContainsKey(key: sourceAuthority) ? 1 : 0)
    );
    private void SetSpent(WorldTransferKey key, bool committed, bool durable) {
        if (!m_spent.ContainsKey(key: key)) {
            m_sourceRows[key.SourceAuthority] = (m_sourceRows.GetValueOrDefault(key: key.SourceAuthority) + 1);
        }
        m_spent[key] = new SpentKey(
            Committed: committed,
            Durable: durable
        );
    }
    // Drops a row the ledger holds.
    private void RemoveSpent(WorldTransferKey key) {
        _ = m_spent.Remove(key: key);

        var remaining = (m_sourceRows[key.SourceAuthority] - 1);

        if (remaining == 0) {
            _ = m_sourceRows.Remove(key: key.SourceAuthority);
        } else {
            m_sourceRows[key.SourceAuthority] = remaining;
        }
    }
    private bool IsBelowFloor(WorldTransferKey key) => (
        m_floors.TryGetValue(
            key: key.SourceAuthority,
            value: out var floor
        ) &&
        (key.TransferId < floor)
    );
    // Records that a key holds nothing live any more. A key below its source's floor needs no row, and a death already
    // recorded keeps its durability.
    private void Spend(WorldTransferKey key, bool committed) {
        if (!IsBelowFloor(key: key) && (committed || !m_spent.ContainsKey(key: key))) {
            SetSpent(
                committed: committed,
                durable: committed,
                key: key
            );
        }
    }
    // Kills a live lease: its capacity is released and its key is dead.
    private void KillLease(WorldTransferKey key) {
        if (m_leases.ContainsKey(key: key)) {
            ReleaseLease(key: key);
            Spend(
                committed: false,
                key: key
            );
        }
    }
    // Answers Missing for a key that holds nothing live, first making its death durable: a key below its source's floor
    // or already durably dead answers at once, and any other is tombstoned in the crossing log. A tombstone not known to
    // be durable leaves the key dead here and answers Uncertain, since only this authority's recovery can then say what
    // its successor answers. A full ledger, or a source whose share of it is full, tombstones nothing and answers
    // Uncertain too.
    private WorldTransferStatus AnswerMissing(WorldTransferKey key, ref string reason) {
        var known = m_spent.TryGetValue(
            key: key,
            value: out var spent
        );

        if (IsBelowFloor(key: key) || spent.Durable) {
            return WorldTransferStatus.Missing;
        }
        if (
            !known &&
            ((LedgerRows >= WorldTransferLimits.TransferKeys) || (SourceRows(sourceAuthority: key.SourceAuthority) >= WorldTransferLimits.TransferKeysPerSource))
        ) {
            reason = $"transfer {key.TransferId} cannot be tombstoned: the transfer-key ledger, or its source's share of it, is full until the source's floor advances";
            return WorldTransferStatus.Uncertain;
        }

        var durability = m_server.RecordCrossingHeld(
            reason: out var detail,
            record: new WorldCrossingRecord.Tombstone(Key: key)
        );
        var durable = (durability == WorldCrossingDurability.Durable);

        SetSpent(
            committed: false,
            durable: durable,
            key: key
        );
        if (durable) {
            return WorldTransferStatus.Missing;
        }
        reason = $"transfer {key.TransferId} is dead here but its tombstone is not durable ({detail}); only this authority's recovery answers it";
        return WorldTransferStatus.Uncertain;
    }
    // Refuses a reservation the ledger has no room for once its floor has dropped its source's rows below it: the new
    // lease and, for a source with no floor yet, the floor row.
    private string? LedgerRefusal(WorldTransferReservationRequest request) {
        var source = request.SourceAuthority;
        var dropped = m_spent.Keys.Count(predicate: key => (
            string.Equals(
                a: key.SourceAuthority,
                b: source,
                comparisonType: StringComparison.Ordinal
            ) &&
            (key.TransferId < request.SourceFloor)
        ));
        var added = (((request.SourceFloor == 0UL) || m_floors.ContainsKey(key: source))
            ? 1
            : 2);

        return (
            (((LedgerRows - dropped) + added) > WorldTransferLimits.TransferKeys) ||
            (((SourceRows(sourceAuthority: source) - dropped) + added) > WorldTransferLimits.TransferKeysPerSource)
        )
            ? $"the transfer-key ledger, or '{source}'s share of it, is full; the source's rising settled floor frees it"
            : null;
    }
    // Raises a source's floor and drops its rows below it. A floor never falls.
    private void RaiseFloor(string sourceAuthority, ulong floor) {
        if (
            (floor == 0UL) ||
            (m_floors.TryGetValue(
                key: sourceAuthority,
                value: out var current
            ) && (current >= floor))
        ) {
            return;
        }

        m_floors[sourceAuthority] = floor;
        foreach (var key in m_spent.Keys.Where(predicate: key => (
            string.Equals(
                a: key.SourceAuthority,
                b: sourceAuthority,
                comparisonType: StringComparison.Ordinal
            ) &&
            (key.TransferId < floor)
        )).ToArray()) {
            RemoveSpent(key: key);
        }
    }

    /// <summary>Redoes a crossing-log lease record: raises its source's floor and holds the key leased until the
    /// arrival that may follow it in the log lands, or until <see cref="FinishRecovery"/> declares it dead. The caller
    /// holds the authority gate.</summary>
    /// <param name="key">The leased key.</param>
    /// <param name="floor">The source's settled floor the reservation carried.</param>
    internal void RedoLease(WorldTransferKey key, ulong floor) {
        RaiseFloor(
            floor: floor,
            sourceAuthority: key.SourceAuthority
        );
        _ = m_recoveredLeases.Add(item: key);
    }
    /// <summary>Redoes a crossing-log tombstone: the key is dead, and a live lease the recovered checkpoint holds for it
    /// is released. A committed key is never tombstoned, since <see cref="Commit"/> and <see cref="Status"/> answer it
    /// Committed. The caller holds the authority gate.</summary>
    /// <param name="key">The dead key.</param>
    internal void RedoTombstone(WorldTransferKey key) {
        _ = m_recoveredLeases.Remove(item: key);
        ReleaseLease(key: key);
        if (!IsBelowFloor(key: key)) {
            SetSpent(
                committed: false,
                durable: true,
                key: key
            );
        }
    }
    /// <summary>Ends a crossing recovery: every key the log leased whose arrival never followed is dead. The log holds
    /// its lease record, so every later recovery declares it dead too, and the death is durable. The caller holds the
    /// authority gate.</summary>
    internal void FinishRecovery() {
        foreach (var key in m_recoveredLeases.Where(predicate: key => !IsBelowFloor(key: key))) {
            SetSpent(
                committed: false,
                durable: true,
                key: key
            );
        }
        m_recoveredLeases.Clear();
    }
}
