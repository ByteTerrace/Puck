using Puck.AdvancedGamingBrick;
using Puck.Assets;
using System.CommandLine;

namespace Puck.Cli;

/// <summary>Prewarms the same source-backed products runtime loaders consume, into a player package.</summary>
public static class PlayerDerivedProducts {
    /// <summary>Creates the shared player-cache preparation command.</summary>
    /// <returns>The prewarm command.</returns>
    public static Command Create() {
        var output = CliOptions.Output(description: "Player directory receiving the prewarmed derived cache.", required: true);
        var command = new Command("prewarm", "Produce the AGB BIOS and Rust addon caches for a player build.") { output };
        command.SetAction(result => Prepare(Path.GetFullPath(result.GetRequiredValue(output))));
        return command;
    }
    /// <summary>Produces the native BIOS and every authored Rust guest variant without copying repository binaries.</summary>
    /// <param name="output">The player's asset or executable directory.</param>
    public static void Prepare(string output) {
        var cache = Path.Combine(output, "derived");
        _ = AgbFirmwareCompiler.GetImage(packageDirectory: cache, prewarm: true);
        foreach (var build in WasmGuestBuild.ReadBuilds()) {
            _ = build.GetImage(packageDirectory: cache, prewarm: true);
        }
        Console.WriteLine($"Prewarmed AGB BIOS and Rust addon guests in {cache.Replace('\\', '/')}.");
    }
}
