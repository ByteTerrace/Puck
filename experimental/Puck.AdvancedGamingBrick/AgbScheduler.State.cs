namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbScheduler : IAgbSnapshotable {
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
    // (PPU, serial) re-scheduling in their own LoadState — a delegate is never serialized. The machine restores the
    // scheduler first, so its ResetQueue empties the live queue before those peripherals re-arm into a clean slate.
    public void SaveState(AgbStateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteInt64(value: Now);
    }

    /// <inheritdoc/>
    public void LoadState(AgbStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        Now = reader.ReadInt64();
        ResetQueue();
    }
}
