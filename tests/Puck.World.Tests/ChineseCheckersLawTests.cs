using Xunit;
using Puck.Maths;

namespace Puck.World.Tests;

/// <summary>Gameplay laws compiled directly from the Parlor package's canonical Chinese Checkers source.</summary>
public sealed class ChineseCheckersLawTests {
    private static readonly Lazy<WorldDefinition> Source = new(valueFactory: () => AuthoredGameFixtures.Load(
        "worlds/parlor/chinese-checkers.puck"));

    private static WorldDefinition Game => Source.Value;

    private static long[] Cells(string row) => WorldDefinitionRows.FindStateRow(Game.State, row)!.Cells!.Select(selector: c => c.Value.Raw).ToArray();

    private static CompiledTopology Star => TopologyCompilation.Find(lattices: Game.StateRaw!.Lattices, name: "ccStar")!;

    private static WorldDefinition Position(long[] before, long[] after, int turn = 0, int winner = -1) => Move(
        after: after,
        basis: Basis(before: before, winner: winner),
        turn: turn
    );
    // The position a move is judged from: every marble on its last legal cell. A move replaces only the pieceCell and
    // turn rows, so every other row stays the same instance across the moves judged from one basis.
    private static WorldDefinition Basis(long[] before, int winner = -1) {
        var board = new long[121];
        var codes = Cells(row: "pieceCode");

        for (var i = 0; (i < before.Length); i++) { board[before[i]] = codes[i]; }
        var rows = Game.StateRaw!.World!.Select(selector: row => row.Name.Value switch {
            "lastCell" => row with { Cells = row.Cells!.Select(selector: (c, i) => c with { Value = CellValue.Int(value: before[i]) }).ToArray() },
            "lastLegal" => row with { Cells = board.Select(selector: (v, i) => new StateCell(CellName.Parse(candidate: i.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)), CellValue.Int(value: v))).ToArray() },
            "settleHold" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(value: 60))] },
            "winner" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(value: winner))] },
            _ => row,
        }).ToArray();

        return Game with {
            StateRaw = Game.StateRaw with { World = rows },
            SearchRaw = null,
            Rules = Game.Rules!.Where(predicate: r => (!r.Name.Value.StartsWith(comparisonType: StringComparison.Ordinal, value: "cc-ai-") &&
                !r.Name.Value.StartsWith(comparisonType: StringComparison.Ordinal, value: "cc-settle-") && (r.Name.Value != "cc-observe") &&
                !r.Name.Value.StartsWith(comparisonType: StringComparison.Ordinal, value: "cc-score-"))).ToArray(),
        };
    }
    private static WorldDefinition Move(WorldDefinition basis, long[] after, int turn) => basis with {
        StateRaw = basis.StateRaw! with {
            World = basis.StateRaw.World!.Select(selector: row => row.Name.Value switch {
                "pieceCell" => row with { Cells = row.Cells!.Select(selector: (c, i) => c with { Value = CellValue.Int(value: after[i]) }).ToArray() },
                "turn" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(value: turn))] },
                _ => row,
            }).ToArray(),
        },
    };
    // Independent endpoint oracle: explicit paths over the star, with every other marble stationary.
    private static HashSet<int> Destinations(long[] before, int mover) {
        var occupied = before.Where(predicate: (_, i) => (i != mover)).Select(selector: v => ((int)v)).ToHashSet();
        var source = ((int)before[mover]);
        var result = new HashSet<int>();
        var seen = new HashSet<int> { source };
        var pending = new Stack<int>();

        pending.Push(item: source);
        while (pending.TryPop(result: out var cell)) {
            for (var direction = 0; (direction < 6); direction++) {
                var middle = Star.Neighbour(cell: cell, direction: direction);

                if ((middle < 0) || !occupied.Contains(item: middle)) { continue; }
                var to = Star.Neighbour(cell: middle, direction: direction);

                if ((to >= 0) && !occupied.Contains(item: to) && seen.Add(item: to)) { result.Add(item: to); pending.Push(item: to); }
            }
        }
        for (var direction = 0; (direction < 6); direction++) {
            var to = Star.Neighbour(cell: source, direction: direction);

            if ((to >= 0) && !occupied.Contains(item: to)) { result.Add(item: to); }
        }
        result.Remove(item: source);
        return result;
    }

    [Fact]
    public void FullStarHasSixTenHoleCampsAndThirtyDistinctMarbles() {
        Assert.Equal(121, Star.CellCount);
        var camps = Cells(row: "camp");

        Assert.Equal(61, camps.Count(predicate: c => (c == -1)));
        for (var camp = 0; (camp < 6); camp++) { Assert.Equal(10, camps.Count(predicate: c => (c == camp))); }
        Assert.Equal(30, Cells(row: "lastCell").Distinct().Count());
        Assert.Equal(30, Game.Placements.Count(predicate: p => (p.Inhabit is not null)));
    }
    [Fact]
    public void EveryOpeningMoveAndSeededMidgameMoveMatchesTheEndpointOracle() {
        var before = Cells(row: "lastCell");
        var fixture = new RuleArenaFixture(definition: Position(before, before));
        var random = new Random(Seed: 1809);

        for (var position = 0; (position < 4); position++) {
            if (position > 0) {
                before = Enumerable.Range(count: 121, start: 0).OrderBy(keySelector: _ => random.Next()).Take(count: 30).Select(selector: i => ((long)i)).ToArray();
            }
            var basis = Basis(before);

            for (var mover = 0; (mover < 30); mover++) {
                var legal = Destinations(before: before, mover: mover);

                for (var target = 0; (target < 121); target++) {
                    if (target == before[mover]) { continue; }
                    var after = before.ToArray();

                    after[mover] = target;
                    fixture.Evaluate(position: Move(after: after, basis: basis, turn: (mover / 10)));
                    Assert.Equal((legal.Contains(item: target) ? 1 : 0), fixture.Read("verdict"));
                    Assert.Equal((legal.Contains(item: target) ? (((mover / 10) + 1) % 3) : (mover / 10)), fixture.Read("turn"));
                    Assert.Equal((legal.Contains(item: target) ? target : before[mover]), fixture.Read(key: $"piece{mover}", row: "lastCell"));
                }
            }
        }
    }
    [InlineData("opponent")]
    [InlineData("missing")]
    [InlineData("collision")]
    [InlineData("two")]
    [InlineData("finished")]
    [Theory]
    public void IllegalPhysicalEditsKeepTheLastLegalPosition(string edit) {
        var before = Cells(row: "lastCell");
        var after = before.ToArray();
        var destination = Destinations(before: before, mover: 0).FirstOrDefault(defaultValue: -1);
        var mover = 0;

        while (destination < 0) { destination = Destinations(before: before, mover: ++mover).FirstOrDefault(defaultValue: -1); }
        after[mover] = destination;
        if (edit == "missing") { after[mover] = -1; }
        if (edit == "collision") { after[mover] = before[((mover + 1) % 10)]; }
        if (edit == "two") { after[((mover + 1) % 10)] = -1; }
        var turn = ((edit == "opponent") ? 1 : 0);
        var position = Position(after: after, before: before, turn: turn, winner: ((edit == "finished") ? 2 : -1));
        var fixture = new RuleArenaFixture(definition: position);

        fixture.Evaluate(position: position);
        Assert.Equal(0, fixture.Read("verdict"));
        Assert.Equal(turn, fixture.Read("turn"));
        Assert.Equal(0, fixture.Read("moveCount"));
        for (var i = 0; (i < 30); i++) { Assert.Equal(before[i], fixture.Read(key: $"piece{i}", row: "lastCell")); }
    }
    [Fact]
    public void OnlyALegalTenthArrivalWinsAndTheWinnerIsLatched() {
        var camps = Cells(row: "camp");
        var goal = Enumerable.Range(count: 121, start: 0).Where(predicate: i => (camps[i] == 3)).ToArray();
        var final = goal.First(predicate: i => Enumerable.Range(count: 6, start: 0).Any(predicate: d => ((Star.Neighbour(cell: i, direction: d) is var n) && (n >= 0) && (camps[n] == -1))));
        var from = Enumerable.Range(count: 6, start: 0).Select(selector: d => Star.Neighbour(cell: final, direction: d)).First(predicate: n => ((n >= 0) && (camps[n] == -1)));
        var before = goal.Where(predicate: i => (i != final)).Select(selector: i => ((long)i)).Append(element: from).ToList();

        before.AddRange(collection: Enumerable.Range(count: 121, start: 0).Where(predicate: i => ((i != final) && !before.Contains(item: i))).Take(count: 20).Select(selector: i => ((long)i)));
        var start = before.ToArray();
        var after = start.ToArray();

        after[9] = final;
        var fixture = new RuleArenaFixture(definition: Position(start, start));

        fixture.Evaluate(position: Position(start, start));
        Assert.Equal(9, fixture.Read(key: "0", row: "home"));
        Assert.Equal(-1, fixture.Read("winner"));
        fixture.Evaluate(position: Position(start, after, turn: 1));
        Assert.Equal(-1, fixture.Read("winner"));
        fixture.Evaluate(position: Position(start, after));
        Assert.Equal(1, fixture.Read("verdict"));
        Assert.Equal(0, fixture.Read("winner"));
        fixture.Evaluate(position: Position(after, start, turn: 1, winner: 0));
        Assert.Equal(0, fixture.Read("verdict"));
        Assert.Equal(0, fixture.Read("winner"));
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void AiMovesOnePhysicalMarbleAndThenWaits(int seat) {
        var world = Game with {
            StateRaw = Game.StateRaw! with {
                World = Game.StateRaw.World!.Select(selector: row =>
            ((row.Name.Value is "aiSide" or "turn") ? row with { Cells = [new(StateRow.SlotKey, CellValue.Int(value: seat))] } : row)).ToArray(),
            },
        };
        using var fixture = Fixtures.FreshServer(world);

        long Read(string row) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells![0].Value.Raw;
        for (var tick = 0; ((tick < 2400) && (Read(row: "moveCount") == 0)); tick++) { fixture.Step(); }
        Assert.Equal(1, Read(row: "moveCount"));
        for (var tick = 0; (tick < 120); tick++) { fixture.Step(); }
        Assert.Equal(1, Read(row: "moveCount"));
        Assert.Equal(((seat + 1) % 3), Read(row: "turn"));
        Assert.Equal(1, Read(row: "verdict"));
        Assert.Equal(0, Read(row: "missingCount"));
        Assert.Equal(0, Read(row: "boardCollisions"));
        Assert.Equal(0, Read(row: "aiEnabled"));
    }
    [Fact]
    public void AHumanCanCorrectAnIllegalMoveAndTheAiReplies() {
        var world = Game with {
            StateRaw = Game.StateRaw! with {
                World = Game.StateRaw.World!.Select(selector: row =>
            ((row.Name.Value == "aiSide") ? row with { Cells = [new(StateRow.SlotKey, CellValue.Int(value: 1))] } : row)).ToArray(),
            },
        };
        using var fixture = Fixtures.FreshServer(world);

        long Read(string row) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells![0].Value.Raw;
        void Step(int ticks) { for (var tick = 0; (tick < ticks); tick++) { fixture.Step(); } }
        var before = Cells(row: "lastCell");
        var mover = Enumerable.Range(count: 10, start: 0).First(predicate: i => (Destinations(before: before, mover: i).Count != 0));
        var legal = Destinations(before: before, mover: mover);
        var bad = Enumerable.Range(count: 121, start: 0).First(predicate: i => (!before.Contains(value: i) && !legal.Contains(item: i)));
        var placement = Game.Placements.Select(selector: (p, i) => (p, i)).Single(predicate: pair => (pair.p.Id == $"piece{mover}")).i;

        Step(ticks: 500);
        var body = fixture.Server.Body(index: fixture.Server.Population.BodyForPlacementOrdinal(ordinal: placement))!;
        var topology = WorldTopologyCompilation.Find(definition: fixture.Server.Definition, name: "ccStar")!;

        void Place(int cell) => body.Pose(position: topology.CellCentre(cell: cell), yawRadians: FixedQ4816.Zero,
            pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);
        Place(cell: bad);
        Step(ticks: 400);
        Assert.Equal(0, Read(row: "verdict"));
        Assert.Equal(0, Read(row: "turn"));
        Assert.Equal(0, Read(row: "moveCount"));
        Place(cell: legal.First());
        for (var tick = 0; ((tick < 2400) && (Read(row: "moveCount") < 2)); tick++) { fixture.Step(); }
        Assert.Equal(2, Read(row: "moveCount"));
        Assert.Equal(2, Read(row: "turn"));
        Assert.Equal(1, Read(row: "verdict"));
        Assert.Equal(1, Read(row: "illegalCount"));
    }
    [Fact]
    public void ChangingThePositionInvalidatesAnOtherwiseMatchingAiAnswer() {
        var world = Game with {
            Rules = [
            new WorldRule(Name: CellName.Parse(candidate: "change-position"),
                Gate: new ActionPredicate.CompareState(State: "$tick", Comparison: ExpressionOp.Equal, Value: 500),
                Effects: [
                    new ActionEffect.SetState(State: "aiSide", Value: 0),
                    new ActionEffect.SetState(State: "aiBest", Key: "token", Value: 0),
                    new ActionEffect.SetState(State: "aiBest", Key: "to", Value: 60),
                    new ActionEffect.SetState(State: "aiBest", Key: "revision", Expression: ExpressionProgram.Parse(text: "aiRevision")),
                    new ActionEffect.SetState(State: "turn", Value: 1),
                    new ActionEffect.SetState(State: "aiSide", Value: 1),
                ]),
            .. Game.Rules!,
        ],
        };
        using var fixture = Fixtures.FreshServer(world);

        for (var tick = 0; (tick < 900); tick++) { fixture.Step(); }
        var observed = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "pieceCell")!.Cells!;

        Assert.Equal(Cells(row: "lastCell")[0], observed.Single(predicate: c => (c.Key.Value == "piece0")).Value.Raw);
        Assert.NotEqual(60, observed.Single(predicate: c => (c.Key.Value == "piece0")).Value.Raw);
    }
}

