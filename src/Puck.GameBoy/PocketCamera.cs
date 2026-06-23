using System.Numerics;

namespace Puck.GameBoy;

/// <summary>
/// The pocket-camera mapper (MAC-GBD): MBC3-like ROM/RAM banking plus the camera sensor's register block, mapped
/// into <c>0xA000</c>–<c>0xBFFF</c> when bit&#160;4 of the RAM-bank register is set. There is no image sensor in this
/// emulator, so a capture completes instantly and the sensor registers report "ready"; the captured image area in
/// RAM is left as the game last wrote it. Banking and saves are fully functional.
/// </summary>
public sealed class PocketCamera : ICartridge {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamBankSize = 0x2000;

    private readonly byte[] m_ram;
    private readonly int m_ramBankMask;
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;
    private readonly byte[] m_cameraRegisters = new byte[0x36];

    private bool m_ramEnabled;
    private bool m_cameraSelected;
    private int m_ramBank;
    private int m_romBank = 1;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes a pocket-camera cartridge from a ROM image and a RAM size.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public PocketCamera(byte[] rom, int ramByteCount = 0) {
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
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 1:
                m_romBank = ((value & 0x3F) == 0) ? 1 : (value & 0x3F);

                break;
            case 2:
                m_cameraSelected = ((value & 0x10) != 0);
                m_ramBank = (value & 0x0F);

                break;
            default:
                break;
        }
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        if (m_cameraSelected) {
            // Register 0 bit 0 is the capture-busy flag; with no sensor a capture is always already complete.
            return ((((address - RamBase) & 0x7F) == 0) ? (byte)0x00 : (byte)0x00);
        }

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
        if (m_cameraSelected) {
            var register = ((address - RamBase) & 0x7F);

            if (register < m_cameraRegisters.Length) {
                // Writing bit 0 of register 0 triggers a capture, which completes immediately (cleared right back).
                m_cameraRegisters[register] = ((register == 0) ? (byte)(value & 0x06) : value);
            }

            return;
        }

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
