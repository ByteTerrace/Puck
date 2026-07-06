namespace Puck.Demo.Forge;

/// <summary>
/// The Chroma cartridge's public face — the five-star re-forge of the original hand-authored colour-match ROM,
/// rebuilt on the SM83 game framework (<see cref="Framework.GameFramework"/>) with its identity declared as a
/// <see cref="Framework.GameManifest"/>: title/attract/high-score/play/pause/game-over/entry states, the
/// cursor-swap/cascade well, battery-backed high scores, PRNG drips, and input-entropy PRNG seeding. The
/// <see cref="Build"/>/<see cref="Verify"/> pair keeps the original call sites (the forge CLI and the overworld's
/// cart-type table) unchanged.
/// </summary>
internal static class ChromaRom {
    /// <summary>Assembles the Chroma <c>.gbc</c> (a genuine 32 KiB MBC1 + RAM + BATTERY Color image).</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The ROM image.</returns>
    public static byte[] Build(string title = "CHROMA") => ChromaGame.Build(title: title);

    /// <summary>Resolves (and creates the directory for) the cartridge's default battery-save path, following the
    /// demo's local-state convention (the bindings profile store's <c>%LOCALAPPDATA%\Puck\Demo</c>).</summary>
    /// <returns>The save-file path.</returns>
    public static string PrepareDefaultSavePath() => RomForge.PrepareDefaultSavePath(saveFileName: "chroma.sav");

    /// <summary>Boots the ROM on real Humble machines and asserts the game's observable behaviour — boot→title,
    /// attract, seed entropy and same-frame well replay, the ported well battery, pause, a staged clear scoring, the
    /// top-out → game-over → initials → high-score flow, SRAM persistence with an independent checksum, and corruption
    /// recovery. Throws on any violation (the forge's "verify by running" gate).</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => ChromaVerify.Run(rom: rom);
}
