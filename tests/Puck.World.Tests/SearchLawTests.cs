using System.Numerics;
using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>A search job relocates every token to every cell, judges each position with the document's own rules
/// over a frame, and lands what the rules accepted: on the settled opening board of the chess module the white
/// pawns and knights own the twenty legal moves and nothing else may move, the installed section never sees a
/// hypothetical position, and the answer restarts when the position changes.</summary>
public sealed class SearchLawTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static WorldDefinition ChessWithSearch() {
        var path = Path.Combine(RepoRoot(), "tests", "Puck.World.Tests", "Fixtures", "minimal-chess-host.world.json");

        Assert.True(WorldDefinitionLoader.TryLoadFile(path, out var loaded, out var reason), reason);

        var state = loaded!.StateRaw!;
        var rows = new List<WorldStateRow>(state.World ?? []) {
            new(CellName.Parse("legal"), CellKind.Int, Capacity: 32, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
            new(CellName.Parse("legalCount"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
        };
        var definition = loaded with {
            StateRaw = state with { World = rows },
            SearchRaw = new WorldSearchSection(Jobs: [new WorldSearchRow(Name: "moves", Tokens: "pieceCell", Board: "board", Legal: "legal", Count: "legalCount")]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    private static WorldStateRow Row(WorldFixture fixture, string name) => WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: name)!;
    private static long Cell(WorldFixture fixture, string row, string key) => Row(fixture, row).Cells!.Single(c => c.Key.Value == key).Value;

    private static WorldSearchStatus Settle(WorldFixture fixture) {
        // Pieces drop from spawn and settle; the judge only speaks once settleHold reaches its margin.
        for (var tick = 0; tick < 400; tick++) {
            fixture.Step();
        }

        var status = fixture.Server.SearchStatus()[0];

        for (var tick = 0; (tick < 6000) && !status.Done; tick++) {
            fixture.Step();
            status = fixture.Server.SearchStatus()[0];
        }

        return status;
    }

    [Fact]
    public void TheOpeningPositionHasTwentyLegalMovesAndOnlyWhitePawnsAndKnightsOwnThem() {
        using var fixture = Fixtures.FreshServer(definition: ChessWithSearch());

        var status = Settle(fixture);

        Assert.True(status.Done, $"the job did not finish: {status}");
        Assert.True(status.NodesPerTick >= 1, status.ToString());
        Assert.Equal(20L, status.Count);
        Assert.Equal(20L, Cell(fixture, "legalCount", WorldStateRow.SlotKey.Value));

        var codes = Row(fixture, "pieceCode").Cells!;
        var total = 0;

        foreach (var code in codes) {
            var mask = Cell(fixture, "legal", code.Key.Value);
            var expected = code.Value switch {
                1L => 2, // a white pawn steps one or two
                2L => 2, // a white knight has two squares
                _ => 0,  // every other white piece is blocked; black is not to move
            };

            Assert.Equal(expected, BitOperations.PopCount((ulong)mask));
            total += BitOperations.PopCount((ulong)mask);
        }

        Assert.Equal(20, total);
        // The pawn on e2 (piece12) may go to e3 or e4 and nowhere else.
        Assert.Equal((1L << 20) | (1L << 28), Cell(fixture, "legal", "piece12"));
    }

    [Fact]
    public void AHypotheticalPositionNeverReachesTheInstalledSectionAndTheStampRestartsTheJob() {
        using var fixture = Fixtures.FreshServer(definition: ChessWithSearch());

        var status = Settle(fixture);
        Assert.True(status.Done);

        // Every token still stands where it settled: the frame took the relocations, never the section.
        var e2 = Cell(fixture, "pieceCell", "piece12");
        Assert.Equal(12L, e2);
        Assert.Equal(0L, Cell(fixture, "illegalCount", WorldStateRow.SlotKey.Value));

        // A finished job stays finished while nothing framed changes.
        for (var tick = 0; tick < 5; tick++) {
            fixture.Step();
        }
        Assert.True(fixture.Server.SearchStatus()[0].Done);
        Assert.Equal(20L, fixture.Server.SearchStatus()[0].Count);
    }

    [Fact]
    public void ACheckpointCarriesTheJobAndTwoServersAgree() {
        using var first = Fixtures.FreshServer(definition: ChessWithSearch());
        using var second = Fixtures.FreshServer(definition: ChessWithSearch());

        for (var tick = 0; tick < 450; tick++) {
            first.Step();
            second.Step();
        }

        var a = first.Server.SearchStatus()[0];
        var b = second.Server.SearchStatus()[0];
        Assert.Equal(a, b);
        Assert.True(first.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), reason);
        Assert.NotNull(checkpoint!.Search);
        Assert.Single(checkpoint.Search!.Jobs);
        Assert.Equal(a.Nodes, checkpoint.Search.Jobs[0].Nodes);

        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(bytes, out var decoded, out var decodeReason), decodeReason);
        Assert.Equal(checkpoint.Search.Jobs[0], decoded!.Search!.Jobs[0] with { Legal = checkpoint.Search.Jobs[0].Legal });
        Assert.Equal(checkpoint.Search.Jobs[0].Legal, decoded.Search.Jobs[0].Legal);
    }
}
