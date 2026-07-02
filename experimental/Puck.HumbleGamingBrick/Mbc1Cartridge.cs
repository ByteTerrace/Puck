namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC1 mapper: up to 2&#160;MiB of ROM and 32&#160;KiB of banked RAM, split across a five-bit primary bank select
/// and a two-bit secondary select that the mode flag routes either to the upper ROM-bank bits or to the RAM bank. The
/// quirk that a written primary bank of zero reads as one — making banks <c>0x00</c>/<c>0x20</c>/<c>0x40</c>/<c>0x60</c>
/// unreachable in the switchable region — is preserved.
/// </summary>
public sealed class Mbc1Cartridge : CartridgeBase {
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    private bool m_advancedMode;
    private int m_primaryBank;
    private bool m_ramEnabled;
    private int m_secondaryBank;

    /// <summary>Creates an MBC1 cartridge with its registers at reset (primary bank 1, RAM disabled, simple mode).</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public Mbc1Cartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) {
        m_primaryBank = 1;
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (Header.HasRam && m_ramEnabled);

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: RAM enable
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 1: // 0x2000-0x3FFF: primary (low five) bank bits, zero reads as one
                m_primaryBank = (value & 0x1F);

                if (m_primaryBank == 0) {
                    m_primaryBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: secondary (two) bank bits
                m_secondaryBank = (value & 0x03);

                break;
            case 3: // 0x6000-0x7FFF: banking mode
                m_advancedMode = ((value & 0x01) != 0);

                break;
            default:
                break;
        }
    }

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) {
        if (address <= MemoryMap.RomBank0End) {
            // The fixed region is bank 0 in simple mode, but tracks the secondary bits in advanced mode on large ROMs.
            var lowBank = m_advancedMode ? (m_secondaryBank << 5) : 0;

            return ((lowBank * RomBankSize) + address);
        }

        var highBank = ((m_secondaryBank << 5) | m_primaryBank);

        return ((highBank * RomBankSize) + (address - MemoryMap.RomBankNStart));
    }
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) {
        var bank = m_advancedMode ? m_secondaryBank : 0;

        return ((bank * RamBankSize) + (address - MemoryMap.ExternalRamStart));
    }
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteBoolean(value: m_advancedMode);
        writer.WriteInt32(value: m_primaryBank);
        writer.WriteBoolean(value: m_ramEnabled);
        writer.WriteInt32(value: m_secondaryBank);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        m_advancedMode = reader.ReadBoolean();
        m_primaryBank = reader.ReadInt32();
        m_ramEnabled = reader.ReadBoolean();
        m_secondaryBank = reader.ReadInt32();
    }
}
