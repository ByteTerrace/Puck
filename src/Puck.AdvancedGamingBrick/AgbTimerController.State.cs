namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbTimerController : ISnapshotable {
    /// <inheritdoc/>
    // Per-timer live counters/reloads/control plus the deferred-by-one-cycle latch discipline (control + reload
    // pending flags and their latched values), the enable-reload pending flags, and the in-flight overflow-IRQ delay
    // countdowns — the latches and countdowns are load-bearing: dropping them would lose a write, or an overflow's
    // pending interrupt, in flight at the snapshot instant. The closed-form anchors (clock + value) and the scheduled
    // flag capture where each prescaler timer is between overflows; the overflow events themselves are never
    // serialized — they are re-derived from the anchors on restore.
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBlock<int>(values: m_period);
        writer.WriteBlock<int>(values: m_reload);
        writer.WriteBlock<int>(values: m_frequency);
        WriteBooleans(writer: writer, values: m_enable);
        WriteBooleans(writer: writer, values: m_irqEnabled);
        WriteBooleans(writer: writer, values: m_cascade);
        WriteBooleans(writer: writer, values: m_pending);
        writer.WriteBlock<int>(values: m_irqCountdown);

        writer.WriteBlock<long>(values: m_anchorClock);
        writer.WriteBlock<int>(values: m_anchorValue);

        WriteBooleans(writer: writer, values: m_controlFlag);
        writer.WriteBlock<int>(values: m_latchControl);
        writer.WriteBlock<int>(values: m_reloadFlags);
        writer.WriteBlock<int>(values: m_latchReload);
        writer.WriteBoolean(value: m_timerLatched);
        writer.WriteBoolean(value: m_scheduled);
    }

    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBlock<int>(destination: m_period);
        reader.ReadBlock<int>(destination: m_reload);
        reader.ReadBlock<int>(destination: m_frequency);
        ReadBooleans(reader: reader, values: m_enable);
        ReadBooleans(reader: reader, values: m_irqEnabled);
        ReadBooleans(reader: reader, values: m_cascade);
        ReadBooleans(reader: reader, values: m_pending);
        reader.ReadBlock<int>(destination: m_irqCountdown);

        reader.ReadBlock<long>(destination: m_anchorClock);
        reader.ReadBlock<int>(destination: m_anchorValue);

        ReadBooleans(reader: reader, values: m_controlFlag);
        reader.ReadBlock<int>(destination: m_latchControl);
        reader.ReadBlock<int>(destination: m_reloadFlags);
        reader.ReadBlock<int>(destination: m_latchReload);
        m_timerLatched = reader.ReadBoolean();
        m_scheduled = reader.ReadBoolean();

        // Re-derive the overflow events from the anchors (the scheduler cleared its queue in its own LoadState, run
        // first). Only a scheduled prescaler timer owns a live overflow event; everything else stays descheduled.
        for (var timer = 0; (timer < 4); ++timer) {
            if (m_scheduled && m_enable[timer] && !m_cascade[timer]) {
                ScheduleOverflow(timer: timer);
            }
        }
    }

    private static void WriteBooleans(StateWriter writer, bool[] values) {
        foreach (var value in values) {
            writer.WriteBoolean(value: value);
        }
    }
    private static void ReadBooleans(StateReader reader, bool[] values) {
        for (var i = 0; (i < values.Length); ++i) {
            values[i] = reader.ReadBoolean();
        }
    }
}
