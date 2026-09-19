using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Game imports retain their current rules, state, patterns, and topology in authored order.
/// Chess's placement keys and parent frames remain valid when composition changes body ordering.
/// Gameplay laws separately check behavior, so authoring can evolve without reversing changes to a migration fixture.</summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class GardenSplitLawTests {
    private static HashSet<string> KeysAsPlacementIds(WorldDefinition definition, string rowName) {
        var row = WorldDefinitionRows.FindStateRow(
            definition.State,
            rowName
        )!;

        return [.. (row.Cells ?? []).Select(selector: cell => cell.Key.Value)];
    }
    private static WorldDefinition LoadGarden() => AuthoredGameFixtures.Nexus;
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

    // pieceCell/pieceCode are keyed by PLACEMENT ID (piece0..piece31) — the placement-addressed re-authoring: a
    // game's own content never keys itself by body index, which is an artefact of wherever WorldPopulation happens
    // to seat inhabited placements today (see chess.world.json's own remarks and docs/game/design.md's module-
    // convention entry). This checks the declared key set matches the 32 declared piece placements exactly — never
    // where those placements land in the entity table, which the placement:$each/placement-ordinal machinery
    // resolves at runtime rather than at authoring time.
    [Fact]
    public void ChessPieceBodies_MatchDeclaredPiecePlacementIds() {
        var definition = LoadGarden();
        var population = AuthoredGameFixtures.PopulationForIdentityChecks(definition: definition);

        var fromPieceCell = KeysAsPlacementIds(
            definition: definition,
            rowName: "pieceCell"
        );
        var fromPieceCode = KeysAsPlacementIds(
            definition: definition,
            rowName: "pieceCode"
        );
        var declaredPieceIds = definition.Placements.Where(predicate: p => (p.Id.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "piece"
        ) && (p.Inhabit is not null))).Select(selector: p => p.Id).ToHashSet();
        var fromPopulation = new HashSet<string>();

        for (var index = 0; (index < population.Capacity); index++) {
            if (
                (population.InhabitantPlacementId(index: index) is { } placementId) &&
                placementId.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "piece"
            )
            ) {
                fromPopulation.Add(item: placementId);
            }
        }

        Assert.Equal(
            32,
            declaredPieceIds.Count
        );
        Assert.Equal(
            actual: fromPieceCode,
            expected: fromPieceCell
        );
        Assert.Equal(
            actual: fromPieceCell,
            expected: declaredPieceIds
        );
        Assert.Equal(
            actual: fromPopulation,
            expected: declaredPieceIds
        );

        // Control: a body-index-shaped key set is NOT what pieceCell/pieceCode declare — proving this law actually
        // discriminates a stale body-index literal rather than passing on any old set.
        var bodyIndexShaped = Enumerable.Range(
            count: 32,
            start: 74
        ).Select(selector: n => n.ToString()).ToHashSet();

        Assert.NotEqual(
            actual: fromPieceCell,
            expected: bodyIndexShaped
        );
    }
    // Every piece placement composes over 'tabletop', its position/yaw the LOCAL offset the tabletop's composed
    // frame resolves — the placement-parent primitive chess.world.json rides so any host can restate the anchor
    // at a different position and every piece/board-square follows, without touching a single body index.
    [Fact]
    public void ChessPiecesAndBoardSquares_ComposeOverTabletop() {
        var definition = LoadGarden();

        foreach (var placement in definition.Placements) {
            if (
                placement.Id.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "piece"
            ) &&
                (placement.Inhabit is not null)
            ) {
                Assert.Equal(
                    "tabletop",
                    placement.Parent
                );
            } else if (placement.Id.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "boardSquare-"
            )) {
                Assert.Equal(
                    "tabletop",
                    placement.Parent
                );
            }
        }
    }
    [InlineData("chess")]
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
