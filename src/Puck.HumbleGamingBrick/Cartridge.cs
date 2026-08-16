using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>Loads a ROM image into the cartridge that matches its header's mapper.</summary>
public static class Cartridge {
    /// <summary>Parses a ROM image's header and builds the corresponding cartridge.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <returns>The cartridge, ready to be driven by the bus.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The header names a mapper that is not yet implemented.</exception>
    public static ICartridge Load(byte[] rom) {
        ArgumentNullException.ThrowIfNull(argument: rom);

        return Load(
            rom: rom,
            header: CartridgeHeader.Parse(rom: rom)
        );
    }
    /// <summary>Builds the cartridge for an already-parsed header — the overload the machine's composition uses so the
    /// header shared through the container is parsed exactly once.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The header parsed from <paramref name="rom"/>.</param>
    /// <returns>The cartridge, ready to be driven by the bus.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The header names a mapper that is not yet implemented.</exception>
    public static ICartridge Load(byte[] rom, CartridgeHeader header) {
        ArgumentNullException.ThrowIfNull(argument: rom);
        ArgumentNullException.ThrowIfNull(argument: header);

        return header.Mapper switch {
            MapperKind.RomOnly => new RomOnlyCartridge(
            header: header,
            rom: rom
        ),
            MapperKind.Mbc1 => new Mbc1Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.Mbc2 => new Mbc2Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.Mbc3 => new Mbc3Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.Mbc5 => new Mbc5Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.Mbc7 => new Mbc7Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.Mmm01 => new Mmm01Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.HuC1 => new HuC1Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.HuC3 => new HuC3Cartridge(
            header: header,
            rom: rom
        ),
            MapperKind.Camera => new CameraCartridge(
            header: header,
            rom: rom
        ),
            _ => throw new NotSupportedException(message: $"The mapper '{header.Mapper}' is not yet implemented."),
        };
    }
}
