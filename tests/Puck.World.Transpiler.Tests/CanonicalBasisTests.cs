using System.Text;
using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A document reference names the document, and the composer reads the document's <c>.puck</c> source
/// wherever one exists: a <c>.world.json</c> file beside that source is never read in its place, and one standing
/// alone is read.</summary>
public class CanonicalBasisTests {
    private const string Root = """{ "schema": "puck.world.definition.v1", "basis": "avatars/moth", "documentId": "courtyard" }""";

    private static JsonObject Compose(string rootPath) {
        Assert.True(
            condition: PuckDocumentComposer.TryComposeWorldDocument(
                chainBytes: out _,
                composed: out var composed,
                reason: out var reason,
                rootBytes: Encoding.UTF8.GetBytes(s: Root),
                rootResolvedPath: rootPath
            ),
            userMessage: reason
        );

        return composed!;
    }

    [Fact]
    public void AStaleDocumentBesideItsSourceIsNeverRead() {
        using var directory = new TemporaryDirectory();
        var avatars = Directory.CreateDirectory(path: directory.PathOf(name: "avatars")).FullName;

        File.Copy(
            destFileName: Path.Combine(
                path1: avatars,
                path2: "moth.puck"
            ),
            sourceFileName: ShippedWorlds.PathOf(relativePath: "avatars/moth.puck")
        );

        var rootPath = directory.PathOf(name: "courtyard.world.json");
        var fromSource = Compose(rootPath: rootPath);

        // A document file left beside the source from an earlier compile, carrying a member the source never wrote.
        File.WriteAllText(
            contents: """{ "schema": "puck.world.definition.v1", "documentId": "stale", "stale": true }""",
            path: Path.Combine(
                path1: avatars,
                path2: "moth.world.json"
            )
        );

        var besideStale = Compose(rootPath: rootPath);

        Assert.True(
            condition: JsonNode.DeepEquals(
                node1: fromSource,
                node2: besideStale
            ),
            userMessage: "a document file beside its source was read in the source's place"
        );
        Assert.False(condition: besideStale.ContainsKey(propertyName: "stale"));
    }
    [Fact]
    public void ADocumentWithNoSourceIsReadFromItsDocumentFile() {
        using var directory = new TemporaryDirectory();
        var avatars = Directory.CreateDirectory(path: directory.PathOf(name: "avatars")).FullName;

        File.WriteAllText(
            contents: """{ "schema": "puck.world.definition.v1", "documentId": "moth", "authored": true }""",
            path: Path.Combine(
                path1: avatars,
                path2: "moth.world.json"
            )
        );

        Assert.True(condition: Compose(rootPath: directory.PathOf(name: "courtyard.world.json")).ContainsKey(propertyName: "authored"));
    }
    [Fact]
    public void AReferenceSpelledAsAFileIsRefusedByName() {
        using var directory = new TemporaryDirectory();

        Assert.False(condition: PuckDocumentComposer.TryComposeWorldDocument(
            chainBytes: out _,
            composed: out _,
            reason: out var reason,
            rootBytes: Encoding.UTF8.GetBytes(s: """{ "schema": "puck.world.definition.v1", "basis": "avatars/moth.world.json" }"""),
            rootResolvedPath: directory.PathOf(name: "courtyard.world.json")
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "'avatars/moth.world.json' names a file; a document reference names the document ('avatars/moth')"
        );
    }
}
