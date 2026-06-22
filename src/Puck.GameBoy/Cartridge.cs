namespace Puck.GameBoy;

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

        // Mappers are added in their build phase; until then only the no-mapper types load.
        return header.CartridgeType switch {
            0x00 or 0x08 or 0x09 => new RomOnlyCartridge(rom: rom, ramByteCount: header.RamByteCount),
            _ => throw new NotSupportedException(
                message: $"Cartridge type 0x{header.CartridgeType:X2} is not yet implemented."
            ),
        };
    }
}
