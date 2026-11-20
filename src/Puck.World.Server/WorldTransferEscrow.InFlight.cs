namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // The key whose lease or arrival record is in flight (the authority sends one record at a time, so one key at most),
    // and for an arrival the record as it was sent and the commit's members. Its lease stays held under the key, out of
    // expiry, abort and retirement, until the authority settles the record; every decision about the key meanwhile waits
    // for that (WorldServer.DecideCrossingAsync). Nothing of an arrival in flight is landed.
    private OpenCrossing? m_open;
    // Set by CommitLease when its lease must outlive the commit's decision: its arrival record is in flight, or another
    // record is and the commit is decided again once it settles.
    private bool m_keepLease;

    private sealed record OpenCrossing(WorldTransferKey Key, WorldCrossingArrival? Arrival, IReadOnlyList<WorldTransferCommitMember> Members);

    private bool IsOpen(WorldTransferKey key) => (m_open?.Key == key);
    // Holds `key` while its lease record is in flight: the slots stay reserved, and the acceptance waits for the record.
    private void OpenLease(WorldTransferKey key) => m_open = new OpenCrossing(
        Arrival: null,
        Key: key,
        Members: []
    );
    // A lease record settled. A durable one raises its source's floor as a durable lease does at once; any other changes
    // nothing here, and the key gives back the row its held lease took.
    private void SettleLease(WorldTransferKey key, ulong floor, WorldCrossingStep step) {
        m_open = null;
        if (step.Outcome == WorldCrossingDurability.Durable) {
            RaiseFloor(
                floor: floor,
                sourceAuthority: key.SourceAuthority
            );
            return;
        }
        ReleaseLease(key: key);
        LeaveRow(key: key);
    }
    // An arrival record settled. A durable one lands the arrival exactly as recorded, through the reland door recovery
    // uses, under a lease narrowed to what it landed: the speculative landing that built the record was undone in full,
    // generations included, so the cohort lands at the generations the record names. A refused one keeps the lease, so
    // the commit decided again (with the refusal answering its append) rolls back and kills the key as a refused record
    // always does; an uncertain one holds the arrival uncertain.
    private void SettleArrival(WorldCrossingStep step) {
        var open = m_open;

        m_open = null;
        if (open is not { Arrival: { } arrival }) {
            return;
        }

        var key = open.Key;

        if (step.Outcome == WorldCrossingDurability.Uncertain) {
            ReleaseLease(key: key);
            _ = HoldUncertain(
                arrival: arrival,
                detail: step.Reason,
                reason: out _
            );
            return;
        }
        if ((step.Outcome != WorldCrossingDurability.Durable) || !m_leases.TryGetValue(key: key, value: out var held)) {
            return;
        }

        ReleaseLease(key: key);
        _ = InstallLease(
            arrival: held.Arrival,
            deadline: held.DeadlineTick,
            key: key,
            request: arrival.Request,
            slots: [.. arrival.Slots]
        );
        if (RelandUnder(
            arrival: arrival,
            reason: out var reason,
            recorded: null
        )) {
            m_committedMembers[key] = [.. open.Members];
            return;
        }
        // A durable arrival this activation cannot land answers Uncertain for its key, and the authority records nothing
        // more: its recovery redoes the arrival from the log, or refuses the activation by name.
        _ = HoldUncertain(
            arrival: arrival,
            detail: reason,
            reason: out _
        );
        m_server.LatchCrossing(step: step);
        if (m_server.Output.HasNarrationSink) {
            m_server.Output.Narrate(
                channel: "world.transfer",
                text: $"[world.transfer: transfer={key.TransferId} from '{key.SourceAuthority}' is durable but could not land — {reason}; this authority records nothing more until it recovers]"
            );
        }
    }
    // Undoes the landings a commit made only to build its arrival record, as if they never happened: each traveler leaves
    // its index and the index takes back the generation it had, since no one observed the landing between it and this
    // undo. The record's landing happens again, at those same generations, once the record is durable.
    private void UndoSpeculativeLanding(WorldCrossingArrival offered, List<int> landedOrdinals, List<int> generations) {
        var landed = Narrowed(
            arrival: offered,
            ordinals: landedOrdinals
        );

        RollBackArrival(
            arrival: landed,
            landed: generations.Count
        );
        for (var index = 0; (index < generations.Count); index++) {
            m_server.Population.RestoreGeneration(
                generation: (generations[index] - 1),
                index: landed.Slots[index]
            );
        }
    }
}
