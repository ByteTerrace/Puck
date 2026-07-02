namespace Puck.HumbleGamingBrick;

/// <summary>
/// The Hudson HuC1 mapper: MBC1-style banking — a six-bit ROM bank at <c>0x2000</c>–<c>0x3FFF</c> (a written zero
/// reading as one) and a two-bit RAM bank at <c>0x4000</c>–<c>0x5FFF</c> — plus an infrared transceiver. The
/// <c>0x0000</c>–<c>0x1FFF</c> region selects what the <c>0xA000</c>–<c>0xBFFF</c> window exposes: writing exactly
/// <c>0x0E</c> selects the IR register, any other value selects cartridge RAM (which needs no separate enable). With
/// no IR peer in the machine the IR register reads <c>0xC0</c> — the fixed handshake bits with the received-light bit
/// clear — and transmit writes are dropped, keeping the link deterministic.
/// </summary>
public sealed class HuC1Cartridge : CartridgeBase {
    // The IR register's read-back: bits 7-6 are wired high and bit 0 carries received light — never lit without a peer.
    private const byte InfraredNoLight = 0xC0;
    private const int RamBankSize = 0x2000;
    private const int RomBankSize = 0x4000;

    private readonly int m_ramBankWrapMask;

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
    protected override bool RamAccessible =>
        Header.HasRam;

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        switch (address >> 13) {
            case 0: // 0x0000-0x1FFF: exactly 0x0E routes the external window to the IR register; anything else to RAM
                m_infraredMode = (value == 0x0E);

                break;
            case 1: // 0x2000-0x3FFF: six-bit ROM bank, zero reads as one
                m_romBank = (value & 0x3F);

                if (m_romBank == 0) {
                    m_romBank = 1;
                }

                break;
            case 2: // 0x4000-0x5FFF: two-bit RAM bank
                m_ramBank = (value & 0x03);

                break;
            default: // 0x6000-0x7FFF: no register
                break;
        }
    }
    /// <summary>Reads from the external window: the IR register while in IR mode, otherwise banked RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The IR handshake value <c>0xC0</c> in IR mode, or the RAM byte.</returns>
    public override byte ReadRam(ushort address) =>
        m_infraredMode ? InfraredNoLight : base.ReadRam(address: address);
    /// <summary>Writes to the external window: dropped while in IR mode (transmit has no observable peer), otherwise
    /// banked RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value to store.</param>
    public override void WriteRam(ushort address, byte value) {
        if (m_infraredMode) {
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
