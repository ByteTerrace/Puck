using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>The row-version scheduler is an optimization, never a second evaluator: a rule set driven through a
/// <see cref="FrameHost"/> with <see cref="RuleEvaluator.SchedulingEnabled"/> on must produce the identical latch,
/// the identical fired effects, and the identical frame at every tick as the same rule set driven with it off, over
/// a scripted sequence of writes exercising a Level rule that closes and stays closed, a forEach rule whose
/// per-binding verdicts close at different ticks, an Edge rule that re-arms, and a binding whose own row goes
/// several ticks unchanged while its rule's gate stays open.</summary>
public sealed class RowVersionSchedulingLawTests {
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
    private static ValueExpression Expr(string text) {
        Assert.True(ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var error), error);

        return new ValueExpression(Tokens: tokens);
    }
    private static Rule R(string name, ActionPredicate? gate, ActionTriggerMode mode = ActionTriggerMode.Level, string? forEach = null, params ActionEffect[] effects) =>
        new(Name: Name(name), Effects: effects, Gate: gate, Mode: mode, ForEach: forEach);
    private static ActionPredicate.CompareState CS(string state, ActionStateComparison comparison, decimal value, string? key = null) =>
        new(State: state, Comparison: comparison, Value: value, Key: key);
    private static ActionEffect.AddState Add(string state, decimal value, string? key = null) =>
        new(State: state, Value: value, Key: key);

    // The four rules a scripted sequence exercises: a Level rule that closes at a fixed tick and stays closed, a
    // forEach rule whose two keys close at different ticks, a binding whose gate stays open for many ticks while its
    // own read row ("baseVal") does not change, and an Edge rule that re-arms.
    private static (FrameHost Host, CompiledRule[] Rules) Build(bool scheduling) {
        StateRow[] rows = [
            Slot(name: "score", value: 0L),
            Keyed(name: "hp", ("7", 2L), ("9", 1L)),
            Slot(name: "baseVal", value: 1L),
            Slot(name: "total", value: 0L),
            Slot(name: "armed", value: 0L),
            Slot(name: "hits", value: 0L),
            Slot(name: "noise", value: 0L),
        ];
        var catalog = StateCatalog.Compile(section: new Section(rows: rows));
        var layout = new FrameLayout(rows: rows, topology: static _ => null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);

        host.Frame.Load(source: new RowStore(rows: rows));
        host.Evaluator.SchedulingEnabled = scheduling;

        Rule[] rules = [
            R(name: "count", gate: CS(state: "score", comparison: ActionStateComparison.Less, value: 5m), effects: Add(state: "score", value: 1m)),
            R(name: "decay", gate: CS(state: "hp", comparison: ActionStateComparison.Greater, value: 0m, key: "$each"), forEach: "hp", effects: Add(state: "hp", value: -1m, key: "$each")),
            new Rule(
                Name: Name(value: "double"),
                Gate: new ActionPredicate.CompareValue(Left: Expr(text: "$bind:doubled"), Comparison: ActionStateComparison.GreaterOrEqual, Right: Expr(text: "6"), Kind: CellKind.Int),
                Effects: [new ActionEffect.SetState(State: "total", Expression: Expr(text: "$bind:doubled + 1"))],
                Bindings: [new RuleBinding(Name: Name(value: "doubled"), Kind: CellKind.Int, Expression: Expr(text: "baseVal * 2"))]
            ),
            R(name: "flash", gate: CS(state: "armed", comparison: ActionStateComparison.Equal, value: 1m), mode: ActionTriggerMode.Edge, effects: Add(state: "hits", value: 1m)),
        ];
        var context = new RuleCompileContext(section: new Section(rows: rows), catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);

        return (host, RuleCompiler.CompileAll(rules: rules, context: context));
    }

    private static void Set(FrameHost host, string row, long value) {
        var target = host.Frame.Find(name: row)!;

        Assert.True(host.Frame.TryWrite(row: target, key: StateRow.SlotKey, value: value, write: StateWriteKind.Set, reason: out var reason), reason);
    }

    [Fact]
    public void SchedulingProducesIdenticalLatchesEffectsAndFramesAsFullEvaluation() {
        var (scheduled, scheduledRules) = Build(scheduling: true);
        var (full, fullRules) = Build(scheduling: false);
        var scheduledLatch = new RuleLatch();
        var fullLatch = new RuleLatch();

        for (var tick = 1UL; tick <= 12UL; tick++) {
            // An unrelated row bumps every tick: no compiled rule reads it, so it must never change what a
            // scheduler-driven run skips.
            Set(host: scheduled, row: "noise", value: (long)tick);
            Set(host: full, row: "noise", value: (long)tick);

            if (tick == 1UL) {
                Set(host: scheduled, row: "armed", value: 1L);
                Set(host: full, row: "armed", value: 1L);
            } else if (tick == 3UL) {
                Set(host: scheduled, row: "armed", value: 0L);
                Set(host: full, row: "armed", value: 0L);
                Set(host: scheduled, row: "baseVal", value: 4L);
                Set(host: full, row: "baseVal", value: 4L);
            } else if (tick == 8UL) {
                Set(host: scheduled, row: "armed", value: 1L);
                Set(host: full, row: "armed", value: 1L);
            }

            var wroteScheduled = scheduled.Evaluator.Evaluate(rules: scheduledRules, latch: scheduledLatch, tick: tick, stepTicks: 1UL);
            var wroteFull = full.Evaluator.Evaluate(rules: fullRules, latch: fullLatch, tick: tick, stepTicks: 1UL);

            Assert.Equal(expected: wroteFull, actual: wroteScheduled);
            Assert.Equal(expected: full.Frame.Values.ToArray(), actual: scheduled.Frame.Values.ToArray());

            var scheduledHash = new Fnv1aHash();
            var fullHash = new Fnv1aHash();

            scheduledLatch.AppendStateHash(hash: ref scheduledHash, compiled: scheduledRules);
            fullLatch.AppendStateHash(hash: ref fullHash, compiled: fullRules);
            Assert.Equal(expected: fullHash.Value, actual: scheduledHash.Value);
            Assert.Equal(expected: full.Refusals, actual: scheduled.Refusals);
        }

        // The scripted sequence actually reaches every regime a skip must prove safe over: "count" closed and
        // stayed closed, "decay" closed both keys, "double" stayed open across unchanged reads, "flash" re-armed.
        Assert.Equal(expected: 5L, actual: full.Frame.TryStored(row: full.Frame.Find(name: "score")!, key: StateRow.SlotKey, value: out var score, text: out _) ? score : -1L);
        Assert.Equal(expected: 2L, actual: full.Frame.TryStored(row: full.Frame.Find(name: "hits")!, key: StateRow.SlotKey, value: out var hits, text: out _) ? hits : -1L);
    }
}
