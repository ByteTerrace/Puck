using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>Builds the right <see cref="ICartridge"/> for a ROM image by reading its header and selecting the
/// matching mapper.</summary>
public static class Cartridge {
    /// <summary>Loads a ROM image and constructs the cartridge with the mapper its header selects.</summary>
    /// <param name="rom">The full cartridge ROM image.</param>
    /// <returns>An <see cref="ICartridge"/> implementing the cartridge's mapper.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The cartridge type is not yet implemented.</exception>
    public static ICartridge Load(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        var header = CartridgeHeader.Decode(rom: rom);
        var ram = header.RamByteCount;

        return header.CartridgeType switch {
            0x00 or 0x08 or 0x09 => new RomOnlyCartridge(rom: rom, ramByteCount: ram),
            0x01 or 0x02 or 0x03 => new Mbc1(rom: rom, ramByteCount: ram),
            0x05 or 0x06 => new Mbc2(rom: rom),
            // 0x0B-0x0D are the MMM01 multi-game menu controllers.
            0x0B or 0x0C or 0x0D => new Mmm01(rom: rom, ramByteCount: ram),
            // 0x0F/0x10 carry the real-time clock; 0x11-0x13 are plain MBC3.
            0x0F or 0x10 => new Mbc3(rom: rom, ramByteCount: ram, hasRtc: true),
            0x11 or 0x12 or 0x13 => new Mbc3(rom: rom, ramByteCount: ram),
            // 0x19-0x1E are MBC5 with various RAM/rumble combinations; the rumble motor bit is ignored.
            >= 0x19 and <= 0x1E => new Mbc5(rom: rom, ramByteCount: ram),
            0x22 => new Mbc7(rom: rom),
            0xFC => new PocketCamera(rom: rom, ramByteCount: ram),
            0xFE => new HuC3(rom: rom, ramByteCount: ram),
            0xFF => new HuC1(rom: rom, ramByteCount: ram),
            // MBC6 (0x20) and TAMA5 (0xFD) each appear in a single obscure cartridge with bespoke flash/clock
            // hardware that is not yet emulated.
            _ => throw new NotSupportedException(
                message: $"Cartridge type 0x{header.CartridgeType:X2} is not yet implemented."
            ),
        };
    }
}
