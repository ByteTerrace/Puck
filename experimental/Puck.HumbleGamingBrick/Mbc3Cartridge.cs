using System.Buffers.Binary;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC3 mapper: up to 2&#160;MiB of ROM through a seven-bit bank select, up to 32&#160;KiB of banked RAM, and an
/// optional real-time clock. Its control regions are <c>0x0000</c>–<c>0x1FFF</c> (RAM and RTC enable),
/// <c>0x2000</c>–<c>0x3FFF</c> (seven-bit ROM bank, a written zero reading as one), <c>0x4000</c>–<c>0x5FFF</c> (which
/// selects either a RAM bank <c>0x00</c>–<c>0x03</c> or an RTC register <c>0x08</c>–<c>0x0C</c>), and
/// <c>0x6000</c>–<c>0x7FFF</c> (a <c>0</c>-then-<c>1</c> write latches the live clock into the readable copy). When an
/// RTC register is selected, the <c>0xA000</c>–<c>0xBFFF</c> window reads and writes that register instead of RAM.
/// <para>
/// The clock is <b>deterministic</b>: it never reads host wall-clock. It is an LCD-domain <see cref="IClockedComponent"/>
/// whose <see cref="Tick"/> counts one PPU dot; <see cref="DotsPerSecond"/> emulated dots advance the clock by one
/// second. Because the LCD clock runs at a fixed rate regardless of the Color speed switch — like the real RTC's own
/// crystal — the same emulated run always yields the same clock reading. Every counter, the halt and day-carry flags,
/// the latched copies, and the latch edge-detector are plain snapshotted fields.
/// </para>
/// </summary>
public sealed class Mbc3Cartridge : CartridgeBase, IClockedComponent {
    // One RTC second is 2^22 CPU T-cycles at normal speed, which is exactly 2^22 PPU dots; the LCD clock does not change
    // rate with the speed switch, matching the real RTC crystal's independence from CPU speed.
    private const int DotsPerSecond = 4194304;
    // The de-facto standard MBC3 save-file RTC footer (the widely adopted emulator save-format convention): five live registers, five latched copies
    // (each a 4-byte little-endian word in the RTC register encoding), then an 8-byte little-endian UNIX timestamp.
    private const int PersistentClockFooterByteCount = 48;
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    // Live clock — advanced by Tick from emulated dots only.
    private int m_dayCarry;
    private int m_dayCounter;
    private int m_dotAccumulator;
    private bool m_halted;
    private int m_hours;
    private byte m_latchTrigger;
    // Latched clock — the copy the RAM window reads, refreshed on the 0->1 latch write.
    private int m_latchedDayCarry;
    private int m_latchedDayCounter;
    private int m_latchedHours;
    private int m_latchedMinutes;
    private int m_latchedSeconds;
    private int m_minutes;
    private int m_ramBankOrRtcRegister;
    private bool m_ramEnabled;
    private int m_romBank;
    private int m_seconds;

    /// <summary>Creates an MBC3 cartridge with its registers at reset (ROM bank 1, RAM/RTC disabled) and its clock at
    /// zero.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public Mbc3Cartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) {
        m_latchTrigger = 0xFF;
        m_romBank = 1;
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Lcd;

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (Header.HasRam && m_ramEnabled);

    /// <inheritdoc/>
    /// <remarks>This MBC3 always emulates the clock (the header's TIMER distinction is not modeled), so every MBC3
    /// battery save carries the footer — a timer-less variant just persists an untouched clock.</remarks>
    public override int PersistentClockByteCount =>
        PersistentClockFooterByteCount;

