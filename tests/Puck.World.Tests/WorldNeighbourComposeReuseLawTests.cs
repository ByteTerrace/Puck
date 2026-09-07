using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The serialized home for every law that reads <see cref="WorldDefinitionFileSource"/>'s process-wide composition
/// accounting. The counts belong to the process, not to a test, so a class composing a document beside one of these
/// laws moves the numbers it is asserting on. Runs one class at a time, apart from every parallel collection.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class DocumentCompositionCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(DocumentCompositionCollection.Name)]</c>.</summary>
    public const string Name = "document-composition";
}
/// <summary>
/// Proves the composed-document reuse behind a quilt boot: a shard whose basis, whose adjacency neighbours and whose
/// derived corners all name one island document merges that document exactly once per process; the reused image is
/// the same value a fresh merge produces, byte for byte, so nothing about reuse can move a state hash; an edit
/// anywhere in a held image's chain is never served from the image; and a held image never stretches the
/// composition-depth rule for a reader standing deeper in a chain than the reader that first composed it.
/// </summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class WorldNeighbourComposeReuseLawTests {
    private static string IslandPath => Path.GetFullPath(path: Path.Combine(
        path1: AuthoredGameFixtures.Root,
        path2: "src/Puck.World/Assets/worlds/puck.world.json"
    ));
    private static string ShardsDirectory => Path.GetFullPath(path: Path.Combine(
        path1: AuthoredGameFixtures.Root,
        path2: "src/Puck.World/Assets/worlds/shards"
    ));

    private static string ShardPath(string name) => Path.Combine(
        path1: ShardsDirectory,
        path2: name
    );
    private static WorldFileNeighbourResolver ResolverBesideShards() => new(baseDirectory: () => ShardsDirectory);

    [Fact]
    public void AShardWhoseBasisAndNeighboursNameOneIsland_MergesEachDocumentExactlyOnce() {
        WorldDefinitionFileSource.ForgetComposedDocuments();

        var shard = ShardPath(name: "quilt-nw.world.json");

        Assert.False(condition: WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: IslandPath));
        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(
            definition: out _,
            neighbours: ResolverBesideShards(),
            path: shard,
            reason: out var reason
        ), userMessage: reason);

        // The shard names the island as its own basis, again as a bare adjacency neighbour, and once more through
        // every derived corner the adjacency proof walks — so a merge per reach would be several merges of one
        // document. One merge per distinct document is what "composes it once" means, and it is the whole claim:
        // the count of merges performed equals the count of documents held.
        Assert.True(condition: WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: IslandPath));
        Assert.True(
            condition: (WorldDefinitionFileSource.DocumentCompositionsShared > 0L),
            userMessage: "a shard boot reaches the same documents several times; none of those reaches merged again"
        );
        Assert.Equal(
            expected: ((long)WorldDefinitionFileSource.ComposedDocumentsHeld),
            actual: WorldDefinitionFileSource.DocumentsComposed
        );
    }
    [Fact]
    public void ANeighbourResolution_NamesWhetherItsDocumentWasShared() {
        WorldDefinitionFileSource.ForgetComposedDocuments();

        var resolver = ResolverBesideShards();
        var first = resolver.Resolve(document: "../puck.world.json");
        var second = resolver.Resolve(document: "../puck.world.json");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Resolved, actual: first.Kind);
        Assert.Equal(expected: WorldNeighbourResolutionKind.Resolved, actual: second.Kind);
        Assert.False(condition: first.Shared, userMessage: "the first resolution of a document this process has not composed merged it itself");
        Assert.True(condition: second.Shared, userMessage: "the second resolution of the same document came from the image the first one left");
    }
    [Fact]
    public void AReusedCompose_SerializesIdenticallyToAFreshOne() {
        WorldDefinitionFileSource.ForgetComposedDocuments();

        var shard = ShardPath(name: "quilt-ne.world.json");

        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(
            definition: out var fresh,
            neighbours: ResolverBesideShards(),
            path: shard,
            reason: out var freshReason
        ), userMessage: freshReason);
        Assert.True(condition: WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: Path.GetFullPath(path: shard)));
        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(
            definition: out var reused,
            neighbours: ResolverBesideShards(),
            path: shard,
            reason: out var reusedReason
        ), userMessage: reusedReason);

        // The composed definition is the same value either way, so every hash taken over it — a content pin, a
        // state hash, a neighbour proof — reads the same whether the document was merged here or reused.
        Assert.Equal(
            expected: WorldDefinitionSerialization.Serialize(definition: fresh!),
            actual: WorldDefinitionSerialization.Serialize(definition: reused!)
        );
    }
    [Fact]
    public void AnEditedBasis_IsNeverServedFromAHeldImage() {
        using var files = new TempWorldDirectory();

        var root = files.WriteText(
            name: "root.world.json",
            text: /*lang=json*/ """{ "basis": "basis.world.json" }"""
        );

        _ = files.WriteText(
            name: "basis.world.json",
            text: /*lang=json*/ """{ "schema": "puck.world.def.v1", "name": "first" }"""
        );

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(
            path: root,
            reason: out var firstReason,
            tree: out var first
        ), userMessage: firstReason);
        Assert.Equal(
            actual: ((string?)first!["name"]),
            expected: "first"
        );

        // Only the basis moves; the document the reader names still holds byte-for-byte what it held. A reuse
        // keyed on the named document's own bytes alone would answer with the stale merge.
        _ = files.WriteText(
            name: "basis.world.json",
            text: /*lang=json*/ """{ "schema": "puck.world.def.v1", "name": "second" }"""
        );

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(
            path: root,
            reason: out var secondReason,
            tree: out var second
        ), userMessage: secondReason);
        Assert.Equal(
            actual: ((string?)second!["name"]),
            expected: "second"
        );
    }
    [Fact]
    public void AHeldImage_NeverStretchesTheCompositionDepthRule() {
        using var files = new TempWorldDirectory();

        // A chain exactly as long as the rule admits: MaxChainDepth documents, the last one flat.
        for (var index = 0; (index < (WorldDocumentBasis.MaxChainDepth - 1)); index++) {
            _ = files.WriteText(
                name: $"link{index}.world.json",
                text: $$"""{ "basis": "link{{(index + 1)}}.world.json" }"""
            );
        }

        _ = files.WriteText(
            name: $"link{(WorldDocumentBasis.MaxChainDepth - 1)}.world.json",
            text: /*lang=json*/ """{ "schema": "puck.world.def.v1", "name": "tail" }"""
        );

        var head = Path.Combine(
            path1: files.RootPath,
            path2: "link0.world.json"
        );

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(
            path: head,
            reason: out var headReason,
            tree: out _
        ), userMessage: headReason);

        // One document above the head pushes the SAME subtree one link past the rule. The image the head left
        // behind carries how far it reaches, so it declines to answer here and the walk refuses by name — a reuse
        // that ignored its own reach would compose a chain the rule forbids.
        var above = files.WriteText(
            name: "above.world.json",
            text: /*lang=json*/ """{ "basis": "link0.world.json" }"""
        );

        Assert.False(condition: WorldDefinitionFileSource.TryComposeDocumentTree(
            path: above,
            reason: out var aboveReason,
            tree: out _
        ));
        Assert.Contains(
            actualString: aboveReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds {WorldDocumentBasis.MaxChainDepth} documents"
        );
    }
}
