using System.Text.Json;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Writes the authored cartridge as a bootable image.</summary>
/// <remarks>Set <c>PUCK_TETRIS_EXPORT</c> to the destination path; without it this does nothing.</remarks>
public sealed class TetrisExport {
    [Fact]
    public void WriteImage() {
        if (Environment.GetEnvironmentVariable(variable: "PUCK_TETRIS_EXPORT") is not { Length: > 0 } destination) {
            return;
        }

        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        var json = File.ReadAllText(path: Path.Combine(
            path1: directory!.FullName, path2: "src/Puck.World/Assets/cartridges", path3: "tetris.cgb.cartridge.json"));
        var document = JsonSerializer.Deserialize<CartridgeDocument>(json: json, options: new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
        })!;

        var result = new HgbCartridgeCompiler().Compile(document: document);
        File.WriteAllBytes(path: destination, bytes: result.Rom);
    }
}