    /// <inheritdoc/>
    public override byte[] ExportPersistentClock(long unixTimestampSeconds) {
        var footer = new byte[PersistentClockFooterByteCount];
        var span = footer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[0..], value: (uint)m_seconds);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[4..], value: (uint)m_minutes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[8..], value: (uint)m_hours);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[12..], value: (uint)(m_dayCounter & 0xFF));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[16..], value: PackDayHigh(dayCounter: m_dayCounter, halted: m_halted, dayCarry: m_dayCarry));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[20..], value: (uint)m_latchedSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[24..], value: (uint)m_latchedMinutes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[28..], value: (uint)m_latchedHours);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[32..], value: (uint)(m_latchedDayCounter & 0xFF));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[36..], value: PackDayHigh(dayCounter: m_latchedDayCounter, halted: m_halted, dayCarry: m_latchedDayCarry));
        BinaryPrimitives.WriteInt64LittleEndian(destination: span[40..], value: unixTimestampSeconds);

        return footer;
    }
    /// <inheritdoc/>
    public override void ImportPersistentClock(ReadOnlySpan<byte> source) {
        if (source.Length < PersistentClockFooterByteCount) {
            return;
        }

        // The register encodings sanitize a foreign file exactly as the RTC register writes would; the trailing
        // timestamp is deliberately ignored (the deterministic clock resumes, never advancing from wall time), and
        // the sub-second prescaler restarts — the one sub-second of drift a real battery swap also loses.
        m_seconds = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[0..]) & 0x3F);
        m_minutes = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[4..]) & 0x3F);
        m_hours = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[8..]) & 0x1F);

        var dayLow = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[12..]) & 0xFF);
        var dayHigh = BinaryPrimitives.ReadUInt32LittleEndian(source: source[16..]);

        m_dayCounter = (dayLow | (int)((dayHigh & 0x01) << 8));
        m_halted = ((dayHigh & 0x40) != 0);
        m_dayCarry = (int)((dayHigh >> 7) & 0x01);
        m_latchedSeconds = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[20..]) & 0x3F);
        m_latchedMinutes = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[24..]) & 0x3F);
        m_latchedHours = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[28..]) & 0x1F);

        var latchedDayLow = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[32..]) & 0xFF);
        var latchedDayHigh = BinaryPrimitives.ReadUInt32LittleEndian(source: source[36..]);

        m_latchedDayCounter = (latchedDayLow | (int)((latchedDayHigh & 0x01) << 8));
        m_latchedDayCarry = (int)((latchedDayHigh >> 7) & 0x01);
        m_dotAccumulator = 0;
    }

    private static uint PackDayHigh(int dayCounter, bool halted, int dayCarry) =>
        (uint)(((dayCounter >> 8) & 0x01) | (halted ? 0x40 : 0x00) | ((dayCarry & 0x01) << 7));

    private bool RtcRegisterSelected =>
        (m_ramBankOrRtcRegister >= 0x08);

    /// <inheritdoc/>
    public void Tick() {
        // Determinism: the sole time source is the emulated LCD clock. A halted clock (day-high bit 6 set) freezes.
        if (m_halted) {
            return;
        }

        if (++m_dotAccumulator < DotsPerSecond) {
            return;
        }

        m_dotAccumulator = 0;

        AdvanceOneSecond();
    }
    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: RAM + RTC enable
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 1: // 0x2000-0x3FFF: seven-bit ROM bank, zero reads as one
                m_romBank = (value & 0x7F);

                if (m_romBank == 0) {
                    m_romBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: RAM bank (0x00-0x03) or RTC register (0x08-0x0C)
                m_ramBankOrRtcRegister = (value & 0x0F);

                break;
            default: // 0x6000-0x7FFF: latch clock on a 0-then-1 write
                if ((m_latchTrigger == 0x00) && (value == 0x01)) {
                    LatchClock();
                }

                m_latchTrigger = value;

                break;
        }
    }
    /// <summary>Reads from the external window: an RTC register when one is selected, otherwise banked RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The selected RTC register's latched value, the RAM byte, or open-bus <c>0xFF</c> while disabled.</returns>
    public override byte ReadRam(ushort address) {
        if (!m_ramEnabled) {
            return 0xFF;
        }

        return RtcRegisterSelected ? ReadRtcRegister() : base.ReadRam(address: address);
    }
    /// <summary>Writes to the external window: an RTC register when one is selected, otherwise banked RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value to store.</param>
    public override void WriteRam(ushort address, byte value) {
        if (!m_ramEnabled) {
            return;
        }

        if (RtcRegisterSelected) {
            WriteRtcRegister(value: value);

            return;
        }

        base.WriteRam(address: address, value: value);
    }

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) =>
        (address <= MemoryMap.RomBank0End)
            ? address
            : ((m_romBank * RomBankSize) + (address - MemoryMap.RomBankNStart));
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        (((m_ramBankOrRtcRegister & 0x03) * RamBankSize) + (address - MemoryMap.ExternalRamStart));
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteBoolean(value: m_ramEnabled);
        writer.WriteInt32(value: m_romBank);
        writer.WriteInt32(value: m_ramBankOrRtcRegister);
        writer.WriteByte(value: m_latchTrigger);
        writer.WriteBoolean(value: m_halted);
        writer.WriteInt32(value: m_dotAccumulator);
        writer.WriteInt32(value: m_seconds);
        writer.WriteInt32(value: m_minutes);
        writer.WriteInt32(value: m_hours);
        writer.WriteInt32(value: m_dayCounter);
        writer.WriteInt32(value: m_dayCarry);
        writer.WriteInt32(value: m_latchedSeconds);
        writer.WriteInt32(value: m_latchedMinutes);
        writer.WriteInt32(value: m_latchedHours);
        writer.WriteInt32(value: m_latchedDayCounter);
        writer.WriteInt32(value: m_latchedDayCarry);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        m_ramEnabled = reader.ReadBoolean();
        m_romBank = reader.ReadInt32();
        m_ramBankOrRtcRegister = reader.ReadInt32();
        m_latchTrigger = reader.ReadByte();
        m_halted = reader.ReadBoolean();
        m_dotAccumulator = reader.ReadInt32();
        m_seconds = reader.ReadInt32();
        m_minutes = reader.ReadInt32();
        m_hours = reader.ReadInt32();
        m_dayCounter = reader.ReadInt32();
        m_dayCarry = reader.ReadInt32();
        m_latchedSeconds = reader.ReadInt32();
        m_latchedMinutes = reader.ReadInt32();
        m_latchedHours = reader.ReadInt32();
        m_latchedDayCounter = reader.ReadInt32();
        m_latchedDayCarry = reader.ReadInt32();
    }

    private void AdvanceOneSecond() {
        if (++m_seconds < 60) {
            return;
        }

        m_seconds = 0;

        if (++m_minutes < 60) {
            return;
        }

        m_minutes = 0;

        if (++m_hours < 24) {
            return;
        }

        m_hours = 0;

        if (++m_dayCounter <= 0x1FF) {
            return;
        }

        // The day counter is nine bits; overflow past 511 wraps to zero and latches the sticky day-carry flag.
        m_dayCounter = 0;
        m_dayCarry = 1;
    }
    private void LatchClock() {
        m_latchedSeconds = m_seconds;
        m_latchedMinutes = m_minutes;
        m_latchedHours = m_hours;
        m_latchedDayCounter = m_dayCounter;
        m_latchedDayCarry = m_dayCarry;
    }
    private byte ReadRtcRegister() =>
        m_ramBankOrRtcRegister switch {
            0x08 => (byte)m_latchedSeconds,
            0x09 => (byte)m_latchedMinutes,
            0x0A => (byte)m_latchedHours,
            0x0B => (byte)m_latchedDayCounter,
            0x0C => (byte)(((m_latchedDayCounter >> 8) & 0x01) | (m_halted ? 0x40 : 0x00) | (m_latchedDayCarry << 7)),
            _ => 0xFF,
        };
    private void WriteRtcRegister(byte value) {
        // A write updates the live counter directly; the day-high register also carries the halt and day-carry flags.
        switch (m_ramBankOrRtcRegister) {
            case 0x08:
                m_seconds = (value & 0x3F);
                // Writing the seconds register resets the sub-second accumulator, as the hardware's prescaler does.
                m_dotAccumulator = 0;

                break;
            case 0x09:
                m_minutes = (value & 0x3F);

                break;
            case 0x0A:
                m_hours = (value & 0x1F);

                break;
            case 0x0B:
                m_dayCounter = ((m_dayCounter & 0x100) | value);

                break;
            case 0x0C:
                m_dayCounter = ((m_dayCounter & 0x0FF) | ((value & 0x01) << 8));
                m_halted = ((value & 0x40) != 0);
                m_dayCarry = ((value >> 7) & 0x01);

                break;
            default:
                break;
        }
    }
}
