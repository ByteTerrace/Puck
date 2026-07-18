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
        writer.WriteInt32(value: m_lengthCounter);
        writer.WriteBoolean(value: m_lengthEnabled);
        writer.WriteInt32(value: m_envelopeVolume);
        writer.WriteInt32(value: m_envelopeInitial);
        writer.WriteBoolean(value: m_envelopeIncrease);
        writer.WriteInt32(value: m_envelopePeriod);
        writer.WriteInt32(value: m_envelopeTimer);
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
        m_lengthCounter = reader.ReadInt32();
        m_lengthEnabled = reader.ReadBoolean();
        m_envelopeVolume = reader.ReadInt32();
        m_envelopeInitial = reader.ReadInt32();
        m_envelopeIncrease = reader.ReadBoolean();
        m_envelopePeriod = reader.ReadInt32();
        m_envelopeTimer = reader.ReadInt32();
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
