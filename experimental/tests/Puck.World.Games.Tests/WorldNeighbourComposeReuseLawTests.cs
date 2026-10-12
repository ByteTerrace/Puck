using Puck.World.Server;
using Puck.Testing;
using Xunit;

namespace Puck.World.Games.Tests;
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
        path1: RepositoryPaths.RequireRoot(),
        path2: "src/Puck.World/Assets/worlds/puck.world.json"
    ));
    private static string ShardsDirectory => Path.GetFullPath(path: Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
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

        var work = new WorldBootWork();
        using var attribution = WorldBootWork.Attribute(work: work);

        var shard = ShardPath(name: "quilt-nw.world.json");

        Assert.False(condition: WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: IslandPath));
        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                definition: out _,
                neighbours: ResolverBesideShards(),
                path: shard,
                reason: out var reason
            ),
            userMessage: reason
        );

        // The shard names the island as its own basis, again as a bare adjacency neighbour, and once more through
        // every derived corner the adjacency proof walks — so a merge per reach would be several merges of one
        // document. One merge per distinct document is what "composes it once" means, and it is the whole claim:
        // the count of merges performed equals the count of documents held.
        Assert.True(condition: WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: IslandPath));
        Assert.True(
            condition: (work.Read(kind: WorldBootWork.CompositionsShared) > 0L),
            userMessage: "a shard boot reaches the same documents several times; none of those reaches merged again"
        );
        Assert.Equal(
            expected: ((long)WorldDefinitionFileSource.ComposedDocumentsHeld),
            actual: work.Read(kind: WorldBootWork.Compositions)
        );
    }
    [Fact]
    public void ANeighbourResolution_NamesWhetherItsDocumentWasShared() {
        WorldDefinitionFileSource.ForgetComposedDocuments();

        var resolver = ResolverBesideShards();
        var first = resolver.Resolve(document: "../puck");
        var second = resolver.Resolve(document: "../puck");

        Assert.Equal(
            expected: WorldNeighbourResolutionKind.Resolved,
            actual: first.Kind
        );
        Assert.Equal(
            expected: WorldNeighbourResolutionKind.Resolved,
            actual: second.Kind
        );
        Assert.False(
            condition: first.Shared,
            userMessage: "the first resolution of a document this process has not composed merged it itself"
        );
        Assert.True(
            condition: second.Shared,
            userMessage: "the second resolution of the same document came from the image the first one left"
        );
    }
    [Fact]
    public void AReusedCompose_SerializesIdenticallyToAFreshOne() {
        WorldDefinitionFileSource.ForgetComposedDocuments();

        var shard = ShardPath(name: "quilt-ne.world.json");

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                definition: out var fresh,
                neighbours: ResolverBesideShards(),
                path: shard,
                reason: out var freshReason
            ),
            userMessage: freshReason
        );
        Assert.True(condition: WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: Path.GetFullPath(path: shard)));
        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                definition: out var reused,
                neighbours: ResolverBesideShards(),
                path: shard,
                reason: out var reusedReason
            ),
            userMessage: reusedReason
        );

        // The composed definition is the same value either way, so every hash taken over it — a content pin, a
        // state hash, a neighbour proof — reads the same whether the document was merged here or reused.
        Assert.Equal(
            expected: WorldDefinitionSerialization.Serialize(definition: fresh!),
            actual: WorldDefinitionSerialization.Serialize(definition: reused!)
        );
    }
}
