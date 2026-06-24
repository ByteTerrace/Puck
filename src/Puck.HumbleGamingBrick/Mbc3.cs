using System.Numerics;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC3 memory-bank controller: up to 2&#160;MiB of ROM, 32&#160;KiB of battery-backable RAM, and — on the
/// RTC-bearing cartridge types — a real-time clock. The ROM bank is a full seven bits at <c>0x2000</c>–<c>0x3FFF</c>
/// (with the MBC1-style zero-to-one remap), and <c>0x4000</c>–<c>0x5FFF</c> selects either a RAM bank
/// (<c>0x00</c>–<c>0x03</c>) or one of the five RTC registers (<c>0x08</c>–<c>0x0C</c>), which then appear in the
/// <c>0xA000</c>–<c>0xBFFF</c> window. Writing <c>0x00</c> then <c>0x01</c> to <c>0x6000</c>–<c>0x7FFF</c> latches a
/// stable snapshot of the running clock for reading. The clock advances off the emulated master cycle, so it is
/// deterministic.
/// </summary>
public sealed class Mbc3 : ICartridge, IClockedComponent {
    private const ushort RamBase = 0xA000;
    private const int RomBankSize = 0x4000;
    private const int RamBankSize = 0x2000;

    // The SM83 master clock; the RTC crystal advances one second per this many emulated master cycles.
    private const int CyclesPerSecond = 4194304;

    private readonly bool m_hasRtc;
    private readonly byte[] m_ram;
    private readonly int m_ramBankMask;
    private readonly byte[] m_rom;
    private readonly int m_romBankMask;

    private bool m_ramEnabled;
    private int m_ramBankOrRtc;
    private int m_romBank = 1;
    private int m_latchState = -1;

    // The live clock and its latched snapshot. Days span nine bits; the carry latches on overflow past day 511.
    private int m_rtcSeconds;
    private int m_rtcMinutes;
    private int m_rtcHours;
    private int m_rtcDays;
    private bool m_rtcHalted;
    private bool m_rtcDayCarry;
    private int m_rtcSubSecond;

    private int m_latchedSeconds;
    private int m_latchedMinutes;
    private int m_latchedHours;
    private int m_latchedDays;
    private bool m_latchedHalted;
    private bool m_latchedDayCarry;

    /// <inheritdoc />
    public ClockDomain Domain =>
        // The Lcd domain advances by the master-cycle count per machine cycle (matching real time in both speed
        // modes), which is exactly the rate the RTC crystal counts.
        ClockDomain.Lcd;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes an MBC3 cartridge from a ROM image, a RAM size, and whether it carries an RTC.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <param name="hasRtc">Whether the cartridge type includes a real-time clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public Mbc3(byte[] rom, int ramByteCount = 0, bool hasRtc = false) {
        ArgumentNullException.ThrowIfNull(rom);

        m_hasRtc = hasRtc;
        m_ram = ((ramByteCount > 0)
            ? new byte[ramByteCount]
            : []);
        m_ramBankMask = BankMask(byteCount: ramByteCount, bankSize: RamBankSize);
        m_rom = rom;
        m_romBankMask = BankMask(byteCount: rom.Length, bankSize: RomBankSize);
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        if (!m_hasRtc || m_rtcHalted) {
            return;
        }

        m_rtcSubSecond += tCycles;

        while (m_rtcSubSecond >= CyclesPerSecond) {
            m_rtcSubSecond -= CyclesPerSecond;

            TickRtcSecond();
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
                m_ramEnabled = ((value & 0x0F) == 0x0A);

                break;
            case 1:
                // Seven-bit ROM bank, with bank 0 remapped to 1 in the high ROM area.
                m_romBank = ((value & 0x7F) == 0) ? 1 : (value & 0x7F);

                break;
            case 2:
                m_ramBankOrRtc = value;

                break;
            default:
                LatchClock(value: value);

                break;
        }
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        if (!m_ramEnabled) {
            return 0xFF;
        }

        if (m_hasRtc && (m_ramBankOrRtc >= 0x08) && (m_ramBankOrRtc <= 0x0C)) {
            return ReadLatchedRtc(register: m_ramBankOrRtc);
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

        if (m_hasRtc && (m_ramBankOrRtc >= 0x08) && (m_ramBankOrRtc <= 0x0C)) {
            WriteLiveRtc(register: m_ramBankOrRtc, value: value);

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

    private void TickRtcSecond() {
        m_rtcSeconds += 1;

        if (m_rtcSeconds < 60) {
            return;
        }

        m_rtcSeconds = 0;
        m_rtcMinutes += 1;

        if (m_rtcMinutes < 60) {
            return;
        }

        m_rtcMinutes = 0;
        m_rtcHours += 1;

        if (m_rtcHours < 24) {
            return;
        }

        m_rtcHours = 0;
        m_rtcDays += 1;

        if (m_rtcDays > 0x1FF) {
            m_rtcDays = 0;
            m_rtcDayCarry = true;
        }
    }

    private void LatchClock(byte value) {
        // The 0x00 -> 0x01 sequence snapshots the running clock into the registers the CPU reads.
        if ((m_latchState == 0) && (value == 1)) {
            m_latchedSeconds = m_rtcSeconds;
            m_latchedMinutes = m_rtcMinutes;
            m_latchedHours = m_rtcHours;
            m_latchedDays = m_rtcDays;
            m_latchedHalted = m_rtcHalted;
            m_latchedDayCarry = m_rtcDayCarry;
        }

        m_latchState = value;
    }

    private byte ReadLatchedRtc(int register) =>
        register switch {
            0x08 => (byte)m_latchedSeconds,
            0x09 => (byte)m_latchedMinutes,
            0x0A => (byte)m_latchedHours,
            0x0B => (byte)(m_latchedDays & 0xFF),
            _ => (byte)(((m_latchedDays >> 8) & 0x01) | (m_latchedHalted ? 0x40 : 0x00) | (m_latchedDayCarry ? 0x80 : 0x00)),
        };

    private void WriteLiveRtc(int register, byte value) {
        switch (register) {
            case 0x08:
                m_rtcSeconds = (value & 0x3F);
                m_rtcSubSecond = 0;

                break;
            case 0x09:
                m_rtcMinutes = (value & 0x3F);

                break;
            case 0x0A:
                m_rtcHours = (value & 0x1F);

                break;
            case 0x0B:
                m_rtcDays = ((m_rtcDays & 0x100) | value);

                break;
            default:
                m_rtcDays = ((m_rtcDays & 0xFF) | ((value & 0x01) << 8));
                m_rtcHalted = ((value & 0x40) != 0);
                m_rtcDayCarry = ((value & 0x80) != 0);

                break;
        }
    }

    private int RamOffset(ushort address) =>
        (((m_ramBankOrRtc & 0x03 & m_ramBankMask) * RamBankSize) + (address - RamBase));

    private static int BankMask(int byteCount, int bankSize) {
        var bankCount = (byteCount / bankSize);

        return ((bankCount > 1) && BitOperations.IsPow2(value: (uint)bankCount)
            ? (bankCount - 1)
            : 0);
    }
}
