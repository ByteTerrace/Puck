using System.Numerics;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC1 memory-bank controller: up to 2&#160;MiB of ROM and 32&#160;KiB of battery-backable RAM. Writes into
/// the ROM range are register writes — RAM enable (<c>0x0000</c>–<c>0x1FFF</c>), the low five ROM-bank bits
/// (<c>0x2000</c>–<c>0x3FFF</c>), a two-bit secondary bank used as the upper ROM-bank bits or the RAM bank
/// (<c>0x4000</c>–<c>0x5FFF</c>), and the banking mode (<c>0x6000</c>–<c>0x7FFF</c>). The famous quirk is that a
/// zero in the low five bits reads as one, so banks 0x00/0x20/0x40/0x60 are never selectable in the high ROM area.
/// </summary>
public sealed class Mbc1 : ICartridge {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamBankSize = 0x2000;

    private readonly byte[] m_ram;
    private readonly int m_ramBankMask;
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;

    private bool m_advancedMode;
    private bool m_ramEnabled;
    private int m_romBankLow = 1;
    private int m_secondaryBank;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes an MBC1 cartridge from a ROM image and a RAM size.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public Mbc1(byte[] rom, int ramByteCount = 0) {
        ArgumentNullException.ThrowIfNull(rom);

        m_ram = ((ramByteCount > 0)
            ? new byte[ramByteCount]
            : []);
        m_ramBankMask = BankMask(byteCount: ramByteCount, bankSize: RamBankSize);
        m_rom = rom;
        m_romBankMask = BankMask(byteCount: rom.Length, bankSize: RomBankSize);
    }

    /// <inheritdoc />
    public byte ReadRom(ushort address) {
        // The low region maps to bank 0 normally; in advanced mode a large ROM can bank it via the secondary bits.
        var bank = ((address < RomBankSize)
            ? (m_advancedMode ? ((m_secondaryBank << 5) & m_romBankMask) : 0)
            : (((m_secondaryBank << 5) | LowBank()) & m_romBankMask));

        return ReadRomBank(
            bank: bank,
            offset: (address & (RomBankSize - 1))
        );
    }
    /// <inheritdoc />
    public void WriteRom(ushort address, byte value) {
        switch (address >> 13) {
            case 0:
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 1:
                m_romBankLow = (value & 0x1F);

                break;
            case 2:
                m_secondaryBank = (value & 0x03);

                break;
            default:
                m_advancedMode = ((value & 0x01) != 0);

                break;
        }
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        if (!m_ramEnabled) {
            return 0xFF;
        }

        var offset = (RamOffset(address: address));

        return (((uint)offset < (uint)m_ram.Length)
            ? m_ram[offset]
            : (byte)0xFF);
    }
    /// <inheritdoc />
    public void WriteRam(ushort address, byte value) {
        if (!m_ramEnabled) {
            return;
        }

        var offset = (RamOffset(address: address));

        if ((uint)offset < (uint)m_ram.Length) {
            m_ram[offset] = value;
        }
    }
    /// <inheritdoc />
    public void LoadSaveRam(ReadOnlySpan<byte> data) {
        if (data.Length == m_ram.Length) {
            data.CopyTo(destination: m_ram);
        }
    }

    // The low five bits, with the hardware quirk that a value of zero reads as one.
    private int LowBank() =>
        ((m_romBankLow == 0) ? 1 : m_romBankLow);

    private int RamOffset(ushort address) {
        // RAM banking only applies in advanced mode; otherwise bank 0.
        var bank = (m_advancedMode ? (m_secondaryBank & m_ramBankMask) : 0);

        return ((bank * RamBankSize) + (address - RamBase));
    }

    private byte ReadRomBank(int bank, int offset) {
        var index = ((bank * RomBankSize) + offset);

        return (((uint)index < (uint)m_rom.Length)
            ? m_rom[index]
            : (byte)0xFF);
    }

    private static int BankMask(int byteCount, int bankSize) {
        var bankCount = (byteCount / bankSize);

        // Bank selection is a bit-mask on real hardware, which equals modulo for the power-of-two bank counts every
        // licensed cartridge uses; guard with a 1-bank floor for tiny/empty images.
        return ((bankCount > 1) && BitOperations.IsPow2(value: (uint)bankCount)
            ? (bankCount - 1)
            : 0);
    }
}
