using System.Text;
using System.Text.Json;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Renders the authored cartridge beside a reference cartridge, frame for frame, so the two can be judged against each
/// other rather than against a description of the original.
/// </summary>
/// <remarks>
/// <para>
/// Point <c>PUCK_TETRIS_REFERENCE</c> at a cartridge image to run it, and <c>PUCK_TETRIS_COMPARE_OUT</c> at a file to
/// write to; without the first it does nothing, because the reference is the owner's to supply and is not in the tree.
/// </para>
/// <para>
/// It asserts nothing. Two cartridges do not share a random sequence or a title screen's length, so any automatic
/// verdict would be about the script rather than about fidelity. What it produces is the pair of pictures.
/// </para>
/// </remarks>
public sealed class TetrisSideBySide {
    private const int Height = 144;
    private const int Width = 160;
    private const string DefaultReference = "roms/tetris-world-rev1.gb";

    [Fact]
    public void RenderBothForComparison() {
        // A relative path is read against the checkout, not the test binary's directory; roms/ is ignored by git, so a
        // licensed cartridge can sit in the tree without ever being committed.
        var named = Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_REFERENCE") ?? DefaultReference;
        var reference = Path.IsPathRooted(path: named) ? named : Path.Combine(path1: Checkout(), path2: named);
        if (!File.Exists(path: reference)) {
            return;
        }

        var authored = new HgbCartridgeCompiler().Compile(document: Authored());
        using var ours = new VerifyMachineDriver(rom: authored.Rom, label: "authored");
        using var theirs = new VerifyMachineDriver(rom: File.ReadAllBytes(path: reference), label: "reference");

        // Enough presses to carry a title screen and a mode menu, then a long look at whatever is playing.
        var page = new StringBuilder();
        foreach (var (caption, frames, buttons) in Script()) {
            ours.RunFrames(buttons: buttons, frames: frames);
            theirs.RunFrames(buttons: buttons, frames: frames);
            page.AppendLine(value: $"=== {caption} ===");
            page.AppendLine(value: Beside(left: ours, right: theirs));
        }

        var destination = Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_COMPARE_OUT")
            ?? Path.Combine(path1: Path.GetTempPath(), path2: "tetris-side-by-side.txt");
        File.WriteAllText(path: destination, contents: page.ToString());
    }

    // The reference walks a logo and three menus before it deals a piece, so the run-in presses start several times
    // with room to settle between. The authored cartridge ignores the presses it has no screen for.
    private static (string Caption, int Frames, JoypadButtons Buttons)[] Script() {
        var script = new List<(string, int, JoypadButtons)> { ("boot", 300, JoypadButtons.None) };
        for (var press = 0; press < 8; ++press) {
            script.Add(item: ($"start {press}", 6, JoypadButtons.Start));
            script.Add(item: ($"settling {press}", 40, JoypadButtons.None));
        }

        script.Add(item: ("playing", 240, JoypadButtons.None));
        script.Add(item: ("holding left", 40, JoypadButtons.Left));
        script.Add(item: ("playing on", 300, JoypadButtons.None));

        return [.. script];
    }

    // Two screens in one column pair, sampled a tile at a time: a full-pixel diff of two different games says nothing,
    // while the shapes at tile resolution are what a reader compares.
    private static string Beside(VerifyMachineDriver left, VerifyMachineDriver right) {
        var page = new StringBuilder();
        page.AppendLine(value: "authored                 reference");
        for (var y = 0; y < Height; y += 8) {
            page.Append(value: Row(machine: left, y: y)).Append(value: "  |  ").AppendLine(value: Row(machine: right, y: y));
        }

        return page.ToString();
    }

    private static string Row(VerifyMachineDriver machine, int y) {
        var backdrop = machine.ReadPixel(x: Width - 2, y: Height - 2);
        var line = new StringBuilder();
        for (var x = 0; x < Width; x += 8) {
            line.Append(value: machine.ReadPixel(x: x + 4, y: y + 4) == backdrop ? '.' : '#');
        }

        return line.ToString();
    }

    private static string Checkout() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    private static CartridgeDocument Authored() {
        var json = File.ReadAllText(path: Path.Combine(
            path1: Checkout(), path2: "src/Puck.World/Assets/cartridges", path3: "tetris.cgb.cartridge.json"));

        return JsonSerializer.Deserialize<CartridgeDocument>(json: json, options: new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
