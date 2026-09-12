using System.Text.Json;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Writes the cartridge's screens as raw pictures, so colour and art can be looked at rather than described.
/// </summary>
/// <remarks>
/// Set <c>PUCK_TETRIS_SHOTS</c> to a directory to run it. Each moment lands as
/// <c>&lt;name&gt;.rgb</c> — 160 by 144 pixels, three bytes each, top row first — which any converter can read and
/// which needs no image library in the test project.
/// </remarks>
public sealed class TetrisShots {
    private const int Height = 144;
    private const int Width = 160;

    [Fact]
    public void Capture() {
        if (Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_SHOTS") is not { Length: > 0 } directory) {
            return;
        }

        Directory.CreateDirectory(path: directory);

        var result = new HgbCartridgeCompiler().Compile(document: Document());

        // The mark the cartridge carries, as the machine shows it before handing over.
        using (var booting = new VerifyMachineDriver(
            bootRom: BootRomBuilder.Build(model: ConsoleModel.CgbE, mark: BootRomMark.House),
            label: "boot",
            rom: result.Rom)) {
            foreach (var frames in new[] { 4, 6, 8, 10, 14, 20 }) {
                Shoot(directory: directory, machine: booting, name: $"00-boot-{frames:00}", buttons: JoypadButtons.None, frames: frames);
            }
        }

        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "shots");

        Shoot(directory: directory, machine: machine, name: "01-title-name", buttons: JoypadButtons.None, frames: 12);
        Shoot(directory: directory, machine: machine, name: "02-title-game", buttons: JoypadButtons.None, frames: 30);
        Shoot(directory: directory, machine: machine, name: "03-title-rest", buttons: JoypadButtons.None, frames: 90);

        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        Shoot(directory: directory, machine: machine, name: "04-menu", buttons: JoypadButtons.None, frames: 120);

        machine.RunFrames(buttons: JoypadButtons.Right, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.Right, frames: 4);
        Shoot(directory: directory, machine: machine, name: "05-menu-level", buttons: JoypadButtons.None, frames: 20);

        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        Shoot(directory: directory, machine: machine, name: "06-first-piece", buttons: JoypadButtons.None, frames: 130);
        Shoot(directory: directory, machine: machine, name: "07-playing", buttons: JoypadButtons.None, frames: 60 * 12);
        Shoot(directory: directory, machine: machine, name: "08-playing-later", buttons: JoypadButtons.None, frames: 60 * 20);

        // Top out, so the screen that ends a run is in the set too.
        for (var row = 0; row < 8; ++row) {
            for (var column = 0; column < 9; ++column) {
                machine.Write(address: (ushort)(result.Arrays["field"] + (uint)((row * 10) + column)), value: (byte)((column % 7) + 1));
            }
        }

        Shoot(directory: directory, machine: machine, name: "09-game-over", buttons: JoypadButtons.None, frames: 60 * 8);
    }

    [Fact]
    public void CaptureMotion() {
        if (Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_SHOTS") is not { Length: > 0 } directory) {
            return;
        }

        Directory.CreateDirectory(path: directory);

        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "motion");
        var phase = (ushort)result.Variables["ph"];

        // The picture dimming on the way from the title to the mode menu, sampled across the ramp.
        machine.RunFrames(buttons: JoypadButtons.None, frames: 120);
        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        Settle(directory: directory, machine: machine, name: "10-fade-0", phase: phase, wanted: 12, after: 0);
        Shoot(directory: directory, machine: machine, name: "10-fade-1", buttons: JoypadButtons.None, frames: 6);
        Shoot(directory: directory, machine: machine, name: "10-fade-2", buttons: JoypadButtons.None, frames: 6);

        // Into a game, with a row one cell short of complete so the next piece finishes it. Seeding the well writes
        // its memory, not the picture, so the republish pass has to run before any of it is on screen.
        machine.RunFrames(buttons: JoypadButtons.None, frames: 130);
        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 130);
        for (var column = 0; column < 10; ++column) {
            machine.Write(address: (ushort)(result.Arrays["field"] + (17 * 10) + (uint)column), value: (byte)((column % 6) + 2));
        }

        machine.Write(address: phase, value: 5);
        Settle(directory: directory, machine: machine, name: "11-seeded", phase: phase, wanted: 0, after: 4);

        // Straight into the scan, which finds the row complete. What the run through a landing piece proves is
        // gameplay; what this proves is what the clear LOOKS like.
        machine.Write(address: phase, value: 2);

        // The completed row coming apart, a column pair at a time. The pass is entered ONCE and sampled across its
        // own length; waiting for it again would wait for a second clear that never comes.
        Settle(directory: directory, machine: machine, name: "12-clearing-0", phase: phase, wanted: 19, after: 0);

        for (var shot = 1; shot < 5; ++shot) {
            Shoot(directory: directory, machine: machine, name: $"12-clearing-{shot}", buttons: JoypadButtons.None, frames: 2);
        }
    }

    // Steps a frame at a time until the cartridge reaches a phase, holds for a few more, then shoots. A fixed frame
    // count cannot catch a pass that lasts ten frames and starts whenever the piece happens to land.
    private static void Settle(string directory, VerifyMachineDriver machine, string name, ushort phase, byte wanted, int after) {
        for (var guard = 0; (guard < 60 * 40); ++guard) {
            if (machine.Read(address: phase) == wanted) {
                Shoot(directory: directory, machine: machine, name: name, buttons: JoypadButtons.None, frames: after);

                return;
            }

            machine.RunFrames(buttons: JoypadButtons.None, frames: 1);
        }

        throw new InvalidOperationException(message: $"The cartridge never reached phase {wanted} for '{name}'.");
    }

    private static void Shoot(string directory, VerifyMachineDriver machine, string name, JoypadButtons buttons, int frames) {
        machine.RunFrames(buttons: buttons, frames: frames);

        var pixels = new byte[Width * Height * 3];
        var index = 0;
        for (var y = 0; y < Height; ++y) {
            for (var x = 0; x < Width; ++x) {
                var colour = machine.ReadPixel(x: x, y: y);
                pixels[index++] = ((byte)((colour >> 16) & 0xFF));
                pixels[index++] = ((byte)((colour >> 8) & 0xFF));
                pixels[index++] = ((byte)(colour & 0xFF));
            }
        }

        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: name + ".rgb"), bytes: pixels);
    }

    private static CartridgeDocument Document() {
        var folder = new DirectoryInfo(path: AppContext.BaseDirectory);
        while ((folder is not null) && !File.Exists(path: Path.Combine(path1: folder.FullName, path2: "Puck.slnx"))) {
            folder = folder.Parent;
        }

        var json = File.ReadAllText(path: Path.Combine(
            path1: folder!.FullName, path2: "src/Puck.World/Assets/cartridges", path3: "tetris.cgb.cartridge.json"));

        return JsonSerializer.Deserialize<CartridgeDocument>(json: json, options: new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
