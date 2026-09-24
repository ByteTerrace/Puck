namespace Puck.AdvancedGamingBrick;

public sealed partial class ApuWaveChannel : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // The two-bank wave RAM plus the frequency timer, sample position, length/volume unit, and the bank-mode state.
    internal void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_waveRam);
        transfer.Int32(value: ref m_frequency);
        transfer.Int32(value: ref m_frequencyTimer);
        transfer.Int32(value: ref m_samplePosition);
        transfer.Int32(value: ref m_length.Counter);
        transfer.Int32(value: ref m_volumeShift);
        transfer.Boolean(value: ref m_forceVolume75);
        transfer.Boolean(value: ref m_dacEnabled);
        transfer.Boolean(value: ref m_enabled);
        transfer.Boolean(value: ref m_length.Enabled);
        transfer.Boolean(value: ref m_twoBank);
        transfer.Int32(value: ref m_bank);
    }
}
