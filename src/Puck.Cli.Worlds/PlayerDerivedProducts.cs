using Puck.AdvancedGamingBrick;
using Puck.Assets;
using Puck.World;
using System.CommandLine;
using System.Text.Json;

namespace Puck.Cli;

/// <summary>Prewarms the source-backed products runtime loaders consume, into a player package.</summary>
public static class PlayerDerivedProducts {
    /// <summary>The derived products a set of world definitions reference: the AGB BIOS when a machine of the AGB engine
    /// takes the bundled firmware, and each authored Rust guest variant an addon row's module path names.</summary>
    public sealed class Needs {
        private readonly Dictionary<string, WasmGuestBuild> m_guests = new(comparer: StringComparer.Ordinal);

        /// <summary>Gets whether a machine takes the bundled AGB BIOS.</summary>
        public bool AgbBios { get; private set; }
        /// <summary>Gets the Rust guest variants addon rows name, by their first logical output.</summary>
        public IReadOnlyCollection<WasmGuestBuild> Guests => m_guests.Values;

        /// <summary>Notes what one definition references.</summary>
        /// <param name="definition">The definition.</param>
        /// <param name="documentDirectory">The directory beside which its addon module paths resolve.</param>
        public void Note(WorldDefinition definition, string documentDirectory) {
            foreach (var machine in definition.Machines) {
                AgbBios |= (
                    string.Equals(a: machine.Engine, b: AdvancedGamingBrickEngine.EngineId, comparisonType: StringComparison.Ordinal) &&
                    !((machine.Configuration.ValueKind == JsonValueKind.Object) && machine.Configuration.TryGetProperty(propertyName: "firmware", value: out _))
                );
            }
            foreach (var addon in definition.Addons) {
                if (
                    WorldDocumentPaths.TryResolve(documentDirectory: documentDirectory, path: addon.ModulePath, reason: out _, resolved: out var module) &&
                    WasmGuestBuild.TryFind(path: module, build: out var build, workspace: out _)
                ) {
                    m_guests[build!.Outputs[0]] = build;
                }
            }
        }
    }

    /// <summary>Creates the shared player-cache preparation command, which prewarms every product the engine can
    /// derive.</summary>
    /// <returns>The prewarm command.</returns>
    public static Command Create() {
        var output = CliOptions.Output(description: "Player directory receiving the prewarmed derived cache.", required: true);
        var command = new Command("prewarm", "Produce the AGB BIOS and every Rust addon cache for a player build; needs clang and ld.lld, and a Rust toolchain with wasm32-unknown-unknown.") { output };
        command.SetAction(result => {
            Prepare(output: Path.GetFullPath(result.GetRequiredValue(output)), agbBios: true, guests: WasmGuestBuild.ReadBuilds());
            return 0;
        });
        return command;
    }
    /// <summary>Produces the named derived products without copying repository binaries.</summary>
    /// <param name="output">The player's asset or executable directory.</param>
    /// <param name="agbBios">Whether to produce the native AGB BIOS.</param>
    /// <param name="guests">The authored Rust guest variants to produce.</param>
    public static void Prepare(string output, bool agbBios, IReadOnlyCollection<WasmGuestBuild> guests) {
        var cache = Path.Combine(output, DerivedBytes.ShippingDirectoryName);

        if (agbBios) {
            _ = AgbFirmwareCompiler.GetImage(packageDirectory: cache, prewarm: true);
        }
        foreach (var build in guests) {
            _ = build.GetImage(packageDirectory: cache, prewarm: true);
        }
        Console.WriteLine($"Prewarmed {(agbBios ? "the AGB BIOS and " : string.Empty)}{guests.Count} Rust addon guest(s) in {cache.Replace('\\', '/')}.");
    }
}
