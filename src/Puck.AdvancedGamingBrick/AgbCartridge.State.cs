namespace Puck.AdvancedGamingBrick;

public sealed partial class AgbCartridge : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // The non-volatile save image plus every backup state machine: the flash command/unlock phase + selected bank,
    // the serial EEPROM's command buffer / detected bus width / read-shift state, the game-pak burst-page counter,
    // and the GPIO pins wired to whichever devices are present — the S-3511A RTC's serial-protocol state + latched
    // BCD time registers, the rumble motor's latched on/off bool, and the solar sensor's counter/edge/threshold. The
    // ROM image and the cycle-provider delegate are immutable wiring, not state, so they are not serialized (the
    // snapshot restores into the same cartridge, keeping both). SaveDirty rides along so a restore reflects the
    // recorded persistence state. The host separately requests a disk flush on restore, because disk contents do
    // not rewind with this flag.
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_save);
        transfer.Boolean(value: ref m_saveDirty);

        transfer.Int32(value: ref m_eepromAddressBits);
        transfer.Block(values: m_eepromCommand);
        transfer.Int32(value: ref m_eepromCommandLength);
        transfer.UInt64(value: ref m_eepromReadData);
        transfer.Int32(value: ref m_eepromReadBitsRemaining);

        transfer.Int32(value: ref m_flashPhase);
        transfer.Byte(value: ref m_flashCommand);
        transfer.Int32(value: ref m_flashBank);

        transfer.UInt32(value: ref m_romBurstPage);
        transfer.Boolean(value: ref m_romBurst);

        transfer.Int32(value: ref m_gpioPins);
        transfer.Int32(value: ref m_gpioDirection);
        transfer.Boolean(value: ref m_gpioReadable);

        transfer.Block(values: m_rtcTime);
        transfer.Boolean(value: ref m_rtcSckEdge);
        transfer.Boolean(value: ref m_rtcCommandActive);
        transfer.Boolean(value: ref m_rtcSioOutput);
        transfer.Int32(value: ref m_rtcCommand);
        transfer.Int32(value: ref m_rtcBits);
        transfer.Int32(value: ref m_rtcBitsRead);
        transfer.Int32(value: ref m_rtcBytesRemaining);
        transfer.Byte(value: ref m_rtcControl);

        transfer.Boolean(value: ref m_rumbleMotorOn);

        transfer.Int32(value: ref m_lightCounter);
        transfer.Boolean(value: ref m_lightEdge);
        transfer.Byte(value: ref m_lightThreshold);

        transfer.Int32(value: ref m_tiltState);
        transfer.Int32(value: ref m_tiltLiveX);
        transfer.Int32(value: ref m_tiltLiveY);
        transfer.Int32(value: ref m_tiltX);
        transfer.Int32(value: ref m_tiltY);
    }
}
