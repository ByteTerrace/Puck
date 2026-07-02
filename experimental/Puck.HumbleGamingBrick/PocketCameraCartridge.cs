namespace Puck.HumbleGamingBrick;

/// <summary>
/// The Pocket Camera (MAC-GBD) mapper: MBC3-like banking — a six-bit ROM bank (a written zero reading as one) and a
/// four-bit RAM bank — plus the camera's register block, which bit&#160;4 of the RAM-bank register maps over the whole
/// <c>0xA000</c>–<c>0xBFFF</c> window (no RAM enable required, mirroring every <c>0x80</c> bytes). The sensor is a
/// <b>deliberately deterministic stub</b>: there is no image source and no noise generator, so a capture triggered
/// through register&#160;0 completes within the write itself — the busy bit is stored already clear and the sensor
/// always reads ready — and the capture area in RAM keeps whatever was last written. The register file is real
/// snapshotted state, and reads of it return <c>0x00</c> exactly as the write-only hardware block does.
/// </summary>
public sealed class PocketCameraCartridge : CartridgeBase {
    private const int CameraRegisterCount = 0x36;
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    private readonly byte[] m_cameraRegisters;
    private readonly int m_ramBankWrapMask;

    private bool m_cameraSelected;
    private int m_ramBank;
    private bool m_ramEnabled;
    private int m_romBank;

    /// <summary>Creates a Pocket Camera cartridge with its registers at reset (ROM bank 1, RAM disabled, RAM window
    /// selected) and a zeroed camera register file.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public PocketCameraCartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) {
        m_cameraRegisters = new byte[CameraRegisterCount];
        m_ramBankWrapMask = ComputeBankWrapMask(byteCount: header.RamByteCount, bankSize: RamBankSize);
        m_romBank = 1;
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (Header.HasRam && m_ramEnabled);

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: RAM enable (the camera block ignores it)
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 1: // 0x2000-0x3FFF: six-bit ROM bank, zero reads as one
                m_romBank = (value & 0x3F);

                if (m_romBank == 0) {
                    m_romBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: bit 4 maps the camera block over the window; bits 3-0 select the RAM bank
                m_cameraSelected = ((value & 0x10) != 0);
                m_ramBank = (value & 0x0F);

                break;
            default: // 0x6000-0x7FFF: no register
                break;
        }
    }
    /// <summary>Reads from the external window: <c>0x00</c> over the whole camera block (the register file is
    /// write-only, and the always-ready sensor keeps register&#160;0's busy bit clear), otherwise banked RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns><c>0x00</c> while the camera is selected, or the RAM byte.</returns>
    public override byte ReadRam(ushort address) =>
        m_cameraSelected ? (byte)0x00 : base.ReadRam(address: address);
    /// <summary>Writes to the external window: a camera register while the camera block is selected, otherwise banked
    /// RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value to store.</param>
    public override void WriteRam(ushort address, byte value) {
        if (!m_cameraSelected) {
            base.WriteRam(address: address, value: value);

            return;
        }

        var register = ((address - MemoryMap.ExternalRamStart) & 0x7F);

        if (register < CameraRegisterCount) {
            // Register 0's bit 0 triggers a capture; the always-ready sensor completes it within this write, so the
            // busy bit is stored already clear and only the capture-mode bits (1-2) persist. No noise, no randomness.
            m_cameraRegisters[register] = ((register == 0) ? (byte)(value & 0x06) : value);
        }
    }

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) =>
        (address <= MemoryMap.RomBank0End)
            ? address
            : ((m_romBank * RomBankSize) + (address - MemoryMap.RomBankNStart));
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        (((m_ramBank & m_ramBankWrapMask) * RamBankSize) + (address - MemoryMap.ExternalRamStart));
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteBytes(value: m_cameraRegisters);
        writer.WriteBoolean(value: m_cameraSelected);
        writer.WriteInt32(value: m_ramBank);
        writer.WriteBoolean(value: m_ramEnabled);
        writer.WriteInt32(value: m_romBank);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        reader.ReadBytes(destination: m_cameraRegisters);
        m_cameraSelected = reader.ReadBoolean();
        m_ramBank = reader.ReadInt32();
        m_ramEnabled = reader.ReadBoolean();
        m_romBank = reader.ReadInt32();
    }
}
