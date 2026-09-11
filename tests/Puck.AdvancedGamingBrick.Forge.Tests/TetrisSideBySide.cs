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

    [Fact]
    public void RenderBothForComparison() {
        if (Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_REFERENCE") is not { Length: > 0 } reference) {
            return;
        }

        Assert.True(condition: File.Exists(path: reference), userMessage: $"No cartridge at {reference}.");

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

    private static (string Caption, int Frames, JoypadButtons Buttons)[] Script() => [
        ("boot", 120, JoypadButtons.None),
        ("start pressed", 8, JoypadButtons.Start),
        ("settling", 60, JoypadButtons.None),
        ("start pressed", 8, JoypadButtons.Start),
        ("playing", 180, JoypadButtons.None),
        ("holding left", 40, JoypadButtons.Left),
        ("playing on", 240, JoypadButtons.None),
    ];

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

    private static CartridgeDocument Authored() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        var json = File.ReadAllText(path: Path.Combine(
            path1: directory!.FullName, path2: "src/Puck.World/Assets/cartridges", path3: "tetris.cgb.cartridge.json"));

        return JsonSerializer.Deserialize<CartridgeDocument>(json: json, options: new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}
