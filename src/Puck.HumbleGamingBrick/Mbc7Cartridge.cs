using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC7 mapper: an eight-bit ROM bank select (a written zero reading as one) plus a two-axis accelerometer and a
/// 93LC56 serial EEPROM (128 sixteen-bit words) for saves, both reached through the <c>0xA000</c>–<c>0xAFFF</c>
/// register window once BOTH enable gates are open — <c>0x0A</c> at <c>0x0000</c>–<c>0x1FFF</c> and then <c>0x40</c>
/// at <c>0x4000</c>–<c>0x5FFF</c>. The second gate only arms while the first is open and drops with it. The
/// accelerometer follows the erase-then-latch protocol (<c>0x55</c> resets the latches to <c>0x8000</c>, <c>0xAA</c>
/// captures a reading from <see cref="Sensor"/>) — the <see cref="ITiltSensor"/> DI seam, on the same <c>TryAdd</c>
/// precedent as the camera cartridge's <c>ICameraSensor</c>. The default sensor is motionless (every capture reads the
/// centered value <c>0x81D0</c>, bit-identical to the mapper's pre-seam behavior); a host that records tilt input
/// registers its own sensor in place of it. The EEPROM's command state machine (EWEN/EWDS/READ/WRITE/ERASE/ERAL/WRAL)
/// is implemented bit-serially over the CS/CLK/DI/DO pins with the chip's clear-then-OR write semantics and the
/// busy-then-ready pattern on DO after programming commands; EWDS write protection covers the data bits as well as the
/// command itself.
/// </summary>
public sealed class Mbc7Cartridge : CartridgeBase {
    // The latch value after the 0x55 erase step, before 0xAA captures a reading.
    private const int AccelerometerErased = 0x8000;
    private const int EepromByteCount = 0x100;
    private const int EepromWordCount = 0x80;
    private const int RomBankSize = 0x4000;

    private readonly byte[] m_eeprom;
    private int m_argumentBitsLeft;
    private bool m_chipSelect;
    private bool m_clockLine;
    private int m_commandShift;
    private bool m_dataIn;
    private bool m_dataOut;
    private bool m_latchErased;
    private bool m_ramEnablePrimary;
    private bool m_ramEnableSecondary;
    private int m_readShift;
    private int m_romBank;
    private bool m_writeEnabled;
    private int m_xLatch;
    private int m_yLatch;
    private ITiltSensor m_sensor = new TiltSensorComponent();

    /// <summary>Creates an MBC7 cartridge with its registers at reset: ROM bank 1, both gates closed, the
    /// accelerometer latches erased, and the EEPROM idle with DO signalling ready.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public Mbc7Cartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header, ramByteCount: 0) {
        m_dataOut = true;
        m_eeprom = new byte[EepromByteCount];
        m_readShift = 0xFFFF;
        m_romBank = 1;
        m_xLatch = AccelerometerErased;
        m_yLatch = AccelerometerErased;
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (m_ramEnablePrimary && m_ramEnableSecondary);

    /// <summary>Gets or sets the accelerometer's tilt source, read at each <c>0xAA</c> latch step. Defaults to a
    /// motionless <see cref="TiltSensorComponent"/> (bit-identical to the mapper's pre-seam fixed-center behavior); a
    /// host wires its own recorded-input sensor here (the same DI seam <c>CameraCartridge.Sensor</c> uses).</summary>
    public ITiltSensor Sensor {
        get => m_sensor;
        set => m_sensor = (value ?? throw new ArgumentNullException(paramName: nameof(value)));
    }

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: primary enable; dropping it also drops the secondary gate
                m_ramEnablePrimary = ((value & 0x0F) == 0x0A);

                if (!m_ramEnablePrimary) {
                    m_ramEnableSecondary = false;
                }

                break;
            case 1: // 0x2000-0x3FFF: eight-bit ROM bank, zero reads as one
                m_romBank = value;

