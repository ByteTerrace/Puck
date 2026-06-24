using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC7 memory-bank controller: ROM banking plus a two-axis accelerometer and a 256-byte serial EEPROM (a
/// 93LC56) for saves. The accelerometer and EEPROM are reached through the <c>0xA000</c>–<c>0xBFFF</c> window once
/// both the primary RAM enable (<c>0x0A</c> at <c>0x0000</c>–<c>0x1FFF</c>) and the secondary enable (<c>0x40</c> at
/// <c>0x4000</c>–<c>0x5FFF</c>) are set. Tilt input has no source here, so the accelerometer reads centered; the
/// EEPROM command protocol is fully emulated so battery saves work.
/// </summary>
public sealed class Mbc7 : ICartridge {
    private const int RomBankSize = 0x4000;
    private const int EepromByteCount = 256;

    private readonly byte[] m_eeprom = new byte[EepromByteCount];
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;

    private bool m_ramEnabled;
    private bool m_secondaryRamEnabled;
    private int m_romBank = 1;

    // Centered accelerometer readings (no tilt source); a host may drive these later.
    private int m_accelerometerX;
    private int m_accelerometerY;
    private bool m_latchReady;
    private int m_xLatch = 0x8000;
    private int m_yLatch = 0x8000;

    // The 93LC56 EEPROM serial state machine.
    private bool m_eepromCs;
    private bool m_eepromClk;
    private bool m_eepromDi;
    private bool m_eepromDo;
    private bool m_eepromWriteEnabled;
    private int m_eepromCommand;
    private int m_eepromReadBits;
    private int m_argumentBitsLeft;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_eeprom;

    /// <summary>Initializes an MBC7 cartridge from a ROM image.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public Mbc7(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        m_rom = rom;
        m_romBankMask = BankMask(byteCount: rom.Length, bankSize: RomBankSize);
        m_eepromReadBits = 0xFFFF;
        m_eepromDo = true;
    }

    /// <summary>Sets the accelerometer reading the next latch will sample, each axis a small signed tilt offset.</summary>
    /// <param name="x">The X-axis tilt.</param>
    /// <param name="y">The Y-axis tilt.</param>
    public void SetAccelerometer(int x, int y) {
        m_accelerometerX = x;
        m_accelerometerY = y;
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
                m_romBank = value;

                break;
            case 2:
                m_secondaryRamEnabled = (value == 0x40);

                break;
            default:
                break;
        }
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        if (!m_ramEnabled || !m_secondaryRamEnabled || (address >= 0xB000)) {
            return 0xFF;
        }

        return ((address >> 4) & 0x0F) switch {
            2 => (byte)m_xLatch,
            3 => (byte)(m_xLatch >> 8),
            4 => (byte)m_yLatch,
            5 => (byte)(m_yLatch >> 8),
            6 => 0,
            8 => (byte)((m_eepromDo ? 0x01 : 0x00) | (m_eepromDi ? 0x02 : 0x00) | (m_eepromClk ? 0x40 : 0x00) | (m_eepromCs ? 0x80 : 0x00)),
            _ => 0xFF,
        };
    }
    /// <inheritdoc />
    public void WriteRam(ushort address, byte value) {
        if (!m_ramEnabled || !m_secondaryRamEnabled || (address >= 0xB000)) {
            return;
        }

        switch ((address >> 4) & 0x0F) {
            case 0:
                if (value == 0x55) {
                    m_latchReady = true;
                    m_xLatch = 0x8000;
                    m_yLatch = 0x8000;
                }

                break;
            case 1:
                if ((value == 0xAA) && m_latchReady) {
                    m_latchReady = false;
                    m_xLatch = (0x81D0 + (0x70 * m_accelerometerX));
                    m_yLatch = (0x81D0 + (0x70 * m_accelerometerY));
                }

                break;
            case 8:
                WriteEeprom(value: value);

                break;
            default:
                break;
        }
    }
    /// <inheritdoc />
    public void LoadSaveRam(ReadOnlySpan<byte> data) {
        if (data.Length == m_eeprom.Length) {
            data.CopyTo(destination: m_eeprom);
        }
    }

    private void WriteEeprom(byte value) {
        m_eepromCs = ((value & 0x80) != 0);
        m_eepromDi = ((value & 0x02) != 0);

        // A rising clock edge while selected shifts one bit through the command/data state machine.
        if (m_eepromCs && !m_eepromClk && ((value & 0x40) != 0)) {
            m_eepromDo = ((m_eepromReadBits & 0x8000) != 0);
            m_eepromReadBits = (((m_eepromReadBits << 1) | 0x01) & 0xFFFF);

            if (m_argumentBitsLeft == 0) {
                ShiftCommandBit();
            }
            else {
                ShiftArgumentBit();
            }
        }

        m_eepromClk = ((value & 0x40) != 0);
    }

    private void ShiftCommandBit() {
        m_eepromCommand = ((m_eepromCommand << 1) | (m_eepromDi ? 0x01 : 0x00));

        if ((m_eepromCommand & 0x400) == 0) {
            return;
        }

        var word = (m_eepromCommand & 0x7F);

        switch ((m_eepromCommand >> 6) & 0x0F) {
            case 0x8 or 0x9 or 0xA or 0xB:
                // READ: load the 16-bit word so the next clocks shift it out high bit first.
                m_eepromReadBits = ReadWord(word: word);
                m_eepromCommand = 0;

                break;
            case 0x3:
                m_eepromWriteEnabled = true;
                m_eepromCommand = 0;

                break;
            case 0x0:
                m_eepromWriteEnabled = false;
                m_eepromCommand = 0;

                break;
            case 0x4 or 0x5 or 0x6 or 0x7:
                // WRITE: clear the word, then collect 16 data bits.
                if (m_eepromWriteEnabled) {
                    WriteWord(word: word, value: 0);
                }

                m_argumentBitsLeft = 16;

                break;
            case 0xC or 0xD or 0xE or 0xF:
                // ERASE.
                if (m_eepromWriteEnabled) {
                    WriteWord(word: word, value: 0xFFFF);
                    m_eepromReadBits = 0x3FFF;
                }

                m_eepromCommand = 0;

                break;
            case 0x2:
                // ERAL (erase all).
                if (m_eepromWriteEnabled) {
                    Array.Fill(array: m_eeprom, value: (byte)0xFF);
                    m_eepromReadBits = 0xFF;
                }

                m_eepromCommand = 0;

                break;
            default:
                // WRAL (write all): clear all words, then collect 16 data bits.
                if (m_eepromWriteEnabled) {
                    Array.Clear(array: m_eeprom);
                }

                m_argumentBitsLeft = 16;

                break;
        }
    }

    private void ShiftArgumentBit() {
        m_argumentBitsLeft -= 1;
        m_eepromDo = true;

        if (m_eepromDi) {
            var bit = (1 << m_argumentBitsLeft);

            if ((m_eepromCommand & 0x100) != 0) {
                WriteWord(word: (m_eepromCommand & 0x7F), value: (ReadWord(word: (m_eepromCommand & 0x7F)) | bit));
            }
            else {
                for (var i = 0; i < 0x7F; i += 1) {
                    WriteWord(word: i, value: (ReadWord(word: i) | bit));
                }
            }
        }

        if (m_argumentBitsLeft == 0) {
            m_eepromReadBits = (((m_eepromCommand & 0x100) != 0) ? 0xFF : 0x3FFF);
            m_eepromCommand = 0;
        }
    }

    private int ReadWord(int word) =>
        (m_eeprom[word * 2] | (m_eeprom[(word * 2) + 1] << 8));

    private void WriteWord(int word, int value) {
        m_eeprom[word * 2] = (byte)value;
        m_eeprom[(word * 2) + 1] = (byte)(value >> 8);
    }

    private static int BankMask(int byteCount, int bankSize) {
        var bankCount = (byteCount / bankSize);

        return ((bankCount > 1) && System.Numerics.BitOperations.IsPow2(value: (uint)bankCount)
            ? (bankCount - 1)
            : 0);
    }
}
