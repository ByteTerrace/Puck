using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Every relative path a world document authors resolves beside that document (<see cref="WorldDocumentPaths"/>):
/// a loaded file's asset row reads the file beside it and never the executable's copy, a document with no directory
/// refuses a relative row by name, a basis in another directory keeps naming its own files once merged, and a save to
/// another directory re-expresses the paths it writes.</summary>
public sealed class WorldDocumentPathLawTests {
    private const string Patch = "patches/stinger.synth.json";

    private static string World(string patchSource) => CompiledWorldLawTests.World.Replace(
        newValue: patchSource,
        oldValue: "PATCH"
    );
    private static byte[] ShippedPatch() => File.ReadAllBytes(path: PuckPaths.Shipped(relativePath: $"Assets/worlds/{Patch}"));

    [Fact]
    public void ALoadedDocumentReadsItsAssetBesideItAndADirectorylessCopyRefusesByName() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteBytes(bytes: ShippedPatch(), name: $"worlds/{Patch}");

        var path = directory.WriteText(name: "worlds/beside.world.json", text: World(patchSource: Patch));

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(definition: out var loaded, path: path, reason: out var reason),
            userMessage: reason
        );
        Assert.Equal(expected: WorldDocumentPaths.DirectoryOf(documentPath: path), actual: loaded!.DocumentDirectory);
        Assert.False(condition: WorldDefinitionLoader.TryReadPublishable(
            definition: out _,
            json: File.ReadAllText(path: path),
            reason: out var refusal,
            sourceName: "in-memory"
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: $"'{Patch}' is relative, and this document has no directory");
    }
    [Fact]
    public void ARelativeSourceNeverReadsTheExecutablesCopy() {
        using var directory = new TemporaryDirectory();
        var shipped = $"Assets/worlds/{Patch}";

        Assert.True(condition: File.Exists(path: PuckPaths.Shipped(relativePath: shipped)));

        var path = directory.WriteText(name: "alone.world.json", text: World(patchSource: shipped));

        Assert.False(condition: WorldDefinitionLoader.TryLoadFile(definition: out _, path: path, reason: out var refusal));
        Assert.Contains(actualString: refusal, expectedSubstring: $"source '{shipped}' does not exist");
    }
    [Fact]
    public void ABasisInAnotherDirectoryKeepsNamingItsOwnAssetOnceMerged() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteBytes(bytes: ShippedPatch(), name: "base/stinger.synth.json");
        _ = directory.WriteText(name: "base/law.world.json", text: World(patchSource: "stinger.synth.json"));

        var path = directory.WriteText(
            name: "child/child.world.json",
            text: """{ "schema": "puck.world.definition.v1", "basis": "../base/law", "documentId": "child" }"""
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(definition: out var loaded, path: path, reason: out var reason),
            userMessage: reason
        );
        Assert.Equal(expected: "../base/stinger.synth.json", actual: Assert.Single(collection: loaded!.Patches).Source);
    }
    [Fact]
    public void ASaveToAnotherDirectoryReexpressesThePathsItWrites() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteBytes(bytes: ShippedPatch(), name: $"worlds/{Patch}");

        var path = directory.WriteText(name: "worlds/saved.world.json", text: World(patchSource: Patch));

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(definition: out var loaded, path: path, reason: out var reason),
            userMessage: reason
        );

        var elsewhere = directory.PathOf(name: "exports/deep/saved.world.json");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: elsewhere)!);
        _ = WorldDefinitionSerialization.Save(definition: loaded!, path: elsewhere);

        var written = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: elsewhere))!;

        Assert.Equal(expected: $"../../worlds/{Patch}", actual: written["patches"]![0]!["source"]!.GetValue<string>());
        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(definition: out _, path: elsewhere, reason: out var reloaded),
            userMessage: reloaded
        );

        // The control: a save beside the document writes the path as it was authored.
        _ = WorldDefinitionSerialization.Save(definition: loaded!, path: path);
        Assert.Equal(expected: Patch, actual: JsonNode.Parse(utf8Json: File.ReadAllBytes(path: path))!["patches"]![0]!["source"]!.GetValue<string>());
    }
}
