using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a search judge evaluates every candidate over a fresh latch, which keeps no edge bit,
/// and that gives each candidate exactly what evaluating it over a latch reset before the candidate gives: the same
/// verdict, the same fault, the same state hash and the same landed search writes. The fresh latch writes no
/// bookkeeping into the candidate's scope, so its journal never holds more than the reset latch's does, and holds
/// less wherever a gate closes.</summary>
public sealed class ArenaSearchFreshLatchLawTests {
    // The judge path the fresh latch stands for: a latch reset before every candidate, whose every binding then writes
    // its edge bit through a scope of its own.
    private sealed class ResetLatchJudge(ArenaSearchEffectHost host, Puck.State.Rules.CompiledRule[] rules, Puck.State.Rules.CompiledProgram? score) : IArenaSearchJudge {
        private readonly Puck.State.Rules.RuleEvaluator m_evaluator = new(host: host) { SchedulingEnabled = false };
        private readonly Puck.State.Rules.RuleLatch m_latch = new();
        private readonly RuleArenaSearchJudge m_shape = new(host: host, rules: rules, score: score);

        public StateArena Arena => host.Arena;
        public IReadOnlyList<int> KeyRows => m_shape.KeyRows;
        public bool ReadsTick => m_shape.ReadsTick;
        public int RuleCount => m_shape.RuleCount;
        public bool Scores => m_shape.Scores;

        public JudgeOutcome Judge(in ArenaSearchView view) {
            host.SearchPly = view.Ply;
            host.Advance(engineTick: view.EngineTick, tick: view.Tick);
            m_latch.Reset(journal: host.Arena.Journal);

            try {
                var outcome = m_evaluator.Evaluate(applied: out var applied, latch: m_latch, rules: rules, stepTicks: 1UL);

                return new JudgeOutcome(Applied: applied, Faulted: (outcome == Puck.State.Rules.RuleOutcome.Faulted));
            } finally {
                host.DiscardQueuedArms();
            }
        }
        public Outcome Score(in ArenaSearchView view) => m_shape.Score(view: in view);
        public bool TryAdmit(ArenaSearchPlan plan, out string refusal) => m_shape.TryAdmit(plan: plan, refusal: out refusal);
    }

