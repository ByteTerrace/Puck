namespace Puck.HumbleGamingBrick;

/// <summary>
/// The MBC2 mapper: up to 256&#160;KiB of ROM through a four-bit bank select and a built-in 512&#160;&#215;&#160;4-bit
/// RAM instead of an external RAM chip. The single control region <c>0x0000</c>–<c>0x3FFF</c> is split by address
/// bit&#160;8: a write with the bit clear toggles RAM enable, a write with the bit set selects the ROM bank (a written
/// value of zero reads as one, so bank <c>0x00</c> is unreachable in the switchable region). Only the low four bits of
/// each RAM cell are stored; a read returns the stored nibble with the high nibble driven to ones. The RAM is a flat
/// 512 bytes echoed across the whole <c>0xA000</c>–<c>0xBFFF</c> window.
/// </summary>
public sealed class Mbc2Cartridge : CartridgeBase {
    private const int RamByteCount = 0x0200;
    private const int RamMask = 0x01FF;
    private const int RomBankSize = 0x4000;

    private bool m_ramEnabled;
    private int m_romBank;

    /// <summary>Creates an MBC2 cartridge with its registers at reset (ROM bank 1, RAM disabled) and its built-in
    /// 512&#160;&#215;&#160;4-bit RAM, which the header does not size.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public Mbc2Cartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header, ramByteCount: RamByteCount) {
        m_romBank = 1;
    }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        m_ramEnabled;

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        // Only the 0x0000-0x3FFF region carries registers; address bit 8 selects between the two.
        if (address > MemoryMap.RomBank0End) {
            return;
        }

        if ((address & 0x0100) == 0) {
            // Bit 8 clear: RAM enable (0x0A in the low nibble enables, anything else disables).
            m_ramEnabled = ((value & 0x0F) == 0x0A);

            return;
        }

        // Bit 8 set: four-bit ROM bank select; a written zero reads as one.
        m_romBank = (value & 0x0F);

        if (m_romBank == 0) {
            m_romBank = 1;
        }
    }
    /// <summary>Reads a byte from the built-in RAM, returning the stored nibble in the low four bits with the high nibble
    /// driven to ones, or open-bus <c>0xFF</c> when the RAM is disabled.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The value <c>0xF0 | nibble</c>, or <c>0xFF</c> when disabled.</returns>
    public override byte ReadRam(ushort address) =>
        RamAccessible ? (byte)(0xF0 | (base.ReadRam(address: address) & 0x0F)) : (byte)0xFF;
    /// <summary>Writes the low four bits of <paramref name="value"/> to the built-in RAM; dropped when disabled.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value whose low nibble is stored.</param>
    public override void WriteRam(ushort address, byte value) =>
        base.WriteRam(address: address, value: (byte)(value & 0x0F));

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) =>
        (address <= MemoryMap.RomBank0End)
            ? address
            : ((m_romBank * RomBankSize) + (address - MemoryMap.RomBankNStart));
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        ((address - MemoryMap.ExternalRamStart) & RamMask);
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        writer.WriteBoolean(value: m_ramEnabled);
        writer.WriteInt32(value: m_romBank);
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        m_ramEnabled = reader.ReadBoolean();
        m_romBank = reader.ReadInt32();
    }
}
