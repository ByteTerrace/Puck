using System.Buffers.Binary;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The HuC3 mapper: full-byte ROM banking (a written zero reading as one), full-byte RAM banking, an infrared
/// transceiver, and a real-time clock reached through a nibble command protocol. A write to
/// <c>0x0000</c>–<c>0x1FFF</c> selects the mode of the <c>0xA000</c>–<c>0xBFFF</c> window: <c>0xA</c>&#160;=&#160;RAM,
/// <c>0xB</c>&#160;=&#160;RTC command, <c>0xC</c>&#160;=&#160;RTC read, <c>0xD</c>&#160;=&#160;status semaphore
/// (always ready), <c>0xE</c>&#160;=&#160;IR (no peer, so no incoming light). Each RTC command carries a nibble:
/// <c>0x1n</c> fetches the register at the access index (post-increment), <c>0x2n</c>/<c>0x3n</c> store one
/// (<c>0x3n</c> post-increments), <c>0x4n</c>/<c>0x5n</c> set the index's low/high nibble, and <c>0x6n</c> arms the
/// command flags. The IR window (<c>0xE</c>) is a view of the machine's one shared <see cref="IInfrared"/> transceiver —
/// the same LED and receiver the Color RP register drives: an IR-mode read reports the received-light bit, and an
/// IR-mode write drives the shared cart LED from bit 0 — so with no peer it reads dark, the lone-hardware value.
/// <para>
/// The clock counts minutes (three nibbles, <c>0</c>–<c>1439</c>) and days (four nibbles, wrapping at twelve bits) and
/// is <b>deterministic</b>: it is an LCD-domain <see cref="IClockedComponent"/> that advances one minute per
/// <see cref="DotsPerMinute"/> emulated dots — exactly like the MBC3 clock, never from host wall-clock — so identical
/// runs read identical time.
/// </para>
/// </summary>
public sealed class HuC3Cartridge : CartridgeBase, IClockedComponent, IInfraredCartridge {
    // One RTC minute is 60 * 2^22 PPU dots; the LCD clock's rate is speed-switch independent, like the RTC crystal.
    private const int DotsPerMinute = (60 * 4194304);
    private const int MinutesPerDay = 1440;
    private const int ModeInfrared = 0x0E;
    private const int ModeRam = 0x0A;
    private const int ModeRtcCommand = 0x0B;
    private const int ModeRtcRead = 0x0C;
    private const int ModeStatus = 0x0D;
    // The persistent-clock footer: u32 minutes + u32 days (the two counters the nibble protocol exposes), then the
    // 8-byte interop timestamp — the same shape as the MBC3 footer, sized to the HuC3's smaller clock. HuC3 save
    // files have no cross-emulator standard footer, so this is OUR convention (documented by the layout alone).
    private const int PersistentClockFooterByteCount = 16;
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    private readonly int m_ramBankWrapMask;
    // The machine's shared IR transceiver, injected by the component factory (null when built bare in a test — then the IR
    // window keeps its lone-hardware behaviour: dark reads, dropped writes). Host wiring, never serialized.
    private IInfrared? m_infrared;
    private int m_accessFlags;
    private int m_accessIndex;
    private int m_days;
    private int m_dotAccumulator;
    private int m_minutes;
    private int m_mode;
    private int m_ramBank;
    private int m_readValue;
    private int m_romBank;

    /// <summary>Creates a HuC3 cartridge with its registers at reset (ROM bank 1, window disabled) and its clock at
    /// zero.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public HuC3Cartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) {
        m_ramBankWrapMask = ComputeBankWrapMask(byteCount: header.RamByteCount, bankSize: RamBankSize);
        m_romBank = 1;
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Lcd;

