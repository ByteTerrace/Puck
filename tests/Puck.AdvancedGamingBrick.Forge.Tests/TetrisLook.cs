using System.Text.Json;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Renders the authored cartridge as text so its look can be judged rather than described.</summary>
/// <remarks>Set <c>PUCK_TETRIS_LOOK=1</c> to write it; <c>PUCK_TETRIS_LOOK_OUT</c> names the file.</remarks>
public sealed class TetrisLook {
    [Fact]
    public void Dump() {
        if (Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_LOOK") is not "1") { return; }

        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) { directory = directory.Parent; }
        var json = File.ReadAllText(path: Path.Combine(path1: directory!.FullName, path2: "src/Puck.World/Assets/cartridges", path3: "tetris.cgb.cartridge.json"));
        var document = JsonSerializer.Deserialize<CartridgeDocument>(json: json, options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var result = new HgbCartridgeCompiler().Compile(document: document);
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "look");

        var art = new System.Text.StringBuilder();
        machine.RunFrames(buttons: JoypadButtons.None, frames: int.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_LOOK_FRAMES"), out var wanted) ? wanted : 200);
        var backdrop = machine.ReadPixel(x: 158, y: 142);
        for (var y = 0; y < 144; ++y) {
            var line = new System.Text.StringBuilder();
            for (var x = 0; x < 160; x += 2) {
                line.Append(value: machine.ReadPixel(x: x, y: y) == backdrop ? ' ' : '#');
            }

            art.AppendLine(value: line.ToString().TrimEnd());
        }

        File.WriteAllText(
            path: Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_LOOK_OUT")
                ?? Path.Combine(path1: Path.GetTempPath(), path2: "tetris-look.txt"),
            contents: art.ToString());
    }
}
