namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbBus : ISnapshotable {
    /// <inheritdoc/>
    // The bus-owned memories (on-board EWRAM, on-chip IWRAM, the I/O register backing) and every bus-level latch: the
    // open-bus value with its pipeline-fetch companion and the post-DMA lingering value/window, the BIOS read latch
    // and its in-BIOS/code-fetch flags, the DMA active/stall/prefetch-break flags, the halt/stop flags, the APU sync clock, POSTFLG
    // and KEYCNT, the WAITCNT-derived wait-state table (persisted directly so no re-derive is needed), and the whole
    // game-pak prefetch FIFO with its clock state. The BIOS image itself is immutable and identified by the
    // snapshot's identity stamp, so it is not serialized.
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBytes(value: m_ewram);
        writer.WriteBytes(value: m_iwram);
        writer.WriteBytes(value: m_io);

        writer.WriteUInt32(value: m_openBus);
        writer.WriteUInt32(value: m_prevFetchHalf);
        writer.WriteUInt32(value: m_dmaOpenBus);
        writer.WriteInt32(value: m_dmaOpenBusWindow);
        writer.WriteUInt32(value: m_lastBiosOpcode);
        writer.WriteBoolean(value: m_inCodeFetch);
        writer.WriteBoolean(value: m_executingInBios);
        writer.WriteBoolean(value: m_dmaActive);
        writer.WriteBoolean(value: m_dmaStalling);
        writer.WriteBoolean(value: m_dmaBrokeStream);
        writer.WriteBoolean(value: m_halted);
        writer.WriteBoolean(value: m_stopped);
        writer.WriteInt64(value: m_apuClock);
        writer.WriteByte(value: m_postFlag);
        writer.WriteUInt16(value: m_keyControl);

        writer.WriteInt32(value: m_ws0N);
        writer.WriteInt32(value: m_ws0S);
        writer.WriteInt32(value: m_ws1N);
        writer.WriteInt32(value: m_ws1S);
        writer.WriteInt32(value: m_ws2N);
        writer.WriteInt32(value: m_ws2S);
        writer.WriteInt32(value: m_sram);

        writer.WriteBoolean(value: m_prefetchEnabled);
        writer.WriteBlock<ushort>(values: m_prefetchSlots);
        writer.WriteUInt32(value: m_prefetchAddr);
        writer.WriteUInt32(value: m_prefetchLoad);
        writer.WriteInt32(value: m_prefetchWait);
        writer.WriteBoolean(value: m_prefetchStopped);
        writer.WriteBoolean(value: m_prefetchAhead);
    }

    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBytes(destination: m_ewram);
        reader.ReadBytes(destination: m_iwram);
        reader.ReadBytes(destination: m_io);

        m_openBus = reader.ReadUInt32();
        m_prevFetchHalf = reader.ReadUInt32();
        m_dmaOpenBus = reader.ReadUInt32();
        m_dmaOpenBusWindow = reader.ReadInt32();
        m_lastBiosOpcode = reader.ReadUInt32();
        m_inCodeFetch = reader.ReadBoolean();
        m_executingInBios = reader.ReadBoolean();
        m_dmaActive = reader.ReadBoolean();
        m_dmaStalling = reader.ReadBoolean();
        m_dmaBrokeStream = reader.ReadBoolean();
        m_halted = reader.ReadBoolean();
        m_stopped = reader.ReadBoolean();
        m_apuClock = reader.ReadInt64();
        m_postFlag = reader.ReadByte();
        m_keyControl = reader.ReadUInt16();

        m_ws0N = reader.ReadInt32();
        m_ws0S = reader.ReadInt32();
        m_ws1N = reader.ReadInt32();
        m_ws1S = reader.ReadInt32();
        m_ws2N = reader.ReadInt32();
        m_ws2S = reader.ReadInt32();
        m_sram = reader.ReadInt32();

        m_prefetchEnabled = reader.ReadBoolean();
        reader.ReadBlock<ushort>(destination: m_prefetchSlots);
        m_prefetchAddr = reader.ReadUInt32();
        m_prefetchLoad = reader.ReadUInt32();
        m_prefetchWait = reader.ReadInt32();
        m_prefetchStopped = reader.ReadBoolean();
        m_prefetchAhead = reader.ReadBoolean();
    }
}
