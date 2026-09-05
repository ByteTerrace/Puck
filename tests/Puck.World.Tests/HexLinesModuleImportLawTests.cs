using System.Numerics;

using Xunit;

using Puck.Maths;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>games/hexlines.world.json</c> is a self-contained, placement-addressed module any host can import and
/// position with one restated placement — the same contract <see cref="ChessModuleImportLawTests"/> proves for chess.
/// The board is a radius-4 hexagonal disk of 61 pointy-top tiles on a hexagonal table with two stone trays; every
/// tile, tray, and stone composes over <c>hexTable</c>, and the <c>hexLinesBoard</c> topology's origin is the board's
/// centre on the table's top. Cell geometry follows one convention, shared with the engine: a cell is a
/// <see cref="HexagonalCoordinate"/> <c>(Q, R)</c> in the Eisenstein basis, its centre on the board's XZ plane at the
/// origin plus <c>cellSize · (Q − R/2, 0, R·√3/2)</c> (so +X is the direction-0 neighbour and <c>cellSize</c> is the
/// centre-to-centre spacing), and cell index <c>i</c> is <see cref="HexagonalIndex"/>'s ring order. The tiles are
/// checked against that formula computed HERE from <see cref="HexagonalIndex"/> itself, never against a second copy of
/// the generated positions. Loads <c>Fixtures/minimal-hexlines-host.world.json</c>, a MINIMAL host (standard.basis
/// plus the substrate sections the garden itself authors) that imports the fragment and restates <c>hexTable</c> at
/// <c>[20, -0.5, -12]</c> — a different position than the garden's own <c>[-16, -0.5, 5]</c>; the host restates the
/// topology's origin beside it, since a Hex topology cannot yet anchor through a placement's <c>board</c> facet the
/// way chess's Grid does. Every stone resolves through <see cref="WorldPopulation.BodyForPlacementOrdinal"/>, never a
/// literal body index.
/// </summary>
public sealed class HexLinesModuleImportLawTests {
    private const string TopologyName = "hexLinesBoard";
    private const int Radius = 4;
    private const int CellCount = 61; // 1 + 3·4·5
    private const float CellSize = 0.2f;
    private const float TableTop = 1.3f; // the board plane, local to hexTable
    private const float TileCentre = 1.305f; // TableTop plus the tile's half thickness
    private const int StonesPerTray = 15;
    private const float TrayHalfLength = 0.55f;
    private const float TrayHalfDepth = 0.25f;

    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static WorldDefinition Load(params string[] segments) {
        var path = Path.Combine([RepoRoot(), .. segments]);

        Assert.True(WorldDefinitionLoader.TryLoadFile(path, out var definition, out var reason), reason);

        return definition!;
    }

    private static WorldDefinition LoadGarden() => Load("src", "Puck.World", "Assets", "worlds", "puck.world.json");
    private static WorldDefinition LoadMinimalHost() => Load("tests", "Puck.World.Tests", "Fixtures", "minimal-hexlines-host.world.json");

    private static WorldPlacement Placement(WorldDefinition definition, string id) {
        var placement = definition.Placements.SingleOrDefault(p => string.Equals(a: p.Id, b: id, comparisonType: StringComparison.Ordinal));

        Assert.NotNull(placement);

        return placement!;
    }

    private static Vector3 WorldPosition(WorldDefinition definition, string id) =>
        WorldDefinitionRows.ResolvedFrame(definition: definition, placement: Placement(definition, id)).Position;

    // The one convention: cell (Q, R) sits at the board origin plus cellSize · (Q − R/2, 0, R·√3/2).
    private static Vector3 CellCentre(Vector3 origin, HexagonalCoordinate cell) => origin + new Vector3(
        x: (CellSize * (cell.Q - (cell.R / 2f))),
        y: 0f,
        z: (CellSize * cell.R * (MathF.Sqrt(3f) / 2f))
    );

