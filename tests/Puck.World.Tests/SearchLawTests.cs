using System.Numerics;
using Xunit;

using Puck.Assets.Documents;
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

    // A hand-built, non-chess fixture: a 1x4 grid, two tokens ("a"/"b"), and a rule that accepts every relocation
    // unconditionally (evicting whichever token stood on the target) and flips turn — the minimal shape that
    // satisfies "a ply is an accepted relocation that changes the turn key" without any board occupancy gate of its
    // own. Score sums both tokens' own cell indices, so the search always has a discriminating best answer.
    private static WorldDefinition MiniNegamaxWorld(int depth, int? nodes = null, long tokenA = 0L, long tokenB = 3L) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), tokenA), new StateCell(CellName.Parse("b"), tokenB)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("best"), CellKind.Int, Cells: [new StateCell(CellName.Parse("token"), 0L), new StateCell(CellName.Parse("to"), 0L), new StateCell(CellName.Parse("score"), 0L)]),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict", Depth: depth, Score: "pieceCell[a] + pieceCell[b]", Best: "best", Nodes: nodes),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    // An independent oracle over the SAME declared rule ("any token to any other cell, evicting whoever stood
    // there, always accepted, turn always flips") rather than a second reading of the runtime's own code: plain
    // recursion over a two-element array, token-major/target-ascending enumeration (matching WorldSearchRuntime's
    // own walk order) so a tie resolves to the same candidate.
    private static (long Value, int Token, int Target) ReferenceNegamax(long[] cells, int boardCells, int plies) {
        var best = -WorldSearchCapacity.MateScore;
        var bestToken = -1;
        var bestTarget = -1;

        for (var token = 0; token < cells.Length; token++) {
            var from = cells[token];

            // A token off the board (any value that is no cell — here, the eviction sentinel -1) is left alone,
            // exactly as WorldSearchRuntime.Walk's own off-board guard leaves it.
            if ((from < 0L) || (from >= boardCells)) {
                continue;
            }

            for (var target = 0; target < boardCells; target++) {
                if (target == from) {
                    continue;
                }

                var next = (long[])cells.Clone();
                next[token] = target;

                for (var other = 0; other < cells.Length; other++) {
                    if ((other != token) && (cells[other] == target)) {
                        next[other] = -1L;
                    }
                }

                var value = ((plies <= 1) ? (next[0] + next[1]) : -ReferenceNegamax(next, boardCells, (plies - 1)).Value);

                if (value > best) {
                    best = value;
                    bestToken = token;
                    bestTarget = target;
                }
            }
        }

        return (best, bestToken, bestTarget);
    }

    private static WorldSearchStatus RunToCompletion(WorldFixture fixture, int maxTicks = 4000) {
        var status = fixture.Server.SearchStatus()[0];

        for (var tick = 0; (tick < maxTicks) && !status.Done; tick++) {
            fixture.Step();
            status = fixture.Server.SearchStatus()[0];
        }

        return status;
    }

    [Fact]
    public void DepthOneWithAnAuthoredScoreStillLandsBest() {
        using var fixture = Fixtures.FreshServer(definition: MiniNegamaxWorld(depth: 1));

        var status = RunToCompletion(fixture);
        var expected = ReferenceNegamax(cells: [0L, 3L], boardCells: 4, plies: 1);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(6L, status.Count);
        Assert.Equal(1, status.Depth);
        Assert.Equal(expected.Value, status.BestScore);
        Assert.Equal(expected.Token, status.BestToken);
        Assert.Equal(expected.Target, status.BestTarget);
        Assert.Equal(expected.Token, Cell(fixture, "best", "token"));
        Assert.Equal(expected.Target, Cell(fixture, "best", "to"));
        Assert.Equal(expected.Value, Cell(fixture, "best", "score"));
    }

    [Fact]
    public void DepthTwoNegamaxPicksTheMoveThatMaximizesTheAuthoredScoreAgainstTheBestReply() {
        using var fixture = Fixtures.FreshServer(definition: MiniNegamaxWorld(depth: 2));

        var status = RunToCompletion(fixture);
        var expected = ReferenceNegamax(cells: [0L, 3L], boardCells: 4, plies: 2);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(2, status.Depth);
        // The root walk that always populates legal/count is unchanged by depth: still every (token, target != own
        // cell) pair, six of them on this four-cell board.
        Assert.Equal(6L, status.Count);
        Assert.Equal(expected.Value, status.BestScore);
        Assert.Equal(expected.Token, status.BestToken);
        Assert.Equal(expected.Target, status.BestTarget);
        Assert.Equal(expected.Value, Cell(fixture, "best", "score"));
    }

    [Fact]
    public void ACheckpointMidNegamaxSearchRestoresAndFinishesIdenticallyToAnUninterruptedRun() {
        // One node per tick forces the depth-2 search to span many ticks, so a capture midway is genuinely mid-walk
        // (not merely the tick boundary right before it would have finished anyway).
        var document = MiniNegamaxWorld(depth: 2, nodes: 1);

        using var continuous = Fixtures.FreshServer(definition: document);
        var continuousStatus = RunToCompletion(continuous);
        Assert.True(continuousStatus.Done, continuousStatus.ToString());

        using var interrupted = Fixtures.FreshServer(definition: document);

        for (var tick = 0; tick < 5; tick++) {
            interrupted.Step();
        }

        var midStatus = interrupted.Server.SearchStatus()[0];
        Assert.False(midStatus.Done, "the capture must land mid-search for a restore to prove anything");
        Assert.Equal(5L, midStatus.Nodes);

        Assert.True(interrupted.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), reason);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: Directory.CreateTempSubdirectory(prefix: "puck-search-tests-").FullName, machineId: Guid.NewGuid(), template: restoredDefinition)
        );
        using var resumed = new WorldFixture(server: restoredServer, machines: restoredMachines, stateDirectory: Directory.CreateTempSubdirectory(prefix: "puck-search-tests-").FullName);

        var interruptedFinal = RunToCompletion(interrupted);
        var resumedFinal = RunToCompletion(resumed);

        Assert.True(interruptedFinal.Done, interruptedFinal.ToString());
        Assert.True(resumedFinal.Done, resumedFinal.ToString());
        Assert.Equal(continuousStatus.BestScore, interruptedFinal.BestScore);
        Assert.Equal(continuousStatus.BestToken, interruptedFinal.BestToken);
        Assert.Equal(continuousStatus.BestTarget, interruptedFinal.BestTarget);
        Assert.Equal(interruptedFinal.BestScore, resumedFinal.BestScore);
        Assert.Equal(interruptedFinal.BestToken, resumedFinal.BestToken);
        Assert.Equal(interruptedFinal.BestTarget, resumedFinal.BestTarget);
        Assert.Equal(interruptedFinal.Count, resumedFinal.Count);
    }
}
