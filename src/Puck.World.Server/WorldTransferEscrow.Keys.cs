namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // A key that holds no live lease, retained commit or uncertain arrival here any more: committed (it answers
    // Committed) or dead (it answers Missing). No reservation leases either again and no commit lands under either.
    // Durable is whether a recovery from this authority's checkpoint and crossing log finds the key dead too; Missing is
    // answered only for a durable death, so a crash can never hand a resent commit a lease its source was told was gone.
    private readonly record struct SpentKey(bool Committed, bool Durable);

    private readonly Dictionary<WorldTransferKey, SpentKey> m_spent = new();
    // Every key that holds a row here, whatever it holds: a live lease, a retained commit, an uncertain arrival or a
    // spent key. A key keeps its one row as it moves between those; it takes a row only through EnterRow after
    // RowRefusal admits it (or when recovery redoes state that was admitted live), and gives it up only through
    // LeaveRow once its source's floor passes it.
    private readonly HashSet<WorldTransferKey> m_rows = [];
    // Each source namespace's count of keys in m_rows.
    private readonly Dictionary<string, int> m_sourceRows = new(comparer: StringComparer.Ordinal);
    // Per authenticated source namespace, its highest settled floor (WorldTransferReservationRequest.SourceFloor): every
    // id below it is dead here without a row. Each floor is a row of its source too.
    private readonly Dictionary<string, ulong> m_floors = new(comparer: StringComparer.Ordinal);

    private int TotalRows => (m_rows.Count + m_floors.Count);

    private int SourceRows(string sourceAuthority) => (
        m_sourceRows.GetValueOrDefault(key: sourceAuthority) +
        (m_floors.ContainsKey(key: sourceAuthority) ? 1 : 0)
    );
    private bool IsBelowFloor(WorldTransferKey key) => (
        m_floors.TryGetValue(
            key: key.SourceAuthority,
            value: out var floor
        ) &&
        (key.TransferId < floor)
    );
    // The one admission check: whether `key` may take a row once `floor` has dropped its source's spent rows below it,
    // within both the ledger's bound and its source's share. It changes nothing; a key that already holds a row is
    // admitted.
    private string? RowRefusal(WorldTransferKey key, ulong floor) {
        if (m_rows.Contains(item: key)) {
            return null;
        }

        var source = key.SourceAuthority;
        var dropped = SpentBelow(
            floor: EffectiveFloor(
                floor: floor,
                sourceAuthority: source
            ),
            sourceAuthority: source
        ).Count();
        var added = (((floor == 0UL) || m_floors.ContainsKey(key: source))
            ? 1
            : 2);

        return (
            (((TotalRows - dropped) + added) > WorldTransferLimits.TransferKeys) ||
            (((SourceRows(sourceAuthority: source) - dropped) + added) > WorldTransferLimits.TransferKeysPerSource)
        )
            ? $"transfer {key.TransferId}: the transfer-key ledger, or '{source}'s share of it, is full until the source's settled floor rises"
            : null;
    }
    private void EnterRow(WorldTransferKey key) {
        if (m_rows.Add(item: key)) {
            m_sourceRows[key.SourceAuthority] = (m_sourceRows.GetValueOrDefault(key: key.SourceAuthority) + 1);
        }
    }
    // Gives up a key's row. The count moves only with the set, so a key that holds no row changes nothing.
    private void LeaveRow(WorldTransferKey key) {
        _ = m_spent.Remove(key: key);
        if (m_rows.Remove(item: key)) {
            var remaining = (m_sourceRows[key.SourceAuthority] - 1);

            if (remaining == 0) {
                _ = m_sourceRows.Remove(key: key.SourceAuthority);
            } else {
                m_sourceRows[key.SourceAuthority] = remaining;
            }
        }
    }
    // Records that a key holding a row has nothing live any more. Below its source's floor it gives up its row; a death
    // already recorded keeps its durability.
    private void Spend(WorldTransferKey key, bool committed) {
        if (IsBelowFloor(key: key)) {
            LeaveRow(key: key);
        } else if (committed || !m_spent.ContainsKey(key: key)) {
            m_spent[key] = new SpentKey(
                Committed: committed,
                Durable: committed
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
    // or already durably dead answers at once, and any other is tombstoned in the crossing log and takes its row only
    // once the tombstone is durable. A key the ledger cannot admit, or whose tombstone is not known to be durable,
    // changes nothing and answers Uncertain, since only a later answer can be final.
    private WorldTransferStatus AnswerMissing(WorldTransferKey key, ref string reason, IReadOnlyList<int>? burned = null) {
        if (IsBelowFloor(key: key) || (m_spent.TryGetValue(key: key, value: out var spent) && spent.Durable)) {
            return WorldTransferStatus.Missing;
        }
        if (RowRefusal(floor: 0UL, key: key) is { } refusal) {
            reason = refusal;
            return WorldTransferStatus.Uncertain;
        }

        var durability = m_server.RecordCrossingHeld(
            reason: out var detail,
            record: new WorldCrossingRecord.Tombstone(Burned: burned, Key: key)
        );

        if (durability != WorldCrossingDurability.Durable) {
            reason = $"transfer {key.TransferId} cannot be tombstoned yet ({detail}); only a later answer is final";
            return WorldTransferStatus.Uncertain;
        }
        EnterRow(key: key);
        m_spent[key] = new SpentKey(
            Committed: false,
            Durable: true
        );
        return WorldTransferStatus.Missing;
    }
    // The floor a source holds once `floor` is applied: a floor never falls.
    private ulong EffectiveFloor(string sourceAuthority, ulong floor) => Math.Max(
        val1: floor,
        val2: m_floors.GetValueOrDefault(key: sourceAuthority)
    );
    private IEnumerable<WorldTransferKey> SpentBelow(string sourceAuthority, ulong floor) => m_spent.Keys.Where(predicate: key => (
        string.Equals(
            a: key.SourceAuthority,
            b: sourceAuthority,
            comparisonType: StringComparison.Ordinal
        ) &&
        (key.TransferId < floor)
    ));
    // Applies a source's floor and drops its spent rows below it, as RowRefusal credited.
    private void RaiseFloor(string sourceAuthority, ulong floor) {
        var effective = EffectiveFloor(
            floor: floor,
            sourceAuthority: sourceAuthority
        );

        if (effective == 0UL) {
            return;
        }
        m_floors[sourceAuthority] = effective;
        foreach (var key in SpentBelow(floor: effective, sourceAuthority: sourceAuthority).ToArray()) {
            LeaveRow(key: key);
        }
    }

    // Recovery is a fold over the crossing log: each record sets the fate of the key it names, so the ledger after a
    // record depends only on the records before it, however the redo is cut into calls. A lease leaves its key dead, a
    // tombstone leaves it dead, and an arrival landing under it leaves it committed (CommitLease). A floor drops every
    // settled row below it as it is redone; no later record names a key below a floor, since a source raises its floor
    // past a key only once it has its answer for that key.

    /// <summary>Redoes a crossing-log lease record: raises its source's floor and leaves the key dead, which is its fate
    /// unless an arrival record later in the log lands it. The log holds the lease record, so every recovery declares
    /// the key dead the same way, and the death is durable. The caller holds the authority gate.</summary>
    /// <param name="key">The leased key.</param>
    /// <param name="floor">The source's settled floor the reservation carried.</param>
    internal void RedoLease(WorldTransferKey key, ulong floor) {
        RaiseFloor(
            floor: floor,
            sourceAuthority: key.SourceAuthority
        );
        RedoDeath(key: key);
    }
    /// <summary>Redoes a crossing-log tombstone: the key is dead, a live lease the recovered checkpoint holds for it is
    /// released, and every slot the refused commit landed at takes the generation that landing burned. A committed key
    /// is never tombstoned, since <see cref="Commit"/> and <see cref="Status"/> answer it Committed. The caller holds the
    /// authority gate.</summary>
    /// <param name="key">The dead key.</param>
    /// <param name="burned">The body indices the refused commit landed at before rolling them back.</param>
    internal void RedoTombstone(WorldTransferKey key, IReadOnlyList<int> burned) {
        ReleaseLease(key: key);
        RedoDeath(key: key);
        foreach (var slot in burned) {
            m_server.Population.BurnGeneration(index: slot);
        }
    }
    // A redone durable death: admitted when it was recorded, so it enters without the bounds.
    private void RedoDeath(WorldTransferKey key) {
        EnterRow(key: key);
        m_spent[key] = new SpentKey(
            Committed: false,
            Durable: true
        );
    }
}
