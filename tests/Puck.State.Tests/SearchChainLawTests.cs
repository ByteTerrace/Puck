using System.Numerics;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Tests;

/// <summary>The <c>jump</c> shape's chain (<see cref="SearchShapePlan.MaxHops"/>) and the walk's max-n accept test
/// (<see cref="SearchPlan.Scores"/>) — bare-row fixtures over <see cref="SearchRuntime"/>, no world or document
/// concept in reach, matching <see cref="SearchRuntimeLawTests"/>'s own register.</summary>
public sealed class SearchChainLawTests {
    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<StateRow> Rows => rows;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
    }

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateRow Slot(string name, long value) =>
        new(Name: Name(name), Kind: CellKind.Int, Cells: [new StateCell(Key: StateRow.SlotKey, Value: value)]);
    private static StateRow Keyed(string name, params (string Key, long Value)[] cells) =>
        new(Name: Name(name), Kind: CellKind.Int, Capacity: 8, Cells: [.. cells.Select(static c => new StateCell(Key: Name(c.Key), Value: c.Value))]);
    private static CompiledTopology Strip(int width) => TopologyCompilation.Compile(
        topology: new LatticeTopology.Grid(Name: "strip", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: width, Depth: 1),
        anchorOffset: Vector3.Zero
    );

    // A 1x9 strip: "a" at 0, an obstacle at every odd cell (1, 3, 5, 7). Jumping east from 0 clears one obstacle
    // per hop and lands on the next even cell; a chain may stop after any hop or continue to the next.
    private static (SearchRuntime Runtime, SearchPlan Plan) ChainFixture(int maxHops) {
        StateRow[] rows = [
            Keyed(name: "piece", ("a", 0L), ("b", 1L), ("c", 3L), ("d", 5L), ("e", 7L)),
            Slot(name: "turn", value: 0L),
            Slot(name: "verdict", value: 0L),
            Keyed(name: "legal", ("a", 0L), ("b", 0L), ("c", 0L), ("d", 0L), ("e", 0L)),
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);
        var judge = new[] {
            RuleCompiler.Compile(
                rule: new Rule(Name: Name("accept"), Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1m),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse(text: "1 - turn")),
                ]),
                context: context
            ),
        };
        var topology = Strip(width: 9);
        var plan = new SearchPlan(
            Name: "chain", Tokens: "piece", Topology: topology, Zones: [], CellCount: 9, Turn: "turn", Verdict: "verdict", Off: -1L,
            Nodes: 256, JudgeCost: 1L, Depth: 1, Score: null, Best: null,
            Shapes: [new SearchShapePlan(Kind: SearchShapeKind.Jump, Displace: false, Directions: [topology.Direction(token: "E")], PairWithIndex: -1, MaxHops: maxHops)],
            Legal: "legal"
        );
        var runtime = new SearchRuntime(live: () => rows);

        runtime.Rebuild(plans: [plan], judge: judge, rows: rows, catalog: catalog, topology: name => ((name == "strip") ? topology : null), patterns: CompiledPatterns.Empty, tables: []);

        return (runtime, plan);
    }

    [Fact]
    public void AChainOfThreeHopsEnumeratesExactlyTheLegalSequences() {
        var (runtime, plan) = ChainFixture(maxHops: 3);

        Assert.Equal(expected: 8, actual: plan.Shapes[0].CandidateCount(cellCount: plan.CellCount));

        IReadOnlyList<SearchWrite>? landed = null;
        var installed = runtime.Step(tick: 1UL, apply: writes => { landed = writes; return true; });

        Assert.True(installed);
        // A hop chain over one occupied cell per step, on this board, reaches only 2, 4, and 6 — one hop, two hops,
        // and the full three; nothing else in the shape's 8-candidate encoding resolves (a stop followed by a
        // further hop, or the all-stop index, name no chain at all).
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "legal", Key: "a", Value: 0b1010100L }));
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "legal", Key: "b", Value: 0L }));
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "legal", Key: "c", Value: 0L }));
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "legal", Key: "d", Value: 0L }));
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "legal", Key: "e", Value: 0L }));
    }

    [Fact]
    public void MaxHopsBoundsTheChainsEnumeration() {
        var (runtime, plan) = ChainFixture(maxHops: 2);

        Assert.Equal(expected: 4, actual: plan.Shapes[0].CandidateCount(cellCount: plan.CellCount));

        IReadOnlyList<SearchWrite>? landed = null;
        var installed = runtime.Step(tick: 1UL, apply: writes => { landed = writes; return true; });

        Assert.True(installed);
        // The same board still has an obstacle at 7 a third hop could clear, but maxHops 2 never tries it: only
        // one and two hops (cells 2 and 4) land, never 6.
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "legal", Key: "a", Value: 0b0010100L }));
    }

    // Depth 2, three seats, and a scores row: root (seat 0) moves to cell 1 or 2; whichever it picks, seat 1 then
    // has exactly one reply (cell 1 to 3, or cell 2 to 4), landing the position the scores row is read from. Seat
    // 0's own value is 10 through cell 1 and 8 through cell 2 — max-n prefers cell 1. A two-sided reading that
    // instead negated seat 1's own value at the reply (9 through cell 1, 1 through cell 2, so -9 and -1) would have
    // preferred cell 2, since -1 exceeds -9: the two readings disagree, and only max-n's is asked for here.
    [Fact]
    public void MaxNMaximizesTheMoversOwnSeatWhereNegatingTheReplyWouldChooseDifferently() {
        StateRow[] rows = [
            Keyed(name: "piece", ("p", 0L)),
            Slot(name: "turn", value: 0L),
            Slot(name: "branch", value: 0L),
            Slot(name: "isRoot", value: 0L),
            Slot(name: "isReply", value: 0L),
            Slot(name: "accepted", value: 0L),
            Slot(name: "verdict", value: 0L),
            Keyed(name: "scores", ("0", 0L), ("1", 0L), ("2", 0L)),
            Keyed(name: "best", ("token", -1L), ("to", -1L), ("score", 0L)),
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);
        var judge = new[] {
            RuleCompiler.Compile(
                rule: new Rule(Name: Name("judge"), Effects: [
                    new ActionEffect.SetState(State: "isRoot", Expression: ValueExpression.Parse(text: "(turn == 0) & ((piece[p] == 1) | (piece[p] == 2))")),
                    new ActionEffect.SetState(State: "isReply", Expression: ValueExpression.Parse(text: "(turn == 1) & (((branch == 1) & (piece[p] == 3)) | ((branch == 2) & (piece[p] == 4)))")),
                    new ActionEffect.SetState(State: "accepted", Expression: ValueExpression.Parse(text: "isRoot | isReply")),
                    new ActionEffect.SetState(State: "verdict", Expression: ValueExpression.Parse(text: "accepted")),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse(text: "turn + accepted")),
                    new ActionEffect.SetState(State: "branch", Expression: ValueExpression.Parse(text: "isRoot ? piece[p] : branch")),
                    new ActionEffect.SetState(State: "scores", Key: "0", Expression: ValueExpression.Parse(text: "(piece[p] == 3) ? 10 : ((piece[p] == 4) ? 8 : 0)")),
                    new ActionEffect.SetState(State: "scores", Key: "1", Expression: ValueExpression.Parse(text: "(piece[p] == 3) ? 9 : ((piece[p] == 4) ? 1 : 0)")),
                    new ActionEffect.SetState(State: "scores", Key: "2", Value: 0m),
                ]),
                context: context
            ),
        };
        var plan = new SearchPlan(
            Name: "maxn", Tokens: "piece", Topology: null, Zones: [], CellCount: 6, Turn: "turn", Verdict: "verdict", Off: -1L,
            Nodes: 256, JudgeCost: 1L, Depth: 2, Score: null, Best: "best",
            Shapes: [new SearchShapePlan(Kind: SearchShapeKind.Relocate, Displace: false, Directions: [], PairWithIndex: -1)],
            Scores: "scores"
        );
        var runtime = new SearchRuntime(live: () => rows);

        runtime.Rebuild(plans: [plan], judge: judge, rows: rows, catalog: catalog, topology: static _ => null, patterns: CompiledPatterns.Empty, tables: []);

        IReadOnlyList<SearchWrite>? landed = null;
        var installed = runtime.Step(tick: 1UL, apply: writes => { landed = writes; return true; });

        Assert.True(installed);
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "best", Key: "to", Value: 1L }));
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "best", Key: "score", Value: 10L }));
        // The reading this fold rejects, stated as arithmetic rather than asserted against the engine: negating
        // seat 1's own reply value prefers cell 2 over cell 1.
        Assert.True(condition: (-1L > -9L));
    }
}
