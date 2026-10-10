using System.CommandLine;
using System.Text.Json;
using Puck.World;

namespace Puck.Cli.Automation;

/// <summary>One committed build of a WebAssembly guest crate.</summary>
/// <param name="Crate">The Cargo package name.</param>
/// <param name="Features">The Cargo features the build selects in place of the crate's defaults, or <see langword="null"/>
/// for the defaults.</param>
/// <param name="Outputs">The repository-relative paths, with forward slashes, the built module is written to.</param>
public sealed record WasmGuestBuild(string Crate, IReadOnlyList<string>? Features, IReadOnlyList<string> Outputs) {
    /// <summary>The <c>cargo build</c> arguments that make this build.</summary>
    public IReadOnlyList<string> CargoArguments => [
        "build", "--release", "--package", Crate,
        .. ((Features is { } features) ? (IEnumerable<string>)["--no-default-features", "--features", string.Join(separator: ',', values: features)] : []),
    ];
    /// <summary>The module Cargo writes for the crate, relative to the workspace.</summary>
    public string Module => $"target/wasm32-unknown-unknown/release/{Crate.Replace(newChar: '_', oldChar: '-')}.wasm";
}
/// <summary><c>puck wasm build</c>: builds every committed WebAssembly guest with Cargo and writes each module to the
/// repository paths its crate declares under <c>[package.metadata.puck]</c>.</summary>
public static class WasmBuildCommand {
    private const string Verb = "wasm build";

    /// <summary>Reads every workspace crate's committed builds from <c>cargo metadata --format-version 1 --no-deps</c>:
    /// each entry of a crate's <c>[package.metadata.puck] builds</c> array, which names its <c>outputs</c> and, when it
    /// does not build the crate's default features, its <c>features</c>.</summary>
    /// <param name="metadata">The <c>cargo metadata</c> report.</param>
    /// <returns>The builds, in ordinal order of their first output.</returns>
    /// <exception cref="InvalidDataException">A declared build names no output, or a value is not of its declared type.</exception>
    public static IReadOnlyList<WasmGuestBuild> ReadBuilds(string metadata) {
        using var document = JsonDocument.Parse(json: metadata);
        var builds = new List<WasmGuestBuild>();

        foreach (var package in document.RootElement.GetProperty(propertyName: "packages").EnumerateArray()) {
            var crate = package.GetProperty(propertyName: "name").GetString()!;

            if (
                !package.TryGetProperty(propertyName: "metadata", value: out var declared) ||
                (declared.ValueKind != JsonValueKind.Object) ||
                !declared.TryGetProperty(propertyName: "puck", value: out var puck) ||
                !puck.TryGetProperty(propertyName: "builds", value: out var entries)
            ) {
                continue;
            }

            foreach (var entry in entries.EnumerateArray()) {
                var outputs = Strings(element: entry.GetProperty(propertyName: "outputs"));

                if (outputs.Count == 0) { throw new InvalidDataException(message: $"{crate} declares a build with no outputs."); }

                builds.Add(item: new WasmGuestBuild(
                    Crate: crate,
                    Features: (entry.TryGetProperty(propertyName: "features", value: out var features) ? Strings(element: features) : null),
                    Outputs: outputs
                ));
            }
        }

        return [.. builds.OrderBy(keySelector: static build => build.Outputs[0], comparer: StringComparer.Ordinal)];
    }

    private static List<string> Strings(JsonElement element) => [.. element.EnumerateArray().Select(selector: static item => (item.GetString() ?? throw new InvalidDataException(message: "a build's features and outputs are strings.")))];
    private static async Task<int> RunAsync(CancellationToken cancellationToken) {
        var root = Puck.RepositoryPaths.RequireRoot();
        var workspace = Path.Combine(path1: root, path2: "wasm");
        var builds = ReadBuilds(metadata: await CliProcess.RunCheckedAsync(
            arguments: ["metadata", "--format-version", "1", "--no-deps"],
            cancellationToken: cancellationToken,
            capture: true,
            fileName: "cargo",
            workingDirectory: workspace
        ));
        var declared = builds.SelectMany(selector: static build => build.Outputs).ToHashSet(comparer: StringComparer.Ordinal);
        var listing = CliGit.Run(root, "ls-files", "-z", "--", "wasm/*.wasm", "src/*.wasm");

        if (listing.ExitCode != 0) { throw new InvalidOperationException(message: $"git ls-files failed: {listing.Stderr}"); }

        // A committed guest no crate declares would go stale on the next ABI change with nothing to rebuild it.
        var undeclared = listing.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0').Where(predicate: path => !declared.Contains(item: path)).Order(comparer: StringComparer.Ordinal).ToArray();

        if (undeclared.Length != 0) {
            return CliExit.Refuse(
                verb: Verb,
                what: string.Join(separator: ", ", values: undeclared),
                why: "committed WebAssembly that no crate's [package.metadata.puck] builds names as an output; declare the build that makes it."
            );
        }

        foreach (var build in builds) {
            await CliProcess.RunCheckedAsync(
                arguments: build.CargoArguments,
                cancellationToken: cancellationToken,
                fileName: "cargo",
                workingDirectory: workspace
            );

            var module = File.ReadAllBytes(path: Path.Combine(path1: workspace, path2: build.Module));

            foreach (var output in build.Outputs) {
                var path = Path.Combine(path1: root, path2: output);

                // A module ships as content, so built outputs hold hard links to it: replace it, never write through it.
                Puck.Assets.AtomicFile.WriteAllBytes(bytes: module, path: path);
                // The host's hash: its pin reads the leading 64 SHA-256 bits little-endian.
                Console.WriteLine(value: $"{output} {WorldDefinitionFileSource.ComputeContentHash(content: module)} (cargo {string.Join(separator: ' ', values: build.CargoArguments)})");
            }
        }

        Console.WriteLine(value: $"{Verb}: wrote {declared.Count} committed module(s) from {builds.Count} build(s); update the hash of every addons row that pins a module whose hash moved.");

        return CliExit.Success;
    }

    /// <summary>Creates <c>puck wasm</c> and its <c>build</c> verb.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var build = new Command(
            description: "Build every committed WASM guest with cargo and write each to the paths its crate declares.",
            name: "build"
        );
        var command = new Command(
            description: "Build and refresh the committed WASM guests.",
            name: "wasm"
        ) { build };

        build.SetAction(action: (_, cancellationToken) => RunAsync(cancellationToken: cancellationToken));
        return command;
    }
}
