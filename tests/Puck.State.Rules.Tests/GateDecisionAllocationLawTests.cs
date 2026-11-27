using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A gate decides through its kept closure tree with nothing allocated per decision: no value stack, no
/// leased storage and no tree built again, whatever comparisons, programs and groups it holds.</summary>
public sealed class GateDecisionAllocationLawTests {
    [Fact]
    public void AWarmedGateDecidesWithNoAllocation() {
        static CellName Name(string value) => CellName.Parse(candidate: value);
        static StateRow Slot(string name, long value) => new(Name(value: name), CellKind.Int, Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: value))]);

        var section = new StateSection(Rows: [
            Slot(name: "a", value: 3L),
            Slot(name: "b", value: 5L),
            new StateRow(Name(value: "scores"), CellKind.Int, Capacity: 4, Cells: [
                new StateCell(Name(value: "0"), CellValue.Int(value: 2L)),
                new StateCell(Name(value: "1"), CellValue.Int(value: 7L)),
            ]),
        ]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);
        var host = new ArenaEffectHost(arena: arena);
        var gate = new ActionPredicate.All(Predicates: [
            new ActionPredicate.CompareState("a", ExpressionOp.Greater, Value: 0m),
            new ActionPredicate.Any(Predicates: [
                new ActionPredicate.CompareState("b", ExpressionOp.Equal, ComparandState: "a"),
                new ActionPredicate.CompareValue(
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Left: ExpressionProgram.Parse(text: "count(scores, s -> s > a) + a + b - 1"),
                    Right: ExpressionProgram.Parse(text: "b")
                ),
            ]),
            new ActionPredicate.Not(Predicate: new ActionPredicate.CompareValue(
                Comparison: ExpressionOp.Less,
                Left: ExpressionProgram.Parse(text: "scores[0] ?? 0"),
                Right: ExpressionProgram.Parse(text: "a and b > 1 ? 0 : 9")
            )),
        ]);
        var compiled = RuleCompiler.Compile(new Rule(Name(value: "allocation"), [], Gate: gate), context, requireEffects: false);

        for (var warm = 0; (warm < 64); warm++) {
            var frame = new EvaluationFrame(reader: host);

            Assert.Equal(expected: Verdict.Open, actual: RuleEvaluation.Decide(frame: ref frame, gate: compiled.Gate));
        }

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var run = 0; (run < 256); run++) {
                var frame = new EvaluationFrame(reader: host);

                _ = RuleEvaluation.Decide(frame: ref frame, gate: compiled.Gate);
            }
        }));
    }
}
