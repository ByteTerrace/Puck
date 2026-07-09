namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbDmaController : IAgbSnapshotable {
    /// <inheritdoc/>
    // Per-channel programmed registers (source/dest/count/control) plus every internal cursor and latch: the
    // source/dest address latches, the remaining-word counter, and the open-bus data latch, together with the
    // per-channel active flags and the burst-in-progress bookkeeping (running + active channel). These internal
    // cursors are the transfer's real position — a mid-burst snapshot must carry them to resume identically.
    public void SaveState(AgbStateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBlock<uint>(values: m_source);
        writer.WriteBlock<uint>(values: m_destination);
        writer.WriteBlock<uint>(values: m_count);
        writer.WriteBlock<ushort>(values: m_control);
        writer.WriteBlock<uint>(values: m_sourceLatch);
        writer.WriteBlock<uint>(values: m_destinationLatch);
        writer.WriteBlock<uint>(values: m_remaining);
        writer.WriteBlock<uint>(values: m_dataLatch);

        foreach (var value in m_active) {
            writer.WriteBoolean(value: value);
        }

        writer.WriteBoolean(value: m_running);
        writer.WriteInt32(value: m_activeChannel);
    }

    /// <inheritdoc/>
    public void LoadState(AgbStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBlock<uint>(destination: m_source);
        reader.ReadBlock<uint>(destination: m_destination);
        reader.ReadBlock<uint>(destination: m_count);
        reader.ReadBlock<ushort>(destination: m_control);
        reader.ReadBlock<uint>(destination: m_sourceLatch);
        reader.ReadBlock<uint>(destination: m_destinationLatch);
        reader.ReadBlock<uint>(destination: m_remaining);
        reader.ReadBlock<uint>(destination: m_dataLatch);

        for (var i = 0; (i < m_active.Length); ++i) {
            m_active[i] = reader.ReadBoolean();
        }

        m_running = reader.ReadBoolean();
        m_activeChannel = reader.ReadInt32();
    }
}
