using Puck.HumbleGamingBrick;

namespace Puck.Demo.Forge;

/// <summary>
/// The Solitaire cartridge's public face — five-star Klondike (draw one) on the SM83 game framework and the shared
/// card layer (<c>Forge/Cards/</c>): title menu, scripted attract, deterministic deal-from-seed, legal-move rules
/// from the card record table, undo, battery-backed high scores and win streaks, the win fanfare, and initials
/// entry. The <see cref="Build"/>/<see cref="Verify"/> pair keeps the forge-CLI call-site shape every framework
/// game shares.
/// </summary>
internal static class SolitaireRom {
    /// <summary>Assembles the Solitaire <c>.gbc</c> (a genuine 32 KiB MBC1 + RAM + BATTERY Color image).</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The ROM image.</returns>
    public static byte[] Build(string title = "SOLITAIRE") => SolitaireGame.Build(title: title);

    /// <summary>Resolves (and creates the directory for) the cartridge's default battery-save path, following the
    /// demo's local-state convention.</summary>
    /// <returns>The save-file path.</returns>
    public static string PrepareDefaultSavePath() => RomForge.PrepareDefaultSavePath(saveFileName: "solitaire.sav");

    /// <summary>The second boot proof beside the title's <c>.emulated.png</c>: boots a real machine, confirms NEW
    /// DEAL on the title, lets the deal repaint settle, and dumps the framebuffer — the dealt board as pixels.</summary>
    /// <param name="rom">The ROM image.</param>
    /// <param name="path">Where to write the PNG.</param>
    public static void WritePlayProof(byte[] rom, string path) => RomForge.WriteCardGamePlayProof(
        rom: rom,
        path: path,
        (JoypadButtons.None, 40),
        (JoypadButtons.Start, 8),
        (JoypadButtons.None, 120)
    );

    /// <summary>Boots the ROM on real Humble machines and asserts the game's observable behaviour — boot→title,
    /// attract (with its constant-seed deal matched against the C# oracle), seed entropy and replay determinism,
    /// the deal-from-seed proof, draw/recycle/undo, legal and illegal moves, the flip, pause, the four-king win →
    /// streak → initials → high-score flow, SRAM persistence with an independent checksum, and corruption
    /// recovery. Throws on any violation (the forge's "verify by running" gate).</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => SolitaireVerify.Run(rom: rom);
}
