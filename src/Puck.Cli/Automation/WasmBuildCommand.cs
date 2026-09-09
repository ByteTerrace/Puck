
using Puck.World;

namespace Puck.Cli.Automation;

internal static class WasmBuildCommand {
    public static async Task<int> RunAsync(string[] args) {
        if (args is ["-h" or "--help"]) {
            Console.WriteLine(value: "puck wasm build");
            return 0;
        }
        if (args.Length != 0) { throw new ArgumentException(message: "Usage: puck wasm build"); }
        // Locate the checkout at runtime; CI can map compiler source paths to /_/.
        var scriptDirectory = Puck.RepositoryPaths.Resolve(relativePath: "wasm");

        await CliProcess.RunCheckedAsync(root: scriptDirectory, executable: "cargo", arguments: ["build", "--release"]);
        var wasmDirectory = Path.Combine(path1: scriptDirectory, path2: "target", path3: "wasm32-unknown-unknown", path4: "release");
        var modules = (Directory.Exists(path: wasmDirectory)
            ? Directory.GetFiles(path: wasmDirectory, searchPattern: "*.wasm")
            : []);

        if (modules.Length == 0) {
            Console.Error.WriteLine(value: $"Build succeeded but no .wasm file was found under {wasmDirectory}");

            return 1;
        }
        foreach (var module in modules) {
            Console.WriteLine(value: $"Built: {module}");
        }
        var defaultModule = Path.Combine(path1: wasmDirectory, path2: "puck_addon_default.wasm");

        if (!File.Exists(path: defaultModule)) {
            Console.Error.WriteLine(value: "puck_addon_default.wasm was not among the built modules; cannot refresh Puck.World.");

            return 1;
        }
        var targetPath = Path.GetFullPath(
            path: Path.Combine(scriptDirectory, "..", "src", "Puck.World", "Assets", "addons", "puck-addon-default.wasm"));

        File.Copy(destFileName: targetPath, overwrite: true, sourceFileName: defaultModule);
        // Use the host's hash implementation: its pin reads the leading 64 SHA-256 bits little-endian.
        var contentHash = WorldDefinitionFileSource.ComputeContentHash(content: File.ReadAllBytes(path: targetPath));

        Console.WriteLine(value: $"Refreshed: {targetPath}");
        Console.WriteLine(value: $"Content hash (update the hash field of each addons row referencing this module): {contentHash}");
        return 0;
    }
}
