using Xunit;

namespace Puck.State.Tests;

public sealed class VectorTraceTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    private static StateSpace TestSpace(string name = "testSpace", int dimensions = 8) => new(
        Name: Name(name),
        Model: "test-model",
        Revision: "1",
        Dimensions: dimensions
    );

    [Fact]
    public void VectorCopy_EmitsTracedEffect() {
        var space = TestSpace();
        sbyte[] comps = [127, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(StateVector.TryCreate(components: comps, vector: out var vec, error: out _));

        var targetRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("k1"))]
        );

        var section = new StateSection(Spaces: [space], Rows: [targetRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var effect = new ActionEffect.SetState(
            State: "dest",
            Key: "k1",
            Vector: vec!.ToBase64Url()
        );

        var compiledEffects = RuleCompiler.CompileEffects(effects: [effect], ruleName: "tracedRule", context: context, subject: "rule");

        var compiledRule = new CompiledRule(
            Name: "tracedRule",
            Mode: ActionTriggerMode.Level,
            Gate: [],
            Effects: compiledEffects
        );

        var rows = new[] { targetRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);

        var evaluator = new RuleEvaluator(host: host);
        Assert.True(evaluator.ArmTrace(evaluations: 1, rule: "tracedRule"));

        var latch = new RuleLatch();
        var evaluated = evaluator.Evaluate(engineTick: 1UL, latch: latch, rules: [compiledRule], stepTicks: 1UL, tick: 1UL);
        Assert.True(evaluated);

        var trace = evaluator.DescribeTrace(verb: "trace");
        Assert.NotNull(trace);
        Assert.Contains("tracedRule", trace);
        Assert.Contains("applied", trace);
        Assert.Contains(vec!.ToBase64Url(), trace);
    }

    private sealed class CrossRowTestHost(FrameHost inner) : IRuleHost {
        public ulong Tick => inner.Tick;
        public ulong EngineTick => inner.EngineTick;
        public StateStore Store => inner.Store;
        public StateCatalog Catalog => inner.Catalog;
        public CompiledPatterns Patterns => inner.Patterns;
        public string? BoundEachKey => inner.BoundEachKey;
        public string? BoundTokenKey { get => inner.BoundTokenKey; set => inner.BoundTokenKey = value; }
        public string? BoundPreviousKey { get => inner.BoundPreviousKey; set => inner.BoundPreviousKey = value; }
        public bool TableKeyMissing { get => inner.TableKeyMissing; set => inner.TableKeyMissing = value; }

        public Span<long> PatternWord => inner.PatternWord;
        public int BoundIndex(BoundKey key) => inner.BoundIndex(key);
        public long BindingValue(int ordinal) => inner.BindingValue(ordinal);
        public CompiledTable Table(int ordinal) => inner.Table(ordinal);
        public void ReportTableKeyMissing(string table, long key) => inner.ReportTableKeyMissing(table, key);
        public Span<long> BoardScratch(int cells) => inner.BoardScratch(cells);

        public void BeginPreflight() => inner.BeginPreflight();
        public void EndPreflight() => inner.EndPreflight();
        public bool TryCommitPreflight(ulong tick, out string reason) => inner.TryCommitPreflight(tick, out reason);
        public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) =>
            inner.FireEffect(effect, ruleName, tick, stepTicks, preflight);
        public bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) =>
            inner.TryEvaluateOwn(rule, latch, tick, stepTicks, out applied);
        public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => inner.RefusalRecorded(in diagnostic);

        public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
            if (mutation is StateMutation.ApplyVector) {
                reason = string.Empty;
                return true;
            }
            return inner.TryApply(mutation, tick, preflight, out reason);
        }
    }

    [Fact]
    public void VectorTrace_RecordsVectorCall_NearestWrites_AndRememberVerdict() {
        var space = TestSpace();
        Assert.True(StateVector.TryCreate([127, 0, 0, 0, 0, 0, 0, 0], out var v1, out var err1), err1);
        Assert.True(StateVector.TryCreate([0, 127, 0, 0, 0, 0, 0, 0], out var v2, out var err2), err2);

        var srcRow = new StateRow(
            Name: Name("candidates"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Vector: v1),
                new StateCell(Key: Name("c2"), Vector: v2)
            ]
        );

        var queryRow = new StateRow(
            Name: Name("queries"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("q1"), Vector: v1),
                new StateCell(Key: Name("q2"), Vector: v2)
            ]
        );

        var destIntRow = new StateRow(
            Name: Name("ranks"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: []
        );

        var rememberRow = new StateRow(
            Name: Name("memories"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("m1"), Vector: v1)]
        );

        var rows = new[] { srcRow, queryRow, destIntRow, rememberRow };
        var section = new StateSection(Spaces: [space], Rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        // 1. Vector call: similarity between queries[q1] and candidates[c1] -> cosine = 1.0000
        var simExpr = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Similarity,
                Left: new VectorOperandToken.Cell("queries", "q1"),
                Right: new VectorOperandToken.Cell("candidates", "c1")
            )
        ]);

        var rule = new Rule(
            Name: Name("vectorTracedRule"),
            Bindings: [new RuleBinding(Name: Name("sim"), Kind: CellKind.Fixed, Expression: simExpr)],
            Gate: null,
            Effects: [
                // 2. Nearest effect: nearest candidates -> ranks
                new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
                    From: "candidates",
                    Query: "queries[q1]",
                    Into: "ranks",
                    K: 2
                )),
                // 3. Remember effect: remember memories[m2] from queries[q1] unless within 0.5 (matches m1)
                new ActionEffect.TransformState(Transform: new StateTransform.Remember(
                    Into: "memories",
                    Key: "m2",
                    From: "queries[q1]",
                    UnlessWithin: "0.5"
                )),
                // 4. Remember effect: remember memories[m3] from queries[q2] unless within 0.5 (stored, orthogonal to m1)
                new ActionEffect.TransformState(Transform: new StateTransform.Remember(
                    Into: "memories",
                    Key: "m3",
                    From: "queries[q2]",
                    UnlessWithin: "0.5"
                ))
            ]
        );

        var compiledRule = RuleCompiler.Compile(rule: rule, context: context);

        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var frameHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        frameHost.Frame.Load(new RowStore(rows));
        var host = new CrossRowTestHost(frameHost);

        var evaluator = new RuleEvaluator(host: host);
        Assert.True(evaluator.ArmTrace(evaluations: 1, rule: "vectorTracedRule"));

        var latch = new RuleLatch();
        var evaluated = evaluator.Evaluate(engineTick: 1UL, latch: latch, rules: [compiledRule], stepTicks: 1UL, tick: 1UL);
        Assert.True(evaluated);

        var trace = evaluator.DescribeTrace(verb: "trace");
        Assert.NotNull(trace);

        // 1. Asserts vector call's value
        Assert.Contains("sim=1", trace);

        // 2. Asserts what nearest wrote (the matching candidates and scores)
        Assert.Contains("nearest candidates -> ranks = c1: 16129, c2: 0", trace);

        // 3. Asserts remember's verdict with its matching key, and stored
        Assert.Contains("remember queries[q1] -> memories[m2] = matched m1", trace);
        Assert.Contains("remember queries[q2] -> memories[m3] = stored", trace);
    }
}