    private static StateRow[] Rows() => [
        .. Board(cells: 4, tokens: 2),
        Keyed(name: "marks", ("t0", 0L), ("t1", 0L)),
        Slot(name: "closedHits", value: 0L),
        Slot(name: "edgeHits", value: 0L),
    ];
    // Every shape a binding's bookkeeping can take: a gate that never opens, a keyed sweep that spends its one firing,
    // an Edge, a gate that faults where the tokens share a cell, a firing that faults and rewinds where t0 stands on
    // cell 1, and the verdict. A faulted judge lands nothing and restarts its step, so a search that must land leaves
    // the two faulting rules out.
    private static Rule[] Rules(bool faults = true) => [.. AllRules().Where(predicate: rule => (faults || ((rule.Name.Value != "fault") && (rule.Name.Value != "refuse"))))];
    private static Rule[] AllRules() => [
        new(
            Name: Name(value: "never"),
            Effects: [new ActionEffect.SetState(State: "closedHits", Value: 1m)],
            Gate: new ActionPredicate.CompareState(State: "turn", Comparison: ExpressionOp.GreaterOrEqual, Value: 100m)
        ),
        new(
            Name: Name(value: "sweep"),
            Effects: [new ActionEffect.SetState(Key: "$each", State: "marks", Expression: ExpressionProgram.Parse(text: "marks[$each] + 1"))],
            Gate: new ActionPredicate.CompareState(State: "piece", Key: "$each", Comparison: ExpressionOp.GreaterOrEqual, Value: 2m),
            ForEach: "piece",
            MaxFirings: 1
        ),
        new(
            Name: Name(value: "edge"),
            Effects: [new ActionEffect.SetState(State: "edgeHits", Expression: ExpressionProgram.Parse(text: "edgeHits + 1"))],
            Gate: new ActionPredicate.CompareValue(Left: ExpressionProgram.Parse(text: "piece[t0] + piece[t1]"), Comparison: ExpressionOp.GreaterOrEqual, Right: ExpressionProgram.Parse(text: "3")),
            Mode: ActionTriggerMode.Edge
        ),
        new(
            Name: Name(value: "fault"),
            Effects: [new ActionEffect.SetState(State: "closedHits", Expression: ExpressionProgram.Parse(text: "closedHits + 10"))],
            Gate: new ActionPredicate.CompareValue(Left: ExpressionProgram.Parse(text: "1 / (piece[t0] - piece[t1])"), Comparison: ExpressionOp.Equal, Right: ExpressionProgram.Parse(text: "0"))
        ),
        new(
            Name: Name(value: "refuse"),
            Effects: [new ActionEffect.SetState(State: "edgeHits", Expression: ExpressionProgram.Parse(text: "edgeHits / (piece[t0] - 1)"))],
            Gate: new ActionPredicate.CompareState(State: "turn", Comparison: ExpressionOp.LessOrEqual, Value: 1m)
        ),
        new(
            Name: Name(value: "accept"),
            Effects: [
                new ActionEffect.SetState(State: "verdict", Value: 1m),
                new ActionEffect.SetState(Expression: ExpressionProgram.Parse(text: "turn + 1"), State: "turn"),
            ],
            Gate: new ActionPredicate.CompareValue(Left: ExpressionProgram.Parse(text: "piece[t0]"), Comparison: ExpressionOp.NotEqual, Right: ExpressionProgram.Parse(text: "piece[t1]"))
        ),
    ];
    private const string ScoreText = "(piece[t0] * 3) - piece[t1] + marks[t0] + edgeHits";

    private static (RuleArenaSearchJudge Fresh, ResetLatchJudge Reset) Judges(Position position, bool faults = true) {
        var host = new ArenaSearchEffectHost(arena: position.Arena);
        var rules = CompileRulesJudge(position: position, rules: Rules(faults: faults));
        var score = (Puck.State.Rules.CompiledProgram?)CompileRules(context: position.RulesContext, text: ScoreText);

        return (new RuleArenaSearchJudge(host: host, rules: rules, score: score), new ResetLatchJudge(host: host, rules: rules, score: score));
    }

    private readonly record struct Judged(int Ply, long T0, long T1, bool Applied, bool Faulted, ulong Hash, long JournalBytes);

    // Judges every two-ply line of token placements, the second ply's scope nested in the first's as a search's
    // deeper position is, and rewinds every scope, so the arena ends as it began.
    private static List<Judged> Walk(Position position, IArenaSearchJudge judge) {
        var arena = position.Arena;
        var piece = position.Ordinal(name: "piece");
        var t0 = position.Key(name: "t0");
        var t1 = position.Key(name: "t1");
        var judged = new List<Judged>();
        var before = arena.ComputeHash();

        void Place(long first, long second) {
            Assert.True(condition: arena.TryWrite(key: t0, operand: first, reason: out var reason, rowOrdinal: piece, write: StateWriteKind.Set), userMessage: reason);
            Assert.True(condition: arena.TryWrite(key: t1, operand: second, reason: out reason, rowOrdinal: piece, write: StateWriteKind.Set), userMessage: reason);
        }
        void Judge(int ply, long first, long second) {
            var outcome = judge.Judge(view: new ArenaSearchView(Arena: arena, EngineTick: 0UL, Ply: ply, Tick: 1UL));

            judged.Add(item: new Judged(ply, first, second, outcome.Applied, outcome.Faulted, arena.ComputeHash(), arena.Journal.Bytes));
        }

        for (var first = 0L; (first < 4L); first++) {
            for (var second = 0L; (second < 4L); second++) {
                var outer = arena.BeginScope();

                Place(first: first, second: second);
                Judge(ply: 1, first: first, second: second);

                for (var reply = 0L; (reply < 4L); reply++) {
                    var inner = arena.BeginScope();

                    Place(first: reply, second: ((second + reply) % 4L));
                    Judge(ply: 2, first: reply, second: ((second + reply) % 4L));
                    arena.Rewind(mark: inner);
                }

                arena.Rewind(mark: outer);
            }
        }

        Assert.Equal(actual: arena.ComputeHash(), expected: before);

        return judged;
    }

