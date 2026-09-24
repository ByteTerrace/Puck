namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbPpu : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // The video memories (palette/VRAM/OAM), the display register file, the current raster position and its H/V-blank
    // and DMA-trigger latches, the internal affine reference accumulators (which walk per scanline within a frame),
    // the raster event's fire instant, AND the framebuffer itself — a mid-frame snapshot has already committed this
    // frame's earlier scanlines, so the framebuffer must travel with the state to resume bit-identically. The
    // per-scanline scratch layers are recomputed from scratch each line, so they are not persisted.
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_palette);
        transfer.Block(values: m_vram);
        transfer.Block(values: m_oam);
        transfer.Block(values: m_registers);
        transfer.Block(values: m_framebuffer);
        transfer.Block(values: m_affineRefX);
        transfer.Block(values: m_affineRefY);

        transfer.Int32(value: ref m_line);
        transfer.Boolean(value: ref m_inHBlank);
        transfer.UInt16(value: ref m_dispStatControl);
        transfer.Boolean(value: ref m_hblankFlag);
        transfer.Boolean(value: ref m_vblankStarted);
        transfer.Boolean(value: ref m_hblankStarted);
        transfer.Boolean(value: ref m_videoCaptureStarted);
        transfer.Boolean(value: ref m_videoCaptureEnded);

        m_scheduler.TransferEvent(
            e: m_event,
            transfer: transfer
        );
    }
}
