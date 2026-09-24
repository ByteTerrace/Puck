namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbDmaController : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // Per-channel programmed registers (source/dest/count/control) plus every internal cursor and latch: the
    // source/dest address latches, the remaining-word counter, and the open-bus data latch, together with the
    // per-channel active flags and the burst-in-progress bookkeeping (running + active channel). These internal
    // cursors are the transfer's real position — a mid-burst snapshot must carry them to resume identically.
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_source);
        transfer.Block(values: m_destination);
        transfer.Block(values: m_count);
        transfer.Block(values: m_control);
        transfer.Block(values: m_sourceLatch);
        transfer.Block(values: m_destinationLatch);
        transfer.Block(values: m_remaining);
        transfer.Block(values: m_dataLatch);
        transfer.Block(values: m_active);
        transfer.Boolean(value: ref m_running);
        transfer.Int32(value: ref m_activeChannel);
    }
}