    /// <inheritdoc/>
    public IInfrared? Infrared {
        set => m_infrared = value;
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        (Header.HasRam && (m_mode == ModeRam));

    /// <inheritdoc/>
    public override int PersistentClockByteCount =>
        PersistentClockFooterByteCount;

    /// <inheritdoc/>
    public override byte[] ExportPersistentClock(long unixTimestampSeconds) {
        var footer = new byte[PersistentClockFooterByteCount];
        var span = footer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[0..], value: (uint)m_minutes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[4..], value: (uint)m_days);
        BinaryPrimitives.WriteInt64LittleEndian(destination: span[8..], value: unixTimestampSeconds);

        return footer;
    }
    /// <inheritdoc/>
    public override void ImportPersistentClock(ReadOnlySpan<byte> source) {
        if (source.Length < PersistentClockFooterByteCount) {
            return;
        }

        // Masked to the three/four nibbles the write protocol itself can produce; the trailing timestamp is
        // deliberately ignored (the deterministic clock resumes, never advancing from wall time), and the
        // sub-minute prescaler restarts.
        m_minutes = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[0..]) & 0xFFF);
        m_days = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source: source[4..]) & 0xFFF);
        m_dotAccumulator = 0;
    }

    /// <inheritdoc/>
    public void Tick() {
        // Determinism: the sole time source is the emulated LCD clock.
        if (++m_dotAccumulator < DotsPerMinute) {
            return;
        }

        m_dotAccumulator = 0;

        if (++m_minutes < MinutesPerDay) {
            return;
        }

        m_minutes = 0;
        m_days = (m_days + 1) & 0xFFF;
    }
    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: window mode nibble
                m_mode = value & 0x0F;

                break;
            case 1: // 0x2000-0x3FFF: full-byte ROM bank, zero reads as one
                m_romBank = ((value == 0) ? 1 : value);

                break;
            case 2: // 0x4000-0x5FFF: RAM bank
                m_ramBank = value;

                break;
            default: // 0x6000-0x7FFF: no register
                break;
        }
    }
    /// <summary>Reads from the external window according to the selected mode: banked RAM, the RTC read register (which
    /// reports <c>0x01</c> while the command flags are armed, else the fetched nibble), the always-ready status
    /// semaphore, the dark IR receiver, or the protocol idle value <c>0x01</c>.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The mode-selected value.</returns>
    public override byte ReadRam(ushort address) =>
        m_mode switch {
            ModeRam => base.ReadRam(address: address),
            ModeRtcRead => ((m_accessFlags == 0x02) ? (byte)0x01 : (byte)m_readValue),
            ModeStatus => 0x01,
            ModeInfrared => (byte)((m_infrared?.ReceivedLight ?? false) ? 0x01 : 0x00),
            _ => 0x01,
        };
    /// <summary>Writes to the external window according to the selected mode: banked RAM, an RTC command, or nothing —
    /// the read-only protocol modes and the disabled window consume the write.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value (or command byte) to store.</param>
    public override void WriteRam(ushort address, byte value) {
        switch (m_mode) {
            case ModeRam:
                base.WriteRam(address: address, value: value);

                break;
            case ModeRtcCommand:
                ExecuteRtcCommand(command: value);

                break;
            case ModeInfrared:
                // Bit 0 drives the shared IR LED (the same LED the RP register drives).
                if (m_infrared is not null) {
                    m_infrared.CartLightOut = ((value & 0x01) != 0);
                }

                break;
            default: // read-only protocol modes and the disabled window drop the write
                break;
        }
    }
    /// <inheritdoc/>
    /// <remarks>Overridden: the window is entirely mode-selected (RAM, RTC command/read, status, IR), so it stays on
    /// the interface path.</remarks>
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
        (((m_ramBank & m_ramBankWrapMask) * RamBankSize) + (address - MemoryMap.ExternalRamStart));
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteInt32(value: m_accessFlags);
        writer.WriteInt32(value: m_accessIndex);
        writer.WriteInt32(value: m_days);
        writer.WriteInt32(value: m_dotAccumulator);
        writer.WriteInt32(value: m_minutes);
        writer.WriteInt32(value: m_mode);
        writer.WriteInt32(value: m_ramBank);
        writer.WriteInt32(value: m_readValue);
        writer.WriteInt32(value: m_romBank);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        m_accessFlags = reader.ReadInt32();
        m_accessIndex = reader.ReadInt32();
        m_days = reader.ReadInt32();
        m_dotAccumulator = reader.ReadInt32();
        m_minutes = reader.ReadInt32();
        m_mode = reader.ReadInt32();
        m_ramBank = reader.ReadInt32();
        m_readValue = reader.ReadInt32();
        m_romBank = reader.ReadInt32();
    }

    private void ExecuteRtcCommand(byte command) {
        var nibble = command & 0x0F;

        switch (command >> 4) {
            case 0x1: // fetch the nibble at the access index, post-increment
                m_readValue = ReadRtcNibble(index: m_accessIndex);
                ++m_accessIndex;

                break;
            case 0x2: // store a nibble at the access index
                WriteRtcNibble(index: m_accessIndex, nibble: nibble);

                break;
            case 0x3: // store a nibble, post-increment
                WriteRtcNibble(index: m_accessIndex, nibble: nibble);
                ++m_accessIndex;

                break;
            case 0x4: // access index, low nibble
                m_accessIndex = (m_accessIndex & 0xF0) | nibble;

                break;
            case 0x5: // access index, high nibble
                m_accessIndex = (m_accessIndex & 0x0F) | (nibble << 4);

                break;
            case 0x6: // command/semaphore flags
                m_accessFlags = nibble;

                break;
            default:
                break;
        }
    }
    private int ReadRtcNibble(int index) {
        if (index < 3) {
            return (m_minutes >> (index * 4)) & 0x0F;
        }

        if (index < 7) {
            return (m_days >> ((index - 3) * 4)) & 0x0F;
        }

        return 0;
    }
    private void WriteRtcNibble(int index, int nibble) {
        if (index < 3) {
            var shift = (index * 4);

            m_minutes = (m_minutes & ~(0x0F << shift)) | (nibble << shift);
        } else if (index < 7) {
            var shift = ((index - 3) * 4);

            m_days = (m_days & ~(0x0F << shift)) | (nibble << shift);
        }

        // Higher indices hold the write-only alarm block, which has no observable readback here.
    }
}
