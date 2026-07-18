namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbSerialController : ISnapshotable {
    /// <inheritdoc/>
    // Every decomposed SIOCNT/RCNT/JOY-bus field, the SIOMULTI/SIODATA registers, and the pending-transfer event's
    // fire instant. The link partner (m_link/m_node) is topology, not machine state, so a restore into the same
    // machine keeps its live connection; only the transfer event's schedule is rebuilt (the callback is already bound
    // to this instance — a delegate is never serialized).
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBoolean(value: m_shiftClockInternal);
        writer.WriteBoolean(value: m_shiftClock2MHz);
        writer.WriteBoolean(value: m_recvEnable);
        writer.WriteBoolean(value: m_sendEnable);
        writer.WriteBoolean(value: m_startBit);
        writer.WriteByte(value: m_uartFlags);
        writer.WriteInt32(value: m_sioMode);
        writer.WriteBoolean(value: m_irqEnable);
        writer.WriteInt32(value: m_multiplayerId);
        writer.WriteBoolean(value: m_multiplayerError);

        writer.WriteBlock<ushort>(values: m_data);
        writer.WriteUInt16(value: m_dataSend);

        writer.WriteBoolean(value: m_rcntSc);
        writer.WriteBoolean(value: m_rcntSd);
        writer.WriteBoolean(value: m_rcntSi);
        writer.WriteBoolean(value: m_rcntSo);
        writer.WriteBoolean(value: m_rcntScMode);
        writer.WriteBoolean(value: m_rcntSdMode);
        writer.WriteBoolean(value: m_rcntSiMode);
        writer.WriteBoolean(value: m_rcntSoMode);
        writer.WriteBoolean(value: m_siIrqEnable);
        writer.WriteInt32(value: m_rcntMode);

        writer.WriteBoolean(value: m_joyReset);
        writer.WriteBoolean(value: m_joyRecvComplete);
        writer.WriteBoolean(value: m_joySendComplete);
        writer.WriteBoolean(value: m_joyResetIrqEnable);
        writer.WriteUInt32(value: m_joyRecv);
        writer.WriteUInt32(value: m_joyTrans);
        writer.WriteBoolean(value: m_joyRecvFlag);
        writer.WriteBoolean(value: m_joySendFlag);
        writer.WriteInt32(value: m_joyGeneralFlag);

        writer.WriteBoolean(value: m_transferEvent.Scheduled);
        writer.WriteInt64(value: m_transferEvent.When);
    }

    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_shiftClockInternal = reader.ReadBoolean();
        m_shiftClock2MHz = reader.ReadBoolean();
        m_recvEnable = reader.ReadBoolean();
        m_sendEnable = reader.ReadBoolean();
        m_startBit = reader.ReadBoolean();
        m_uartFlags = reader.ReadByte();
        m_sioMode = reader.ReadInt32();
        m_irqEnable = reader.ReadBoolean();
        m_multiplayerId = reader.ReadInt32();
        m_multiplayerError = reader.ReadBoolean();

        reader.ReadBlock<ushort>(destination: m_data);
        m_dataSend = reader.ReadUInt16();

        m_rcntSc = reader.ReadBoolean();
        m_rcntSd = reader.ReadBoolean();
        m_rcntSi = reader.ReadBoolean();
        m_rcntSo = reader.ReadBoolean();
        m_rcntScMode = reader.ReadBoolean();
        m_rcntSdMode = reader.ReadBoolean();
        m_rcntSiMode = reader.ReadBoolean();
        m_rcntSoMode = reader.ReadBoolean();
        m_siIrqEnable = reader.ReadBoolean();
        m_rcntMode = reader.ReadInt32();

        m_joyReset = reader.ReadBoolean();
        m_joyRecvComplete = reader.ReadBoolean();
        m_joySendComplete = reader.ReadBoolean();
        m_joyResetIrqEnable = reader.ReadBoolean();
        m_joyRecv = reader.ReadUInt32();
        m_joyTrans = reader.ReadUInt32();
        m_joyRecvFlag = reader.ReadBoolean();
        m_joySendFlag = reader.ReadBoolean();
        m_joyGeneralFlag = reader.ReadInt32();

        // Rebuild the pending-transfer event on the (already-cleared) scheduler queue: read its recorded fire instant
        // and re-arm it only if it was in flight. ScheduleAbsolute re-inserts sorted; the callback is intact.
        var scheduled = reader.ReadBoolean();
        var when = reader.ReadInt64();

        if (scheduled) {
            m_scheduler.ScheduleAbsolute(e: m_transferEvent, when: when);
        }
    }
}
