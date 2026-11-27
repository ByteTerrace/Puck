using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A rule sweeping a row family binds each member's keys, the key to <c>$each</c> and the member's family index
/// to <c>$member</c>; the due list visits a (member, key) binding only when something it reads moved, and answers what
/// judging every binding in place answers.</summary>
public sealed class FamilySweepDueLawTests {
    // Two stations, each a member of every family: a station's cards (`cards0`, `cards1`) and the slot each card was last
    // copied to (`slot0`, `slot1`), a station-keyed switch (`on`), a shared scale keyed by card, and a station-keyed
    // count of what each station's pile holds.
    private static StateSection Section() {
        static StateRow Row(string name, params (string Key, long Value)[] cells) => new(
            Name: TransformFixture.Name(value: name), Kind: CellKind.Int, Capacity: 4,
            Cells: [.. cells.Select(cell => TransformFixture.Cell(key: cell.Key, value: cell.Value))]);

        return new StateSection(
            Families: [
                new StateFamily(Name: TransformFixture.Name(value: "cards"), Size: 2),
                new StateFamily(Name: TransformFixture.Name(value: "slot"), Size: 2),
            ],
            Rows: [
                Row("cards0", ("a", 1L), ("b", 2L)),
                Row("cards1", ("a", 3L)),
                Row("slot0", ("a", 0L), ("b", 0L)),
                Row("slot1", ("a", 0L), ("b", 0L)),
                Row("on", ("0", 1L), ("1", 0L)),
                Row("scale", ("a", 10L), ("b", 100L)),
                Row("held", ("0", 0L), ("1", 0L)),
            ]);
    }

    private static Rule[] Rules() => [
        // Each card of a station that is on lies at its value times the shared scale of its key.
        new Rule(
            Name: TransformFixture.Name(value: "place"), ForEach: "cards", Mode: ActionTriggerMode.Level,
            Gate: new ActionPredicate.All(Predicates: [
                new ActionPredicate.CompareState(Comparison: ExpressionOp.Equal, Key: "$member", State: "on", Value: 1m),
                new ActionPredicate.CompareValue(Comparison: ExpressionOp.NotEqual, Left: RulesFixture.Program(text: "slot[$member][$each]"), Right: RulesFixture.Program(text: "cards[$member][$each] * scale[$each]")),
            ]),
            Effects: [new ActionEffect.SetState(Key: "$each", State: "slot[$member]", Expression: RulesFixture.Program(text: "cards[$member][$each] * scale[$each]"))]),
        // A station's count of its own cards, read through a family selection under $member.
        new Rule(
            Name: TransformFixture.Name(value: "count"), ForEach: "cards", Mode: ActionTriggerMode.Level,
            Gate: new ActionPredicate.CompareValue(Comparison: ExpressionOp.NotEqual, Left: RulesFixture.Program(text: "held[$member]"), Right: RulesFixture.Program(text: "count(cards[$member])")),
            Effects: [new ActionEffect.SetState(Key: "$member", State: "held", Expression: RulesFixture.Program(text: "count(cards[$member])"))]),
    ];

    [Fact]
    public void ADueFamilySweepAnswersWhatJudgingEveryBindingAnswers() {
        static (List<string> Trace, CompiledRule[] Rules) Run(bool due) {
            var section = Section();
            var context = TransformFixture.Context(section: section);
            var rules = RuleCompiler.CompileAll(context: context, rules: Rules());
            var host = TransformFixture.Host(context: context, section: section);
            var evaluator = new RuleEvaluator(host: host) { SchedulingEnabled = due };
            var latch = new RuleLatch();
            var trace = new List<string>();
            string Listing(string row) => TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: row));
            void Write(string row, string key, long value) {
                var mark = host.Arena.BeginScope();

                Assert.True(condition: host.Arena.TryWrite(
                    key: host.Arena.Keys.Intern(name: TransformFixture.Name(value: key)),
                    operand: value,
                    reason: out var reason,
                    rowOrdinal: TransformFixture.Ordinal(context: context, name: row),
                    write: StateWriteKind.Set
                ), userMessage: reason);
                host.Arena.Commit(mark: mark);
            }
            void Mint(string row, string key, long value) {
                var mark = host.Arena.BeginScope();

                Assert.True(condition: host.Arena.TryMint(
                    key: out _,
                    name: TransformFixture.Name(value: key),
                    reason: out var reason,
                    rowOrdinal: TransformFixture.Ordinal(context: context, name: row),
                    value: CellValue.Int(value: value)
                ), userMessage: reason);
                host.Arena.Commit(mark: mark);
            }

            for (var tick = 0UL; (tick < 10UL); tick++) {
                switch (tick) {
                    case 2UL: Write(row: "cards0", key: "b", value: 5L); break;      // one station's card moves
                    case 4UL: Write(row: "on", key: "1", value: 1L); break;          // the other station turns on
                    case 6UL: Write(row: "scale", key: "a", value: 20L); break;      // a key every station shares
                    case 8UL: Mint(row: "cards1", key: "b", value: 7L); break;       // a card joins a station
                }
                host.Advance(engineTick: tick, tick: tick);
                _ = (due
                    ? evaluator.EvaluateDue(latch: latch, rules: rules, stepTicks: 1UL)
                    : evaluator.Evaluate(latch: latch, rules: rules, stepTicks: 1UL));
                trace.Add(item: $"{tick}: slot0={Listing("slot0")} slot1={Listing("slot1")} held={Listing("held")}");
            }
            Assert.Empty(evaluator.Diagnostics());

            return (trace, rules);
        }

        var (inPlace, compiled) = Run(due: false);

        Assert.NotNull(compiled[0].ForEachFamily);
        // In place, station 0 places both its cards, station 1 places its first once it turns on and its second once
        // the card joins, the shared scale reaches both, and each station counts its own cards.
        Assert.Contains("9: slot0=a=20,b=500 slot1=a=60,b=700 held=0=2,1=2", inPlace[^1], StringComparison.Ordinal);
        Assert.Equal(expected: inPlace, actual: Run(due: true).Trace);
    }
}
