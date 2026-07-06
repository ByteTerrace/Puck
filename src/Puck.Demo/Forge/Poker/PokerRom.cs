using Puck.Capture;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

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
    public static string PrepareDefaultSavePath() {
        var directory = Path.Combine(Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), "Puck", "Demo");

        _ = Directory.CreateDirectory(path: directory);

        return Path.Combine(directory, "poker.sav");
    }

    /// <summary>The second boot proof beside the title's <c>.emulated.png</c>: boots a real machine, confirms DEAL
    /// on the title, lets the deal and the first table actions settle, and dumps the framebuffer — the dealt table
    /// as pixels.</summary>
    /// <param name="rom">The ROM image.</param>
    /// <param name="path">Where to write the PNG.</param>
    public static void WritePlayProof(byte[] rom, string path) {
        ArgumentNullException.ThrowIfNull(rom);

        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        var joypad = machine.GetRequiredService<IJoypad>();

        void RunFrames(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                joypad.SetButtons(pressed: buttons);
                machine.Machine.Run(tCycles: 70224UL);
            }
        }

        RunFrames(buttons: JoypadButtons.None, frames: 40);
        RunFrames(buttons: JoypadButtons.Start, frames: 8);
        RunFrames(buttons: JoypadButtons.None, frames: 150);
        PngEncoder.Write(height: Framebuffer.ScreenHeight, path: path, rgba: RomForge.FramebufferToRgba(machine: machine), width: Framebuffer.ScreenWidth);
    }

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
