using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Game imports retain their current rules, state, patterns, and topology in authored order.</summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class GardenSplitLawTests {
    private static string RepoRoot() {
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

    [InlineData("poker")]
    [InlineData("dominoes")]
    [InlineData("billiards")]
    [InlineData("bowling")]
    [InlineData("tictactoe")]
    [InlineData("hexlines")]
    [Theory]
    public void ComposedGardenRetainsEachModulesProgramInOrder(string module) {
        var worlds = Path.Combine(
            path1: RepoRoot(),
            path2: "src/Puck.World/Assets/worlds"
        );
        var source = JsonNode.Parse(File.ReadAllText(path: Path.Combine(
            path1: worlds,
            path2: "games",
            path3: (module + ".world.json")
        )))!;

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                Path.Combine(
                    path1: worlds,
                    path2: "puck.world.json"
                ),
                out var composed,
                out var reason
            ),
            userMessage: reason
        );
        foreach (var path in new[] { "rules", "patterns", "state.world", "state.lattices" }) {
            var expected = ((JsonNode?)source);
            var actual = ((JsonNode?)composed);

            foreach (var segment in path.Split('.')) {
                expected = expected?[segment];
                actual = actual?[segment];
            }
            if (
                (expected is not JsonArray rows) ||
                (rows.Count == 0)
            ) { continue; }
            var merged = Assert.IsType<JsonArray>(@object: actual);
            var previous = -1;

            foreach (var row in rows) {
                var name = row!["name"]!.GetValue<string>();
                var match = Assert.Single(
                    collection: merged,
                    predicate: r => (r?["name"]?.GetValue<string>() == name)
                );

                Assert.True(
                    condition: JsonNode.DeepEquals(
                        node1: row,
                        node2: match
                    ),
                    userMessage: $"{module}: {path} '{name}' changed during composition"
                );
                var ordinal = merged.IndexOf(item: match);

                Assert.True(
                    condition: (ordinal > previous),
                    userMessage: $"{module}: {path} '{name}' moved before its predecessor"
                );
                previous = ordinal;
            }
        }
    }
    [Fact]
    public void HoundIdentitiesBelongToTheGardenAndFitTheirDestinationRows() {
        var garden = JsonNode.Parse(File.ReadAllText(path: Path.Combine(
            path1: RepoRoot(),
            path2: "src/Puck.World/Assets/worlds/puck.world.json"
        )))!;
        var rows = garden["state"]!["world"]!.AsArray();
        var identities = Assert.Single(
            collection: rows,
            predicate: r => (r?["name"]?.GetValue<string>() == "houndIdentity")
        )!;
        var hounds = Assert.Single(
            collection: rows,
            predicate: r => (r?["name"]?.GetValue<string>() == "hound")
        )!;

        foreach (var hound in hounds["cells"]!.AsArray()) {
            var key = hound!["key"]!.GetValue<string>();
            var cell = Assert.Single(
                collection: identities["cells"]!.AsArray(),
                predicate: c => (c!["key"]!.GetValue<string>() == key)
            )!;
            var id = cell["value"]!.GetValue<int>();

            Assert.Equal(
                int.Parse(s: key),
                id
            );
            foreach (var name in new[] { "boneHolder", "reporter" }) {
                var destination = Assert.Single(
                    collection: rows,
                    predicate: r => (r?["name"]?.GetValue<string>() == name)
                )!;

                Assert.InRange(
                    id,
                    destination["min"]!.GetValue<int>(),
                    destination["max"]!.GetValue<int>()
                );
            }
        }
    }

}