                if (m_romBank == 0) {
                    m_romBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: secondary enable (exactly 0x40), only armable while the primary gate is open
                if (m_ramEnablePrimary) {
                    m_ramEnableSecondary = (value == 0x40);
                }

                break;
            default: // 0x6000-0x7FFF: no register
                break;
        }
    }
    /// <summary>Reads a register from the <c>0xA000</c>–<c>0xAFFF</c> window (address bits 7–4 select it): the latched
    /// accelerometer bytes, the EEPROM pins, or open bus while either gate is closed or above the window.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The selected register byte, or <c>0xFF</c>.</returns>
    public override byte ReadRam(ushort address) {
        if (!RamAccessible || (address >= 0xB000)) {
            return 0xFF;
        }

        return ((address >> 4) & 0x0F) switch {
            0x02 => (byte)m_xLatch,
            0x03 => (byte)(m_xLatch >> 8),
            0x04 => (byte)m_yLatch,
            0x05 => (byte)(m_yLatch >> 8),
            0x06 => 0x00, // the unpopulated Z axis reads low
            0x08 => ReadEepromPins(),
            _ => 0xFF,
        };
    }
    /// <summary>Writes a register in the <c>0xA000</c>–<c>0xAFFF</c> window: the accelerometer erase (<c>0x55</c>) and
    /// latch (<c>0xAA</c>) steps or the EEPROM pins; dropped while either gate is closed or above the window.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value written.</param>
    public override void WriteRam(ushort address, byte value) {
        if (!RamAccessible || (address >= 0xB000)) {
            return;
        }

        switch ((address >> 4) & 0x0F) {
            case 0x00: // erase: reset the latches and arm the capture step
                if (value == 0x55) {
                    m_latchErased = true;
                    m_xLatch = AccelerometerErased;
                    m_yLatch = AccelerometerErased;
                }

                break;
            case 0x01: // latch: capture a reading from the tilt sensor, only after an erase
                if ((value == 0xAA) && m_latchErased) {
                    m_latchErased = false;
                    m_sensor.Read(x: out m_xLatch, y: out m_yLatch);
                }

                break;
            case 0x08:
                WriteEepromPins(value: value);

                break;
            default:
                break;
        }
    }
    /// <inheritdoc/>
    /// <remarks>Overridden: the whole window is the accelerometer latch and EEPROM bit-serial protocol, not RAM — it
    /// stays on the interface path.</remarks>
    public override bool TryComputeRamWindow(out int offset, out int length) {
        offset = 0;
        length = 0;

        return false;
    }

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) =>
        ((address <= MemoryMap.RomBank0End)
            ? address
            : ((m_romBank * RomBankSize) + (address - MemoryMap.RomBankNStart)));
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        (address - MemoryMap.ExternalRamStart);
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteBytes(value: m_eeprom);
        writer.WriteInt32(value: m_argumentBitsLeft);
        writer.WriteBoolean(value: m_chipSelect);
        writer.WriteBoolean(value: m_clockLine);
        writer.WriteInt32(value: m_commandShift);
        writer.WriteBoolean(value: m_dataIn);
        writer.WriteBoolean(value: m_dataOut);
        writer.WriteBoolean(value: m_latchErased);
        writer.WriteBoolean(value: m_ramEnablePrimary);
        writer.WriteBoolean(value: m_ramEnableSecondary);
        writer.WriteInt32(value: m_readShift);
        writer.WriteInt32(value: m_romBank);
        writer.WriteBoolean(value: m_writeEnabled);
        writer.WriteInt32(value: m_xLatch);
        writer.WriteInt32(value: m_yLatch);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        reader.ReadBytes(destination: m_eeprom);
        m_argumentBitsLeft = reader.ReadInt32();
        m_chipSelect = reader.ReadBoolean();
        m_clockLine = reader.ReadBoolean();
        m_commandShift = reader.ReadInt32();
        m_dataIn = reader.ReadBoolean();
        m_dataOut = reader.ReadBoolean();
        m_latchErased = reader.ReadBoolean();
        m_ramEnablePrimary = reader.ReadBoolean();
        m_ramEnableSecondary = reader.ReadBoolean();
        m_readShift = reader.ReadInt32();
        m_romBank = reader.ReadInt32();
        m_writeEnabled = reader.ReadBoolean();
        m_xLatch = reader.ReadInt32();
        m_yLatch = reader.ReadInt32();
    }

    private byte ReadEepromPins() =>
        (byte)((m_dataOut ? 0x01 : 0x00) | (m_dataIn ? 0x02 : 0x00) | (m_clockLine ? 0x40 : 0x00) | (m_chipSelect ? 0x80 : 0x00));
    private void WriteEepromPins(byte value) {
        m_chipSelect = ((value & 0x80) != 0);
        m_dataIn = ((value & 0x02) != 0);

        var clockHigh = ((value & 0x40) != 0);

        if (m_chipSelect && clockHigh && !m_clockLine) {
            // A rising clock edge while selected: DO presents the next read/ready bit, then the incoming bit shifts
            // into whichever phase is active — the 11-bit command or a programming command's 16 data bits.
            m_dataOut = ((m_readShift & 0x8000) != 0);
            m_readShift = ((m_readShift << 1) | 0x01) & 0xFFFF;

            if (m_argumentBitsLeft == 0) {
                ShiftCommandBit();
            } else {
                ShiftDataBit();
            }
        }

        m_clockLine = clockHigh;
    }
    private void ShiftCommandBit() {
        m_commandShift = (m_commandShift << 1) | (m_dataIn ? 1 : 0);

        if ((m_commandShift & 0x400) == 0) {
            // The start bit has not reached the top yet: still collecting the start + opcode + address bits.
            return;
        }

        // Bits 9-8 are the opcode; the extended opcode-00 commands split on address bits 7-6.
        switch ((m_commandShift >> 6) & 0x0F) {
            case 0x0: // EWDS: disable programming
                m_writeEnabled = false;
                m_commandShift = 0;

                break;
            case 0x1: // WRAL: clear every word, then collect 16 data bits to OR into all of them
                if (m_writeEnabled) {
                    Array.Clear(array: m_eeprom);
                }

                m_argumentBitsLeft = 16;

                break;
            case 0x2: // ERAL: erase every word
                if (m_writeEnabled) {
                    Array.Fill(array: m_eeprom, value: (byte)0xFF);
                    m_readShift = 0x00FF;
                }

                m_commandShift = 0;

                break;
            case 0x3: // EWEN: enable programming
                m_writeEnabled = true;
                m_commandShift = 0;

                break;
            case <= 0x7: // WRITE: clear the addressed word, then collect 16 data bits to OR into it
                if (m_writeEnabled) {
                    WriteEepromWord(word: m_commandShift & 0x7F, value: 0x0000);
                }

                m_argumentBitsLeft = 16;

                break;
            case <= 0xB: // READ: load the addressed word so the following clocks shift it out, high bit first
                m_readShift = ReadEepromWord(word: m_commandShift & 0x7F);
                m_commandShift = 0;

                break;
            default: // ERASE: drive the addressed word to all ones
                if (m_writeEnabled) {
                    WriteEepromWord(word: m_commandShift & 0x7F, value: 0xFFFF);
                    m_readShift = 0x3FFF;
                }

                m_commandShift = 0;

                break;
        }
    }
    private void ShiftDataBit() {
        --m_argumentBitsLeft;
        m_dataOut = true;

        // Clear-then-OR: the command already cleared the target, so each 1 data bit ORs in at its position. EWDS
        // protection covers the data phase too — a write-disabled command consumes its bits without programming.
        if (m_dataIn && m_writeEnabled) {
            var bit = (1 << m_argumentBitsLeft);

            if ((m_commandShift & 0x100) != 0) {
                var word = m_commandShift & 0x7F;

                WriteEepromWord(word: word, value: ReadEepromWord(word: word) | bit);
            } else {
                for (var word = 0; (word < EepromWordCount); ++word) {
                    WriteEepromWord(word: word, value: ReadEepromWord(word: word) | bit);
                }
            }
        }

        if (m_argumentBitsLeft == 0) {
            // Programming has finished: DO reads busy (low) for a spell and then ready (high), which games poll.
            m_readShift = (((m_commandShift & 0x100) != 0) ? 0x00FF : 0x3FFF);
            m_commandShift = 0;
        }
    }
    private int ReadEepromWord(int word) =>
        m_eeprom[(word * 2)] | (m_eeprom[((word * 2) + 1)] << 8);
    private void WriteEepromWord(int word, int value) {
        m_eeprom[(word * 2)] = (byte)value;
        m_eeprom[((word * 2) + 1)] = (byte)(value >> 8);
    }
}
