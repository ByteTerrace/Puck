namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbApu : IAgbSnapshotable {
    /// <inheritdoc/>
    // Each PSG channel snapshots itself; the APU adds the two Direct Sound FIFOs, the SOUNDCNT/bias registers, the
    // frame-sequencer phase, the latched Direct Sound samples + refill requests, and the sample-generation timing.
    // The drainable OUTPUT ring is deliberately NOT captured: it is host-facing audio that has already left the
    // machine, not state that feeds back into emulation, so excluding it keeps the image small and the round-trip
    // (framebuffer + register) comparison unaffected.
    //
    // The two Direct Sound FIFOs (the hardware-measured 7-word ring + playing buffer) snapshot themselves via
    // DirectSoundFifo.SaveState/LoadState — a field change there stays a local edit in that nested class.
    public void SaveState(AgbStateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        m_pulse1.SaveState(writer: writer);
        m_pulse2.SaveState(writer: writer);
        m_wave.SaveState(writer: writer);
        m_noise.SaveState(writer: writer);

        m_fifoA.SaveState(writer: writer);
        m_fifoB.SaveState(writer: writer);

        writer.WriteUInt16(value: m_soundControlLow);
        writer.WriteUInt16(value: m_soundControlHigh);
        writer.WriteBoolean(value: m_masterEnable);
        writer.WriteUInt16(value: m_soundBias);
        writer.WriteInt32(value: m_frameSequencerTimer);
        writer.WriteInt32(value: m_frameSequencerStep);
        writer.WriteInt32(value: m_directSoundA);
        writer.WriteInt32(value: m_directSoundB);
        writer.WriteBoolean(value: m_fifoARefill);
        writer.WriteBoolean(value: m_fifoBRefill);
        writer.WriteInt32(value: m_cyclesPerSample);
        writer.WriteInt32(value: m_sampleTimer);
    }

    /// <inheritdoc/>
    public void LoadState(AgbStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_pulse1.LoadState(reader: reader);
        m_pulse2.LoadState(reader: reader);
        m_wave.LoadState(reader: reader);
        m_noise.LoadState(reader: reader);

        m_fifoA.LoadState(reader: reader);
        m_fifoB.LoadState(reader: reader);

        m_soundControlLow = reader.ReadUInt16();
        m_soundControlHigh = reader.ReadUInt16();
        m_masterEnable = reader.ReadBoolean();
        m_soundBias = reader.ReadUInt16();
        m_frameSequencerTimer = reader.ReadInt32();
        m_frameSequencerStep = reader.ReadInt32();
        m_directSoundA = reader.ReadInt32();
        m_directSoundB = reader.ReadInt32();
        m_fifoARefill = reader.ReadBoolean();
        m_fifoBRefill = reader.ReadBoolean();
        m_cyclesPerSample = reader.ReadInt32();
        m_sampleTimer = reader.ReadInt32();
    }

}
