using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a bound value served from its memo is the value a fresh evaluation would bind. A
/// binding that reads through a live zone rests on the cell that selects the zone as well as on the zones it may
/// select, so moving the selection to another zone binds that zone's answer, never the last one's.</summary>
public sealed class BindingMemoLawTests {
    [Fact]
    public void ABindingThroughALiveZoneFollowsTheSelectionToAnotherZone() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Locals: [new RuleLocal(
                        Expression: RulesFixture.Program(text: "$reduce:count:$zones[(cooldown)]"),
                        Kind: CellKind.Int,
                        Name: RulesFixture.Name(value: "held")
                    )],
                Effects: [new ActionEffect.SetState(
                        Expression: RulesFixture.Program(text: "$local:held"),
                        State: "score"
                    )],
                Name: RulesFixture.Name(value: "count"),
                Zones: ["deck", "hand"]
            )],
            section: RulesFixture.Section()
        );

        void Select(long zone) {
            var mark = host.Arena.BeginScope();

            Write(zone: zone);
            host.Arena.Commit(mark: mark);
        }
        void Write(long zone) => Assert.True(condition: host.Arena.TryWrite(
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: zone,
            reason: out _,
            rowOrdinal: EvaluatorFixture.Ordinal(
                host: host,
                row: "cooldown"
            ),
            write: StateWriteKind.Set
        ));
        long Counted(ulong tick) {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            _ = evaluator.Evaluate(
                latch: latch,
                rules: rules,
                stepTicks: 1UL
            );

            return EvaluatorFixture.Cell(
                host: host,
                row: "score"
            );
        }

        // The deck holds one card and the hand none.
        Assert.Equal(
            actual: Counted(tick: 1UL),
            expected: 1L
        );
        Assert.Equal(
            actual: Counted(tick: 2UL),
            expected: 1L
        );
        Select(zone: 1L);
        Assert.Equal(
            actual: Counted(tick: 3UL),
            expected: 0L
        );
        Select(zone: 0L);
        Assert.Equal(
            actual: Counted(tick: 4UL),
            expected: 1L
        );
    }
}
