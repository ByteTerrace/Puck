namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbBus : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // The bus-owned memories (on-board EWRAM, on-chip IWRAM, the I/O register backing) and every bus-level latch: the
    // open-bus value with its pipeline-fetch companion and the post-DMA lingering value/window, the BIOS read latch
    // and its in-BIOS/code-fetch flags, the DMA active/stall/prefetch-break flags, the halt/stop flags, the APU sync clock, POSTFLG
    // and KEYCNT, the WAITCNT-derived wait-state table (persisted directly so no re-derive is needed), and the whole
    // game-pak prefetch FIFO with its clock state. The BIOS image itself is immutable and identified by the
    // snapshot's identity stamp, so it is not serialized.
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_ewram);
        transfer.Block(values: m_iwram);
        transfer.Block(values: m_io);

        transfer.UInt32(value: ref m_openBus);
        transfer.UInt32(value: ref m_prevFetchHalf);
        transfer.UInt32(value: ref m_dmaOpenBus);
        transfer.Int32(value: ref m_dmaOpenBusWindow);
        transfer.UInt32(value: ref m_lastBiosOpcode);
        transfer.Boolean(value: ref m_inCodeFetch);
        transfer.Boolean(value: ref m_executingInBios);
        transfer.Boolean(value: ref m_dmaActive);
        transfer.Boolean(value: ref m_dmaStalling);
        transfer.Boolean(value: ref m_dmaBrokeStream);
        transfer.Boolean(value: ref m_halted);
        transfer.Boolean(value: ref m_stopped);
        transfer.Int64(value: ref m_apuClock);
        transfer.Byte(value: ref m_postFlag);
        transfer.UInt16(value: ref m_keyControl);

        transfer.Int32(value: ref m_ws0N);
        transfer.Int32(value: ref m_ws0S);
        transfer.Int32(value: ref m_ws1N);
        transfer.Int32(value: ref m_ws1S);
        transfer.Int32(value: ref m_ws2N);
        transfer.Int32(value: ref m_ws2S);
        transfer.Int32(value: ref m_sram);

        transfer.Boolean(value: ref m_prefetchEnabled);
        transfer.Block(values: m_prefetchSlots);
        transfer.UInt32(value: ref m_prefetchAddr);
        transfer.UInt32(value: ref m_prefetchLoad);
        transfer.Int32(value: ref m_prefetchWait);
        transfer.Boolean(value: ref m_prefetchStopped);
        transfer.Boolean(value: ref m_prefetchAhead);
    }
}
