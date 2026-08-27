namespace Puck.AdvancedGamingBrick;

public sealed partial class ApuNoiseChannel : ISnapshotable {
    /// <inheritdoc/>
    // The LFSR plus the frequency timer, length/envelope unit, and the polynomial (divisor/width/shift) fields.
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteInt32(value: m_frequencyTimer);
        writer.WriteInt32(value: m_length.Counter);
        writer.WriteInt32(value: m_envelope.Volume);
        writer.WriteInt32(value: m_envelope.Initial);
        writer.WriteInt32(value: m_envelope.Period);
        writer.WriteInt32(value: m_envelope.Timer);
        writer.WriteInt32(value: m_divisorCode);
        writer.WriteInt32(value: m_shiftClock);
        writer.WriteBoolean(value: m_envelope.Increase);
        writer.WriteBoolean(value: m_widthMode);
        writer.WriteBoolean(value: m_dacEnabled);
        writer.WriteBoolean(value: m_enabled);
        writer.WriteBoolean(value: m_length.Enabled);
        writer.WriteUInt16(value: m_lfsr);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_frequencyTimer = reader.ReadInt32();
        m_length.Counter = reader.ReadInt32();
        m_envelope.Volume = reader.ReadInt32();
        m_envelope.Initial = reader.ReadInt32();
        m_envelope.Period = reader.ReadInt32();
        m_envelope.Timer = reader.ReadInt32();
        m_divisorCode = reader.ReadInt32();
        m_shiftClock = reader.ReadInt32();
        m_envelope.Increase = reader.ReadBoolean();
        m_widthMode = reader.ReadBoolean();
        m_dacEnabled = reader.ReadBoolean();
        m_enabled = reader.ReadBoolean();
        m_length.Enabled = reader.ReadBoolean();
        m_lfsr = reader.ReadUInt16();
    }
}
