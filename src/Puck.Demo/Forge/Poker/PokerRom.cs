using Puck.HumbleGamingBrick;

namespace Puck.Demo.Forge;

/// <summary>
/// The Poker cartridge's public face — five-star five-card draw on the SM83 game framework and the shared card
/// layer (<c>Forge/Cards/</c>): a four-seat table (the player plus three data-table AI personalities), the
/// deterministic deal-from-seed, fixed-limit betting, the draw phase, full-evaluation showdowns, packed-BCD chips,
/// battery-backed bankrolls and records, scripted attract, and initials entry. The <see cref="Build"/>/<see cref="Verify"/>
/// pair keeps the forge-CLI call-site shape every framework game shares.
/// </summary>
internal static class PokerRom {
    /// <summary>Assembles the Poker <c>.gbc</c> (a genuine 32 KiB MBC1 + RAM + BATTERY Color image).</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The ROM image.</returns>
    public static byte[] Build(string title = "POKER") => PokerGame.Build(title: title);

    /// <summary>Resolves (and creates the directory for) the cartridge's default battery-save path, following the
    /// demo's local-state convention.</summary>
    /// <returns>The save-file path.</returns>
    public static string PrepareDefaultSavePath() => RomForge.PrepareDefaultSavePath(saveFileName: "poker.sav");

    /// <summary>The second boot proof beside the title's <c>.emulated.png</c>: boots a real machine, confirms DEAL
    /// on the title, lets the deal and the first table actions settle, and dumps the framebuffer — the dealt table
    /// as pixels.</summary>
    /// <param name="rom">The ROM image.</param>
    /// <param name="path">Where to write the PNG.</param>
    public static void WritePlayProof(byte[] rom, string path) => RomForge.WriteCardGamePlayProof(
        rom: rom,
        path: path,
        (JoypadButtons.None, 40),
        (JoypadButtons.Start, 8),
        (JoypadButtons.None, 150)
    );

    /// <summary>Boots the ROM on real Humble machines and asserts the game's observable behaviour — boot→title,
    /// attract (with its constant-seed deal matched against the C# oracle and an SRAM-untouched sweep), seed
    /// entropy and replay determinism, the deal-from-seed proof, evaluator equivalence against the C# oracle
    /// (random deals, staged categories, the wheel, tiebreak depth), a staged full hand (deal→bet→draw→showdown
    /// with a known winner and every AI action matched against the personality-table oracle), chip/pot arithmetic
    /// and conservation, pause freezing the simulation, the session end → initials → save flow both ways, SRAM
    /// persistence with an independent checksum, and corruption recovery. Throws on any violation (the forge's
    /// "verify by running" gate).</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => PokerVerify.Run(rom: rom);
}
