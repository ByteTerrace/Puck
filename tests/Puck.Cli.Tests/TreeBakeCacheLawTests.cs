using System.Globalization;
using System.Text.RegularExpressions;
using Puck.Testing;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a <c>puck compile --tree</c> run given a <c>--bake-cache</c> reads each creation bake
/// from that content-addressed cache and keeps there every bake it makes, so a second run over unchanged sources bakes
/// nothing, a run after one prototype changed bakes that prototype alone, and the bake pack is byte for byte what a run
/// without the cache writes. The game's build hands its tree run a cache under the project's intermediate directory
/// (<c>build/WorldAssets.targets</c>).</summary>
public sealed partial class TreeBakeCacheLawTests {
    private const string Pip = """{ "id": "pip", "document": { "schema": "puck.creation.v1", "name": "pip", "palette": [{ "color": "#CC3322", "emissive": 0, "specular": 0, "roughness": 0 }], "shapes": [{ "id": 0, "name": "pip", "type": "Sphere", "position": [0, 0.5, 0], "rotation": [0, 0, 0, 1], "scale": [0.5, 0.5, 0.5], "material": 0, "blend": "Union" }] } }""";
    private const string Block = """{ "id": "block", "document": { "schema": "puck.creation.v1", "name": "block", "palette": [{ "color": "#3355CC", "emissive": 0, "specular": 0, "roughness": 0 }], "shapes": [{ "id": 0, "name": "block", "type": "Box", "position": [0, 0.4, 0], "rotation": [0, 0, 0, 1], "scale": [0.8, 0.8, 0.8], "material": 0, "blend": "Union" }] } }""";

    [GeneratedRegex(pattern: @"Wrote bake pack '[^']*' \([^;]*; its bake cache baked (?<baked>\d+) creations and refused (?<refused>\d+)")]
    private static partial Regex PackLine();
    private static string World(string id, params string[] prototypes) =>
        $$"""{ "documentId": "{{id}}", "schema": "puck.world.definition.v1", "prototypes": [{{string.Join(separator: ", ", values: prototypes)}}] }""";
    // Runs the tree into `output`, through `cache` when one is named, and returns the creations the run baked or refused.
    private static int Run(string tree, string output, string? cache) {
        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            tree,
            "--output",
            output,
            .. ((cache is null) ? (string[])[] : ["--bake-cache", cache]),
            .. Directory.EnumerateFiles(path: tree, searchOption: SearchOption.AllDirectories, searchPattern: "*.world.json").Order(comparer: StringComparer.Ordinal),
        ]));

        Assert.True(condition: (exitCode == 0), userMessage: log);

        var line = PackLine().Match(input: log);

        Assert.True(condition: line.Success, userMessage: log);

        return (int.Parse(provider: CultureInfo.InvariantCulture, s: line.Groups["baked"].Value) + int.Parse(provider: CultureInfo.InvariantCulture, s: line.Groups["refused"].Value));
    }

    [Fact]
    public void AnUnchangedCreationIsBakedOnceAcrossRunsAndThePackIsTheSame() {
        using var directory = new TemporaryDirectory();
        var tree = directory.PathOf(name: "worlds");
        var cache = directory.PathOf(name: "bakes");

        // Two worlds share pip, so the tree names two creations.
        directory.WriteText(name: "worlds/east.world.json", text: World("east", Pip, Block));
        directory.WriteText(name: "worlds/west.world.json", text: World("west", Pip));

        Assert.Equal(actual: Run(cache: cache, output: directory.PathOf(name: "first"), tree: tree), expected: 2);
        Assert.Equal(actual: Run(cache: cache, output: directory.PathOf(name: "second"), tree: tree), expected: 0);
        Assert.Equal(actual: Run(cache: null, output: directory.PathOf(name: "uncached"), tree: tree), expected: 2);

        var pack = File.ReadAllBytes(path: directory.PathOf(name: $"first/{WorldBakePack.FileName}"));

        Assert.Equal(actual: File.ReadAllBytes(path: directory.PathOf(name: $"second/{WorldBakePack.FileName}")), expected: pack);
        Assert.Equal(actual: File.ReadAllBytes(path: directory.PathOf(name: $"uncached/{WorldBakePack.FileName}")), expected: pack);

        // One prototype changes: only its new key is baked.
        directory.WriteText(name: "worlds/east.world.json", text: World("east", Pip, Block.Replace(comparisonType: StringComparison.Ordinal, newValue: "[0.6, 0.8, 0.6]", oldValue: "[0.8, 0.8, 0.8]")));

        Assert.Equal(actual: Run(cache: cache, output: directory.PathOf(name: "edited"), tree: tree), expected: 1);
        Assert.Equal(actual: Run(cache: null, output: directory.PathOf(name: "edited-uncached"), tree: tree), expected: 2);
        Assert.Equal(
            actual: File.ReadAllBytes(path: directory.PathOf(name: $"edited/{WorldBakePack.FileName}")),
            expected: File.ReadAllBytes(path: directory.PathOf(name: $"edited-uncached/{WorldBakePack.FileName}"))
        );
    }
    [Fact]
    public void TheTreeCheckReadsNoBakeCache() {
        using var directory = new TemporaryDirectory();

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            directory.PathOf(name: "worlds"),
            "--output",
            directory.PathOf(name: "out"),
            "--check",
            "--bake-cache",
            directory.PathOf(name: "bakes"),
            directory.WriteText(name: "worlds/field.world.json", text: World("field")),
        ]));

        Assert.NotEqual(actual: exitCode, expected: 0);
        Assert.Contains(actualString: log, comparisonType: StringComparison.Ordinal, expectedSubstring: "reads no --bake-cache");
    }
}
