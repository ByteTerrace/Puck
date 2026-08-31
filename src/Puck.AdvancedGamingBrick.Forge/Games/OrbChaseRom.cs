namespace Puck.AdvancedGamingBrick.Forge.Games;

/// <summary>
/// The Orb Chase cartridge: assembles the Thumb routine, wraps it in a direct-boot header, and runs the full
/// <see cref="OrbChaseVerify"/> battery on a real emulated machine before the bytes are handed out — a
/// <see cref="Build"/> that returns is a cart proven to boot, play, and replay deterministically.
/// </summary>
public static class OrbChaseRom {
    /// <summary>Builds and self-verifies the cartridge.</summary>
    /// <returns>The 64 KiB ROM image.</returns>
    public static byte[] Build() {
        var rom = AgbForgeCartridge.Build(
            data: [],
            gameCode: OrbChaseProtocol.GameCode,
            routine: OrbChaseGame.Assemble(),
            title: OrbChaseProtocol.Title
        );

        OrbChaseVerify.Run(rom: rom);

        return rom;
    }
}
