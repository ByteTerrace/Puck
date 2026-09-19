using Xunit;
using Puck.Maths;

namespace Puck.World.Tests;

public sealed class ChineseCheckersLawTests {
    private static readonly Lazy<WorldDefinition> Source = new(() => AuthoredGameFixtures.Load(
        "src/Puck.World/Assets/worlds/games/chinese-checkers.world.json"));

    private static WorldDefinition Game => Source.Value;
    private static long[] Cells(string row) => WorldDefinitionRows.FindStateRow(Game.State, row)!.Cells!.Select(c => c.Value.Raw).ToArray();
    private static CompiledTopology Star => TopologyCompilation.Find(Game.StateRaw!.Lattices, "ccStar")!;

    private static WorldDefinition Position(long[] before, long[] after, int turn = 0, int winner = -1) {
        var board = new long[121];
        var codes = Cells("pieceCode");
        for (var i = 0; i < before.Length; i++) { board[before[i]] = codes[i]; }
        var rows = Game.StateRaw!.World!.Select(row => row.Name.Value switch {
            "pieceCell" => row with { Cells = row.Cells!.Select((c, i) => c with { Value = CellValue.Int(after[i]) }).ToArray() },
            "lastCell" => row with { Cells = row.Cells!.Select((c, i) => c with { Value = CellValue.Int(before[i]) }).ToArray() },
            "lastLegal" => row with { Cells = board.Select((v, i) => new StateCell(CellName.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), CellValue.Int(v))).ToArray() },
            "settleHold" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(60))] },
            "turn" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(turn))] },
            "winner" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(winner))] },
            _ => row,
        }).ToArray();
        return Game with {
            StateRaw = Game.StateRaw with { World = rows },
            SearchRaw = null,
            Rules = Game.Rules!.Where(r => !r.Name.Value.StartsWith("cc-ai-", StringComparison.Ordinal) &&
                !r.Name.Value.StartsWith("cc-settle-", StringComparison.Ordinal) && r.Name.Value != "cc-observe" &&
                !r.Name.Value.StartsWith("cc-score-", StringComparison.Ordinal)).ToArray(),
        };
    }

    // Independent endpoint oracle: explicit paths over the star, with every other marble stationary.
    private static HashSet<int> Destinations(long[] before, int mover) {
        var occupied = before.Where((_, i) => i != mover).Select(v => (int)v).ToHashSet();
        var source = (int)before[mover];
        var result = new HashSet<int>();
        var seen = new HashSet<int> { source };
        var pending = new Stack<int>();
        pending.Push(source);
        while (pending.TryPop(out var cell)) {
            for (var direction = 0; direction < 6; direction++) {
                var middle = Star.Neighbour(cell, direction);
                if (middle < 0 || !occupied.Contains(middle)) { continue; }
                var to = Star.Neighbour(middle, direction);
                if (to >= 0 && !occupied.Contains(to) && seen.Add(to)) { result.Add(to); pending.Push(to); }
            }
        }
        for (var direction = 0; direction < 6; direction++) {
            var to = Star.Neighbour(source, direction);
            if (to >= 0 && !occupied.Contains(to)) { result.Add(to); }
        }
        result.Remove(source);
        return result;
    }

    [Fact]
    public void FullStarHasSixTenHoleCampsAndThirtyDistinctMarbles() {
        Assert.Equal(121, Star.CellCount);
        var camps = Cells("camp");
        Assert.Equal(61, camps.Count(c => c == -1));
        for (var camp = 0; camp < 6; camp++) { Assert.Equal(10, camps.Count(c => c == camp)); }
        Assert.Equal(30, Cells("lastCell").Distinct().Count());
        Assert.Equal(30, Game.Placements.Count(p => p.Inhabit is not null));
    }

    [Fact]
    public void EveryOpeningMoveAndSeededMidgameMoveMatchesTheEndpointOracle() {
        var before = Cells("lastCell");
        var fixture = new RuleArenaFixture(Position(before, before));
        var random = new Random(1809);
        for (var position = 0; position < 4; position++) {
            if (position > 0) {
                before = Enumerable.Range(0, 121).OrderBy(_ => random.Next()).Take(30).Select(i => (long)i).ToArray();
            }
            for (var mover = 0; mover < 30; mover++) {
                var legal = Destinations(before, mover);
                for (var target = 0; target < 121; target++) {
                    if (target == before[mover]) { continue; }
                    var after = before.ToArray();
                    after[mover] = target;
                    fixture.Evaluate(Position(before, after, mover / 10));
                    Assert.Equal(legal.Contains(target) ? 1 : 0, fixture.Read("verdict"));
                    Assert.Equal(legal.Contains(target) ? (mover / 10 + 1) % 3 : mover / 10, fixture.Read("turn"));
                    Assert.Equal(legal.Contains(target) ? target : before[mover], fixture.Read("lastCell", $"piece{mover}"));
                }
            }
        }
    }

    [Theory]
    [InlineData("opponent")]
    [InlineData("missing")]
    [InlineData("collision")]
    [InlineData("two")]
    [InlineData("finished")]
    public void IllegalPhysicalEditsKeepTheLastLegalPosition(string edit) {
        var before = Cells("lastCell");
        var after = before.ToArray();
        var destination = Destinations(before, 0).FirstOrDefault(-1);
        var mover = 0;
        while (destination < 0) { destination = Destinations(before, ++mover).FirstOrDefault(-1); }
        after[mover] = destination;
        if (edit == "missing") { after[mover] = -1; }
        if (edit == "collision") { after[mover] = before[(mover + 1) % 10]; }
        if (edit == "two") { after[(mover + 1) % 10] = -1; }
        var turn = edit == "opponent" ? 1 : 0;
        var position = Position(before, after, turn, edit == "finished" ? 2 : -1);
        var fixture = new RuleArenaFixture(position);
        fixture.Evaluate(position);
        Assert.Equal(0, fixture.Read("verdict"));
        Assert.Equal(turn, fixture.Read("turn"));
        Assert.Equal(0, fixture.Read("moveCount"));
        for (var i = 0; i < 30; i++) { Assert.Equal(before[i], fixture.Read("lastCell", $"piece{i}")); }
    }

    [Fact]
    public void OnlyALegalTenthArrivalWinsAndTheWinnerIsLatched() {
        var camps = Cells("camp");
        var goal = Enumerable.Range(0, 121).Where(i => camps[i] == 3).ToArray();
        var final = goal.First(i => Enumerable.Range(0, 6).Any(d => Star.Neighbour(i, d) is var n && n >= 0 && camps[n] == -1));
        var from = Enumerable.Range(0, 6).Select(d => Star.Neighbour(final, d)).First(n => n >= 0 && camps[n] == -1);
        var before = goal.Where(i => i != final).Select(i => (long)i).Append(from).ToList();
        before.AddRange(Enumerable.Range(0, 121).Where(i => i != final && !before.Contains(i)).Take(20).Select(i => (long)i));
        var start = before.ToArray();
        var after = start.ToArray();
        after[9] = final;
        var fixture = new RuleArenaFixture(Position(start, start));
        fixture.Evaluate(Position(start, start));
        Assert.Equal(9, fixture.Read("home", "0"));
        Assert.Equal(-1, fixture.Read("winner"));
        fixture.Evaluate(Position(start, after, turn: 1));
        Assert.Equal(-1, fixture.Read("winner"));
        fixture.Evaluate(Position(start, after));
        Assert.Equal(1, fixture.Read("verdict"));
        Assert.Equal(0, fixture.Read("winner"));
        fixture.Evaluate(Position(after, start, turn: 1, winner: 0));
        Assert.Equal(0, fixture.Read("verdict"));
        Assert.Equal(0, fixture.Read("winner"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AiMovesOnePhysicalMarbleAndThenWaits(int seat) {
        var world = Game with { StateRaw = Game.StateRaw! with { World = Game.StateRaw.World!.Select(row =>
            row.Name.Value is "aiSide" or "turn" ? row with { Cells = [new(StateRow.SlotKey, CellValue.Int(seat))] } : row).ToArray() } };
        using var fixture = Fixtures.FreshServer(world);
        long Read(string row) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells![0].Value.Raw;
        for (var tick = 0; tick < 2400 && Read("moveCount") == 0; tick++) { fixture.Step(); }
        Assert.Equal(1, Read("moveCount"));
        for (var tick = 0; tick < 120; tick++) { fixture.Step(); }
        Assert.Equal(1, Read("moveCount"));
        Assert.Equal((seat + 1) % 3, Read("turn"));
        Assert.Equal(1, Read("verdict"));
        Assert.Equal(0, Read("missingCount"));
        Assert.Equal(0, Read("boardCollisions"));
        Assert.Equal(0, Read("aiEnabled"));
    }

    [Fact]
    public void AHumanCanCorrectAnIllegalMoveAndTheAiReplies() {
        var world = Game with { StateRaw = Game.StateRaw! with { World = Game.StateRaw.World!.Select(row =>
            row.Name.Value == "aiSide" ? row with { Cells = [new(StateRow.SlotKey, CellValue.Int(1))] } : row).ToArray() } };
        using var fixture = Fixtures.FreshServer(world);
        long Read(string row) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells![0].Value.Raw;
        void Step(int ticks) { for (var tick = 0; tick < ticks; tick++) { fixture.Step(); } }
        var before = Cells("lastCell");
        var mover = Enumerable.Range(0, 10).First(i => Destinations(before, i).Count != 0);
        var legal = Destinations(before, mover);
        var bad = Enumerable.Range(0, 121).First(i => !before.Contains(i) && !legal.Contains(i));
        var placement = Game.Placements.Select((p, i) => (p, i)).Single(pair => pair.p.Id == $"piece{mover}").i;
        Step(500);
        var body = fixture.Server.Body(fixture.Server.Population.BodyForPlacementOrdinal(placement))!;
        var topology = WorldTopologyCompilation.Find(fixture.Server.Definition, "ccStar")!;
        void Place(int cell) => body.Pose(position: topology.CellCentre(cell), yawRadians: FixedQ4816.Zero,
            pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);
        Place(bad);
        Step(400);
        Assert.Equal(0, Read("verdict"));
        Assert.Equal(0, Read("turn"));
        Assert.Equal(0, Read("moveCount"));
        Place(legal.First());
        for (var tick = 0; tick < 2400 && Read("moveCount") < 2; tick++) { fixture.Step(); }
        Assert.Equal(2, Read("moveCount"));
        Assert.Equal(2, Read("turn"));
        Assert.Equal(1, Read("verdict"));
        Assert.Equal(1, Read("illegalCount"));
    }

    [Fact]
    public void ChangingThePositionInvalidatesAnOtherwiseMatchingAiAnswer() {
        var world = Game with { Rules = [
            new WorldRule(Name: CellName.Parse("change-position"),
                Gate: new ActionPredicate.CompareState(State: "$tick", Comparison: ActionStateComparison.Equal, Value: 500),
                Effects: [
                    new ActionEffect.SetState(State: "aiSide", Value: 0),
                    new ActionEffect.SetState(State: "aiBest", Key: "token", Value: 0),
                    new ActionEffect.SetState(State: "aiBest", Key: "to", Value: 60),
                    new ActionEffect.SetState(State: "aiBest", Key: "revision", Expression: ExpressionProgram.Parse("aiRevision")),
                    new ActionEffect.SetState(State: "turn", Value: 1),
                    new ActionEffect.SetState(State: "aiSide", Value: 1),
                ]),
            .. Game.Rules!,
        ] };
        using var fixture = Fixtures.FreshServer(world);
        for (var tick = 0; tick < 900; tick++) { fixture.Step(); }
        var observed = WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "pieceCell")!.Cells!;
        Assert.Equal(Cells("lastCell")[0], observed.Single(c => c.Key.Value == "piece0").Value.Raw);
        Assert.NotEqual(60, observed.Single(c => c.Key.Value == "piece0").Value.Raw);
    }
}
