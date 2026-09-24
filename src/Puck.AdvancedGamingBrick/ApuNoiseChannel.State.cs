namespace Puck.AdvancedGamingBrick;

public sealed partial class ApuNoiseChannel : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // The LFSR plus the frequency timer, length/envelope unit, and the polynomial (divisor/width/shift) fields.
    internal void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Int32(value: ref m_frequencyTimer);
        transfer.Int32(value: ref m_length.Counter);
        transfer.Int32(value: ref m_envelope.Volume);
        transfer.Int32(value: ref m_envelope.Initial);
        transfer.Int32(value: ref m_envelope.Period);
        transfer.Int32(value: ref m_envelope.Timer);
        transfer.Int32(value: ref m_divisorCode);
        transfer.Int32(value: ref m_shiftClock);
        transfer.Boolean(value: ref m_envelope.Increase);
        transfer.Boolean(value: ref m_widthMode);
        transfer.Boolean(value: ref m_dacEnabled);
        transfer.Boolean(value: ref m_enabled);
        transfer.Boolean(value: ref m_length.Enabled);
        transfer.UInt16(value: ref m_lfsr);
    }
}
