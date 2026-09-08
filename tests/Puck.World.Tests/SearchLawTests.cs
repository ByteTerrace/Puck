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
            new(CellName.Parse("counts"), CellKind.Int, Capacity: 32, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
        };
        var definition = loaded with {
            StateRaw = state with { World = rows },
            SearchRaw = new WorldSearchSection(Jobs: [new WorldSearchRow(Name: "moves", Tokens: "pieceCell", Board: "board", Legal: "legal", Counts: "counts")]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    private static WorldStateRow Row(WorldFixture fixture, string name) => WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: name)!;
    private static long Cell(WorldFixture fixture, string row, string key) => Row(fixture, row).Cells!.Single(c => c.Key.Value == key).Value;

    private static SearchStatus Settle(WorldFixture fixture) {
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
        Assert.Equal(20L, Row(fixture, "counts").Cells!.Sum(c => c.Value));

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
        // Checkpoint transport needs a live, partially explored job, not hundreds of physical chess settling ticks.
        var definition = MiniNegamaxWorld(depth: 4, nodes: 1);
        using var first = Fixtures.FreshServer(definition);
        using var second = Fixtures.FreshServer(definition);

        for (var tick = 0; tick < 12; tick++) {
            first.Step();
            second.Step();
        }

        var a = first.Server.SearchStatus()[0];
        var b = second.Server.SearchStatus()[0];
        Assert.Equal(a, b);
        Assert.True(a.Nodes > 0);
        Assert.False(a.Done);
        Assert.True(first.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), reason);
        Assert.NotNull(checkpoint!.Search);
        Assert.Single(checkpoint.Search!.Jobs);
        Assert.Equal(a.Nodes, checkpoint.Search.Jobs[0].Nodes);

        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(bytes, out var decoded, out var decodeReason), decodeReason);
        // This live search carries stack and transposition-table arrays as well as the root move masks.
        Assert.Equal(bytes, WorldAuthorityCheckpointCodec.Encode(decoded!));
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
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
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
    // recursion over a two-element array, token-major/target-ascending enumeration (matching SearchRuntime's
    // own walk order) so a tie resolves to the same candidate.
    private static (long Value, int Token, int Target) ReferenceNegamax(long[] cells, int boardCells, int plies) {
        var best = -SearchCapacity.MateScore;
        var bestToken = -1;
        var bestTarget = -1;

        for (var token = 0; token < cells.Length; token++) {
            var from = cells[token];

            // A token off the board (any value that is no cell — here, the eviction sentinel -1) is left alone,
            // exactly as SearchRuntime.Walk's own off-board guard leaves it.
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

    private static SearchStatus RunToCompletion(WorldFixture fixture, int maxTicks = 4000) {
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

    private static long BoardCellOrEmpty(WorldFixture fixture, string row, int cell) =>
        (Row(fixture, row).Cells?.FirstOrDefault(c => c.Key.Value == cell.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Value ?? 0L);

    // Drop fixture: a 1x4 strip, one token "a" off the board (value -1) and one token "b" standing on cell 2. A
    // drop candidate is a cell no token occupies, so "a" should own every cell but 2 — the rule accepts every
    // candidate the walk itself resolves, so the occupied-cell exclusion is proven by the walk, not the judge.
    [Fact]
    public void DepthThreeNegamaxAgreesWithTheOracleThroughTheTranspositionTable() {
        using var fixture = Fixtures.FreshServer(definition: MiniNegamaxWorld(depth: 3));

        var status = RunToCompletion(fixture);
        var expected = ReferenceNegamax(cells: [0L, 3L], boardCells: 4, plies: 3);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(6L, status.Count);
        Assert.Equal(expected.Value, status.BestScore);
        Assert.Equal(expected.Token, status.BestToken);
        Assert.Equal(expected.Target, status.BestTarget);
    }

    // Promote fixture: a 1x3 strip, "a" at cell 0 carrying code 1. The judge accepts only a position where a's code
    // reads 3, so of the four (cell, code) candidates the shape offers, the two that promote to 3 are accepted.
    private static WorldDefinition PromoteWorld() {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), 0L)]),
                new WorldStateRow(CellName.Parse("pieceCode"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), 1L)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 3, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Expression: ValueExpression.Parse("pieceCode[a] == 3 ? 1 : 0")),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Promote(Codes: "pieceCode", To: [2L, 3L])], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void PromoteOffersEveryCodeOnEveryOtherCellAndTheJudgeReadsTheNewCode() {
        using var fixture = Fixtures.FreshServer(definition: PromoteWorld());

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(2L, status.Count);
        Assert.Equal((1L << 1) | (1L << 2), Cell(fixture, "legal", "a"));
        // The installed section never saw a hypothetical code.
        Assert.Equal(1L, Cell(fixture, "pieceCode", "a"));
    }

    // Zones are cells: a 1x3 strip whose cells are three piles, two cards standing on the first two, and a relocate
    // shape that never evicts — a transfer between zones is a relocation on the zone topology, so the card search
    // needs no vocabulary of its own.
    private static WorldDefinition ZoneWorld() {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("pile"), CellKind.Int, Domain: new StateDomain.CellsOf("zones")),
                new WorldStateRow(CellName.Parse("cardZone"), CellKind.Int, Cells: [new StateCell(CellName.Parse("x"), 0L), new StateCell(CellName.Parse("y"), 1L)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("cardZone"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("cardZone"))),
            ],
            Lattices: [new LatticeTopology.Grid("zones", new DocumentVector3(0, 0, 0), 1, Width: 3, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "cardZone", Board: "pile", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Relocate(Displace: false)], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void AZoneTransferIsARelocationOnTheZoneTopologyAndPilesShareACell() {
        using var fixture = Fixtures.FreshServer(definition: ZoneWorld());

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        // Each card may move to either of the two other zones, including the one the other card stands on.
        Assert.Equal(4L, status.Count);
        Assert.Equal((1L << 1) | (1L << 2), Cell(fixture, "legal", "x"));
        Assert.Equal((1L << 0) | (1L << 2), Cell(fixture, "legal", "y"));
    }

    // Outcome fixture: a 1x4 strip, one token at 0, every relocation accepted. The outcome is read from the mover's
    // side after the ply: cell 3 wins, cell 1 loses, cell 2 is level — so the tree must settle on cell 3.
    private static WorldDefinition UctWorld(int iterations, int? nodes = null) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), 0L)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("best"), CellKind.Int, Cells: [new StateCell(CellName.Parse("token"), 0L), new StateCell(CellName.Parse("to"), 0L), new StateCell(CellName.Parse("score"), 0L)]),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict", Depth: 1,
                    Score: "pieceCell[a] == 3 ? 1000 : (pieceCell[a] == 1 ? -1000 : 0)", Method: SearchMethod.Tree, Best: "best", Iterations: iterations, Nodes: nodes),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void TheTreeSearchSettlesOnTheWinningOutcomeAndLandsIt() {
        using var fixture = Fixtures.FreshServer(definition: UctWorld(iterations: 64));

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.True(status.HasOutcome);
        Assert.Equal(64, status.Iteration);
        Assert.Equal(3L, status.Count);
        Assert.Equal(0, status.BestToken);
        Assert.Equal(3, status.BestTarget);
        Assert.Equal(1000L, status.BestScore);
        Assert.Equal(3L, Cell(fixture, "best", "to"));
        Assert.Equal(1000L, Cell(fixture, "best", "score"));
    }

    [Fact]
    public void ACheckpointMidTreeSearchRestoresAndFinishesIdenticallyToAnUninterruptedRun() {
        var document = UctWorld(iterations: 32, nodes: 1);

        using var continuous = Fixtures.FreshServer(definition: document);
        var continuousStatus = RunToCompletion(continuous);
        Assert.True(continuousStatus.Done, continuousStatus.ToString());

        using var interrupted = Fixtures.FreshServer(definition: document);

        for (var tick = 0; tick < 9; tick++) {
            interrupted.Step();
        }

        var midStatus = interrupted.Server.SearchStatus()[0];
        Assert.False(midStatus.Done, "the capture must land mid-search for a restore to prove anything");
        Assert.True(midStatus.Nodes > 3L, midStatus.ToString());

        Assert.True(interrupted.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), reason);
        Assert.NotNull(checkpoint!.Search!.Jobs[0].Tree);

        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(bytes, out var decoded, out var decodeReason), decodeReason);
        Assert.Equal(checkpoint.Search.Jobs[0].Tree!.Visits, decoded!.Search!.Jobs[0].Tree!.Visits);
        Assert.Equal(checkpoint.Search.Jobs[0].Tree!.Seed, decoded.Search.Jobs[0].Tree!.Seed);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
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
        Assert.Equal(continuousStatus.BestTarget, interruptedFinal.BestTarget);
        Assert.Equal(continuousStatus.BestScore, interruptedFinal.BestScore);
        Assert.Equal(continuousStatus.Nodes, interruptedFinal.Nodes);
        Assert.Equal(interruptedFinal.BestTarget, resumedFinal.BestTarget);
        Assert.Equal(interruptedFinal.BestScore, resumedFinal.BestScore);
        Assert.Equal(interruptedFinal.Nodes, resumedFinal.Nodes);
    }

    private static WorldDefinition DropWorld() {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), -1L), new StateCell(CellName.Parse("b"), 2L)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Drop()], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void DropOffboardTokenReachesEveryEmptyCellAndNoOccupiedOne() {
        using var fixture = Fixtures.FreshServer(definition: DropWorld());

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(3L, status.Count);
        Assert.Equal(3L, Cell(fixture, "counts", "a"));
        Assert.Equal((1L << 0) | (1L << 1) | (1L << 3), Cell(fixture, "legal", "a"));
        Assert.Equal(0L, Cell(fixture, "legal", "b"));
    }

    // Jump fixture: a 1x5 strip, "a" at cell 0 and "b" at cell 1. The judge's own gate reads pieceCell[b] < 0 —
    // true only once the jumped token has actually left the board — so an accepted candidate proves the eviction
    // rather than merely a rule that accepts unconditionally.
    private static WorldDefinition JumpWorld() {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), 0L), new StateCell(CellName.Parse("b"), 1L)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 5, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(
                    Name: CellName.Parse("accept"),
                    Gate: new ActionPredicate.CompareValue(Left: ValueExpression.Parse("pieceCell[b]"), Comparison: ActionStateComparison.Less, Right: ValueExpression.Parse("0"), Kind: CellKind.Int),
                    Effects: [
                        new ActionEffect.SetState(State: "verdict", Value: 1),
                        new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                    ]
                ),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Jump(Over: ["E"])], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void JumpOverAnOccupiedIntermediateCellLandsOnTheEmptyCellBeyondAndEvictsIt() {
        using var fixture = Fixtures.FreshServer(definition: JumpWorld());

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(1L, status.Count);
        Assert.Equal((1L << 2), Cell(fixture, "legal", "a"));
        Assert.Equal(0L, Cell(fixture, "legal", "b"));
    }

    // Relocate fixture: a 1x4 strip, "a" at cell 0 and "b" at cell 2. The judge's own gate reads pieceCell[a] ==
    // pieceCell[b] — true only when "a" lands on "b" without evicting it, so the frame genuinely holds two tokens
    // sharing one cell.
    private static WorldDefinition RelocateWorld(bool displace) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), 0L), new StateCell(CellName.Parse("b"), 2L)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(
                    Name: CellName.Parse("accept"),
                    Gate: new ActionPredicate.CompareValue(Left: ValueExpression.Parse("pieceCell[a]"), Comparison: ActionStateComparison.Equal, Right: ValueExpression.Parse("pieceCell[b]"), Kind: CellKind.Int),
                    Effects: [
                        new ActionEffect.SetState(State: "verdict", Value: 1),
                        new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                    ]
                ),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Relocate(Displace: displace)], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void RelocateWithDisplaceFalseNeverRemovesTheStandingToken() {
        using var fixture = Fixtures.FreshServer(definition: RelocateWorld(displace: false));

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        // Only landing exactly on the OTHER token's cell can satisfy the judge's two-tokens-one-cell gate — "a"
        // onto "b"'s cell 2, or "b" onto "a"'s cell 0 — and each only still reads the other's cell back because
        // displace:false left it standing there.
        Assert.Equal(2L, status.Count);
        Assert.Equal((1L << 2), Cell(fixture, "legal", "a"));
        Assert.Equal((1L << 0), Cell(fixture, "legal", "b"));

        using var control = Fixtures.FreshServer(definition: RelocateWorld(displace: true));
        var controlStatus = RunToCompletion(control);

        // The same gate never once reads two tokens sharing a cell when displace evicts whoever stood there.
        Assert.True(controlStatus.Done, controlStatus.ToString());
        Assert.Equal(0L, controlStatus.Count);
    }

    // A 10x10 board — over BoardMask.MaxCells (64) — with two tokens and the default relocate shape, unconditionally
    // accepted; reach/held/counts work at this size, legal does not.
    private static WorldDefinition WideBoardWorld(bool withLegal) {
        var rows = new List<WorldStateRow> {
            new(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
            new(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), 0L), new StateCell(CellName.Parse("b"), 50L)]),
            new(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
            new(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
            new(CellName.Parse("reach"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
            new(CellName.Parse("held"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
            new(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
        };

        if (withLegal) {
            rows.Add(new(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))));
        }

        var state = new WorldStateSection(World: rows, Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 10, Depth: 10)]);

        return Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Reach: "reach", Held: "held", Counts: "counts", Legal: (withLegal ? "legal" : null)),
            ]),
        };
    }

    [Fact]
    public void ReachHeldAndCountsWorkPastTheLegalMaskCeilingWhereLegalItselfIsRefused() {
        Assert.False(WorldDefinitionValidator.TryValidateLocally(WideBoardWorld(withLegal: true), out var invalidReason));
        Assert.Contains("64 cells", invalidReason);

        var definition = WideBoardWorld(withLegal: false);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var validReason), validReason);

        using var fixture = Fixtures.FreshServer(definition: definition);
        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(198L, status.Count);
        Assert.Equal(99L, Cell(fixture, "counts", "a"));
        Assert.Equal(99L, Cell(fixture, "counts", "b"));

        // "held" defaults to 0, "a"'s own ordinal: every cell but its own starting one is a reachable destination.
        for (var cell = 0; cell < 100; cell++) {
            Assert.Equal((cell == 0) ? 0L : 1L, BoardCellOrEmpty(fixture, "reach", cell));
        }
    }

    // Two shapes in one job over the same 1x4 strip: relocate (default, displace true) applies to the on-board
    // token, drop to the off-board one. One node per tick spans the whole six-candidate walk across many ticks, so
    // a capture partway through lands genuinely mid-walk, astride the boundary between the two shapes.
    private static WorldDefinition MultiShapeWorld(int? nodes) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), 0L), new StateCell(CellName.Parse("b"), -1L)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(0, 0, 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Relocate(Displace: true), new WorldSearchShape.Drop()], Legal: "legal", Counts: "counts", Nodes: nodes),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void ACheckpointMidWalkAcrossMultipleShapesRestoresAndFinishesIdentically() {
        var document = MultiShapeWorld(nodes: 1);

        using var continuous = Fixtures.FreshServer(definition: document);
        var continuousStatus = RunToCompletion(continuous);
        Assert.True(continuousStatus.Done, continuousStatus.ToString());
        Assert.Equal(6L, continuousStatus.Count);

        using var interrupted = Fixtures.FreshServer(definition: document);

        for (var tick = 0; tick < 3; tick++) {
            interrupted.Step();
        }

        var midStatus = interrupted.Server.SearchStatus()[0];
        Assert.False(midStatus.Done, "the capture must land mid-search for a restore to prove anything");
        Assert.Equal(3L, midStatus.Nodes);

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
        Assert.Equal(continuousStatus.Count, interruptedFinal.Count);
        Assert.Equal(interruptedFinal.Count, resumedFinal.Count);
        Assert.Equal(14L, Cell(interrupted, "legal", "a"));
        Assert.Equal(14L, Cell(interrupted, "legal", "b"));
        Assert.Equal(Cell(interrupted, "legal", "a"), Cell(resumed, "legal", "a"));
        Assert.Equal(Cell(interrupted, "legal", "b"), Cell(resumed, "legal", "b"));
    }

    // Pair fixture over any lattice: two tokens, an accept-all judge, and the companion carried by the walked token's
    // own translation — the topology decides what a translation is.
    private static WorldDefinition PairWorld(LatticeTopology topology, long a, long b) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new StateDomain.CellsOf(topology.Name)),
                new WorldStateRow(CellName.Parse("pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse("a"), a), new StateCell(CellName.Parse("b"), b)]),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("pieceCell"))),
            ],
            Lattices: [topology]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Paired(With: "b")], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        return definition;
    }

    [Fact]
    public void APairOnAHexCarriesTheCompanionByTheSameTranslationAndTheNeighbourTableIsTheOracle() {
        // a at the centre, b on the ring: every step a takes is one hex direction, and b's own step in that direction
        // exists only where the disk still holds it — the adjacency table says so without any translation code.
        var hex = new LatticeTopology.Hex(Name: "board", Origin: new DocumentVector3(0, 0, 0), CellSize: 1, Radius: 1);
        var definition = PairWorld(hex, a: 0L, b: 1L);
        var topology = WorldTopologyCompilation.Find(definition, "board")!;
        var oracle = 0;

        for (var direction = 0; direction < topology.DirectionCount; direction++) {
            if (topology.Neighbour(cell: 1, direction: direction) >= 0) {
                oracle++;
            }
        }

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(3, oracle);
        Assert.Equal((long)oracle, status.Count);
        Assert.Equal((long)oracle, Cell(fixture, "counts", "a"));
        Assert.Equal(0L, Cell(fixture, "counts", "b"));
    }

    [Fact]
    public void APairOnARingWrapsTheCompanionAroundWithTheWalkedToken() {
        var ring = new LatticeTopology.Ring(Name: "board", Origin: new DocumentVector3(0, 0, 0), CellSize: 1, Width: 6);
        using var fixture = Fixtures.FreshServer(definition: PairWorld(ring, a: 0L, b: 3L));

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        // a may step to any other cell; b always lands three further round, never on a's own target.
        Assert.Equal(5L, status.Count);
        Assert.Equal(0b111110L, Cell(fixture, "legal", "a"));
    }

    // Pile fixture: three cards standing in a deck, in order, two empty piles beside it, and an accept-all judge that
    // also counts the hand through the frame. Pile order is the zones' own: only the deck's end card may move.
    private static WorldDefinition PileWorld(ZoneSelector selector = ZoneSelector.Last, bool best = false, WorldSearchShape? shape = null) {
        var zone = new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true);
        StateCell[] Members(string keys) => [.. keys.Select(static key => new StateCell(CellName.Parse(key.ToString()), 1L))];
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse("cards"), CellKind.Bool, Capacity: 3, Cells: Members("abc")),
                new WorldStateRow(CellName.Parse("deck"), CellKind.Bool, Capacity: 3, Domain: zone, Cells: Members("abc")),
                new WorldStateRow(CellName.Parse("hand"), CellKind.Bool, Capacity: 3, Domain: zone),
                new WorldStateRow(CellName.Parse("discard"), CellKind.Bool, Capacity: 3, Domain: zone),
                new WorldStateRow(CellName.Parse("turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("handCount"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]),
                new WorldStateRow(CellName.Parse("legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("cards"))),
                new WorldStateRow(CellName.Parse("counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse("cards"))),
                new WorldStateRow(CellName.Parse("best"), CellKind.Int, Cells: [new StateCell(CellName.Parse("token"), 0L), new StateCell(CellName.Parse("to"), 0L), new StateCell(CellName.Parse("score"), 0L)]),
            ]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse("accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "handCount", FromState: "$reduce:count:hand"),
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "cards", Zones: ["deck", "hand", "discard"], Turn: "turn", Verdict: "verdict",
                    Shapes: [shape ?? new WorldSearchShape.Transferred(Selector: selector)], Legal: "legal", Counts: "counts",
                    Score: (best ? "handCount" : null), Best: (best ? "best" : null)),
            ]),
        };

        return definition;
    }

    [Fact]
    public void OnlyTheTopOfAPileMayMoveAndItMayMoveOntoEitherOtherPile() {
        var definition = PileWorld();

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(3, status.Cells);
        Assert.Equal(2L, status.Count);
        Assert.Equal(0L, Cell(fixture, "counts", "a"));
        Assert.Equal(0L, Cell(fixture, "counts", "b"));
        Assert.Equal(2L, Cell(fixture, "counts", "c"));
        Assert.Equal((1L << 1) | (1L << 2), Cell(fixture, "legal", "c"));
        // The section's piles never moved: the frame took every transfer.
        Assert.Equal("abc", string.Concat(Row(fixture, "deck").Cells!.Select(c => c.Key.Value)));
        Assert.Empty(Row(fixture, "hand").Cells ?? []);
    }

    [Fact]
    public void TheFirstEndOfAPileMovesWhenTheShapeSaysSo() {
        var definition = PileWorld(selector: ZoneSelector.First);

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        Assert.Equal(2L, Cell(fixture, "counts", "a"));
        Assert.Equal(0L, Cell(fixture, "counts", "c"));
    }

    [Fact]
    public void TheJudgeReadsThePileTheTransferLandedOnAndBestNamesThatZone() {
        var definition = PileWorld(best: true);

        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = RunToCompletion(fixture);

        Assert.True(status.Done, status.ToString());
        // Moving c onto the hand makes the hand count one through the frame; onto the discard it stays zero.
        Assert.Equal(2, status.BestToken);
        Assert.Equal(1, status.BestTarget);
        Assert.Equal(1L, status.BestScore);
        Assert.Equal(1L, Cell(fixture, "best", "to"));
        Assert.Equal(0L, Cell(fixture, "handCount", WorldStateRow.SlotKey.Value));
    }

    [Fact]
    public void AZoneJobRefusesABoardShapeAndABoardJobRefusesATransfer() {
        Assert.False(WorldDefinitionValidator.TryValidateLocally(PileWorld(shape: new WorldSearchShape.Relocate()), out var zoneReason));
        Assert.Contains("transfer", zoneReason);

        var ring = new LatticeTopology.Ring(Name: "board", Origin: new DocumentVector3(0, 0, 0), CellSize: 1, Width: 6);
        var board = PairWorld(ring, a: 0L, b: 3L);
        var mixed = board with {
            SearchRaw = new WorldSearchSection(Jobs: [board.SearchRaw!.Jobs![0] with { Shapes = [new WorldSearchShape.Transferred()] }]),
        };

        Assert.False(WorldDefinitionValidator.TryValidateLocally(mixed, out var boardReason));
        Assert.Contains("zones", boardReason);
    }
}
