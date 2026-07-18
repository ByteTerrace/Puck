namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbCartridge : ISnapshotable {
    /// <inheritdoc/>
    // The non-volatile save image plus every backup state machine: the flash command/unlock phase + selected bank,
    // the serial EEPROM's command buffer / detected bus width / read-shift state, the game-pak burst-page counter,
    // and the GPIO pins wired to whichever devices are present — the S-3511A RTC's serial-protocol state + latched
    // BCD time registers, the rumble motor's latched on/off bool, and the solar sensor's counter/edge/threshold. The
    // ROM image and the cycle-provider delegate are immutable wiring, not state, so they are not serialized (the
    // snapshot restores into the same cartridge, keeping both). SaveDirty rides along so a restore reflects the
    // recorded persistence state.
    public void SaveState(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBytes(value: m_save);
        writer.WriteBoolean(value: SaveDirty);

        writer.WriteInt32(value: m_eepromAddressBits);
        writer.WriteBytes(value: m_eepromCommand);
        writer.WriteInt32(value: m_eepromCommandLength);
        writer.WriteUInt64(value: m_eepromReadData);
        writer.WriteInt32(value: m_eepromReadBitsRemaining);

        writer.WriteInt32(value: m_flashPhase);
        writer.WriteByte(value: m_flashCommand);
        writer.WriteInt32(value: m_flashBank);

        writer.WriteUInt32(value: m_romBurstPage);
        writer.WriteBoolean(value: m_romBurst);

        writer.WriteInt32(value: m_gpioPins);
        writer.WriteInt32(value: m_gpioDirection);
        writer.WriteBoolean(value: m_gpioReadable);

        writer.WriteBytes(value: m_rtcTime);
        writer.WriteBoolean(value: m_rtcSckEdge);
        writer.WriteBoolean(value: m_rtcCommandActive);
        writer.WriteBoolean(value: m_rtcSioOutput);
        writer.WriteInt32(value: m_rtcCommand);
        writer.WriteInt32(value: m_rtcBits);
        writer.WriteInt32(value: m_rtcBitsRead);
        writer.WriteInt32(value: m_rtcBytesRemaining);
        writer.WriteByte(value: m_rtcControl);

        writer.WriteBoolean(value: m_rumbleMotorOn);

        writer.WriteInt32(value: m_lightCounter);
        writer.WriteBoolean(value: m_lightEdge);
        writer.WriteByte(value: m_lightThreshold);

        writer.WriteInt32(value: m_tiltState);
        writer.WriteInt32(value: m_tiltLiveX);
        writer.WriteInt32(value: m_tiltLiveY);
        writer.WriteInt32(value: m_tiltX);
        writer.WriteInt32(value: m_tiltY);
    }

    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBytes(destination: m_save);
        SaveDirty = reader.ReadBoolean();

        m_eepromAddressBits = reader.ReadInt32();
        reader.ReadBytes(destination: m_eepromCommand);
        m_eepromCommandLength = reader.ReadInt32();
        m_eepromReadData = reader.ReadUInt64();
        m_eepromReadBitsRemaining = reader.ReadInt32();

        m_flashPhase = reader.ReadInt32();
        m_flashCommand = reader.ReadByte();
        m_flashBank = reader.ReadInt32();

        m_romBurstPage = reader.ReadUInt32();
        m_romBurst = reader.ReadBoolean();

        m_gpioPins = reader.ReadInt32();
        m_gpioDirection = reader.ReadInt32();
        m_gpioReadable = reader.ReadBoolean();

        reader.ReadBytes(destination: m_rtcTime);
        m_rtcSckEdge = reader.ReadBoolean();
        m_rtcCommandActive = reader.ReadBoolean();
        m_rtcSioOutput = reader.ReadBoolean();
        m_rtcCommand = reader.ReadInt32();
        m_rtcBits = reader.ReadInt32();
        m_rtcBitsRead = reader.ReadInt32();
        m_rtcBytesRemaining = reader.ReadInt32();
        m_rtcControl = reader.ReadByte();

        m_rumbleMotorOn = reader.ReadBoolean();

        m_lightCounter = reader.ReadInt32();
        m_lightEdge = reader.ReadBoolean();
        m_lightThreshold = reader.ReadByte();

        m_tiltState = reader.ReadInt32();
        m_tiltLiveX = reader.ReadInt32();
        m_tiltLiveY = reader.ReadInt32();
        m_tiltX = reader.ReadInt32();
        m_tiltY = reader.ReadInt32();
    }
}
