namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MMM01 multi-game mapper used by compilation cartridges. At reset the controller is <em>unlocked</em> and boots
/// the menu by mapping the image's LAST two banks over the ROM regions — banks <c>-2</c> and <c>-1</c>, wrapped
/// through the power-of-two bank mask. While unlocked, the menu programs the selected game's base-bank and mask
/// registers, then sets bit&#160;6 of the <c>0x0000</c>–<c>0x1FFF</c> register to lock; once locked the controller
/// behaves like an MBC1 windowed onto that game — the mask-frozen bank bits hold the base while the game's own bank
/// writes move only inside its window, and the fixed region duplicating the switchable one bumps the switchable bank
/// by one (the zero-adjust). The multiplex flag reroutes the RAM-bank register's low bits into ROM bank bits 5–6 and
/// the mid ROM bits into the RAM bank, the large-cartridge wiring. Every register is guarded exactly as on hardware:
/// the base, mask, multiplex, and mode-disable fields only accept writes while unlocked.
/// </summary>
public sealed class Mmm01Cartridge : CartridgeBase {
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    private readonly int m_ramBankWrapMask;
    private readonly int m_romBankWrapMask;

    private bool m_locked;
    private bool m_mbc1Mode;
    private bool m_mbc1ModeDisable;
    private bool m_multiplexMode;
    private int m_ramBankHigh;
    private int m_ramBankLow;
    private int m_ramBankMask;
    private bool m_ramEnabled;
    private int m_romBankHigh;
    private int m_romBankLow;
    private int m_romBankMask;
    private int m_romBankMid;

    /// <summary>Creates an MMM01 cartridge at reset: unlocked with the menu banks mapped and RAM disabled.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public Mmm01Cartridge(byte[] rom, CartridgeHeader header)
        : base(
        rom: rom,
        header: header
    ) {
        m_ramBankWrapMask = ComputeBankWrapMask(
            byteCount: header.RamByteCount,
            bankSize: RamBankSize
        );
        m_romBankWrapMask = ComputeBankWrapMask(
            byteCount: rom.Length,
            bankSize: RomBankSize
        );
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (Header.HasRam && m_ramEnabled);

    // The bank the switchable 0x4000-0x7FFF region reads: the menu's last bank while unlocked, else the full low bits
    // with the mid/high bits — bumped by one when it would duplicate the fixed region (the MBC1-style zero-adjust).
    private int ResolveHighRomBank() {
        if (!m_locked) {
            return -1;
        }

        var middleBits = (m_multiplexMode
            ? m_ramBankLow
            : m_romBankMid
        );
        var highBank = m_romBankLow | (middleBits << 5) | (m_romBankHigh << 7);

        return ((highBank == ResolveLowRomBank())
            ? (highBank + 1)
            : highBank
        );
    }
    // The bank the fixed 0x0000-0x3FFF region reads: the menu's second-to-last bank while unlocked (negative, wrapped
    // by the caller), else the mask-frozen base with the mid/high bits — multiplex swaps the RAM-bank register into
    // the mid position, except in MBC1 mode where the fixed region drops the mid bits entirely.
    private int ResolveLowRomBank() {
        if (!m_locked) {
            return -2;
        }

        var middleBits = (m_multiplexMode
            ? (m_mbc1Mode
                ? 0
                : m_ramBankLow)
            : m_romBankMid
        );

        return (m_romBankLow & (m_romBankMask << 1)) | (middleBits << 5) | (m_romBankHigh << 7);
    }
    private int ResolveRamBank() {
        if (!m_locked) {
            return 0;
        }

        return (m_multiplexMode
            ? m_romBankMid | (m_ramBankHigh << 2)
            : m_ramBankLow | (m_ramBankHigh << 2)
        );
    }

    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        (((ResolveRamBank() & m_ramBankWrapMask) * RamBankSize) + (address - MemoryMap.ExternalRamStart));
    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) {
        var bank = ((address <= MemoryMap.RomBank0End)
            ? ResolveLowRomBank()
            : ResolveHighRomBank()
        );

        return (((bank & m_romBankWrapMask) * RomBankSize) + (address & (RomBankSize - 1)));
    }
    /// <inheritdoc/>
    protected override void TransferRegisters<TTransfer>(TTransfer transfer) {
        transfer.Boolean(value: ref m_locked);
        transfer.Boolean(value: ref m_mbc1Mode);
        transfer.Boolean(value: ref m_mbc1ModeDisable);
        transfer.Boolean(value: ref m_multiplexMode);
        transfer.Int32(value: ref m_ramBankHigh);
        transfer.Int32(value: ref m_ramBankLow);
        transfer.Int32(value: ref m_ramBankMask);
        transfer.Boolean(value: ref m_ramEnabled);
        transfer.Int32(value: ref m_romBankHigh);
        transfer.Int32(value: ref m_romBankLow);
        transfer.Int32(value: ref m_romBankMid);
        transfer.Int32(value: ref m_romBankMask);
    }

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: RAM enable always; unlocked, also the RAM-bank mask and the lock bit (bit 6)
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                if (!m_locked) {
                    m_ramBankMask = (value >> 4);
                    m_locked = ((value & 0x40) != 0);
                }

                break;
            case 1: // 0x2000-0x3FFF: low ROM-bank bits — the mask-frozen bits keep the programmed base; unlocked, also the mid bits
                if (!m_locked) {
                    m_romBankMid = (value >> 5);
                }

                m_romBankLow = (m_romBankLow & (m_romBankMask << 1)) | (value & ~(m_romBankMask << 1));

                break;
            case 2: // 0x4000-0x5FFF: low RAM-bank bits (non-mask bits forced high, cancelled by the wrap); unlocked, also the high ROM/RAM bits and the MBC1-mode disable
                m_ramBankLow = value | ~m_ramBankMask;

                if (!m_locked) {
                    m_ramBankHigh = (value >> 2);
                    m_romBankHigh = (value >> 4);
                    m_mbc1ModeDisable = ((value & 0x40) != 0);
                }

                break;
            default: // 0x6000-0x7FFF: MBC1 banking mode (unless disabled); unlocked, also the ROM-bank mask and the multiplex flag
                if (!m_mbc1ModeDisable) {
                    m_mbc1Mode = ((value & 0x01) != 0);
                }

                if (!m_locked) {
                    m_romBankMask = (value >> 2);
                    m_multiplexMode = ((value & 0x40) != 0);
                }

                break;
        }
    }
}
