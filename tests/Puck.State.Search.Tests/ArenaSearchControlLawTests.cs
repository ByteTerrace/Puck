using Xunit;
using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

public sealed class ArenaSearchControlLawTests {
    private static void Write(Position position, string row, long value) => Assert.True(condition: position.Arena.TryWrite(
        rowOrdinal: position.Ordinal(name: row), key: position.SlotKey, write: StateWriteKind.Set, operand: value, reason: out _));

    [Fact]
    public void DisabledSearchDoesNoWorkAndCompletedAnswersCarryTheirPositionRevision() {
        var rows = Board(cells: 4, tokens: 1).Where(predicate: row => (row.Name.Value != "best")).ToArray();
        var position = new Position([
            .. rows, Slot(name: "enabled", value: 0), Slot(name: "revision", value: 7),
            Keyed("best", ("token", -1), ("to", -1), ("score", 0), ("revision", -1)),
        ]);
        var plan = new SearchPlan(Name: "moves", Tokens: "piece", Topology: null, Zones: [], CellCount: 4,
            Turn: "turn", Verdict: "verdict", Off: -1, Nodes: 1, Work: SearchWork.NodeBounded(judge: 1L), Depth: 1, Best: "best",
            Shapes: [new(SearchShapeKind.Relocate, false, [], -1)], Enabled: "enabled", Revision: "revision") { Scored = true };
        var judge = RuleJudge(position, [AcceptEveryCandidate()], score: "$search:ply");
        var search = Build(position, plan, judge);

        for (var step = 0; (step < 10); step++) {
            Assert.False(condition: search.Step(tick: 1, engineTick: 1, apply: _ => throw new InvalidOperationException(message: "disabled search landed")));
        }
        Assert.Equal(0L, search.Status(index: 0).Nodes);
        Write(position: position, row: "enabled", value: 1);
        search.Step(apply: _ => true, engineTick: 1, tick: 1);
        var nodes = search.Status(index: 0).Nodes;

        Assert.True(condition: (nodes > 0));
        Write(position: position, row: "enabled", value: 0);
        search.Step(apply: _ => true, engineTick: 1, tick: 1);
        Assert.Equal(nodes, search.Status(index: 0).Nodes);
        Write(position: position, row: "revision", value: 8);
        Write(position: position, row: "enabled", value: 1);
        var landed = RunArena(catalog: position.Catalog, search: search);

        Assert.Contains(collection: landed, filter: cell => ((cell.Row == "best") && (cell.Key == "revision") && (cell.Value == 8)));
        Assert.Contains(collection: landed, filter: cell => ((cell.Row == "best") && (cell.Key == "score") && (cell.Value == 1)));
    }
    [Fact]
    public void SearchPlyIsZeroOnLiveHostsAndTracksEachJudgeView() {
        var position = new Position(Board(cells: 4, tokens: 1));
        IStateReader live = new Puck.State.Rules.ArenaEffectHost(position.Arena);

        Assert.Equal(0, live.SearchPly);
        var judge = RuleJudge(position, [AcceptEveryCandidate()], score: "$search:ply");

        Assert.Equal(3, judge.Score(view: new ArenaSearchView(position.Arena, 1, 1, 3)));
        Assert.Equal(1, judge.Score(view: new ArenaSearchView(position.Arena, 1, 1, 1)));
        Assert.Equal(0, live.SearchPly);
    }
}
