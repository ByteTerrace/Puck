using System.CommandLine;
using Puck.Assets;

namespace Puck.Cli.Automation;

/// <summary>Exports source-backed Rust guests through the runtime's counted derivation cache.</summary>
public static class WasmBuildCommand {
    private static int Run() {
        var root = Puck.RepositoryPaths.RequireRoot();
        var workspace = Path.Combine(root, "wasm");
        var builds = WasmGuestBuild.ReadBuilds(workspace);
        foreach (var build in builds) {
            var bytes = build.GetImage(workspace);
            foreach (var output in build.Outputs) {
                Puck.Abstractions.AtomicFile.WriteAllBytes(bytes: bytes, path: Path.Combine(root, output));
                Console.WriteLine($"{output} {Puck.Assets.ContentPin.Compute(bytes)}");
            }
        }
        Console.WriteLine($"wasm build: exported {builds.Count} source-backed variants; outputs are ignored caches.");
        return 0;
    }

    /// <summary>Creates the wasm build export command.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var build = new Command("build", "Export the declared Rust guest variants from their counted cache.");
        build.SetAction(_ => Run());
        return new Command("wasm", "Build source-backed Rust addon guests.") { build };
    }
}
