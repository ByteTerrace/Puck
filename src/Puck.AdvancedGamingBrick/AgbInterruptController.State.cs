namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbInterruptController : ISnapshotable {
    /// <inheritdoc/>
    // Both pipeline stages (committed [0] and programmed [1] of IE/IF/IME) plus the synchronizer line — the whole
    // double-buffered state the 1-cycle register-visibility and 2-cycle recognition latencies emerge from.
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteUInt16(value: m_enable0);
        writer.WriteUInt16(value: m_enable1);
        writer.WriteUInt16(value: m_flag0);
        writer.WriteUInt16(value: m_flag1);
        writer.WriteBoolean(value: m_ime0);
        writer.WriteBoolean(value: m_ime1);
        writer.WriteBoolean(value: m_synchronizer);
    }

    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_enable0 = reader.ReadUInt16();
        m_enable1 = reader.ReadUInt16();
        m_flag0 = reader.ReadUInt16();
        m_flag1 = reader.ReadUInt16();
        m_ime0 = reader.ReadBoolean();
        m_ime1 = reader.ReadBoolean();
        m_synchronizer = reader.ReadBoolean();
    }
}
