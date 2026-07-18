namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC5 mapper: up to 8&#160;MiB of ROM through a nine-bit bank select and up to 128&#160;KiB of RAM through a
/// four-bit bank select. Unlike MBC1 it has no unreachable banks — bank zero is selectable in the switchable region —
/// which is why CGB titles that stream from bank zero rely on it. The rumble variants (header type <c>0x1C</c>-
/// <c>0x1E</c>, <see cref="CartridgeHeader.HasRumble"/>) repurpose RAM-bank select bit 3 as the motor line: the
/// hardware masks it out of the bank number (only bits 0-2 select one of up to eight RAM banks on a rumble cart) and
/// latches it as the motor on/off state instead — a deterministic bool, snapshot state like every other register here.
/// </summary>
public sealed class Mbc5Cartridge : CartridgeBase {
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;
    // The rumble motor bit within the RAM-bank select register (0x4000-0x5FFF), masked out of the bank number itself.
    private const int RumbleMotorBit = 0x08;

    private readonly bool m_isRumbleVariant;
    private int m_ramBank;
    private bool m_ramEnabled;
    private int m_romBank;
    private bool m_motorOn;

    /// <summary>Creates an MBC5 cartridge with its registers at reset (ROM bank 1, RAM disabled, motor off).</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public Mbc5Cartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) {
        m_isRumbleVariant = header.HasRumble;
        m_romBank = 1;
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (Header.HasRam && m_ramEnabled);

    /// <summary>Gets the latched rumble motor state — <see langword="true"/> while a rumble-variant cartridge is
    /// driving its motor on. Always <see langword="false"/> on a non-rumble cartridge. Host-facing feedback state
    /// only: it never influences emulated behavior, mirroring the joypad's read-only relationship to host input.</summary>
    public bool MotorOn =>
        (m_isRumbleVariant && m_motorOn);

    /// <inheritdoc/>
    /// <remarks>The MBC5 rumble hardware is on/off only (no PWM), so this is always exactly 0 or 1.</remarks>
    public override float MotorLevel =>
        (MotorOn ? 1f : 0f);

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 12) {
            case 0: // 0x0000-0x0FFF: RAM enable
            case 1: // 0x1000-0x1FFF: RAM enable
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 2: // 0x2000-0x2FFF: low eight ROM-bank bits
                m_romBank = (m_romBank & 0x100) | value;

                break;
            case 3: // 0x3000-0x3FFF: ninth ROM-bank bit
                m_romBank = (m_romBank & 0x0FF) | ((value & 0x01) << 8);

                break;
            case 4: // 0x4000-0x4FFF: RAM bank (+ motor on a rumble variant)
            case 5: // 0x5000-0x5FFF: RAM bank (+ motor on a rumble variant)
                if (m_isRumbleVariant) {
                    m_motorOn = ((value & RumbleMotorBit) != 0);
                    m_ramBank = (value & 0x07);
                } else {
                    m_ramBank = (value & 0x0F);
                }

                break;
            default:
                break;
        }
    }

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) =>
        ((address <= MemoryMap.RomBank0End)
            ? address
            : ((m_romBank * RomBankSize) + (address - MemoryMap.RomBankNStart)));
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        ((m_ramBank * RamBankSize) + (address - MemoryMap.ExternalRamStart));
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteInt32(value: m_ramBank);
        writer.WriteBoolean(value: m_ramEnabled);
        writer.WriteInt32(value: m_romBank);
        writer.WriteBoolean(value: m_motorOn);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        m_ramBank = reader.ReadInt32();
        m_ramEnabled = reader.ReadBoolean();
        m_romBank = reader.ReadInt32();
        m_motorOn = reader.ReadBoolean();
    }
}
