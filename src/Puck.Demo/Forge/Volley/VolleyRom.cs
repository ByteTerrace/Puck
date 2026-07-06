namespace Puck.Demo.Forge;

/// <summary>
/// The Volley cartridge's public face — the five-star re-forge of the original hand-authored ROM, rebuilt on the
/// SM83 game framework (<see cref="Framework.GameFramework"/>) with its identity declared as a
/// <see cref="Framework.GameManifest"/>: title/attract/high-score/play/pause/game-over/entry states, hardware-sprite
/// paddles + ball, battery-backed high scores, PRNG serves, and input-entropy PRNG seeding. The
/// <see cref="Build"/>/<see cref="Verify"/> pair keeps the original call sites (the forge CLI, the overworld's
/// cart-type table, and the bake calibration's <see cref="CalibrationArt"/>) unchanged.
/// </summary>
internal static class VolleyRom {
    /// <summary>Assembles the Volley <c>.gbc</c> (a genuine 32 KiB MBC1 + RAM + BATTERY Color image).</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The ROM image.</returns>
    public static byte[] Build(string title = "VOLLEY") => VolleyGame.Build(title: title);

    /// <summary>Resolves (and creates the directory for) the cartridge's default battery-save path, following the
    /// demo's local-state convention (the bindings profile store's <c>%LOCALAPPDATA%\Puck\Demo</c>).</summary>
    /// <returns>The save-file path.</returns>
    public static string PrepareDefaultSavePath() {
        var directory = Path.Combine(Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), "Puck", "Demo");

        _ = Directory.CreateDirectory(path: directory);

        return Path.Combine(directory, "volley.sav");
    }

    /// <summary>The hand-authored art + palettes the bake calibration (<c>--forge-bake-calibration</c>) measures the
    /// SDF→bake pipeline against (forwarded from <see cref="VolleyTables"/>).</summary>
    /// <returns>The three 16-byte 2bpp tiles and the two 4-colour palettes.</returns>
    internal static (byte[] PaddleTile, byte[] BallTile, byte[] NetTile, HgbImage.Rgb[] BackgroundColours, HgbImage.Rgb[] ObjectColours) CalibrationArt() =>
        VolleyTables.CalibrationArt();

    /// <summary>Boots the ROM on real Humble machines and asserts the game's observable behaviour — boot→title,
    /// attract, seed entropy and replay determinism, the ported court battery, pause, rally/point scoring, the
    /// match-point → game-over → initials → high-score flow, SRAM persistence with an independent checksum, and
    /// corruption recovery. Throws on any violation (the forge's "verify by running" gate).</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => VolleyVerify.Run(rom: rom);
}
