using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The HuC1 mapper: MBC1-style banking — a six-bit ROM bank at <c>0x2000</c>–<c>0x3FFF</c> (a written zero
/// reading as one) and a two-bit RAM bank at <c>0x4000</c>–<c>0x5FFF</c> — plus an infrared transceiver. The
/// <c>0x0000</c>–<c>0x1FFF</c> region selects what the <c>0xA000</c>–<c>0xBFFF</c> window exposes: writing exactly
/// <c>0x0E</c> selects the IR register, any other value selects cartridge RAM (which needs no separate enable). The IR
/// window is a view of the machine's one shared <see cref="IInfrared"/> transceiver — the same LED and receiver the
/// Color RP register drives — so a write sets the shared cart LED (bit 0) and a read reports <c>0xC0</c> with bit 0
/// carrying the received light. With no IR peer the read is <c>0xC0</c> (dark), exactly the lone-hardware value.
/// </summary>
public sealed class HuC1Cartridge : CartridgeBase, IInfraredCartridge {
    // The IR register's read-back base: bits 7-6 wired high, bit 0 carries received light (set only by a lit peer).
    private const byte InfraredNoLight = 0xC0;
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    private readonly int m_ramBankWrapMask;
    // The machine's shared IR transceiver, injected by the component factory (null when built bare in a test — then the
    // window keeps its lone-hardware behaviour: dark reads, dropped writes). Host wiring, never serialized.
    private IInfrared? m_infrared;
    private bool m_infraredMode;
    private int m_ramBank;
    private int m_romBank;

    /// <summary>Creates a HuC1 cartridge with its registers at reset (ROM bank 1, the window on RAM).</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public HuC1Cartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) {
        m_ramBankWrapMask = ComputeBankWrapMask(byteCount: header.RamByteCount, bankSize: RamBankSize);
        m_romBank = 1;
    }

    /// <inheritdoc/>
    public IInfrared? Infrared {
        set => m_infrared = value;
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        Header.HasRam;

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: exactly 0x0E routes the external window to the IR register; anything else to RAM
                m_infraredMode = (value == 0x0E);

                break;
            case 1: // 0x2000-0x3FFF: six-bit ROM bank, zero reads as one
                m_romBank = value & 0x3F;

                if (m_romBank == 0) {
                    m_romBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: two-bit RAM bank
                m_ramBank = value & 0x03;

                break;
            default: // 0x6000-0x7FFF: no register
                break;
        }
    }
    /// <summary>Reads from the external window: the IR register while in IR mode, otherwise banked RAM. The IR read is
    /// <c>0xC0</c> with bit 0 set when the shared transceiver detects a lit peer.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The IR value in IR mode, or the RAM byte.</returns>
    public override byte ReadRam(ushort address) =>
        (m_infraredMode
            ? (byte)(InfraredNoLight | ((m_infrared?.ReceivedLight ?? false) ? 0x01 : 0x00))
            : base.ReadRam(address: address));
    /// <summary>Writes to the external window: in IR mode bit 0 drives the shared IR LED (the same LED the RP register
    /// drives), otherwise banked RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value to store.</param>
    public override void WriteRam(ushort address, byte value) {
        if (m_infraredMode) {
            if (m_infrared is not null) {
                m_infrared.CartLightOut = ((value & 0x01) != 0);
            }

            return;
        }

        base.WriteRam(address: address, value: value);
    }
    /// <inheritdoc/>
    /// <remarks>Overridden: the window is mode-selected between banked RAM and the IR register, so it stays on the
    /// interface path.</remarks>
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
        writer.WriteBoolean(value: m_infraredMode);
        writer.WriteInt32(value: m_ramBank);
        writer.WriteInt32(value: m_romBank);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        m_infraredMode = reader.ReadBoolean();
        m_ramBank = reader.ReadInt32();
        m_romBank = reader.ReadInt32();
    }
}
