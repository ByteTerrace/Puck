namespace Puck.AdvancedGamingBrick;

public sealed partial class ApuPulseChannel : ISnapshotable {
    /// <inheritdoc/>
    // Duty position, frequency timer, length/envelope/sweep unit state — everything but the readonly has-sweep wiring.
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteInt32(value: m_dutyPattern);
        writer.WriteInt32(value: m_dutyStep);
        writer.WriteInt32(value: m_frequency);
        writer.WriteInt32(value: m_frequencyTimer);
        writer.WriteInt32(value: m_length.Counter);
        writer.WriteBoolean(value: m_length.Enabled);
        writer.WriteInt32(value: m_envelope.Volume);
        writer.WriteInt32(value: m_envelope.Initial);
        writer.WriteBoolean(value: m_envelope.Increase);
        writer.WriteInt32(value: m_envelope.Period);
        writer.WriteInt32(value: m_envelope.Timer);
        writer.WriteBoolean(value: m_dacEnabled);
        writer.WriteBoolean(value: m_enabled);
        writer.WriteInt32(value: m_sweepPeriod);
        writer.WriteBoolean(value: m_sweepDecrease);
        writer.WriteInt32(value: m_sweepShift);
        writer.WriteInt32(value: m_sweepTimer);
        writer.WriteInt32(value: m_sweepShadow);
        writer.WriteBoolean(value: m_sweepActive);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_dutyPattern = reader.ReadInt32();
        m_dutyStep = reader.ReadInt32();
        m_frequency = reader.ReadInt32();
        m_frequencyTimer = reader.ReadInt32();
        m_length.Counter = reader.ReadInt32();
        m_length.Enabled = reader.ReadBoolean();
        m_envelope.Volume = reader.ReadInt32();
        m_envelope.Initial = reader.ReadInt32();
        m_envelope.Increase = reader.ReadBoolean();
        m_envelope.Period = reader.ReadInt32();
        m_envelope.Timer = reader.ReadInt32();
        m_dacEnabled = reader.ReadBoolean();
        m_enabled = reader.ReadBoolean();
        m_sweepPeriod = reader.ReadInt32();
        m_sweepDecrease = reader.ReadBoolean();
        m_sweepShift = reader.ReadInt32();
        m_sweepTimer = reader.ReadInt32();
        m_sweepShadow = reader.ReadInt32();
        m_sweepActive = reader.ReadBoolean();
    }
}
