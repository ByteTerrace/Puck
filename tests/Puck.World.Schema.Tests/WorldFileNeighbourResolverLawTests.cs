using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldFileNeighbourResolver"/> hands back a neighbour whose
/// <c>references[].document</c> locators are spelled from the reader's base directory. A locator is authored
/// relative to the document that names it; the derived-corner walk compares two neighbours' locators for one third
/// document by string and resolves the winner beside the reader, so a neighbour living in another directory must
/// re-express what it authored against the base. A sibling's bare spelling is unchanged.
/// </summary>
public sealed class WorldFileNeighbourResolverLawTests : IDisposable {
    private readonly string m_root;

    public WorldFileNeighbourResolverLawTests() {
        m_root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-neighbour-resolver-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path: Path.Combine(
            path1: m_root,
            path2: "shards"
        ));
    }

    public void Dispose() {
        try {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        } catch (IOException) {
        }
    }

    private void WriteDocument(string relativePath, string documentId, params (string Name, string Document)[] references) {
        var rows = string.Join(
            separator: ",",
            values: references.Select(selector: static reference => $"{{\"name\":\"{reference.Name}\",\"document\":\"{reference.Document}\"}}")
        );

        File.WriteAllText(
            path: Path.Combine(
                path1: m_root,
                path2: relativePath
            ),
            contents: $"{{\"schema\":\"{WorldDefinition.SchemaVersion}\",\"documentId\":\"{documentId}\",\"references\":[{rows}]}}"
        );
    }
    private static string Locator(WorldDefinition definition, string name) =>
        WorldDefinitionRows.FindReference(
            references: definition.References,
            name: name
        )!.Document!;

    [Fact]
    public void A_neighbour_below_the_base_spells_its_own_neighbours_from_the_base() {
        WriteDocument(
            relativePath: "hub.world.json",
            documentId: "hub",
            ("nw", "shards/nw.world.json")
        );
        WriteDocument(
            relativePath: Path.Combine(
                path1: "shards",
                path2: "nw.world.json"
            ),
            documentId: "nw",
            ("ne", "ne.world.json"),
            ("hub", "../hub.world.json")
        );
        WriteDocument(
            relativePath: Path.Combine(
                path1: "shards",
                path2: "ne.world.json"
            ),
            documentId: "ne"
        );

        var resolver = new WorldFileNeighbourResolver(baseDirectory: () => m_root);
        var resolution = resolver.Resolve(document: "shards/nw.world.json");

        Assert.Equal(
            actual: resolution.Kind,
            expected: WorldNeighbourResolutionKind.Resolved
        );

        var neighbour = resolution.Definition!;

        Assert.Equal(
            actual: Locator(
                definition: neighbour,
                name: "ne"
            ),
            expected: "shards/ne.world.json"
        );
        Assert.Equal(
            actual: Locator(
                definition: neighbour,
                name: "hub"
            ),
            expected: "hub.world.json"
        );
        // The re-expressed locator is what the reader resolves next, from the same base.
        Assert.Equal(
            actual: resolver.Resolve(document: Locator(
                definition: neighbour,
                name: "ne"
            )).Kind,
            expected: WorldNeighbourResolutionKind.Resolved
        );
    }
    [Fact]
    public void A_neighbour_above_the_base_spells_its_own_neighbours_from_the_base() {
        WriteDocument(
            relativePath: "hub.world.json",
            documentId: "hub",
            ("nw", "shards/nw.world.json"),
            ("ne", "shards/ne.world.json")
        );
        WriteDocument(
            relativePath: Path.Combine(
                path1: "shards",
                path2: "nw.world.json"
            ),
            documentId: "nw",
            ("hub", "../hub.world.json")
        );

        var shards = Path.Combine(
            path1: m_root,
            path2: "shards"
        );
        var resolution = new WorldFileNeighbourResolver(baseDirectory: () => shards).Resolve(document: "../hub.world.json");

        Assert.Equal(
            actual: resolution.Kind,
            expected: WorldNeighbourResolutionKind.Resolved
        );
        Assert.Equal(
            actual: Locator(
                definition: resolution.Definition!,
                name: "ne"
            ),
            expected: "ne.world.json"
        );
    }
    [Fact]
    public void A_sibling_keeps_its_bare_spelling() {
        WriteDocument(
            relativePath: "a.world.json",
            documentId: "a",
            ("b", "b.world.json")
        );
        WriteDocument(
            relativePath: "b.world.json",
            documentId: "b",
            ("a", "a.world.json")
        );

        var resolution = new WorldFileNeighbourResolver(baseDirectory: () => m_root).Resolve(document: "b.world.json");

        Assert.Equal(
            actual: resolution.Kind,
            expected: WorldNeighbourResolutionKind.Resolved
        );
        Assert.Equal(
            actual: Locator(
                definition: resolution.Definition!,
                name: "a"
            ),
            expected: "a.world.json"
        );
    }
}
