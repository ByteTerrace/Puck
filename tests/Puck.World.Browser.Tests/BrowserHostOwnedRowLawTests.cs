using System.Text;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>CONTRACT UNDER TEST: a host-owned row is one the DOCUMENT declares as served by a named facet, and the
/// browser session installs exactly the set the document marks — it never widens it. A validated <c>cellsOf</c> row
/// carrying no field trait resolves to a discrete topology the arena lays out, so no second marking pass can find a
/// row the document's own <c>MarkHostOwned</c> missed.</summary>
public sealed class BrowserHostOwnedRowLawTests {
    private static WorldDefinition ComposedIsland() {
        var path = Path.Combine(
            RepositoryRoot(),
            "src",
            "Puck.World",
            "Assets",
            "worlds",
            "puck.world.json"
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                path: path,
                tree: out var tree,
                reason: out var composeReason
            ),
            userMessage: composeReason
        );

        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(
            condition: BrowserParser.TryParseAndValidate(
                utf8Json: Encoding.UTF8.GetBytes(s: tree!.ToJsonString()),
                errors: errors,
                deferred: deferred,
                definition: out var definition
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );

        return definition!;
    }
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (
            (directory is not null) &&
            !File.Exists(path: Path.Combine(
            path1: directory.FullName,
            path2: "Puck.slnx"
        ))
        ) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static List<string> HostOwned(WorldDefinition definition) {
        var names = new List<string>();

        foreach (var row in definition.State) {
            if (row.HostOwned) {
                names.Add(item: row.Name.Value);
            }
        }

        return names;
    }

    [Fact]
    public void TheSessionInstallsExactlyTheHostOwnedRowsTheDocumentDeclares() {
        var definition = ComposedIsland();
        var declared = HostOwned(definition: definition);

        Assert.NotEmpty(collection: declared);

        var session = new BrowserSession(definition: definition);

        Assert.Equal(
            actual: HostOwned(definition: session.Definition),
            expected: declared
        );
        foreach (var row in session.Definition.State) {
            Assert.Equal(
                actual: row.HostOwned,
                expected: (row.Field is not null)
            );
        }
    }
    [Fact]
    public void EveryBoardTheDocumentLeavesToTheArenaNamesATopologyTheArenaLaysOut() {
        var definition = ComposedIsland();
        var session = new BrowserSession(definition: definition);
        var catalog = session.Definition.StateCatalog;
        var boards = 0;

        foreach (var row in session.Definition.State) {
            Assert.True(
                condition: catalog.TryResolve(
                    handle: out var handle,
                    lane: StateLane.Document,
                    name: row.Name.Value
                ),
                userMessage: row.Name.Value
            );
            Assert.Equal(
                actual: catalog.Descriptors[handle.Ordinal].HostOwned,
                expected: row.HostOwned
            );

            if (row.EffectiveDomain is not StateDomain.CellsOf board) {
                continue;
            }
            if (row.HostOwned) {
                continue;
            }

            // Every board the document leaves to the arena names a topology the arena can lay out, which is why no
            // second marking pass is needed at install.
            Assert.NotNull(@object: TopologyCompilation.Find(
                name: board.Topology,
                section: session.Definition.StateRaw
            ));
            boards++;
        }

        Assert.NotEqual(
            actual: boards,
            expected: 0
        );
    }
}
