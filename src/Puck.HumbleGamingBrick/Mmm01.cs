using System.Numerics;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MMM01 multi-game memory-bank controller used by compilation cartridges. At reset it is <em>unlocked</em>, and
/// the menu program in the last banks of ROM is mapped (the low region reads the second-to-last bank, the high region
/// the last). The menu programs the base-bank registers and then sets the lock bit, after which the controller
/// behaves like an MBC1 windowed onto the selected game. Both ROM regions are independently banked.
/// </summary>
public sealed class Mmm01 : ICartridge {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamBankSize = 0x2000;

    private readonly byte[] m_ram;
    private readonly int m_ramTotalMask;
    private readonly byte[] m_rom;
    private readonly int m_romTotalMask;

    private bool m_locked;
    private bool m_ramEnabled;
    private bool m_mbc1Mode;
    private bool m_mbc1ModeDisable;
    private bool m_multiplexMode;
    private int m_romBankLow;
    private int m_romBankMid;
    private int m_romBankHigh;
    private int m_romBankMask;
    private int m_ramBankLow;
    private int m_ramBankHigh;
    private int m_ramBankMask;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes an MMM01 cartridge from a ROM image and a RAM size.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public Mmm01(byte[] rom, int ramByteCount = 0) {
        ArgumentNullException.ThrowIfNull(rom);

        m_ram = ((ramByteCount > 0)
            ? new byte[ramByteCount]
            : []);
        m_ramTotalMask = BankMask(byteCount: ramByteCount, bankSize: RamBankSize);
        m_rom = rom;
        m_romTotalMask = BankMask(byteCount: rom.Length, bankSize: RomBankSize);
    }

    /// <inheritdoc />
    public byte ReadRom(ushort address) {
        var (lowBank, highBank, _) = Resolve();
        var bank = ((address < RomBankSize) ? lowBank : highBank);
        var index = (((bank & m_romTotalMask) * RomBankSize) + (address & (RomBankSize - 1)));

        return (((uint)index < (uint)m_rom.Length)
            ? m_rom[index]
            : (byte)0xFF);
    }
    /// <inheritdoc />
    public void WriteRom(ushort address, byte value) {
        switch (address >> 13) {
            case 0:
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                if (!m_locked) {
                    m_ramBankMask = (value >> 4);
                    m_locked = ((value & 0x40) != 0);
                }

                break;
            case 1:
                if (!m_locked) {
                    m_romBankMid = (value >> 5);
                }

                m_romBankLow = ((m_romBankLow & (m_romBankMask << 1)) | (~(m_romBankMask << 1) & value));

                break;
            case 2:
                m_ramBankLow = (value | ~m_ramBankMask);

                if (!m_locked) {
                    m_ramBankHigh = (value >> 2);
                    m_romBankHigh = (value >> 4);
                    m_mbc1ModeDisable = ((value & 0x40) != 0);
                }

                break;
            default:
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

    // Resolves the (low-region bank, high-region bank, RAM bank). Unlocked, the menu in the last two banks is mapped.
    private (int LowBank, int HighBank, int RamBank) Resolve() {
        if (!m_locked) {
            return (-2, -1, 0);
        }

        int lowBank;
        int highBank;
        int ramBank;

        if (m_multiplexMode) {
            lowBank = ((m_romBankLow & (m_romBankMask << 1)) | ((m_mbc1Mode ? 0 : m_ramBankLow) << 5) | (m_romBankHigh << 7));
            highBank = (m_romBankLow | (m_ramBankLow << 5) | (m_romBankHigh << 7));
            ramBank = (m_romBankMid | (m_ramBankHigh << 2));
        }
        else {
            lowBank = ((m_romBankLow & (m_romBankMask << 1)) | (m_romBankMid << 5) | (m_romBankHigh << 7));
            highBank = (m_romBankLow | (m_romBankMid << 5) | (m_romBankHigh << 7));
            ramBank = (m_ramBankLow | (m_ramBankHigh << 2));
        }

        if (highBank == lowBank) {
            highBank += 1;
        }

        return (lowBank, highBank, ramBank);
    }

    private int RamOffset(ushort address) {
        var (_, _, ramBank) = Resolve();

        return (((ramBank & m_ramTotalMask) * RamBankSize) + (address - RamBase));
    }

    private static int BankMask(int byteCount, int bankSize) {
        var bankCount = (byteCount / bankSize);

        return ((bankCount > 1) && BitOperations.IsPow2(value: (uint)bankCount)
            ? (bankCount - 1)
            : 0);
    }
}
