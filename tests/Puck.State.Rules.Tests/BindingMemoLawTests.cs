using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a bound value served from its memo is the value a fresh evaluation would bind. A
/// binding that reads through a live zone rests on the cell that selects the zone as well as on the zones it may
/// select, so moving the selection to another zone binds that zone's answer, never the last one's.</summary>
public sealed class BindingMemoLawTests {
    [Fact]
    public void ABindingThatAliasesATickReadThroughACallIsNotMemoizedAcrossTicks() {
        var tick = RulesFixture.Program(text: RuleFacts.Tick);
        var throughCall = new ExpressionProgram(Instructions: [Instruction.Call(subprogram: 0)]) {
            Subprograms = [new Subprogram(
                Arity: 0,
                Instructions: tick.Instructions,
                Name: "now"
            )],
        };

        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Effects: [new ActionEffect.SetState(
                    Expression: RulesFixture.Program(text: "$local:copy"),
                    State: "score"
                )],
                Locals: [
                    new RuleLocal(
                        Expression: throughCall,
                        Name: RulesFixture.Name(value: "now")
                    ),
                    new RuleLocal(
                        Expression: RulesFixture.Program(text: "$local:now"),
                        Name: RulesFixture.Name(value: "copy")
                    ),
                ],
                Name: RulesFixture.Name(value: "clock")
            )]
        );

        long Tick(ulong tickValue) {
            host.Advance(
                engineTick: tickValue,
                tick: tickValue
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

        Assert.Equal(expected: 1L, actual: Tick(tickValue: 1UL));
        Assert.Equal(expected: 2L, actual: Tick(tickValue: 2UL));
    }
    [Fact]
    public void ABindingThroughALocalKeyThatReadsTickIsNotMemoizedAcrossTicks() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Effects: [new ActionEffect.SetState(
                    Expression: RulesFixture.Program(text: "$local:copy"),
                    State: "score"
                )],
                Locals: [
                    new RuleLocal(
                        Expression: RulesFixture.Program(text: RuleFacts.Tick),
                        Name: RulesFixture.Name(value: "key")
                    ),
                    new RuleLocal(
                        Expression: RulesFixture.Program(text: "hand[$local:key]"),
                        Name: RulesFixture.Name(value: "copy")
                    ),
                ],
                Name: RulesFixture.Name(value: "clock-key")
            )]
        );

        var hand = EvaluatorFixture.Ordinal(
            host: host,
            row: "hand"
        );

        Assert.True(condition: host.Arena.TryMint(
            key: out _,
            name: RulesFixture.Name(value: "1"),
            reason: out var oneReason,
            rowOrdinal: hand,
            value: CellValue.Int(value: 1L)
        ), userMessage: oneReason);
        Assert.True(condition: host.Arena.TryMint(
            key: out _,
            name: RulesFixture.Name(value: "2"),
            reason: out var twoReason,
            rowOrdinal: hand,
            value: CellValue.Int(value: 2L)
        ), userMessage: twoReason);
        long Tick(ulong tickValue) {
            host.Advance(
                engineTick: tickValue,
                tick: tickValue
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

        Assert.Equal(expected: 1L, actual: Tick(tickValue: 1UL));
        Assert.Equal(expected: 2L, actual: Tick(tickValue: 2UL));
    }
    // A fold reads every cell of its row, including the cells a rule writes into a row declared with none, so the
    // binding that folds it is recomputed after each committed write and is priced at the row's capacity rather than
    // at its authored cells.
    [Fact]
    public void ABindingThatFoldsARowDeclaredWithNoCellsFollowsTheCellsWrittenIntoIt() {
        var section = new StateSection(Rows: [
            EvaluatorFixture.Slot(
                name: "score",
                value: 0L
            ),
            new StateRow(
                Capacity: 8,
                Kind: CellKind.Int,
                Name: RulesFixture.Name(value: "tally")
            ),
        ]);

        var (host, evaluator, rules, latch, context) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Locals: [new RuleLocal(
                        Expression: RulesFixture.Program(text: "count(tally, c -> c != 0)"),
                        Kind: CellKind.Int,
                        Name: RulesFixture.Name(value: "held")
                    )],
                Effects: [new ActionEffect.SetState(
                        Expression: RulesFixture.Program(text: "$local:held"),
                        State: "score"
                    )],
                Name: RulesFixture.Name(value: "count")
            )],
            section: section
        );
        var tally = EvaluatorFixture.Ordinal(
            host: host,
            row: "tally"
        );

        void Mint(string key) {
            var mark = host.Arena.BeginScope();

            Assert.True(condition: host.Arena.TryMint(
                key: out _,
                name: RulesFixture.Name(value: key),
                reason: out var reason,
                rowOrdinal: tally,
                value: CellValue.Int(value: 1L)
            ), userMessage: reason);
            host.Arena.Commit(mark: mark);
        }
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

        Assert.Equal(
            actual: Counted(tick: 1UL),
            expected: 0L
        );
        Mint(key: "a");
        Assert.Equal(
            actual: Counted(tick: 2UL),
            expected: 1L
        );
        Mint(key: "b");
        Assert.Equal(
            actual: Counted(tick: 3UL),
            expected: 2L
        );

        var fold = Assert.Single(
            collection: rules[0].Locals!.SelectMany(selector: static local => local.Expression),
            predicate: static token => (token.Fold is not null)
        );

        Assert.Equal(
            actual: fold.Fold!.Cells,
            expected: context.RowCapacity(rowOrdinal: tally)
        );
    }
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
