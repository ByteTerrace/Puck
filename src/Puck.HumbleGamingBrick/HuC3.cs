using System.Numerics;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The Hudson HuC3 memory-bank controller: ROM/RAM banking, an infrared transceiver, and a real-time clock reached
/// through a small command protocol. A write to <c>0x0000</c>–<c>0x1FFF</c> selects the mode the
/// <c>0xA000</c>–<c>0xBFFF</c> window operates in — RAM (<c>0xA</c>), the RTC command/read registers (<c>0xB</c>/
/// <c>0xC</c>), status (<c>0xD</c>), or IR (<c>0xE</c>). The clock advances off the emulated master cycle, so it is
/// deterministic; the infrared link reports no incoming light.
/// </summary>
public sealed class HuC3 : ICartridge, IClockedComponent {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamBankSize = 0x2000;

    // One real minute per this many emulated master cycles (the HuC3 clock counts in minutes and days).
    private const long CyclesPerMinute = (60L * 4194304L);

    private readonly byte[] m_ram;
    private readonly int m_ramBankMask;
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;

    private int m_mode;
    private int m_ramBank;
    private int m_romBank = 1;

    private int m_minutes;
    private int m_days;
    private int m_accessIndex;
    private int m_accessFlags;
    private int m_readValue;
    private long m_subMinute;

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Lcd;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes a HuC3 cartridge from a ROM image and a RAM size.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public HuC3(byte[] rom, int ramByteCount = 0) {
        ArgumentNullException.ThrowIfNull(rom);

        m_ram = ((ramByteCount > 0)
            ? new byte[ramByteCount]
            : []);
        m_ramBankMask = BankMask(byteCount: ramByteCount, bankSize: RamBankSize);
        m_rom = rom;
        m_romBankMask = BankMask(byteCount: rom.Length, bankSize: RomBankSize);
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        m_subMinute += tCycles;

        while (m_subMinute >= CyclesPerMinute) {
            m_subMinute -= CyclesPerMinute;
            m_minutes += 1;

            if (m_minutes >= 1440) {
                m_minutes = 0;
                m_days = ((m_days + 1) & 0xFFF);
            }
        }
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
                m_mode = (value & 0x0F);

                break;
            case 1:
                m_romBank = ((value == 0) ? 1 : value);

                break;
            case 2:
                m_ramBank = value;

                break;
            default:
                break;
        }
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        switch (m_mode) {
            case 0x0A:
                break;
            case 0x0C:
                return ((m_accessFlags == 0x02) ? (byte)0x01 : (byte)m_readValue);
            case 0x0D:
                return 0x01;
            case 0x0E:
                // Infrared input: no peer means no incoming light.
                return 0x00;
            default:
                return 0x01;
        }

        var offset = (RamOffset(address: address));

        return (((uint)offset < (uint)m_ram.Length)
            ? m_ram[offset]
            : (byte)0xFF);
    }
    /// <inheritdoc />
    public void WriteRam(ushort address, byte value) {
        if (HandleCommand(value: value)) {
            return;
        }

        // Modes 0x00 (disabled) and 0x0A (RAM) fall through to the RAM array.
        if (m_mode != 0x0A) {
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

    // Returns true when the write was consumed by the RTC/IR command protocol (modes other than disabled/RAM).
    private bool HandleCommand(byte value) {
        switch (m_mode) {
            case 0x0B:
                ProcessRtcCommand(value: value);

                return true;
            case 0x0C:
            case 0x0D:
            case 0x0E:
                return true;
            default:
                return false;
        }
    }

    private void ProcessRtcCommand(byte value) {
        switch (value >> 4) {
            case 1:
                // Read a nibble of the clock at the current access index, then post-increment.
                m_readValue = ReadRtcRegister(index: m_accessIndex);
                m_accessIndex += 1;

                break;
            case 2:
            case 3:
                WriteRtcRegister(index: m_accessIndex, nibble: (value & 0x0F));

                if ((value >> 4) == 3) {
                    m_accessIndex += 1;
                }

                break;
            case 4:
                m_accessIndex = ((m_accessIndex & 0xF0) | (value & 0x0F));

                break;
            case 5:
                m_accessIndex = ((m_accessIndex & 0x0F) | ((value & 0x0F) << 4));

                break;
            case 6:
                m_accessFlags = (value & 0x0F);

                break;
            default:
                break;
        }
    }

    private int ReadRtcRegister(int index) {
        if (index < 3) {
            return ((m_minutes >> (index * 4)) & 0x0F);
        }

        if (index < 7) {
            return ((m_days >> ((index - 3) * 4)) & 0x0F);
        }

        return 0;
    }

    private void WriteRtcRegister(int index, int nibble) {
        if (index < 3) {
            m_minutes = ((m_minutes & ~(0x0F << (index * 4))) | (nibble << (index * 4)));
        }
        else if (index < 7) {
            m_days = ((m_days & ~(0x0F << ((index - 3) * 4))) | (nibble << ((index - 3) * 4)));
        }

        // Higher indices hold the write-only alarm registers, which have no observable effect here and are ignored.
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