    private static void AssertNear(Vector3 expected, Vector3 actual, string what) {
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"{what}: expected {expected}, was {actual}");
    }

    private static HashSet<string> RowKeys(WorldDefinition definition, string rowName) {
        var row = WorldDefinitionRows.FindStateRow(definition.State, rowName);

        Assert.NotNull(row);

        return [.. (row!.Cells ?? []).Select(cell => cell.Key.Value)];
    }

    private static HashSet<string> StoneIds(string tray) => [.. Enumerable.Range(0, StonesPerTray).Select(n => $"{tray}-{n}")];

    // Every tile composes over hexTable at the ring-ordered cell's own centre, in BOTH hosts: the garden's own
    // position and the minimal host's restated one — the tile positions are authored LOCAL, so one restated placement
    // moves the whole board.
    private static void AssertTilesFollowTheTable(WorldDefinition definition) {
        var table = Placement(definition, "hexTable");
        var tableFrame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: table);
        var boardCentre = (tableFrame.Position + new Vector3(0f, TileCentre, 0f));

        Assert.Null(table.Parent);
        Assert.Equal(0f, tableFrame.YawDegrees);

        for (var index = 0; (index < CellCount); index++) {
            var tile = Placement(definition, $"hexTile-{index}");
            var cell = new HexagonalIndex(value: index);

            Assert.Equal("hexTable", tile.Parent);
            Assert.True(cell.Radius <= Radius);
            AssertNear(CellCentre(boardCentre, cell.ToCoordinate()), WorldPosition(definition, tile.Id), $"hexTile-{index}");
        }

        // The disk is COMPLETE rings and nothing more: index 61 would begin ring 5.
        Assert.Equal(Radius + 1, new HexagonalIndex(value: CellCount).Radius);
        Assert.DoesNotContain(definition.Placements, p => string.Equals(a: p.Id, b: $"hexTile-{CellCount}", comparisonType: StringComparison.Ordinal));
    }

    // The topology is a radius-4 hex disk whose origin is the board's centre on the table's top — the anchor law a
    // Grid board facet performs for chess, held here by authored coincidence in each host until Hex boards anchor.
    private static void AssertTopologyIsCentredOnTheTable(WorldDefinition definition) {
        var compiled = WorldTopologyCompilation.Find(definition: definition, name: TopologyName);

        Assert.NotNull(compiled);
        Assert.Equal(WorldTopologyKind.Hex, compiled!.Kind);
        Assert.Equal(CellCount, compiled.CellCount);
        Assert.Equal(CellSize, (float)(double)compiled.CellSize, precision: 4);

        var expected = (WorldPosition(definition, "hexTable") + new Vector3(0f, TableTop, 0f));
        var origin = new Vector3((float)(double)compiled.Origin.X, (float)(double)compiled.Origin.Y, (float)(double)compiled.Origin.Z);

        AssertNear(expected, origin, "hexLinesBoard origin");

        var occupancy = WorldDefinitionRows.FindStateRow(definition.State, "hexBoard");

        Assert.NotNull(occupancy);
        Assert.True(occupancy!.EffectiveDomain is WorldStateDomain.CellsOf { Topology: TopologyName });
        Assert.Equal([.. Enumerable.Range(0, CellCount).Select(n => n.ToString())], RowKeys(definition, "hexBoard"));
    }

    [Fact]
    public void GardenImportsSixtyOneTilesInRingOrderOverTheTable() {
        var definition = LoadGarden();

        Assert.Equal(new Vector3(-16f, -0.5f, 5f), WorldPosition(definition, "hexTable"));
        AssertTilesFollowTheTable(definition);
        AssertTopologyIsCentredOnTheTable(definition);
    }

    [Fact]
    public void MinimalHostRestatesTheTableAndTheBoardFollows() {
        var definition = LoadMinimalHost();

        Assert.Equal(new Vector3(20f, -0.5f, -12f), WorldPosition(definition, "hexTable"));
        AssertTilesFollowTheTable(definition);
        AssertTopologyIsCentredOnTheTable(definition);
    }

    // The per-stone rows are keyed by PLACEMENT ID (hexStoneLight-0..14, hexStoneDark-0..14) — never by body index,
    // which is an artefact of wherever WorldPopulation seats inhabited placements — and those keys are exactly the
    // declared stone placements, which are exactly the bodies the real server seats.
    [Fact]
    public void StoneRowsAreKeyedByTheDeclaredStonePlacements() {
        var definition = LoadGarden();
        var declared = definition.Placements.Where(p => (p.Id.StartsWith("hexStone", StringComparison.Ordinal) && (p.Inhabit is not null))).Select(p => p.Id).ToHashSet();
        var expected = StoneIds("hexStoneLight").Union(StoneIds("hexStoneDark")).ToHashSet();

        Assert.Equal(expected, declared);
        Assert.Equal(expected, RowKeys(definition, "hexStoneCell"));
        Assert.Equal(expected, RowKeys(definition, "hexStoneCode"));

        using var fixture = Fixtures.FreshServer(definition: definition);
        var seated = new HashSet<string>();

        for (var index = 0; (index < fixture.Server.Population.Capacity); index++) {
            if (fixture.Server.Population.InhabitantPlacementId(index) is { } placementId && placementId.StartsWith("hexStone", StringComparison.Ordinal)) {
                seated.Add(placementId);
            }
        }

        Assert.Equal(expected, seated);

        // Control: a body-index-shaped key set is NOT what the rows declare.
        Assert.NotEqual(Enumerable.Range(96, 30).Select(n => n.ToString()).ToHashSet(), RowKeys(definition, "hexStoneCell"));
    }

    // Stones are rigid bodies: spawned above their tray, they DROP through the real server's own contact solve and
    // come to rest inside that tray's well — on the tray, not through it, and never in the other tray. Resolved in the
    // minimal host at the restated table, so the settle rides the composed frames, not the garden's own coordinates.
    [Fact]
    public void StonesSettleInsideTheirOwnTrays() {
        var definition = LoadMinimalHost();

        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var tick = 0; (tick < 400); tick++) {
            fixture.Step();
        }

        var placements = fixture.Server.Definition.Placements;
        var tableTop = (WorldPosition(definition, "hexTable").Y + TableTop);

        foreach (var (tray, ids) in new[] { ("hexTrayLight", StoneIds("hexStoneLight")), ("hexTrayDark", StoneIds("hexStoneDark")) }) {
            var trayCentre = WorldPosition(definition, tray);

            foreach (var id in ids) {
                var ordinal = -1;

                for (var index = 0; (index < placements.Count); index++) {
                    if (string.Equals(a: placements[index].Id, b: id, comparisonType: StringComparison.Ordinal)) {
                        ordinal = index;

                        break;
                    }
                }

                Assert.True(ordinal >= 0, $"'{id}' names no declared placement");

                var bodyIndex = fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal);

                Assert.True(bodyIndex >= 0, $"'{id}' is not inhabited");

                var position = fixture.Server.Body(index: bodyIndex)!.Position;

                Assert.True(MathF.Abs(position.X - trayCentre.X) < TrayHalfLength, $"{id} left its tray along X: {position}");
                Assert.True(MathF.Abs(position.Z - trayCentre.Z) < TrayHalfDepth, $"{id} left its tray along Z: {position}");
                Assert.True(position.Y > tableTop, $"{id} fell through its tray: {position}");
                Assert.True(position.Y < (tableTop + 0.15f), $"{id} never settled: {position}");
            }
        }

        // Control: the two trays sit on opposite sides of the board, so a stone read against the WRONG tray fails
        // the Z check above — the footprint assertion discriminates trays rather than passing on any table point.
        Assert.True(MathF.Abs(WorldPosition(definition, "hexTrayLight").Z - WorldPosition(definition, "hexTrayDark").Z) > (2f * TrayHalfDepth));
    }
}
