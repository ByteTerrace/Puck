namespace Puck.AdvancedGamingBrick;

public sealed partial class ApuPulseChannel : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // Duty position, frequency timer, length/envelope/sweep unit state — everything but the readonly has-sweep wiring.
    internal void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Int32(value: ref m_dutyPattern);
        transfer.Int32(value: ref m_dutyStep);
        transfer.Int32(value: ref m_frequency);
        transfer.Int32(value: ref m_frequencyTimer);
        transfer.Int32(value: ref m_length.Counter);
        transfer.Boolean(value: ref m_length.Enabled);
        transfer.Int32(value: ref m_envelope.Volume);
        transfer.Int32(value: ref m_envelope.Initial);
        transfer.Boolean(value: ref m_envelope.Increase);
        transfer.Int32(value: ref m_envelope.Period);
        transfer.Int32(value: ref m_envelope.Timer);
        transfer.Boolean(value: ref m_dacEnabled);
        transfer.Boolean(value: ref m_enabled);
        transfer.Int32(value: ref m_sweepPeriod);
        transfer.Boolean(value: ref m_sweepDecrease);
        transfer.Int32(value: ref m_sweepShift);
        transfer.Int32(value: ref m_sweepTimer);
        transfer.Int32(value: ref m_sweepShadow);
        transfer.Boolean(value: ref m_sweepActive);
    }
}
