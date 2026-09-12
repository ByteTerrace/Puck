using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Game imports retain their current rules, state, patterns, and topology in authored order.
/// Chess's placement keys and parent frames remain valid when composition changes body ordering.
/// Gameplay laws separately check behavior, so authoring can evolve without reversing changes to a migration fixture.</summary>
public sealed class GardenSplitLawTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    [Theory]
    [InlineData("chess")]
    [InlineData("poker")]
    [InlineData("dominoes")]
    [InlineData("billiards")]
    [InlineData("bowling")]
    [InlineData("tictactoe")]
    [InlineData("hexlines")]
    public void ComposedGardenRetainsEachModulesProgramInOrder(string module) {
        var worlds = Path.Combine(RepoRoot(), "src/Puck.World/Assets/worlds");
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(worlds, "games", module + ".world.json")))!;
        Assert.True(WorldDefinitionFileSource.TryComposeDocumentTree(Path.Combine(worlds, "puck.world.json"), out var composed, out var reason), reason);
        foreach (var path in new[] { "rules", "patterns", "state.world", "state.lattices" }) {
            var expected = (JsonNode?)source;
            var actual = (JsonNode?)composed;
            foreach (var segment in path.Split('.')) {
                expected = expected?[segment];
                actual = actual?[segment];
            }
            if (expected is not JsonArray rows || rows.Count == 0) { continue; }
            var merged = Assert.IsType<JsonArray>(actual);
            var previous = -1;
            foreach (var row in rows) {
                var name = row!["name"]!.GetValue<string>();
                var match = Assert.Single(merged, r => r?["name"]?.GetValue<string>() == name);
                Assert.True(JsonNode.DeepEquals(row, match), $"{module}: {path} '{name}' changed during composition");
                var ordinal = merged.IndexOf(match);
                Assert.True(ordinal > previous, $"{module}: {path} '{name}' moved before its predecessor");
                previous = ordinal;
            }
        }
    }

    private static WorldDefinition LoadGarden() => AuthoredGameFixtures.Nexus;

    [Fact]
    public void HoundIdentitiesBelongToTheGardenAndFitTheirDestinationRows() {
        var garden = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src/Puck.World/Assets/worlds/puck.world.json")))!;
        var rows = garden["state"]!["world"]!.AsArray();
        var identities = Assert.Single(rows, r => r?["name"]?.GetValue<string>() == "houndIdentity")!;
        var hounds = Assert.Single(rows, r => r?["name"]?.GetValue<string>() == "hound")!;
        foreach (var hound in hounds["cells"]!.AsArray()) {
            var key = hound!["key"]!.GetValue<string>();
            var cell = Assert.Single(identities["cells"]!.AsArray(), c => c!["key"]!.GetValue<string>() == key)!;
            var id = cell["value"]!.GetValue<int>();
            Assert.Equal(int.Parse(key), id);
            foreach (var name in new[] { "boneHolder", "reporter" }) {
                var destination = Assert.Single(rows, r => r?["name"]?.GetValue<string>() == name)!;
                Assert.InRange(id, destination["min"]!.GetValue<int>(), destination["max"]!.GetValue<int>());
            }
        }
    }

    private static HashSet<string> KeysAsPlacementIds(WorldDefinition definition, string rowName) {
        var row = WorldDefinitionRows.FindStateRow(definition.State, rowName)!;

        return [.. (row.Cells ?? []).Select(cell => cell.Key.Value)];
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
        var population = AuthoredGameFixtures.PopulationForIdentityChecks(definition);

        var fromPieceCell = KeysAsPlacementIds(definition, "pieceCell");
        var fromPieceCode = KeysAsPlacementIds(definition, "pieceCode");
        var declaredPieceIds = definition.Placements.Where(p => (p.Id.StartsWith("piece", StringComparison.Ordinal) && (p.Inhabit is not null))).Select(p => p.Id).ToHashSet();
        var fromPopulation = new HashSet<string>();

        for (var index = 0; (index < population.Capacity); index++) {
            if (population.InhabitantPlacementId(index) is { } placementId && placementId.StartsWith("piece", StringComparison.Ordinal)) {
                fromPopulation.Add(placementId);
            }
        }

        Assert.Equal(32, declaredPieceIds.Count);
        Assert.Equal(fromPieceCell, fromPieceCode);
        Assert.Equal(declaredPieceIds, fromPieceCell);
        Assert.Equal(declaredPieceIds, fromPopulation);

        // Control: a body-index-shaped key set is NOT what pieceCell/pieceCode declare — proving this law actually
        // discriminates a stale body-index literal rather than passing on any old set.
        var bodyIndexShaped = Enumerable.Range(74, 32).Select(n => n.ToString()).ToHashSet();

        Assert.NotEqual(bodyIndexShaped, fromPieceCell);
    }

    // Every piece placement composes over 'tabletop', its position/yaw the LOCAL offset the tabletop's composed
    // frame resolves — the placement-parent primitive chess.world.json rides so any host can restate the anchor
    // at a different position and every piece/board-square follows, without touching a single body index.
    [Fact]
    public void ChessPiecesAndBoardSquares_ComposeOverTabletop() {
        var definition = LoadGarden();

        foreach (var placement in definition.Placements) {
            if (placement.Id.StartsWith("piece", StringComparison.Ordinal) && (placement.Inhabit is not null)) {
                Assert.Equal("tabletop", placement.Parent);
            } else if (placement.Id.StartsWith("boardSquare-", StringComparison.Ordinal)) {
                Assert.Equal("tabletop", placement.Parent);
            }
        }
    }

}