    [Fact]
    public void AFreshLatchJudgesEveryCandidateAsAResetLatchDoesAndJournalsNoMore() {
        var position = new Position(rows: Rows());
        var (fresh, reset) = Judges(position: position);
        var expected = Walk(judge: reset, position: position);
        var actual = Walk(judge: fresh, position: position);

        Assert.Equal(actual: actual.Count, expected: expected.Count);

        for (var index = 0; (index < expected.Count); index++) {
            var (want, got) = (expected[index], actual[index]);

            Assert.Equal(actual: got with { JournalBytes = 0L }, expected: want with { JournalBytes = 0L });
            Assert.True(condition: (got.JournalBytes <= want.JournalBytes), userMessage: $"candidate {index} journaled {got.JournalBytes} bytes over a fresh latch, more than the {want.JournalBytes} over a reset one");
        }

        // The walk reaches every path the bookkeeping differs on: a verdict accepted, one rejected, a gate that
        // faulted, and closed gates whose bits only the reset latch writes.
        Assert.Contains(collection: actual, filter: static judged => (judged.Applied && !judged.Faulted));
        Assert.Contains(collection: actual, filter: static judged => !judged.Applied);
        Assert.Contains(collection: actual, filter: static judged => judged.Faulted);
        Assert.True(condition: (actual.Sum(selector: static judged => judged.JournalBytes) < expected.Sum(selector: static judged => judged.JournalBytes)));
    }

    private static SearchPlan WalkPlan() => new(
        Name: "moves",
        Tokens: "piece",
        Topology: null,
        Zones: [],
        CellCount: 4,
        Turn: "turn",
        Verdict: "verdict",
        Off: -1L,
        Nodes: 256,
        Work: SearchWork.NodeBounded(judge: 1L),
        Depth: 1,
        Best: "best",
        Shapes: [new SearchShapePlan(Kind: SearchShapeKind.Relocate, Displace: false, Directions: [], CompanionIndex: -1)],
        Counts: "counts",
        Legal: "legal"
    );
    private static SearchPlan TreePlan() => new(
        Name: "tree",
        Tokens: "piece",
        Topology: null,
        Zones: [],
        CellCount: 4,
        Turn: "turn",
        Verdict: "verdict",
        Off: -1L,
        Nodes: 256,
        Work: SearchWork.NodeBounded(judge: 1L),
        Depth: 2,
        Best: "best",
        Shapes: [new SearchShapePlan(Kind: SearchShapeKind.Relocate, Displace: false, Directions: [], CompanionIndex: -1)]
    ) {
        Scored = true,
    };

    private static (List<(string Row, string Key, long Value)> Writes, ulong Hash) Search(SearchPlan plan, bool fresh) {
        var position = new Position(rows: Rows());
        var (freshJudge, resetJudge) = Judges(faults: false, position: position);
        var writes = RunArena(
            catalog: position.Catalog,
            search: Build(judge: (fresh ? freshJudge : resetJudge), plan: plan, position: position)
        );

        return (writes, position.Arena.ComputeHash());
    }

    [Fact]
    public void ASearchOverAFreshLatchLandsWhatASearchOverAResetLatchLands() {
        foreach (var plan in new[] { WalkPlan(), TreePlan() }) {
            var expected = Search(fresh: false, plan: plan);
            var actual = Search(fresh: true, plan: plan);

            Assert.NotEmpty(collection: expected.Writes);
            Assert.Equal(actual: actual.Writes, expected: expected.Writes);
            Assert.Equal(actual: actual.Hash, expected: expected.Hash);
        }
    }
}
