using System.Numerics;

namespace Puck.GameBoy;

/// <summary>
/// The MBC2 memory-bank controller: up to 256&#160;KiB of ROM and a small built-in 512&#215;4-bit RAM (no external
/// RAM chip). It has a single quirk in its register decode — for writes below <c>0x4000</c>, address bit&#160;8
/// chooses the function: when clear the write toggles RAM enable, when set it selects the four-bit ROM bank (with the
/// zero-to-one remap). Its RAM stores only the low nibble of each byte; the high nibble reads back as ones.
/// </summary>
public sealed class Mbc2 : ICartridge {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamByteCount = 512;

    private readonly byte[] m_ram = new byte[RamByteCount];
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;

    private bool m_ramEnabled;
    private int m_romBank = 1;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes an MBC2 cartridge from a ROM image.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public Mbc2(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        m_rom = rom;
        m_romBankMask = BankMask(byteCount: rom.Length, bankSize: RomBankSize);
    }

    /// <inheritdoc />
    public byte ReadRom(ushort address) {
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
        // Only the low half of the address space holds registers; bit 8 selects which.
        if (address >= 0x4000) {
            return;
        }

        if ((address & 0x0100) == 0) {
            m_ramEnabled = ((value & 0x0F) == 0x0A);
        }
        else {
            m_romBank = ((value & 0x0F) == 0) ? 1 : (value & 0x0F);
        }
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        if (!m_ramEnabled) {
            return 0xFF;
        }

        // The 512 nibbles echo through the whole 0xA000-0xBFFF window; the high nibble reads back as ones.
        return (byte)(m_ram[(address - RamBase) & (RamByteCount - 1)] | 0xF0);
    }
    /// <inheritdoc />
    public void WriteRam(ushort address, byte value) {
        if (!m_ramEnabled) {
            return;
        }

        m_ram[(address - RamBase) & (RamByteCount - 1)] = (byte)(value & 0x0F);
    }
    /// <inheritdoc />
    public void LoadSaveRam(ReadOnlySpan<byte> data) {
        if (data.Length == m_ram.Length) {
            data.CopyTo(destination: m_ram);
        }
    }

    private static int BankMask(int byteCount, int bankSize) {
        var bankCount = (byteCount / bankSize);

        return ((bankCount > 1) && BitOperations.IsPow2(value: (uint)bankCount)
            ? (bankCount - 1)
            : 0);
    }
}
