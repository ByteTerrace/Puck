namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbTimerController : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        RefreshPendingLatch();

        // Re-derive the overflow events from the anchors (the scheduler cleared its queue in its own LoadState, run
        // first). Only a scheduled prescaler timer owns a live overflow event; everything else stays descheduled.
        for (var timer = 0; (timer < 4); ++timer) {
            if (
                m_scheduled &&
                m_enable[timer] &&
                !m_cascade[timer]
            ) {
                ScheduleOverflow(timer: timer);
            }
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // Per-timer live counters/reloads/control plus the deferred-by-one-cycle latch discipline (control + reload
    // pending flags and their latched values), the enable-reload pending flags, and the in-flight overflow-IRQ delay
    // countdowns — the latches and countdowns are load-bearing: dropping them would lose a write, or an overflow's
    // pending interrupt, in flight at the snapshot instant. The closed-form anchors (clock + value) and the scheduled
    // flag capture where each prescaler timer is between overflows; the overflow events themselves are never
    // serialized — they are re-derived from the anchors on restore.
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_period);
        transfer.Block(values: m_reload);
        transfer.Block(values: m_frequency);
        transfer.Block(values: m_enable);
        transfer.Block(values: m_irqEnabled);
        transfer.Block(values: m_cascade);
        transfer.Block(values: m_pending);
        transfer.Block(values: m_irqCountdown);

        transfer.Block(values: m_anchorClock);
        transfer.Block(values: m_anchorValue);

        transfer.Block(values: m_controlFlag);
        transfer.Block(values: m_latchControl);
        transfer.Block(values: m_reloadFlags);
        transfer.Block(values: m_latchReload);
        transfer.Boolean(value: ref m_timerLatched);
        transfer.Boolean(value: ref m_scheduled);
    }
}
