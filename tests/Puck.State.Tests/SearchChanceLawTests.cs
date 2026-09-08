using Xunit;

namespace Puck.State.Tests;

/// <summary>A chance node is state-engine vocabulary, exactly like the rest of the search runtime: no world,
/// document, or wire concept anywhere in reach. Negamax averages exactly over a chance row's baked outcome table;
/// a tree job samples one outcome per playout from the job's own stream instead.</summary>
public sealed class SearchChanceLawTests {
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
    private static CompiledExpressionToken[] Score(string text, RuleCompileContext context) {
        Assert.True(ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var parseError), parseError);

        return RuleCompiler.CompileExpression(expression: new ValueExpression(Tokens: tokens), kind: CellKind.Int, ruleName: "chance", verb: "search score", context: context);
    }

    // A one-cell chance row with two equally weighted outcomes (10 and 20), a root chance node (depth one, so no
    // move follows the draw), and a score that reads the row directly: the negamax value is exactly (10+20)/2.
    [Fact]
    public void ExpectiminimaxOverATwoOutcomeChanceEqualsTheHandComputedAverage() {
        StateRow[] rows = [
            Keyed(name: "roll", ("only", 0L)),
            Slot(name: "turn", value: 0L),
            Slot(name: "verdict", value: 0L),
            Keyed(name: "best", ("token", 0L), ("to", 0L), ("score", 0L)),
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);
        var plan = new SearchPlan(
            Name: "chance", Tokens: "roll", Topology: null, Zones: [], CellCount: 1, Turn: "turn", Verdict: "verdict", Off: -1L,
            Nodes: 64, JudgeCost: 1L, Depth: 1, Score: Score(text: "roll[only]", context: context), Best: "best",
            Shapes: [new SearchShapePlan(Kind: SearchShapeKind.Relocate, Displace: true, Directions: [], PairWithIndex: -1)],
            Chance: new SearchChancePlan(Row: "roll", AtDepth: 0, CellCount: 1, Outcomes: [10L, 20L], Weights: [1UL, 1UL])
        );
        var runtime = new SearchRuntime(live: () => rows);

        runtime.Rebuild(plans: [plan], judge: [], rows: rows, catalog: catalog, topology: static _ => null, patterns: CompiledPatterns.Empty, tables: []);

        IReadOnlyList<SearchWrite>? landed = null;
        var installed = runtime.Step(tick: 1UL, apply: writes => { landed = writes; return true; });

        Assert.True(installed);
        Assert.NotNull(landed);
        Assert.Contains(landed, write => (write is SearchWrite.Cell { Row: "best", Key: "score", Value: 15L }));
        // A chance node never chose a move — token and target land the no-move sentinel.
        Assert.Contains(landed, write => (write is SearchWrite.Cell { Row: "best", Key: "token", Value: -1L }));
    }

    // Weights need not be equal: three parts weight on 100 against one part on 0 rounds to 75 (round-half-up over
    // the exact rational (0*1 + 100*3) / 4 = 75).
    [Fact]
    public void ExpectiminimaxWeightsTheAverageByEachOutcomesShare() {
        StateRow[] rows = [
            Keyed(name: "roll", ("only", 0L)),
            Slot(name: "turn", value: 0L),
            Slot(name: "verdict", value: 0L),
            Keyed(name: "best", ("token", 0L), ("to", 0L), ("score", 0L)),
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);
        var plan = new SearchPlan(
            Name: "chance", Tokens: "roll", Topology: null, Zones: [], CellCount: 1, Turn: "turn", Verdict: "verdict", Off: -1L,
            Nodes: 64, JudgeCost: 1L, Depth: 1, Score: Score(text: "roll[only]", context: context), Best: "best",
            Shapes: [new SearchShapePlan(Kind: SearchShapeKind.Relocate, Displace: true, Directions: [], PairWithIndex: -1)],
            Chance: new SearchChancePlan(Row: "roll", AtDepth: 0, CellCount: 1, Outcomes: [0L, 100L], Weights: [1UL, 3UL])
        );
        var runtime = new SearchRuntime(live: () => rows);

        runtime.Rebuild(plans: [plan], judge: [], rows: rows, catalog: catalog, topology: static _ => null, patterns: CompiledPatterns.Empty, tables: []);

        IReadOnlyList<SearchWrite>? landed = null;

        Assert.True(runtime.Step(tick: 1UL, apply: writes => { landed = writes; return true; }));
        Assert.Contains(landed!, write => (write is SearchWrite.Cell { Row: "best", Key: "score", Value: 75L }));
    }

    // A tree job over two tokens, one of which is a pure chance row no move ever touches: the playout's first ply
    // (chance.AtDepth == 1) draws from the job's own stream rather than choosing a candidate. Two runtimes built
    // from byte-identical rows and stepped the same number of times reach the identical drawn value, because both
    // draw from the same job.Stamp-seeded stream.
    private static (SearchRuntime Runtime, SearchPlan Plan, StateRow[] Rows) BuildUctWithChance() {
        StateRow[] rows = [
            Keyed(name: "piece", ("a", 0L)),
            Keyed(name: "luck", ("only", 0L)),
            Slot(name: "turn", value: 0L),
            Slot(name: "verdict", value: 0L),
            Keyed(name: "best", ("token", 0L), ("to", 0L), ("score", 0L)),
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);
        var judge = new[] {
            RuleCompiler.Compile(
                rule: new Rule(Name: Name("accept"), Effects: [
                    new ActionEffect.SetState(State: "verdict", Value: 1m),
                    new ActionEffect.SetState(State: "turn", Expression: ValueExpression.Parse("1 - turn")),
                ]),
                context: context
            ),
        };
        var plan = new SearchPlan(
            Name: "moves", Tokens: "piece", Topology: null, Zones: [], CellCount: 4, Turn: "turn", Verdict: "verdict", Off: -1L,
            Nodes: 64, JudgeCost: 1L, Depth: 2,
            Score: Score(text: "piece[a] == 3 ? 1000 : (piece[a] == 1 ? -1000 : luck[only])", context: context), Best: "best",
            Shapes: [new SearchShapePlan(Kind: SearchShapeKind.Relocate, Displace: true, Directions: [], PairWithIndex: -1)],
            Method: SearchMethod.Tree, Iterations: 32,
            Chance: new SearchChancePlan(Row: "luck", AtDepth: 1, CellCount: 1, Outcomes: [-500L, 500L], Weights: [1UL, 1UL])
        );
        var runtime = new SearchRuntime(live: () => rows);

        runtime.Rebuild(plans: [plan], judge: judge, rows: rows, catalog: catalog, topology: static _ => null, patterns: CompiledPatterns.Empty, tables: []);

        return (runtime, plan, rows);
    }

    [Fact]
    public void TheTreeSearchDrawsTheChancePlyFromTheJobsOwnStreamSoTwoJobsWithTheSameStampAgree() {
        var (first, _, _) = BuildUctWithChance();
        var (second, _, _) = BuildUctWithChance();

        for (var tick = 0; tick < 200; tick++) {
            first.Step(tick: (ulong)tick, apply: static _ => true);
            second.Step(tick: (ulong)tick, apply: static _ => true);
        }

        var a = first.Status()[0];
        var b = second.Status()[0];

        Assert.True(a.Done, a.ToString());
        Assert.True(b.Done, b.ToString());
        Assert.Equal(a.BestScore, b.BestScore);
        Assert.Equal(a.BestToken, b.BestToken);
        Assert.Equal(a.BestTarget, b.BestTarget);

        var captureA = first.Capture().Jobs[0];
        var captureB = second.Capture().Jobs[0];

        Assert.Equal(captureA.Nodes, captureB.Nodes);
        // The chance ply wrote luck's outcome into every playout frame the job pooled: identical between the two
        // independently built runtimes proves the draw came from the job's own seeded stream, not ambient entropy.
        Assert.NotNull(captureA.Tree);
        Assert.NotNull(captureB.Tree);
        Assert.Equal(captureA.Tree!.PlayValues, captureB.Tree!.PlayValues);
        Assert.Equal(captureA.Tree.Seed, captureB.Tree.Seed);
    }
}
