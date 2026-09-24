namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbSerialController : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // Every decomposed SIOCNT/RCNT/JOY-bus field, the SIOMULTI/SIODATA registers, and the pending-transfer event's
    // fire instant. The link partner (m_link/m_node) is topology, not machine state, so a restore into the same
    // machine keeps its live connection; only the transfer event's schedule is rebuilt (the callback is already bound
    // to this instance — a delegate is never serialized).
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Boolean(value: ref m_shiftClockInternal);
        transfer.Boolean(value: ref m_shiftClock2MHz);
        transfer.Boolean(value: ref m_recvEnable);
        transfer.Boolean(value: ref m_sendEnable);
        transfer.Boolean(value: ref m_startBit);
        transfer.Byte(value: ref m_uartFlags);
        transfer.Int32(value: ref m_sioMode);
        transfer.Boolean(value: ref m_irqEnable);
        transfer.Int32(value: ref m_multiplayerId);
        transfer.Boolean(value: ref m_multiplayerError);

        transfer.Block(values: m_data);
        transfer.UInt16(value: ref m_dataSend);

        transfer.Boolean(value: ref m_rcntSc);
        transfer.Boolean(value: ref m_rcntSd);
        transfer.Boolean(value: ref m_rcntSi);
        transfer.Boolean(value: ref m_rcntSo);
        transfer.Boolean(value: ref m_rcntScMode);
        transfer.Boolean(value: ref m_rcntSdMode);
        transfer.Boolean(value: ref m_rcntSiMode);
        transfer.Boolean(value: ref m_rcntSoMode);
        transfer.Boolean(value: ref m_siIrqEnable);
        transfer.Int32(value: ref m_rcntMode);

        transfer.Boolean(value: ref m_joyReset);
        transfer.Boolean(value: ref m_joyRecvComplete);
        transfer.Boolean(value: ref m_joySendComplete);
        transfer.Boolean(value: ref m_joyResetIrqEnable);
        transfer.UInt32(value: ref m_joyRecv);
        transfer.UInt32(value: ref m_joyTrans);
        transfer.Boolean(value: ref m_joyRecvFlag);
        transfer.Boolean(value: ref m_joySendFlag);
        transfer.Int32(value: ref m_joyGeneralFlag);

        m_scheduler.TransferEvent(
            e: m_transferEvent,
            transfer: transfer
        );
    }
}
