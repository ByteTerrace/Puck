using Xunit;

using Puck.Assets.Documents;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>A search job explores document-defined positions without exposing hypothetical state to the installed
/// section, survives checkpoint transport, and returns deterministic answers.</summary>
public sealed class SearchLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void RefusedSearchOutputsAreNarratedAfterConstructionAndArenaReplacement(bool replaceArena) {
        var definition = MiniNegamaxWorld(depth: 1, nodes: 64);

        definition = definition.WithWorldState(definition.AuthoredState.Select(selector: row =>
            ((row.Name.Value == "best") ? row with { Max = 0 } : row)).ToArray());
        using var fixture = Fixtures.FreshServer(definition);
        var server = fixture.Server;
        var sink = new RecordingNarrationSink();
        using var lease = server.AttachNarrationSink(sink: sink);

        if (replaceArena) {
            var time = default(ArenaTime);
            var current = server.Definition;

            Assert.True(condition: StateArena.TryCreate(current.StateCatalog, current.StateRaw, WorldSlotLanes.Options(definition: current),
                in time, out var replacement, out var reason), userMessage: reason);
            server.RecompileRules(current, arena: replacement);
        }
        for (var tick = 0; ((tick < 120) && !sink.Narrations.Any(predicate: narration => (narration.Channel == "state.search"))); tick++) {
            fixture.Step();
        }
        var refusal = Assert.Single(collection: sink.Narrations, predicate: narration => (narration.Channel == "state.search"));

