namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbScheduler : ISnapshotable {
    /// <summary>Empties the event queue, clearing every currently-scheduled event's in-queue bookkeeping. A snapshot
    /// restore calls this before the peripherals re-arm their own events, so the rebuilt queue holds exactly the
    /// events the snapshot recorded — no stale live entry survives. The <see cref="Now"/> clock is untouched.</summary>
    public void ResetQueue() {
        var node = m_root;

        while (node is not null) {
            var next = node.Next;

            node.Scheduled = false;
            node.Next = null;
            node = next;
        }

        m_root = null;
    }
    /// <inheritdoc/>
    // The scheduler owns only the master clock; the event QUEUE is rebuilt by the peripherals that own each event
    // (PPU, serial) moving it through TransferEvent in their own LoadState — a delegate is never serialized. The machine
    // restores the scheduler first, so its ResetQueue empties the live queue before those peripherals re-arm into a
    // clean slate.
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        ResetQueue();
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <summary>Moves one peripheral-owned event's schedule — whether it is queued, then its fire instant — in the
    /// transfer's direction. Saving leaves the event untouched; a load that restores a schedule other than the event's
    /// live one takes the event out of the queue and, when the snapshot had it queued, re-inserts it at the restored
    /// instant. A load that restores exactly the live schedule leaves the queue as it is, so an event is never moved
    /// behind a peer due at the same instant.</summary>
    /// <typeparam name="TTransfer">The direction: <see cref="StateSaveTransfer"/> or <see cref="StateLoadTransfer"/>.</typeparam>
    /// <param name="transfer">The direction's writer or reader.</param>
    /// <param name="e">The event, owned by the calling peripheral and bound to its callback.</param>
    /// <exception cref="ArgumentNullException"><paramref name="e"/> is <see langword="null"/>.</exception>
    public void TransferEvent<TTransfer>(TTransfer transfer, Event e) where TTransfer : struct, IStateTransfer {
        ArgumentNullException.ThrowIfNull(argument: e);

        var scheduled = e.Scheduled;
        var when = e.When;

        transfer.Boolean(value: ref scheduled);
        transfer.Int64(value: ref when);

        if (
            (scheduled == e.Scheduled) &&
            (when == e.When)
        ) {
            return;
        }

        Deschedule(e: e);

        if (scheduled) {
            ScheduleAbsolute(
                e: e,
                when: when
            );
        }
    }

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer =>
        transfer.Int64(value: ref m_now);
}
