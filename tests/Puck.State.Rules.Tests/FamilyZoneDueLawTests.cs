using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A zone selection resolves before the gate, so a local only its index reads is judged with the gate, not
/// deferred to the firing; and the due list answers what judging every binding in place answers.</summary>
public sealed class FamilyZoneDueLawTests {
    // Klondike's deal at a station, reduced: each table (keyed 0 and 1) deals its stock a card a tick into the pile its
    // step names, chosen live through a zone table of families (`$zones[to]`, `to` a local read from the deal table, and
    // read by nothing else), and a later rule moves the step on. Entry 0 of the table is a gap, so a selection that read
    // `to` before computing it selects no row and the rule never applies.
    private static StateSection Section() {
        static StateRow Pile(string name, params string[] tokens) => new(
            Name: TransformFixture.Name(value: name), Kind: CellKind.Int, Capacity: 6,
            Domain: new StateDomain.KeysOf(Ordered: true, Row: TransformFixture.Name(value: "cards")),
            Cells: [.. tokens.Select(token => TransformFixture.Cell(key: token, value: 0L))]);
        static StateRow Keyed(string name, long first, long second) => new(
            Name: TransformFixture.Name(value: name), Kind: CellKind.Int, Capacity: 2,
            Cells: [TransformFixture.Cell(key: "0", value: first), TransformFixture.Cell(key: "1", value: second)]);

        return new StateSection(
            Families: [
                new StateFamily(Name: TransformFixture.Name(value: "stock"), Size: 2),
                new StateFamily(Name: TransformFixture.Name(value: "down2"), Size: 2),
                new StateFamily(Name: TransformFixture.Name(value: "down3"), Size: 2),
            ],
            Rows: [
                new StateRow(Name: TransformFixture.Name(value: "cards"), Kind: CellKind.Int, Capacity: 6,
                    Cells: [.. Enumerable.Range(0, 6).Select(card => TransformFixture.Cell(key: $"{card}", value: card))]),
                Pile("stock0", "0", "1", "2"),
                Pile("stock1", "3", "4", "5"),
                Pile("down20"),
                Pile("down21"),
                Pile("down30"),
                Pile("down31"),
                Keyed(name: "tables", first: 1L, second: 1L),
                Keyed(name: "stage", first: 0L, second: 0L),
                Keyed(name: "step", first: 0L, second: 0L),
                new StateRow(Name: TransformFixture.Name(value: "dealTo"), Kind: CellKind.Int, Capacity: 3,
                    Cells: [TransformFixture.Cell(key: "0", value: 1L), TransformFixture.Cell(key: "1", value: 2L), TransformFixture.Cell(key: "2", value: 2L)]),
            ]);
    }

    private static ActionPredicate Each(string row, ExpressionOp comparison, decimal value) =>
        new ActionPredicate.CompareState(Comparison: comparison, Key: "$each", State: row, Value: value);

    private static Rule[] Rules() => [
        new Rule(
            Name: TransformFixture.Name(value: "deal"), ForEach: "tables", Mode: ActionTriggerMode.Level, Zones: ["", "down2", "down3"],
            Locals: [new RuleLocal(Expression: RulesFixture.Program(text: "dealTo[step[$each]]"), Name: TransformFixture.Name(value: "to"))],
            Gate: new ActionPredicate.All(Predicates: [Each(row: "tables", comparison: ExpressionOp.Equal, value: 1m), Each(row: "stage", comparison: ExpressionOp.Equal, value: 1m)]),
            Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Transfer(From: "stock[$each]", To: "$zones[($local:to)]", Selector: ZoneSelector.First))]),
        new Rule(
            Name: TransformFixture.Name(value: "finish"), ForEach: "tables", Mode: ActionTriggerMode.Level,
            Gate: new ActionPredicate.All(Predicates: [Each(row: "stage", comparison: ExpressionOp.Equal, value: 1m), Each(row: "step", comparison: ExpressionOp.Equal, value: 2m)]),
            Effects: [new ActionEffect.SetState(Key: "$each", State: "stage", Value: 2m)]),
        new Rule(
            Name: TransformFixture.Name(value: "advance"), ForEach: "tables", Mode: ActionTriggerMode.Level,
            Gate: Each(row: "stage", comparison: ExpressionOp.Equal, value: 1m),
            Effects: [new ActionEffect.SetState(Key: "$each", State: "step", Expression: RulesFixture.Program(text: "step[$each] + 1"))]),
    ];

    [Fact]
    public void AZoneSelectedByALocalTheGateDoesNotReadSelectsBeforeTheGate() {
        static List<string> Run(bool due) {
            var section = Section();
            var context = TransformFixture.Context(section: section);
            var rules = RuleCompiler.CompileAll(context: context, rules: Rules());
            var host = TransformFixture.Host(context: context, section: section);
            var evaluator = new RuleEvaluator(host: host) { SchedulingEnabled = due };
            var latch = new RuleLatch();
            var trace = new List<string>();
            string Listing(string row) => TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: row));

            for (var tick = 0UL; (tick < 8UL); tick++) {
                if (tick == 2UL) {
                    // Table 0 starts its deal between ticks, as the shuffle's firing does.
                    var mark = host.Arena.BeginScope();

                    Assert.True(condition: host.Arena.TryWrite(
                        key: host.Arena.Keys.Intern(name: TransformFixture.Name(value: "0")),
                        operand: 1L,
                        reason: out var reason,
                        rowOrdinal: TransformFixture.Ordinal(context: context, name: "stage"),
                        write: StateWriteKind.Set
                    ), userMessage: reason);
                    host.Arena.Commit(mark: mark);
                }
                host.Advance(engineTick: tick, tick: tick);
                _ = (due
                    ? evaluator.EvaluateDue(latch: latch, rules: rules, stepTicks: 1UL)
                    : evaluator.Evaluate(latch: latch, rules: rules, stepTicks: 1UL));
                trace.Add(item: $"{tick}: stock0={Listing("stock0")} down20={Listing("down20")} down30={Listing("down30")} stage={Listing("stage")} step={Listing("step")}");
            }
            Assert.Empty(evaluator.Diagnostics());

            return trace;
        }

        var inPlace = Run(due: false);

        // In place, table 0 deals its first card to down2 and its next to down3, then finishes.
        Assert.True(inPlace.Exists(line => line.Contains("down20=0=0 down30=1=0", StringComparison.Ordinal)), string.Join(Environment.NewLine, inPlace));
        Assert.Equal(expected: inPlace, actual: Run(due: true));
    }
}
