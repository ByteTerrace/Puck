namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC1 mapper: up to 2&#160;MiB of ROM and 32&#160;KiB of banked RAM, split across a five-bit primary bank select
/// and a two-bit secondary select that the mode flag routes either to the upper ROM-bank bits or to the RAM bank. The
/// quirk that a written primary bank of zero reads as one — making banks <c>0x00</c>/<c>0x20</c>/<c>0x40</c>/<c>0x60</c>
/// unreachable in the switchable region — is preserved. A RAM chip smaller than the full 32&#160;KiB (down to a single
/// 8&#160;KiB bank) wraps the secondary select through its own bank count, so a bank the physical chip does not carry
/// aliases back onto one it does rather than reading open bus. A 1&#160;MiB image whose banks <c>0x10</c>,
/// <c>0x20</c>, and <c>0x30</c> each open on a valid header logo is detected as the MBC1M multicart wiring at load:
/// the board leaves the primary register's top address line unconnected, so it drives only its low four bits, and the
/// secondary register's two bits land at ROM address bits 4-5 instead of 5-6 — the wiring that lets four 16-bank
/// sub-games share the one chip.
/// </summary>
public sealed class Mbc1Cartridge : CartridgeBase {
    // The multicart detection heuristic: a header alone cannot distinguish MBC1M from a plain large-ROM MBC1, so the
    // image itself is read for the tell — each of the four sub-games' own bank 0 carries the header logo at 0x0104.
    private const int MulticartByteCount = 0x100000;
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    private static readonly int[] MulticartLogoBanks = [0x10, 0x20, 0x30];

    private readonly bool m_isMulticart;
    private readonly int m_ramBankWrapMask;

    private bool m_advancedMode;
    private int m_primaryBank;
    private bool m_ramEnabled;
    private int m_secondaryBank;

    /// <summary>Creates an MBC1 cartridge with its registers at reset (primary bank 1, RAM disabled, simple mode).</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public Mbc1Cartridge(byte[] rom, CartridgeHeader header)
        : base(
        rom: rom,
        header: header
    ) {
        m_isMulticart = DetectMulticart(rom: rom);
        m_primaryBank = 1;
        m_ramBankWrapMask = ComputeBankWrapMask(
            byteCount: header.RamByteCount,
            bankSize: RamBankSize
        );
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
                m_primaryBank = value & 0x1F;

                if (m_primaryBank == 0) {
                    m_primaryBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: secondary (two) bank bits
                m_secondaryBank = value & 0x03;

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
        // The multicart board leaves the primary register's top address line unconnected and moves the secondary
        // bits down one position (bits 4-5 rather than 5-6), so only the shift and the primary mask differ.
        var secondaryShift = (m_isMulticart
            ? 4
            : 5);

        if (address <= MemoryMap.RomBank0End) {
            // The fixed region is bank 0 in simple mode, but tracks the secondary bits in advanced mode on large ROMs.
            var lowBank = (m_advancedMode
                ? (m_secondaryBank << secondaryShift)
                : 0);

            return ((lowBank * RomBankSize) + address);
        }

        var primary = (m_isMulticart
            ? (m_primaryBank & 0x0F)
            : m_primaryBank);
        var highBank = (m_secondaryBank << secondaryShift) | primary;

        return ((highBank * RomBankSize) + (address - MemoryMap.RomBankNStart));
    }
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) {
        // A bank the physical RAM chip does not have wraps back onto one it does rather than reading open bus.
        var bank = (m_advancedMode
            ? (m_secondaryBank & m_ramBankWrapMask)
            : 0);

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

    private static bool DetectMulticart(byte[] rom) {
        if (rom.Length != MulticartByteCount) {
            return false;
        }

        foreach (var bank in MulticartLogoBanks) {
            var offset = ((bank * RomBankSize) + CartridgeHeader.LogoOffset);

            if (!rom.AsSpan(
                start: offset,
                length: CartridgeHeader.Logo.Length
            ).SequenceEqual(other: CartridgeHeader.Logo)) {
                return false;
            }
        }

        return true;
    }
}
