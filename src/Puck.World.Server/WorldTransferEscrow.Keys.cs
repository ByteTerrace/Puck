namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // A key that holds no live lease, retained commit or uncertain arrival here any more: committed (it answers
    // Committed) or dead (it answers Missing). No reservation leases either again and no commit lands under either.
    // Durable is whether a recovery from this authority's checkpoint and crossing log finds the key dead too; Missing is
    // answered only for a durable death, so a crash can never hand a resent commit a lease its source was told was gone.
    private readonly record struct SpentKey(bool Committed, bool Durable);

    // Every key in it holds a row (SourceLedger.Keys), whatever else the key holds: a live lease, a retained commit, an
    // uncertain arrival or nothing live. A key keeps its one row as it moves between those; it takes a row only through
    // EnterRow after RowRefusal admits it (or when recovery redoes state that was admitted live), and gives it up only
    // through LeaveRow once its source's floor passes it or its session is forgotten.
    private readonly Dictionary<WorldTransferKey, SpentKey> m_spent = new();
    // Per authenticated source namespace, its highest settled floor (WorldTransferReservationRequest.SourceFloor): every
    // id below it is dead here without a row. Each floor is a row of its source too.
    private readonly Dictionary<string, ulong> m_floors = new(comparer: StringComparer.Ordinal);

    private int TotalRows => (m_keyRows + m_floors.Count);

    private int SourceRows(string sourceAuthority) => (
        (m_sources.TryGetValue(key: sourceAuthority, value: out var ledger) ? ledger.Keys.Count : 0) +
        (m_floors.ContainsKey(key: sourceAuthority) ? 1 : 0)
    );
    private bool IsBelowFloor(WorldTransferKey key) => (
        m_floors.TryGetValue(
            key: key.SourceAuthority,
            value: out var floor
        ) &&
        (key.TransferId < floor)
    );
    // The one admission check: whether `key` may take a row once `floor` has dropped its source's spent rows below it.
    // It changes nothing; a key that already holds a row is admitted. A world of the group, and any source of a world
    // outside a closed group, is bounded by the ledger's bound and its own share, counted without any guest's rows. A
    // guest's session is bounded by its own budget and by what the guests may hold together, so no number of guests
    // refuses a world of the group a row.
    private string? RowRefusal(WorldTransferKey key, ulong floor) {
        if (HoldsRow(key: key)) {
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
        var held = ((SourceRows(sourceAuthority: source) - dropped) + added);

        if (IsGuest(sourceAuthority: source)) {
            var session = (m_boundary?.GuestKeysPerSession ?? WorldTransferLimits.GuestTransferKeysPerSession);
            var guests = (m_boundary?.GuestKeys ?? WorldTransferLimits.GuestTransferKeys);

            if (held > session) {
                return $"transfer {key.TransferId}: session '{source}' holds the {session} transfer keys one player's session may hold here until its settled floor rises";
            }
            return ((((m_guests.Rows - dropped) + added) > guests)
                ? $"transfer {key.TransferId}: this world's guests hold the {guests} transfer keys it keeps for players"
                : null);
        }
        return (
            ((((TotalRows - m_guests.Rows) - dropped) + added) > WorldTransferLimits.TransferKeys) ||
            (held > WorldTransferLimits.TransferKeysPerSource)
        )
            ? $"transfer {key.TransferId}: the transfer-key ledger, or '{source}'s share of it, is full until the source's settled floor rises"
            : null;
    }
    private void EnterRow(WorldTransferKey key) {
        var ledger = Source(sourceAuthority: key.SourceAuthority);

        if (ledger.Keys.Add(item: key.TransferId)) {
            m_keyRows++;
            if (ledger.Guest) { m_guests.Rows++; }
        }
    }
    // Gives up a key's row. The counts move only with the row, so a key that holds none changes nothing.
    private void LeaveRow(WorldTransferKey key) {
        _ = m_spent.Remove(key: key);
        if (m_sources.TryGetValue(key: key.SourceAuthority, value: out var ledger) && ledger.Keys.Remove(item: key.TransferId)) {
            m_keyRows--;
            if (ledger.Guest) { m_guests.Rows--; }
            Settle(ledger: ledger, sourceAuthority: key.SourceAuthority);
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
    // Kills a live lease: its capacity is released and its key is dead. A lease whose record or arrival is in flight is
    // decided by that record when it settles, never killed under it.
    private void KillLease(WorldTransferKey key) {
        if (m_leases.ContainsKey(key: key) && !IsOpen(key: key)) {
            ReleaseLease(key: key);
            Spend(
                committed: false,
                key: key
            );
            ReclaimGuest(sourceAuthority: key.SourceAuthority);
        }
    }
    // Answers Missing for a key that holds nothing live, first making its death durable: a key below its source's floor
    // or already durably dead answers at once, and any other is tombstoned in the crossing log and takes its row only
    // once the tombstone is durable. A key the ledger cannot admit, or whose tombstone is not known to be durable,
    // changes nothing and answers Uncertain, since only a later answer can be final. A tombstone still in flight takes
    // its row when the authority settles it, and the next answer about the key is Missing.
    //
    // A guest's key that holds no row is answered Missing with nothing written: no row and no record. The promise a
    // tombstone keeps, that a key answered Missing is never leased afterwards, is one the group makes to a world it
    // rewinds with; a guest carries nothing the group must give back, so a guest that reserves a key it was told is
    // missing gets a lease its own commit decides. A guest's question can therefore never add a row.
    private WorldTransferStatus AnswerMissing(WorldTransferKey key, ref string reason, IReadOnlyList<int>? burned = null) {
        if (IsBelowFloor(key: key) || (m_spent.TryGetValue(key: key, value: out var spent) && spent.Durable)) {
            return WorldTransferStatus.Missing;
        }

        var guest = IsGuest(sourceAuthority: key.SourceAuthority);

        if (guest && !HoldsRow(key: key)) {
            return WorldTransferStatus.Missing;
        }
        if (RowRefusal(floor: 0UL, key: key) is { } refusal) {
            reason = refusal;
            return WorldTransferStatus.Uncertain;
        }

        var durability = m_server.RecordCrossingHeld(
            continuation: step => {
                if (step.Outcome == WorldCrossingDurability.Durable) {
                    ApplyDurableDeath(key: key);
                }
            },
            reason: out var detail,
            record: new WorldCrossingRecord.Tombstone(Burned: burned, Guest: guest, Key: key)
        );

        if (durability != WorldCrossingDurability.Durable) {
            reason = $"transfer {key.TransferId} cannot be tombstoned yet ({detail}); only a later answer is final";
            return WorldTransferStatus.Uncertain;
        }
        ApplyDurableDeath(key: key);
        return WorldTransferStatus.Missing;
    }
    // The floor a source holds once `floor` is applied: a floor never falls.
    private ulong EffectiveFloor(string sourceAuthority, ulong floor) => Math.Max(
        val1: floor,
        val2: m_floors.GetValueOrDefault(key: sourceAuthority)
    );
    // A source's spent keys below a floor, read from the source's own rows.
    private IEnumerable<WorldTransferKey> SpentBelow(string sourceAuthority, ulong floor) {
        if (!m_sources.TryGetValue(key: sourceAuthority, value: out var ledger)) {
            yield break;
        }
        foreach (var id in ledger.Keys) {
            var key = new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: id);

            if ((id < floor) && m_spent.ContainsKey(key: key)) {
                yield return key;
            }
        }
    }
    // Applies a source's floor and drops its spent rows below it, as RowRefusal credited.
    private void RaiseFloor(string sourceAuthority, ulong floor) {
        var effective = EffectiveFloor(
            floor: floor,
            sourceAuthority: sourceAuthority
        );

        if (effective == 0UL) {
            return;
        }
        if (!m_floors.ContainsKey(key: sourceAuthority) && IsGuest(sourceAuthority: sourceAuthority)) {
            m_guests.Rows++;
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
    // past a key only once it has its answer for that key. Each record says whether a guest's session wrote it, so the
    // fold forgets a guest's session wherever the live ledger did, with no boundary to ask.

    /// <summary>Redoes a crossing-log lease record: raises its source's floor and leaves the key dead, which is its fate
    /// unless an arrival record later in the log lands it. The log holds the lease record, so every recovery declares
    /// the key dead the same way, and the death is durable. The caller holds the authority gate.</summary>
    /// <param name="key">The leased key.</param>
    /// <param name="floor">The source's settled floor the reservation carried.</param>
    /// <param name="guest">Whether a guest's session leased the key (<see cref="WorldCrossingRecord.Lease.Guest"/>).</param>
    internal void RedoLease(WorldTransferKey key, ulong floor, bool guest) {
        if (guest) { MarkGuest(sourceAuthority: key.SourceAuthority); }
        RaiseFloor(
            floor: floor,
            sourceAuthority: key.SourceAuthority
        );
        ApplyDurableDeath(key: key);
    }
    /// <summary>Redoes a crossing-log tombstone: the key is dead, a live lease the recovered checkpoint holds for it is
    /// released, and every slot the refused commit landed at takes the generation that landing burned. A committed key
    /// is never tombstoned, since <see cref="Commit"/> and <see cref="Status"/> answer it Committed. The caller holds the
    /// authority gate.</summary>
    /// <param name="key">The dead key.</param>
    /// <param name="burned">The body indices the refused commit landed at before rolling them back.</param>
    /// <param name="guest">Whether the key is a guest's session's (<see cref="WorldCrossingRecord.Tombstone.Guest"/>).</param>
    internal void RedoTombstone(WorldTransferKey key, IReadOnlyList<int> burned, bool guest) {
        if (guest) { MarkGuest(sourceAuthority: key.SourceAuthority); }
        ReleaseLease(key: key);
        ApplyDurableDeath(key: key);
        foreach (var slot in burned) {
            m_server.Population.BurnGeneration(index: slot);
        }
    }
    // A durable death, live or redone: admitted when its record was sent, so it enters without the bounds. A guest's
    // session that holds nothing live keeps no row, a dead key's included.
    private void ApplyDurableDeath(WorldTransferKey key) {
        EnterRow(key: key);
        m_spent[key] = new SpentKey(
            Committed: false,
            Durable: true
        );
        ReclaimGuest(sourceAuthority: key.SourceAuthority);
    }
}
