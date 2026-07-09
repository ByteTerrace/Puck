namespace Puck.AdvancedGamingBrick;

public sealed partial class ApuWaveChannel : IAgbSnapshotable {
    /// <inheritdoc/>
    // The two-bank wave RAM plus the frequency timer, sample position, length/volume unit, and the bank-mode state.
    public void SaveState(AgbStateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBytes(value: m_waveRam);
        writer.WriteInt32(value: m_frequency);
        writer.WriteInt32(value: m_frequencyTimer);
        writer.WriteInt32(value: m_samplePosition);
        writer.WriteInt32(value: m_lengthCounter);
        writer.WriteInt32(value: m_volumeShift);
        writer.WriteBoolean(value: m_forceVolume75);
        writer.WriteBoolean(value: m_dacEnabled);
        writer.WriteBoolean(value: m_enabled);
        writer.WriteBoolean(value: m_lengthEnabled);
        writer.WriteBoolean(value: m_twoBank);
        writer.WriteInt32(value: m_bank);
    }

    /// <inheritdoc/>
    public void LoadState(AgbStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBytes(destination: m_waveRam);
        m_frequency = reader.ReadInt32();
        m_frequencyTimer = reader.ReadInt32();
        m_samplePosition = reader.ReadInt32();
        m_lengthCounter = reader.ReadInt32();
        m_volumeShift = reader.ReadInt32();
        m_forceVolume75 = reader.ReadBoolean();
        m_dacEnabled = reader.ReadBoolean();
        m_enabled = reader.ReadBoolean();
        m_lengthEnabled = reader.ReadBoolean();
        m_twoBank = reader.ReadBoolean();
        m_bank = reader.ReadInt32();
    }
}
