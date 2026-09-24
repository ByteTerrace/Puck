namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbInterruptController : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        RefreshPipelineQuiescent(); // Derived cache; the snapshot continues to contain only hardware state.
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // Both pipeline stages (committed [0] and programmed [1] of IE/IF/IME) plus the synchronizer line — the whole
    // double-buffered state the 1-cycle register-visibility and 2-cycle recognition latencies emerge from.
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.UInt16(value: ref m_enable0);
        transfer.UInt16(value: ref m_enable1);
        transfer.UInt16(value: ref m_flag0);
        transfer.UInt16(value: ref m_flag1);
        transfer.Boolean(value: ref m_ime0);
        transfer.Boolean(value: ref m_ime1);
        transfer.Boolean(value: ref m_synchronizer);
    }
}
