using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A process installs one local document source, and a host that installs none resolves document files
/// only: it refuses a document name whose <c>.puck</c> source stands beside the referrer, by name, rather than reading
/// a document file beside that source or silently missing the source.</summary>
public sealed class LocalDocumentSourceLawTests {
    private sealed class Foreign : IWorldDocumentSource {
        public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
            resolvedName = name;
            content = null;
            reason = "foreign";

            return false;
        }
    }

    private const string Root = """{ "schema": "puck.world.definition.v1", "basis": "moth" }""";

    private static bool ComposeThroughDirectory(TemporaryDirectory directory, out string reason) {
        var root = directory.WriteText(
            name: "root.world.json",
            text: Root
        );

        return WorldDefinitionFileSource.TryComposeDocumentTree(
            documents: WorldDefinitionFileSource.DirectoryDocuments,
            path: root,
            reason: out reason,
            tree: out _
        );
    }

    [Fact]
    public void ThisTestHostInstalledTheComposerAndInstallingItAgainChangesNothing() {
        Assert.Same(
            actual: WorldDefinitionFileSource.LocalDocuments,
            expected: PuckDocumentComposer.Instance
        );

        WorldDefinitionFileSource.UseLocalDocuments(source: PuckDocumentComposer.Instance);

        Assert.Same(
            actual: WorldDefinitionFileSource.LocalDocuments,
            expected: PuckDocumentComposer.Instance
        );
    }
    [Fact]
    public void InstallingADifferentSourceIsRefusedByName() {
        var refusal = Assert.Throws<InvalidOperationException>(testCode: static () => WorldDefinitionFileSource.UseLocalDocuments(source: new Foreign()));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "already installed as Puck.World.Transpiler.Composition.PuckDocumentComposer"
        );
        Assert.Same(
            actual: WorldDefinitionFileSource.LocalDocuments,
            expected: PuckDocumentComposer.Instance
        );
    }
    [Fact]
    public void WithoutAComposerADocumentWithASourceIsRefusedByName() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(
            name: "moth.puck",
            text: "schema: \"puck.world.definition.v1\"\n"
        );

        Assert.False(condition: ComposeThroughDirectory(
            directory: directory,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "no composer is installed"
        );

        // A document file beside the source changes nothing: it is never read in the source's place.
        _ = directory.WriteText(
            name: "moth.world.json",
            text: """{ "schema": "puck.world.definition.v1", "documentId": "stale" }"""
        );

        Assert.False(condition: ComposeThroughDirectory(
            directory: directory,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "no composer is installed"
        );
    }
    [Fact]
    public void WithoutAComposerADocumentFileAloneIsRead() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(
            name: "moth.world.json",
            text: """{ "schema": "puck.world.definition.v1", "documentId": "moth" }"""
        );

        Assert.True(
            condition: ComposeThroughDirectory(
                directory: directory,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
}
