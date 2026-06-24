using System.Numerics;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The Hudson HuC1 memory-bank controller: MBC1-style ROM/RAM banking plus an infrared transceiver. Writing
/// <c>0x0E</c> to <c>0x0000</c>–<c>0x1FFF</c> switches the <c>0xA000</c>–<c>0xBFFF</c> window from cartridge RAM to
/// the IR register; any other value selects RAM. The infrared link itself has no peer in this emulator, so reads of
/// the IR register report "no light received".
/// </summary>
public sealed class HuC1 : ICartridge {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamBankSize = 0x2000;

    // The HuC1 IR register reads back 0xC0 with bit 0 carrying the received-light state; no peer means no light.
    private const byte IrNoSignal = 0xC0;

    private readonly byte[] m_ram;
    private readonly int m_ramBankMask;
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;

    private bool m_infraredMode;
    private int m_ramBank;
    private int m_romBank = 1;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes a HuC1 cartridge from a ROM image and a RAM size.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public HuC1(byte[] rom, int ramByteCount = 0) {
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
        switch (address >> 13) {
            case 0:
                m_infraredMode = (value == 0x0E);

                break;
            case 1:
                m_romBank = ((value & 0x3F) == 0) ? 1 : (value & 0x3F);

                break;
            case 2:
                m_ramBank = (value & 0x03);

                break;
            default:
                break;
        }
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        if (m_infraredMode) {
            return IrNoSignal;
        }

        var offset = (RamOffset(address: address));

        return (((uint)offset < (uint)m_ram.Length)
            ? m_ram[offset]
            : (byte)0xFF);
    }
    /// <inheritdoc />
    public void WriteRam(ushort address, byte value) {
        if (m_infraredMode) {
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
