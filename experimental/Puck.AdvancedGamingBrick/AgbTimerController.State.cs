namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbTimerController : IAgbSnapshotable {
    /// <inheritdoc/>
    // Per-timer live counters/reloads/control plus the deferred-by-one-cycle latch discipline (control + reload
    // pending flags and their latched values) and the enable-reload pending flags — the latches are load-bearing:
    // dropping them would lose a write in flight at the snapshot instant. m_anyRunning is derived, but persisted so a
    // restore needs no re-derivation pass.
    public void SaveState(AgbStateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBlock<int>(values: m_period);
        writer.WriteBlock<int>(values: m_reload);
        writer.WriteBlock<int>(values: m_frequency);
        WriteBooleans(writer: writer, values: m_enable);
        WriteBooleans(writer: writer, values: m_irqEnabled);
        WriteBooleans(writer: writer, values: m_cascade);
        WriteBooleans(writer: writer, values: m_pending);

        WriteBooleans(writer: writer, values: m_controlFlag);
        writer.WriteBlock<int>(values: m_latchControl);
        writer.WriteBlock<int>(values: m_reloadFlags);
        writer.WriteBlock<int>(values: m_latchReload);
        writer.WriteBoolean(value: m_timerLatched);
        writer.WriteBoolean(value: m_anyRunning);
    }

    /// <inheritdoc/>
    public void LoadState(AgbStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBlock<int>(destination: m_period);
        reader.ReadBlock<int>(destination: m_reload);
        reader.ReadBlock<int>(destination: m_frequency);
        ReadBooleans(reader: reader, values: m_enable);
        ReadBooleans(reader: reader, values: m_irqEnabled);
        ReadBooleans(reader: reader, values: m_cascade);
        ReadBooleans(reader: reader, values: m_pending);

        ReadBooleans(reader: reader, values: m_controlFlag);
        reader.ReadBlock<int>(destination: m_latchControl);
        reader.ReadBlock<int>(destination: m_reloadFlags);
        reader.ReadBlock<int>(destination: m_latchReload);
        m_timerLatched = reader.ReadBoolean();
        m_anyRunning = reader.ReadBoolean();
    }

    private static void WriteBooleans(AgbStateWriter writer, bool[] values) {
        foreach (var value in values) {
            writer.WriteBoolean(value: value);
        }
    }

    private static void ReadBooleans(AgbStateReader reader, bool[] values) {
        for (var i = 0; (i < values.Length); ++i) {
            values[i] = reader.ReadBoolean();
        }
    }
}
