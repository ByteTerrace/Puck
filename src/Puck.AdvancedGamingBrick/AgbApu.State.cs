namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbApu : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));

        // Drained PCM belongs to the abandoned presentation timeline, not the restored machine. Keep the
        // restored emit phase but discard pending output so rewind cannot replay a stale startup chime.
        m_output.Clear();
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // Each PSG channel and each Direct Sound FIFO moves its own fields; the APU adds the SOUNDCNT/bias registers, the
    // frame-sequencer phase, the latched Direct Sound samples + refill requests, and the sample-generation phase.
    // The drainable OUTPUT ring is deliberately NOT captured: it is host-facing audio that has already left the
    // machine, not state that feeds back into emulation, so excluding it keeps the image small and the round-trip
    // (framebuffer + register) comparison unaffected. The output sample RATE is likewise excluded — it is
    // presentation config set once at construction, never state-of-record that a restore should overwrite (matching
    // the Humble core's AudioOutputComponent, which excludes it too).
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        m_pulse1.TransferState(transfer: transfer);
        m_pulse2.TransferState(transfer: transfer);
        m_wave.TransferState(transfer: transfer);
        m_noise.TransferState(transfer: transfer);

        m_fifoA.TransferState(transfer: transfer);
        m_fifoB.TransferState(transfer: transfer);

        transfer.UInt16(value: ref m_soundControlLow);
        transfer.UInt16(value: ref m_soundControlHigh);
        transfer.Boolean(value: ref m_masterEnable);
        transfer.UInt16(value: ref m_soundBias);
        transfer.Int32(value: ref m_frameSequencerTimer);
        transfer.Int32(value: ref m_frameSequencerStep);
        transfer.Int32(value: ref m_directSoundA);
        transfer.Int32(value: ref m_directSoundB);
        transfer.Boolean(value: ref m_fifoARefill);
        transfer.Boolean(value: ref m_fifoBRefill);
        m_samplePhase.TransferState(transfer: transfer);
    }
}