        Assert.Contains("outputs were refused", refusal.Text);
        Assert.All(fixture.Row(name: "best").Cells!, cell => Assert.Equal(0L, cell.Value.Raw));
    }
    [Fact]
    public void ACheckpointCarriesTheJobAndTwoServersAgree() {
        // Checkpoint transport needs a live, partially explored job, not hundreds of physical chess settling ticks.
        var definition = MiniNegamaxWorld(depth: 4, nodes: 1);
        using var first = Fixtures.FreshServer(definition);
        using var second = Fixtures.FreshServer(definition);

        for (var tick = 0; (tick < 12); tick++) {
            first.Step();
            second.Step();
        }

        var a = first.Server.SearchStatus()[0];
        var b = second.Server.SearchStatus()[0];

        Assert.Equal(actual: b, expected: a);
        Assert.True(condition: (a.Nodes > 0));
        Assert.False(condition: a.Done);
        Assert.True(condition: first.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: checkpoint!.Search);
        Assert.Single(collection: checkpoint.Search!.Jobs);
        Assert.Equal(a.Nodes, checkpoint.Search.Jobs[0].Nodes);

        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: bytes, checkpoint: out var decoded, reason: out var decodeReason), userMessage: decodeReason);
        // This live search carries stack and transposition-table arrays as well as the root move masks.
        Assert.Equal(bytes, WorldAuthorityCheckpointCodec.Encode(checkpoint: decoded!));
    }

    // A hand-built, non-chess fixture: a 1x4 grid, two tokens ("a"/"b"), and a rule that accepts every relocation
    // unconditionally (evicting whichever token stood on the target) and flips turn — the minimal shape that
    // satisfies "a ply is an accepted relocation that changes the turn key" without any board occupancy gate of its
    // own. Score sums both tokens' own cell indices, so the search always has a discriminating best answer.
    private static WorldDefinition MiniNegamaxWorld(int depth, int? nodes = null, long tokenA = 0L, long tokenB = 3L) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: tokenA)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: tokenB))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "best"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "token"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "to"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "score"), CellValue.Int(value: 0L))]),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict", Depth: depth, Score: "pieceCell[a] + pieceCell[b]", Best: "best", Nodes: nodes),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    // A judge rule draws 'roll' inside every candidate and the score reads it back, so the landed best score is the
    // candidate's draw at cursor 0 (each candidate scope rewinds the cursor). The live rule then snapshots that
    // score and draws the same site at the same cursor 0, which the live world has never advanced.
    [Fact]
    public void ACandidatesDrawEqualsTheLiveDrawAtTheSameCursor() {
        var live = new ActionPredicate.CompareState(State: RuleFacts.SearchPly, Comparison: ExpressionOp.Equal, Value: 0m);
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 3L))]),
                StateFixtures.IntSlot(name: "turn"),
                StateFixtures.IntSlot(name: "verdict"),
                StateFixtures.IntSlot(name: "seen"),
                StateFixtures.IntSlot(name: "taken"),
                StateFixtures.IntSlot(name: "roll") with {
                    Draw = new Draw(
                        Generator: new StateGenerator(Source: GeneratorSource.UniformRange, RangeMin: 1, RangeMax: 1_000_000),
                        Timing: DrawTiming.Event
                    ),
                },
                new WorldStateRow(CellName.Parse(candidate: "best"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "token"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "to"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "score"), CellValue.Int(value: 0L))]),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(
                    Name: CellName.Parse(candidate: "accept"),
                    Gate: new ActionPredicate.CompareState(State: RuleFacts.SearchPly, Comparison: ExpressionOp.GreaterOrEqual, Value: 1m),
                    Effects: [
                        new ActionEffect.Generate(Row: "roll"),
                        new ActionEffect.SetState(State: "verdict", Value: 1),
                        new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                    ]
                ),
                new WorldRule(
                    Name: CellName.Parse(candidate: "liveRoll"),
                    Gate: new ActionPredicate.All(Predicates: [
                        live,
                        new ActionPredicate.CompareState(State: "taken", Comparison: ExpressionOp.Equal, Value: 0m),
                        new ActionPredicate.CompareState(State: "best", Key: "score", Comparison: ExpressionOp.NotEqual, Value: 0m),
                    ]),
                    Effects: [
                        new ActionEffect.SetState(State: "seen", FromState: "best", FromKey: "score"),
                        new ActionEffect.Generate(Row: "roll"),
                        new ActionEffect.SetState(State: "taken", Value: 1),
                    ]
                ),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict", Depth: 1, Score: "roll", Best: "best"),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var tick = 0; ((tick < 4000) && (fixture.KeyedValue(key: "$value", row: "taken") == 0L)); tick++) {
            fixture.Step();
        }

        var seen = fixture.KeyedValue(key: "$value", row: "seen");

        Assert.Equal(1L, fixture.KeyedValue(key: "$value", row: "taken"));
        Assert.InRange(actual: seen, high: 1_000_000L, low: 1L);
        Assert.Equal(expected: seen, actual: fixture.KeyedValue(key: "$value", row: "roll"));
    }
    // What the sheet and one fold of every row leave is divided equally among the jobs, and a share that cannot
    // cover a restart, a full replay and one unit is refused by that sum rather than left to stall.
    [Fact]
    public void AJobsAllowanceIsItsEqualShareOfWhatTheSheetLeavesAndTooSmallAShareIsRefusedByItsSum() {
        var one = MiniNegamaxWorld(depth: 3);
        var two = one with {
            SearchRaw = new WorldSearchSection(Jobs: [one.Search.Rows[0], one.Search.Rows[0] with { Name = "second" }]),
        };

        static SearchPlan[] Plans(WorldDefinition definition) {
            Assert.True(
                condition: WorldSearchCompilation.TryPlanAll(
                    compilation: WorldRuleCompilation.Compile(definition: definition),
                    judges: out _,
                    plans: out var plans,
                    reason: out var reason,
                    scores: out _
                ),
                userMessage: reason
            );

            return plans;
        }

        var alone = Plans(definition: one)[0].Work;
        var shared = Plans(definition: two);

        Assert.True(condition: (alone.Allowance < RuleCapacity.MaxWorkUnitsPerTick));
        Assert.True(condition: (alone.Allowance >= alone.Minimum));
        Assert.Equal(
            (alone.Allowance / 2L),
            shared[0].Work.Allowance
        );
        Assert.Equal(
            shared[0].Work.Allowance,
            shared[1].Work.Allowance
        );

        var context = WorldFactsCompiler.Context(definition: one);
        // The price of a unit folds the position key, so the refusal is asked at the fold the planner itself uses.
        var position = Enumerable.Range(
            count: one.StateCatalog.Descriptors.Count,
            start: 0
        ).Sum(selector: ordinal => context.RowCapacity(rowOrdinal: ordinal));

        Assert.False(condition: WorldSearchCompilation.TryPlan(
            allowance: (alone.Minimum - 1L),
            context: context,
            definition: one,
            judgeCost: alone.Judge,
            plan: out _,
            position: position,
            reason: out var refusal,
            row: one.Search.Rows[0],
            score: out _
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "too little work left"
        );
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: $"may spend {(alone.Minimum - 1L)}"
        );
    }

    // An independent oracle over the SAME declared rule ("any token to any other cell, evicting whoever stood
    // there, always accepted, turn always flips") rather than a second reading of the runtime's own code: plain
    // recursion over a two-element array, token-major/target-ascending enumeration (matching ArenaSearch's
    // own walk order) so a tie resolves to the same candidate.
    private static (long Value, int Token, int Target) ReferenceNegamax(long[] cells, int boardCells, int plies) {
        var best = -SearchCapacity.MateScore;
        var bestToken = -1;
        var bestTarget = -1;

        for (var token = 0; (token < cells.Length); token++) {
            var from = cells[token];

            // A token off the board (any value that is no cell — here, the eviction sentinel -1) is left alone,
            // exactly as ArenaSearch's own off-board guard leaves it.
            if ((from < 0L) || (from >= boardCells)) {
                continue;
            }

            for (var target = 0; (target < boardCells); target++) {
                if (target == from) {
                    continue;
                }

                var next = ((long[])cells.Clone());

                next[token] = target;

                for (var other = 0; (other < cells.Length); other++) {
                    if ((other != token) && (cells[other] == target)) {
                        next[other] = -1L;
                    }
                }

                var value = ((plies <= 1) ? (next[0] + next[1]) : -ReferenceNegamax(boardCells: boardCells, cells: next, plies: (plies - 1)).Value);

                if (value > best) {
                    best = value;
                    bestToken = token;
                    bestTarget = target;
                }
            }
        }

        return (best, bestToken, bestTarget);
    }

    [Fact]
    public void DepthOneWithAnAuthoredScoreStillLandsBest() {
        using var fixture = Fixtures.FreshServer(definition: MiniNegamaxWorld(depth: 1));

        var status = fixture.SettleSearch();
        var expected = ReferenceNegamax(boardCells: 4, cells: [0L, 3L], plies: 1);

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(6L, status.Count);
        Assert.Equal(1, status.Depth);
        Assert.Equal(expected.Value, status.BestScore);
        Assert.Equal(expected.Token, status.BestToken);
        Assert.Equal(expected.Target, status.BestTarget);
        Assert.Equal(expected.Token, fixture.KeyedValue(key: "token", row: "best"));
        Assert.Equal(expected.Target, fixture.KeyedValue(key: "to", row: "best"));
        Assert.Equal(expected.Value, fixture.KeyedValue(key: "score", row: "best"));
    }
    [Fact]
    public void DepthTwoNegamaxPicksTheMoveThatMaximizesTheAuthoredScoreAgainstTheBestReply() {
        using var fixture = Fixtures.FreshServer(definition: MiniNegamaxWorld(depth: 2));

        var status = fixture.SettleSearch();
        var expected = ReferenceNegamax(boardCells: 4, cells: [0L, 3L], plies: 2);

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(2, status.Depth);
        // The root walk that always populates legal/count is unchanged by depth: still every (token, target != own
        // cell) pair, six of them on this four-cell board.
        Assert.Equal(6L, status.Count);
        Assert.Equal(expected.Value, status.BestScore);
        Assert.Equal(expected.Token, status.BestToken);
        Assert.Equal(expected.Target, status.BestTarget);
        Assert.Equal(expected.Value, fixture.KeyedValue(key: "score", row: "best"));
    }
    [Fact]
    public void ACheckpointMidNegamaxSearchRestoresAndFinishesIdenticallyToAnUninterruptedRun() {
        // One node per tick forces the depth-2 search to span many ticks, so a capture midway is genuinely mid-walk
        // (not merely the tick boundary right before it would have finished anyway).
        var document = MiniNegamaxWorld(depth: 2, nodes: 1);

        using var continuous = Fixtures.FreshServer(definition: document);
        var continuousStatus = continuous.SettleSearch();

        Assert.True(condition: continuousStatus.Done, userMessage: continuousStatus.ToString());

        using var interrupted = Fixtures.FreshServer(definition: document);

        for (var tick = 0; (tick < 5); tick++) {
            interrupted.Step();
        }

        var midStatus = interrupted.Server.SearchStatus()[0];

        Assert.False(condition: midStatus.Done, userMessage: "the capture must land mid-search for a restore to prove anything");
        Assert.Equal(5L, midStatus.Nodes);

        Assert.True(condition: interrupted.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), userMessage: reason);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);

        var profilesDirectory = Directory.CreateTempSubdirectory(prefix: "puck-search-tests-").FullName;

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory, machineId: Guid.NewGuid(), template: restoredDefinition)
        );
        // WorldFixture.Dispose owns this directory — the SAME one profiles above was seeded from, not a second one.
        using var resumed = new WorldFixture(server: restoredServer, machines: restoredMachines, stateDirectory: profilesDirectory);

        var interruptedFinal = interrupted.SettleSearch();
        var resumedFinal = resumed.SettleSearch();

        Assert.True(condition: interruptedFinal.Done, userMessage: interruptedFinal.ToString());
        Assert.True(condition: resumedFinal.Done, userMessage: resumedFinal.ToString());
        Assert.Equal(continuousStatus.BestScore, interruptedFinal.BestScore);
        Assert.Equal(continuousStatus.BestToken, interruptedFinal.BestToken);
        Assert.Equal(continuousStatus.BestTarget, interruptedFinal.BestTarget);
        Assert.Equal(interruptedFinal.BestScore, resumedFinal.BestScore);
        Assert.Equal(interruptedFinal.BestToken, resumedFinal.BestToken);
        Assert.Equal(interruptedFinal.BestTarget, resumedFinal.BestTarget);
        Assert.Equal(interruptedFinal.Count, resumedFinal.Count);
    }

    private static long BoardCellOrEmpty(WorldFixture fixture, string row, int cell) =>
        (fixture.Row(name: row).Cells?.FirstOrDefault(predicate: c => (c.Key.Value == cell.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)))?.Value.Raw ?? 0L);

    // Drop fixture: a 1x4 strip, one token "a" off the board (value -1) and one token "b" standing on cell 2. A
    // drop candidate is a cell no token occupies, so "a" should own every cell but 2 — the rule accepts every
    // candidate the walk itself resolves, so the occupied-cell exclusion is proven by the walk, not the judge.
    [Fact]
    public void DepthThreeNegamaxAgreesWithTheOracleThroughTheTranspositionTable() {
        using var fixture = Fixtures.FreshServer(definition: MiniNegamaxWorld(depth: 3));

        var status = fixture.SettleSearch();
        var expected = ReferenceNegamax(boardCells: 4, cells: [0L, 3L], plies: 3);

        Assert.True(condition: status.Done, userMessage: status.ToString());
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
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "pieceCode"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 1L))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 3, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Expression: ExpressionProgram.Parse(text: "pieceCode[a] == 3 ? 1 : 0")),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Promote(Codes: "pieceCode", To: [2L, 3L])], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    [Fact]
    public void PromoteOffersEveryCodeOnEveryOtherCellAndTheJudgeReadsTheNewCode() {
        using var fixture = Fixtures.FreshServer(definition: PromoteWorld());

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(2L, status.Count);
        Assert.Equal((1L << 1) | (1L << 2), fixture.KeyedValue(key: "a", row: "legal"));
        // The installed section never saw a hypothetical code.
        Assert.Equal(1L, fixture.KeyedValue(key: "a", row: "pieceCode"));
    }

    // Zones are cells: a 1x3 strip whose cells are three piles, two cards standing on the first two, and a relocate
    // shape that never evicts — a transfer between zones is a relocation on the zone topology, so the card search
    // needs no vocabulary of its own.
    private static WorldDefinition ZoneWorld() {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "pile"), CellKind.Int, Domain: new StateDomain.CellsOf("zones")),
                new WorldStateRow(CellName.Parse(candidate: "cardZone"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "x"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "y"), CellValue.Int(value: 1L))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cardZone"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cardZone"))),
            ],
            Lattices: [new LatticeTopology.Grid("zones", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 3, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "cardZone", Board: "pile", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Relocate(Displace: false)], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    [Fact]
    public void AZoneTransferIsARelocationOnTheZoneTopologyAndPilesShareACell() {
        using var fixture = Fixtures.FreshServer(definition: ZoneWorld());

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        // Each card may move to either of the two other zones, including the one the other card stands on.
        Assert.Equal(4L, status.Count);
        Assert.Equal((1L << 1) | (1L << 2), fixture.KeyedValue(key: "x", row: "legal"));
        Assert.Equal((1L << 0) | (1L << 2), fixture.KeyedValue(key: "y", row: "legal"));
    }

    // Outcome fixture: a 1x4 strip, one token at 0, every relocation accepted. The outcome is read from the mover's
    // side after the ply: cell 3 wins, cell 1 loses, cell 2 is level — so the tree must settle on cell 3.
    private static WorldDefinition UctWorld(int iterations, int? nodes = null) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "best"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "token"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "to"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "score"), CellValue.Int(value: 0L))]),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict", Depth: 1,
                    Score: "pieceCell[a] == 3 ? 1000 : (pieceCell[a] == 1 ? -1000 : 0)", Method: SearchMethod.MonteCarlo, Best: "best", Iterations: iterations, Nodes: nodes),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    [Fact]
    public void TheTreeSearchSettlesOnTheWinningOutcomeAndLandsIt() {
        using var fixture = Fixtures.FreshServer(definition: UctWorld(iterations: 64));

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.True(condition: status.HasOutcome);
        Assert.Equal(64, status.Iteration);
        Assert.Equal(3L, status.Count);
        Assert.Equal(0, status.BestToken);
        Assert.Equal(3, status.BestTarget);
        Assert.Equal(1000L, status.BestScore);
        Assert.Equal(3L, fixture.KeyedValue(key: "to", row: "best"));
        Assert.Equal(1000L, fixture.KeyedValue(key: "score", row: "best"));
    }
    [Fact]
    public void ACheckpointMidTreeSearchRestoresAndFinishesIdenticallyToAnUninterruptedRun() {
        var document = UctWorld(iterations: 32, nodes: 1);

        using var continuous = Fixtures.FreshServer(definition: document);
        var continuousStatus = continuous.SettleSearch();

        Assert.True(condition: continuousStatus.Done, userMessage: continuousStatus.ToString());

        using var interrupted = Fixtures.FreshServer(definition: document);

        for (var tick = 0; (tick < 9); tick++) {
            interrupted.Step();
        }

        var midStatus = interrupted.Server.SearchStatus()[0];

        Assert.False(condition: midStatus.Done, userMessage: "the capture must land mid-search for a restore to prove anything");
        Assert.True(condition: (midStatus.Nodes > 3L), userMessage: midStatus.ToString());

        Assert.True(condition: interrupted.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: checkpoint!.Search!.Jobs[0].Tree);

        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: bytes, checkpoint: out var decoded, reason: out var decodeReason), userMessage: decodeReason);
        Assert.Equal(checkpoint.Search.Jobs[0].Tree!.Visits, decoded!.Search!.Jobs[0].Tree!.Visits);
        Assert.Equal(checkpoint.Search.Jobs[0].Tree!.Seed, decoded.Search.Jobs[0].Tree!.Seed);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);

        var profilesDirectory = Directory.CreateTempSubdirectory(prefix: "puck-search-tests-").FullName;

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory, machineId: Guid.NewGuid(), template: restoredDefinition)
        );
        // WorldFixture.Dispose owns this directory — the SAME one profiles above was seeded from, not a second one.
        using var resumed = new WorldFixture(server: restoredServer, machines: restoredMachines, stateDirectory: profilesDirectory);

        var interruptedFinal = interrupted.SettleSearch();
        var resumedFinal = resumed.SettleSearch();

        Assert.True(condition: interruptedFinal.Done, userMessage: interruptedFinal.ToString());
        Assert.True(condition: resumedFinal.Done, userMessage: resumedFinal.ToString());
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
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: -1L)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 2L))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Drop()], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    [Fact]
    public void DropOffboardTokenReachesEveryEmptyCellAndNoOccupiedOne() {
        using var fixture = Fixtures.FreshServer(definition: DropWorld());

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(3L, status.Count);
        Assert.Equal(3L, fixture.KeyedValue(key: "a", row: "counts"));
        Assert.Equal((1L << 0) | (1L << 1) | (1L << 3), fixture.KeyedValue(key: "a", row: "legal"));
        Assert.Equal(0L, fixture.KeyedValue(key: "b", row: "legal"));
    }

    // Jump fixture: a 1x5 strip, "a" at cell 0 and "b" at cell 1. The judge's own gate reads pieceCell[b] < 0 —
    // true only once the jumped token has actually left the board — so an accepted candidate proves the eviction
    // rather than merely a rule that accepts unconditionally.
    private static WorldDefinition JumpWorld() {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 1L))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 5, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(
                    Name: CellName.Parse(candidate: "accept"),
                    Gate: new ActionPredicate.CompareValue(Left: ExpressionProgram.Parse(text: "pieceCell[b]"), Comparison: ExpressionOp.Less, Right: ExpressionProgram.Parse(text: "0"), Kind: CellKind.Int),
                    Effects: [
                        new ActionEffect.SetState(State: "verdict", Value: 1),
                        new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                    ]
                ),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Jump(Over: ["E"])], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    // A chain's length is bounded by the candidates it enumerates per token against the nodes a job judges in a
    // tick, and by no count of hops: one direction hops two ways (stop, or go), so twelve hops is 4,096 chains and
    // fits, thirteen is 8,192 and does not, and a chain long enough to overflow its own count refuses the same way.
    [Fact]
    public void AJumpChainIsAsLongAsTheNodesOneTickJudges() {
        WorldDefinition Hops(int maxHops) {
            var world = JumpWorld();

            return world with {
                SearchRaw = new WorldSearchSection(Jobs: [
                    world.Search.Rows[0] with {
                        Counts = null,
                        Legal = null,
                        Shapes = [new WorldSearchShape.Jump(MaxHops: maxHops, Over: ["E"])],
                    },
                ]),
            };
        }

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Hops(maxHops: 12),
                reason: out var admitted
            ),
            userMessage: admitted
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Hops(maxHops: 13),
            reason: out var refused
        ));
        Assert.Contains(
            actualString: refused,
            expectedSubstring: "enumerates 8192 candidates per token"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Hops(maxHops: 64),
            reason: out var overflowed
        ));
        Assert.Contains(
            actualString: overflowed,
            expectedSubstring: "enumerates more than 2147483647 candidates per token"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Hops(maxHops: 0),
            reason: out var none
        ));
        Assert.Contains(
            actualString: none,
            expectedSubstring: "must be at least 1"
        );
    }
    [Fact]
    public void JumpOverAnOccupiedIntermediateCellLandsOnTheEmptyCellBeyondAndEvictsIt() {
        using var fixture = Fixtures.FreshServer(definition: JumpWorld());

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(1L, status.Count);
        Assert.Equal((1L << 2), fixture.KeyedValue(key: "a", row: "legal"));
        Assert.Equal(0L, fixture.KeyedValue(key: "b", row: "legal"));
    }

    // Relocate fixture: a 1x4 strip, "a" at cell 0 and "b" at cell 2. The judge's own gate reads pieceCell[a] ==
    // pieceCell[b] — true only when "a" lands on "b" without evicting it, so the frame genuinely holds two tokens
    // sharing one cell.
    private static WorldDefinition RelocateWorld(bool displace) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 2L))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(
                    Name: CellName.Parse(candidate: "accept"),
                    Gate: new ActionPredicate.CompareValue(Left: ExpressionProgram.Parse(text: "pieceCell[a]"), Comparison: ExpressionOp.Equal, Right: ExpressionProgram.Parse(text: "pieceCell[b]"), Kind: CellKind.Int),
                    Effects: [
                        new ActionEffect.SetState(State: "verdict", Value: 1),
                        new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                    ]
                ),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Relocate(Displace: displace)], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    [Fact]
    public void RelocateWithDisplaceFalseNeverRemovesTheStandingToken() {
        using var fixture = Fixtures.FreshServer(definition: RelocateWorld(displace: false));

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        // Only landing exactly on the OTHER token's cell can satisfy the judge's two-tokens-one-cell gate — "a"
        // onto "b"'s cell 2, or "b" onto "a"'s cell 0 — and each only still reads the other's cell back because
        // displace:false left it standing there.
        Assert.Equal(2L, status.Count);
        Assert.Equal((1L << 2), fixture.KeyedValue(key: "a", row: "legal"));
        Assert.Equal((1L << 0), fixture.KeyedValue(key: "b", row: "legal"));

        using var control = Fixtures.FreshServer(definition: RelocateWorld(displace: true));
        var controlStatus = control.SettleSearch();

        // The same gate never once reads two tokens sharing a cell when displace evicts whoever stood there.
        Assert.True(condition: controlStatus.Done, userMessage: controlStatus.ToString());
        Assert.Equal(0L, controlStatus.Count);
    }

    // A 10x10 board — over BoardMask.MaxCells (64) — with two tokens and the default relocate shape, unconditionally
    // accepted; reach/held/counts work at this size, legal does not.
    private static WorldDefinition WideBoardWorld(bool withLegal) {
        var rows = new List<WorldStateRow> {
            new(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
            new(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 50L))]),
            new(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
            new(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
            new(CellName.Parse(candidate: "reach"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
            new(CellName.Parse(candidate: "held"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
            new(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
        };

        if (withLegal) {
            rows.Add(item: new(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))));
        }

        var state = new WorldStateSection(World: rows, Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 10, Depth: 10)]);

        return Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
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
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: WideBoardWorld(withLegal: true), reason: out var invalidReason));
        Assert.Contains(actualString: invalidReason, expectedSubstring: "64 cells");

        var definition = WideBoardWorld(withLegal: false);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var validReason), userMessage: validReason);

        using var fixture = Fixtures.FreshServer(definition: definition);
        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(198L, status.Count);
        Assert.Equal(99L, fixture.KeyedValue(key: "a", row: "counts"));
        Assert.Equal(99L, fixture.KeyedValue(key: "b", row: "counts"));

        // "held" defaults to 0, "a"'s own ordinal: every cell but its own starting one is a reachable destination.
        for (var cell = 0; (cell < 100); cell++) {
            Assert.Equal(((cell == 0) ? 0L : 1L), BoardCellOrEmpty(cell: cell, fixture: fixture, row: "reach"));
        }
    }

    // Two shapes in one job over the same 1x4 strip: relocate (default, displace true) applies to the on-board
    // token, drop to the off-board one. One node per tick spans the whole six-candidate walk across many ticks, so
    // a capture partway through lands genuinely mid-walk, astride the boundary between the two shapes.
    private static WorldDefinition MultiShapeWorld(int? nodes) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("board")),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: -1L))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
            ],
            Lattices: [new LatticeTopology.Grid("board", new DocumentVector3(x: 0, y: 0, z: 0), 1, Width: 4, Depth: 1)]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Relocate(Displace: true), new WorldSearchShape.Drop()], Legal: "legal", Counts: "counts", Nodes: nodes),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    [Fact]
    public void ACheckpointMidWalkAcrossMultipleShapesRestoresAndFinishesIdentically() {
        var document = MultiShapeWorld(nodes: 1);

        using var continuous = Fixtures.FreshServer(definition: document);
        var continuousStatus = continuous.SettleSearch();

        Assert.True(condition: continuousStatus.Done, userMessage: continuousStatus.ToString());
        Assert.Equal(6L, continuousStatus.Count);

        using var interrupted = Fixtures.FreshServer(definition: document);

        for (var tick = 0; (tick < 3); tick++) {
            interrupted.Step();
        }

        var midStatus = interrupted.Server.SearchStatus()[0];

        Assert.False(condition: midStatus.Done, userMessage: "the capture must land mid-search for a restore to prove anything");
        Assert.Equal(3L, midStatus.Nodes);

        Assert.True(condition: interrupted.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var checkpoint, reason: out var reason), userMessage: reason);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);

        var profilesDirectory = Directory.CreateTempSubdirectory(prefix: "puck-search-tests-").FullName;

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory, machineId: Guid.NewGuid(), template: restoredDefinition)
        );
        // WorldFixture.Dispose owns this directory — the SAME one profiles above was seeded from, not a second one.
        using var resumed = new WorldFixture(server: restoredServer, machines: restoredMachines, stateDirectory: profilesDirectory);

        var interruptedFinal = interrupted.SettleSearch();
        var resumedFinal = resumed.SettleSearch();

        Assert.True(condition: interruptedFinal.Done, userMessage: interruptedFinal.ToString());
        Assert.True(condition: resumedFinal.Done, userMessage: resumedFinal.ToString());
        Assert.Equal(continuousStatus.Count, interruptedFinal.Count);
        Assert.Equal(interruptedFinal.Count, resumedFinal.Count);
        Assert.Equal(14L, interrupted.KeyedValue(key: "a", row: "legal"));
        Assert.Equal(14L, interrupted.KeyedValue(key: "b", row: "legal"));
        Assert.Equal(interrupted.KeyedValue(key: "a", row: "legal"), resumed.KeyedValue(key: "a", row: "legal"));
        Assert.Equal(interrupted.KeyedValue(key: "b", row: "legal"), resumed.KeyedValue(key: "b", row: "legal"));
    }

    // Pair fixture over any lattice: two tokens, an accept-all judge, and the companion carried by the walked token's
    // own translation — the topology decides what a translation is.
    private static WorldDefinition PairWorld(LatticeTopology topology, long a, long b) {
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf(topology.Name)),
                new WorldStateRow(CellName.Parse(candidate: "pieceCell"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: a)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: b))]),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pieceCell"))),
            ],
            Lattices: [topology]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "pieceCell", Board: "board", Turn: "turn", Verdict: "verdict",
                    Shapes: [new WorldSearchShape.Tandem(With: "b")], Legal: "legal", Counts: "counts"),
            ]),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        return definition;
    }

    [Fact]
    public void APairOnAHexCarriesTheCompanionByTheSameTranslationAndTheNeighbourTableIsTheOracle() {
        // a at the centre, b on the ring: every step a takes is one hex direction, and b's own step in that direction
        // exists only where the disk still holds it — the adjacency table says so without any translation code.
        var hex = new LatticeTopology.Hex(Name: "board", Origin: new DocumentVector3(x: 0, y: 0, z: 0), CellSize: 1, Radius: 1);
        var definition = PairWorld(hex, a: 0L, b: 1L);
        var topology = WorldTopologyCompilation.Find(definition: definition, name: "board")!;
        var oracle = 0;

        for (var direction = 0; (direction < topology.DirectionCount); direction++) {
            if (topology.Neighbour(cell: 1, direction: direction) >= 0) {
                oracle++;
            }
        }

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(actual: oracle, expected: 3);
        Assert.Equal(((long)oracle), status.Count);
        Assert.Equal(((long)oracle), fixture.KeyedValue(key: "a", row: "counts"));
        Assert.Equal(0L, fixture.KeyedValue(key: "b", row: "counts"));
    }
    [Fact]
    public void APairOnARingWrapsTheCompanionAroundWithTheWalkedToken() {
        var ring = new LatticeTopology.Ring(Name: "board", Origin: new DocumentVector3(x: 0, y: 0, z: 0), CellSize: 1, Width: 6);
        using var fixture = Fixtures.FreshServer(definition: PairWorld(ring, a: 0L, b: 3L));

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        // a may step to any other cell; b always lands three further round, never on a's own target.
        Assert.Equal(5L, status.Count);
        Assert.Equal(0b111110L, fixture.KeyedValue(key: "a", row: "legal"));
    }

    // Pile fixture: three cards standing in a deck, in order, two empty piles beside it, and an accept-all judge that
    // also counts the hand through the frame. Pile order is the zones' own: only the deck's end card may move.
    private static WorldDefinition PileWorld(ZoneSelector selector = ZoneSelector.Last, bool best = false, WorldSearchShape? shape = null) {
        var zone = new StateDomain.KeysOf(CellName.Parse(candidate: "cards"), Ordered: true);

        StateCell[] Members(string keys) => [.. keys.Select(selector: static key => new StateCell(CellName.Parse(candidate: key.ToString()), CellValue.Bool(value: true)))];
        var state = new WorldStateSection(
            World: [
                new WorldStateRow(CellName.Parse(candidate: "cards"), CellKind.Bool, Capacity: 3, Cells: Members(keys: "abc")),
                new WorldStateRow(CellName.Parse(candidate: "deck"), CellKind.Bool, Capacity: 3, Domain: zone, Cells: Members(keys: "abc")),
                new WorldStateRow(CellName.Parse(candidate: "hand"), CellKind.Bool, Capacity: 3, Domain: zone),
                new WorldStateRow(CellName.Parse(candidate: "discard"), CellKind.Bool, Capacity: 3, Domain: zone),
                new WorldStateRow(CellName.Parse(candidate: "turn"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "verdict"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "handCount"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0L))]),
                new WorldStateRow(CellName.Parse(candidate: "legal"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards"))),
                new WorldStateRow(CellName.Parse(candidate: "counts"), CellKind.Int, Capacity: 4, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards"))),
                new WorldStateRow(CellName.Parse(candidate: "best"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "token"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "to"), CellValue.Int(value: 0L)), new StateCell(CellName.Parse(candidate: "score"), CellValue.Int(value: 0L))]),
            ]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = state,
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "accept"), Mode: ActionTriggerMode.Edge, Effects: [
                    new ActionEffect.SetState(State: "handCount", FromState: "$reduce:count:hand"),
                    new ActionEffect.SetState(State: "verdict", Value: 1),
                    new ActionEffect.SetState(State: "turn", Expression: ExpressionProgram.Parse(text: "1 - turn")),
                ]),
            ],
            SearchRaw = new WorldSearchSection(Jobs: [
                new WorldSearchRow(Name: "search", Tokens: "cards", Zones: ["deck", "hand", "discard"], Turn: "turn", Verdict: "verdict",
                    Shapes: [(shape ?? new WorldSearchShape.Transferred(Selector: selector))], Legal: "legal", Counts: "counts",
                    Score: (best ? "handCount" : null), Best: (best ? "best" : null)),
            ]),
        };

        return definition;
    }

    [Fact]
    public void OnlyTheTopOfAPileMayMoveAndItMayMoveOntoEitherOtherPile() {
        var definition = PileWorld();

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(3, status.Cells);
        Assert.Equal(2L, status.Count);
        Assert.Equal(0L, fixture.KeyedValue(key: "a", row: "counts"));
        Assert.Equal(0L, fixture.KeyedValue(key: "b", row: "counts"));
        Assert.Equal(2L, fixture.KeyedValue(key: "c", row: "counts"));
        Assert.Equal((1L << 1) | (1L << 2), fixture.KeyedValue(key: "c", row: "legal"));
        // The section's piles never moved: the frame took every transfer.
        Assert.Equal("abc", string.Concat(values: fixture.Row(name: "deck").Cells!.Select(selector: c => c.Key.Value)));
        Assert.Empty(collection: (fixture.Row(name: "hand").Cells ?? []));
    }
    [Fact]
    public void TheFirstEndOfAPileMovesWhenTheShapeSaysSo() {
        var definition = PileWorld(selector: ZoneSelector.First);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        Assert.Equal(2L, fixture.KeyedValue(key: "a", row: "counts"));
        Assert.Equal(0L, fixture.KeyedValue(key: "c", row: "counts"));
    }
    [Fact]
    public void TheJudgeReadsThePileTheTransferLandedOnAndBestNamesThatZone() {
        var definition = PileWorld(best: true);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var invalid), userMessage: invalid);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var status = fixture.SettleSearch();

        Assert.True(condition: status.Done, userMessage: status.ToString());
        // Moving c onto the hand makes the hand count one through the frame; onto the discard it stays zero.
        Assert.Equal(2, status.BestToken);
        Assert.Equal(1, status.BestTarget);
        Assert.Equal(1L, status.BestScore);
        Assert.Equal(1L, fixture.KeyedValue(key: "to", row: "best"));
        Assert.Equal(0L, fixture.KeyedValue("handCount", WorldStateRow.SlotKey.Value));
    }
    [Fact]
    public void AZoneJobRefusesABoardShapeAndABoardJobRefusesATransfer() {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: PileWorld(shape: new WorldSearchShape.Relocate()), reason: out var zoneReason));
        Assert.Contains(actualString: zoneReason, expectedSubstring: "transfer");

        var ring = new LatticeTopology.Ring(Name: "board", Origin: new DocumentVector3(x: 0, y: 0, z: 0), CellSize: 1, Width: 6);
        var board = PairWorld(ring, a: 0L, b: 3L);
        var mixed = board with {
            SearchRaw = new WorldSearchSection(Jobs: [board.SearchRaw!.Jobs![0] with { Shapes = [new WorldSearchShape.Transferred()] }]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: mixed, reason: out var boardReason));
        Assert.Contains(actualString: boardReason, expectedSubstring: "zones");
    }
}
