namespace Puck.AdvancedGamingBrick;

public sealed partial class ApuNoiseChannel : IAgbSnapshotable {
    /// <inheritdoc/>
    // The LFSR plus the frequency timer, length/envelope unit, and the polynomial (divisor/width/shift) fields.
    public void SaveState(AgbStateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteInt32(value: m_frequencyTimer);
        writer.WriteInt32(value: m_lengthCounter);
        writer.WriteInt32(value: m_envelopeVolume);
        writer.WriteInt32(value: m_envelopeInitial);
        writer.WriteInt32(value: m_envelopePeriod);
        writer.WriteInt32(value: m_envelopeTimer);
        writer.WriteInt32(value: m_divisorCode);
        writer.WriteInt32(value: m_shiftClock);
        writer.WriteBoolean(value: m_envelopeIncrease);
        writer.WriteBoolean(value: m_widthMode);
        writer.WriteBoolean(value: m_dacEnabled);
        writer.WriteBoolean(value: m_enabled);
        writer.WriteBoolean(value: m_lengthEnabled);
        writer.WriteUInt16(value: m_lfsr);
    }

    /// <inheritdoc/>
    public void LoadState(AgbStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_frequencyTimer = reader.ReadInt32();
        m_lengthCounter = reader.ReadInt32();
        m_envelopeVolume = reader.ReadInt32();
        m_envelopeInitial = reader.ReadInt32();
        m_envelopePeriod = reader.ReadInt32();
        m_envelopeTimer = reader.ReadInt32();
        m_divisorCode = reader.ReadInt32();
        m_shiftClock = reader.ReadInt32();
        m_envelopeIncrease = reader.ReadBoolean();
        m_widthMode = reader.ReadBoolean();
        m_dacEnabled = reader.ReadBoolean();
        m_enabled = reader.ReadBoolean();
        m_lengthEnabled = reader.ReadBoolean();
        m_lfsr = reader.ReadUInt16();
    }
}
