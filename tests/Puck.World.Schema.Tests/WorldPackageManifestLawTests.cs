using Puck.Testing;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>An asset package's manifest names its entry document, and <c>--world &lt;folder&gt;</c> boots that entry:
/// the manifest reads strictly, the entry resolves to its source beside the manifest before its document, and every
/// way a manifest can fail to name a document beside it is refused by name.</summary>
public sealed class WorldPackageManifestLawTests {
    private const string Valid = """{ "schema": "puck.asset-package.v1", "id": "debug", "name": "Debug", "entry": "debug" }""";

    private static string Refusal(string manifest, string? entryFile = null) {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: WorldPackageManifest.FileName, text: manifest);

        if (entryFile is not null) {
            _ = directory.WriteText(name: entryFile, text: "{}");
        }

        Assert.False(condition: WorldPackageManifest.TryResolveEntry(
            entryPath: out var entryPath,
            manifest: out var read,
            path: directory.RootPath,
            reason: out var reason
        ));
        Assert.Null(@object: read);
        Assert.Empty(collection: entryPath);

        return reason;
    }

    [Fact]
    public void AFolderAndItsManifestResolveTheEntrySourceBeforeItsDocument() {
        using var directory = new TemporaryDirectory();
        var manifest = directory.WriteText(name: WorldPackageManifest.FileName, text: Valid);
        var document = directory.WriteText(name: "debug.world.json", text: "{}");

        Assert.True(condition: WorldPackageManifest.IsPackagePath(path: directory.RootPath));
        Assert.True(condition: WorldPackageManifest.IsPackagePath(path: manifest));
        Assert.False(condition: WorldPackageManifest.IsPackagePath(path: document));
        Assert.True(
            condition: WorldPackageManifest.TryResolveEntry(entryPath: out var compiled, manifest: out var read, path: directory.RootPath, reason: out var reason),
            userMessage: reason
        );
        Assert.Equal(expected: new WorldPackageManifest(Entry: "debug", Id: "debug", Name: "Debug"), actual: read);
        Assert.Equal(expected: Path.GetFullPath(path: document), actual: compiled);

        var source = directory.WriteText(name: "debug.puck", text: "");

        Assert.True(
            condition: WorldPackageManifest.TryResolveEntry(entryPath: out var authored, manifest: out _, path: manifest, reason: out reason),
            userMessage: reason
        );
        Assert.Equal(expected: Path.GetFullPath(path: source), actual: authored);
    }
    [Fact]
    public void AManifestThatNamesNoDocumentBesideItIsRefusedByName() {
        using (var empty = new TemporaryDirectory()) {
            Assert.False(condition: WorldPackageManifest.TryResolveEntry(entryPath: out _, manifest: out _, path: empty.RootPath, reason: out var missing));
            Assert.Contains(expectedSubstring: "no package manifest", actualString: missing);
        }

        Assert.Contains(expectedSubstring: "neither debug.puck nor debug.world.json", actualString: Refusal(manifest: Valid));
        Assert.Contains(expectedSubstring: "cannot be read", actualString: Refusal(manifest: "{"));
        Assert.Contains(expectedSubstring: "is not a JSON object", actualString: Refusal(manifest: "[]"));
        Assert.Contains(expectedSubstring: "carries 'version'", actualString: Refusal(manifest: """{ "schema": "puck.asset-package.v1", "id": "debug", "name": "Debug", "entry": "debug", "version": "1" }"""));
        Assert.Contains(expectedSubstring: "'id' is not a string", actualString: Refusal(manifest: """{ "schema": "puck.asset-package.v1", "id": 1, "name": "Debug", "entry": "debug" }"""));
        Assert.Contains(expectedSubstring: "names no 'name'", actualString: Refusal(manifest: """{ "schema": "puck.asset-package.v1", "id": "debug", "entry": "debug" }"""));
        Assert.Contains(expectedSubstring: "declares schema 'puck.product.v1'", actualString: Refusal(manifest: """{ "schema": "puck.product.v1", "id": "debug", "name": "Debug", "entry": "debug" }"""));
        Assert.Contains(expectedSubstring: "names a file", actualString: Refusal(manifest: """{ "schema": "puck.asset-package.v1", "id": "debug", "name": "Debug", "entry": "debug.puck" }""", entryFile: "debug.puck"));
        Assert.Contains(expectedSubstring: "leaves the package", actualString: Refusal(manifest: """{ "schema": "puck.asset-package.v1", "id": "debug", "name": "Debug", "entry": "../debug" }"""));
    }
}
