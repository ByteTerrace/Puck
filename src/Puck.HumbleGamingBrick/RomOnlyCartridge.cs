using System.Numerics;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A cartridge with no memory-bank controller: up to 32&#160;KiB of ROM mapped directly across
/// <c>0x0000</c>–<c>0x7FFF</c> and, optionally, a single small RAM bank at <c>0xA000</c>–<c>0xBFFF</c>. Writes
/// into the ROM range are ignored because there are no mapper registers.
/// </summary>
public sealed class RomOnlyCartridge : ICartridge {
    private const ushort RamBase = 0xA000;

    private readonly byte[] m_ram;
    private readonly byte[] m_rom;

    /// <inheritdoc />
    public ReadOnlySpan<byte> SaveRam =>
        m_ram;

    /// <summary>Initializes a cartridge from a ROM image and an optional RAM size.</summary>
    /// <param name="rom">The cartridge ROM image (up to 32&#160;KiB).</param>
    /// <param name="ramByteCount">The size of cartridge RAM in bytes, or zero for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public RomOnlyCartridge(byte[] rom, int ramByteCount = 0) {
        ArgumentNullException.ThrowIfNull(rom);

        m_ram = ((ramByteCount > 0)
            ? new byte[ramByteCount]
            : []);
        m_rom = rom;
    }

    /// <inheritdoc />
    public byte ReadRom(ushort address) {
        if (m_rom.Length == 0) {
            return 0xFF;
        }

        // A no-MBC cartridge wires only the low address lines, so a power-of-two image smaller than the 32 KiB
        // window mirrors across it rather than reading open bus.
        var index = (BitOperations.IsPow2(value: (uint)m_rom.Length)
            ? (address & (m_rom.Length - 1))
            : address);

        return ((index < m_rom.Length)
            ? m_rom[index]
            : (byte)0xFF);
    }
    /// <inheritdoc />
    public void WriteRom(ushort address, byte value) {
        // No mapper registers; ROM writes are inert.
    }
    /// <inheritdoc />
    public byte ReadRam(ushort address) {
        var offset = (address - RamBase);

        return (((uint)offset < (uint)m_ram.Length)
            ? m_ram[offset]
            : (byte)0xFF);
    }
    /// <inheritdoc />
    public void WriteRam(ushort address, byte value) {
        var offset = (address - RamBase);

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
}
