using System.Numerics;

namespace Puck.GameBoy;

/// <summary>
/// The MBC5 memory-bank controller: up to 8&#160;MiB of ROM and 128&#160;KiB of battery-backable RAM. ROM-bank
/// selection is a full 9-bit value split across two registers (low eight bits at <c>0x2000</c>–<c>0x2FFF</c>, the
/// ninth bit at <c>0x3000</c>–<c>0x3FFF</c>) with — unlike MBC1 — no zero-to-one remapping, so bank 0 is freely
/// selectable in the high ROM area. The RAM bank is a four-bit value at <c>0x4000</c>–<c>0x5FFF</c>. Rumble-variant
/// cartridge types behave identically here (the motor bit is ignored).
/// </summary>
public sealed class Mbc5 : ICartridge {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamBankSize = 0x2000;

    private readonly byte[] m_ram;
    private readonly int m_ramBankMask;
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;

    private bool m_ramEnabled;
    private int m_ramBank;
    private int m_romBank = 1;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes an MBC5 cartridge from a ROM image and a RAM size.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public Mbc5(byte[] rom, int ramByteCount = 0) {
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
        // The low region is always bank 0; the high region selects the masked 9-bit bank.
        var bank = ((address < RomBankSize)
            ? 0
            : (m_romBank & m_romBankMask));
        var index = ((bank * RomBankSize) + (address & (RomBankSize - 1)));

        return (((uint)index < (uint)m_rom.Length)
            ? m_rom[index]
            : (byte)0xFF);
    }
    /// <inheritdoc />
    public void WriteRom(ushort address, byte value) {
        switch (address >> 12) {
            case 0:
            case 1:
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 2:
                // Low eight bits of the ROM bank.
                m_romBank = ((m_romBank & 0x100) | value);

                break;
            case 3:
                // Ninth ROM-bank bit.
                m_romBank = ((m_romBank & 0xFF) | ((value & 0x01) << 8));

                break;
            case 4:
            case 5:
                m_ramBank = (value & 0x0F);

                break;
            default:
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

    private int RamOffset(ushort address) =>
        (((m_ramBank & m_ramBankMask) * RamBankSize) + (address - RamBase));

    private static int BankMask(int byteCount, int bankSize) {
        var bankCount = (byteCount / bankSize);

        return ((bankCount > 1) && BitOperations.IsPow2(value: (uint)bankCount)
            ? (bankCount - 1)
            : 0);
    }
}
