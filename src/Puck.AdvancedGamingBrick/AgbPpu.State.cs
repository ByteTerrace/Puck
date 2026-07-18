namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbPpu : ISnapshotable {
    /// <inheritdoc/>
    // The video memories (palette/VRAM/OAM), the display register file, the current raster position and its H/V-blank
    // and DMA-trigger latches, the internal affine reference accumulators (which walk per scanline within a frame),
    // the raster event's fire instant, AND the framebuffer itself — a mid-frame snapshot has already committed this
    // frame's earlier scanlines, so the framebuffer must travel with the state to resume bit-identically. The
    // per-scanline scratch layers are recomputed from scratch each line, so they are not persisted.
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBytes(value: m_palette);
        writer.WriteBytes(value: m_vram);
        writer.WriteBytes(value: m_oam);
        writer.WriteBlock<ushort>(values: m_registers);
        writer.WriteBlock<uint>(values: m_framebuffer);
        writer.WriteBlock<int>(values: m_affineRefX);
        writer.WriteBlock<int>(values: m_affineRefY);

        writer.WriteInt32(value: m_line);
        writer.WriteBoolean(value: m_inHBlank);
        writer.WriteUInt16(value: m_dispStatControl);
        writer.WriteBoolean(value: m_hblankFlag);
        writer.WriteBoolean(value: m_vblankStarted);
        writer.WriteBoolean(value: m_hblankStarted);
        writer.WriteBoolean(value: m_videoCaptureStarted);
        writer.WriteBoolean(value: m_videoCaptureEnded);

        writer.WriteBoolean(value: m_event.Scheduled);
        writer.WriteInt64(value: m_event.When);
    }

    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBytes(destination: m_palette);
        reader.ReadBytes(destination: m_vram);
        reader.ReadBytes(destination: m_oam);
        reader.ReadBlock<ushort>(destination: m_registers);
        reader.ReadBlock<uint>(destination: m_framebuffer);
        reader.ReadBlock<int>(destination: m_affineRefX);
        reader.ReadBlock<int>(destination: m_affineRefY);

        m_line = reader.ReadInt32();
        m_inHBlank = reader.ReadBoolean();
        m_dispStatControl = reader.ReadUInt16();
        m_hblankFlag = reader.ReadBoolean();
        m_vblankStarted = reader.ReadBoolean();
        m_hblankStarted = reader.ReadBoolean();
        m_videoCaptureStarted = reader.ReadBoolean();
        m_videoCaptureEnded = reader.ReadBoolean();

        // Rebuild the raster event on the (already-cleared) scheduler queue. The PPU always has an event in flight,
        // but the flag is honored for symmetry with the general restore discipline.
        var scheduled = reader.ReadBoolean();
        var when = reader.ReadInt64();

        if (scheduled) {
            m_scheduler.ScheduleAbsolute(e: m_event, when: when);
        }
    }
}
