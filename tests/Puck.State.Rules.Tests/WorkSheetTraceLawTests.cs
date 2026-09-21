using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: over every small world the enumeration below can spell, the work one tick is
/// observed to do never exceeds what the sheet admitted for it. The observation is the evaluator's own trace —
/// which rules were evaluated, under which key, and whether each gate held — priced line by line; the admitted
/// figure is <see cref="RuleWorkBudget.Tally"/>. A closed gate is charged its check, and a sweep its setup.</summary>
public sealed class WorkSheetTraceLawTests {
    private const int Phases = 3;

    private static Rule Phase(int value) => new(
        Effects: [.. Enumerable.Range(
            count: (value + 1),
            start: 0
        ).Select(selector: static _ => EvaluatorFixture.Add(
            row: "score",
            value: 1m
        ))],
        Gate: EvaluatorFixture.Compare(
            comparison: ActionStateComparison.Equal,
            row: "flag",
            value: value
        ),
        Name: RulesFixture.Name(value: $"phase{value}")
    );
    // Moves the discriminator the phase rules pin, gated on a cell none of them pin, so it fires beside them.
    private static Rule Writer(int target) => new(
        Effects: [EvaluatorFixture.Set(
                row: "flag",
                value: target
            )],
        Gate: EvaluatorFixture.Compare(
            comparison: ActionStateComparison.GreaterOrEqual,
            row: "third",
            value: 1m
        ),
        Name: RulesFixture.Name(value: "writer")
    );
    private static Rule Sweep() => new(
        Effects: [EvaluatorFixture.Add(
                row: "other",
                value: 1m
            )],
        ForEach: "hand",
        Name: RulesFixture.Name(value: "sweep")
    );
    // The swept row holds no more than the enumeration fills, so a full hand leaves the sheet no unused
    // evaluations to absorb a deficit elsewhere.
    private static StateSection Section(int hand) {
        var rows = new List<StateRow>();

        foreach (var row in (EvaluatorFixture.Section().Rows ?? [])) {
            rows.Add(item: (string.Equals(
                a: row.Name.Value,
                b: "hand",
                comparisonType: StringComparison.Ordinal
            )
                ? (row with {
                    Capacity = 2,
                    Cells = [.. row.Cells!.Take(count: hand)],
                })
                : row
            ));
        }

        return new StateSection(Rows: rows);
    }
    private static void Write(ArenaEffectHost host, string row, long value) => Assert.True(condition: host.Arena.TryWrite(
        key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
        operand: value,
        reason: out _,
        rowOrdinal: EvaluatorFixture.Ordinal(
            host: host,
            row: row
        ),
        write: StateWriteKind.Set
    ));
    // The observed and the admitted work of one tick of one world.
    private static (long Observed, long Admitted, int PhasesFired) Run(IReadOnlyList<Rule> rules, int hand, long flag, long third) {
        var (host, evaluator, compiled, latch, context) = EvaluatorFixture.Arrange(
            rules: rules,
            section: Section(hand: hand)
        );
        var lines = new List<RuleWorkContributor>(capacity: compiled.Length);
        var multiplied = new List<(CompiledRule Rule, long Multiplier)>(capacity: compiled.Length);

        foreach (var rule in compiled) {
            var multiplier = RuleWorkBudget.ForEachCount(
                context: context,
                rule: rule
            );

            lines.Add(item: RuleWorkBudget.Contributor(
                context: context,
                isInteraction: false,
                multiplier: multiplier,
                rule: rule
            ));
            multiplied.Add(item: (rule, multiplier));
        }

        var (_, admitted) = RuleWorkBudget.Tally(
            contributors: lines,
            writers: RuleWorkBudget.CountWriters(rules: multiplied)
        );

        Write(
            host: host,
            row: "flag",
            value: flag
        );
        Write(
            host: host,
            row: "third",
            value: third
        );
        host.Advance(
            engineTick: 0UL,
            tick: 0UL
        );
        Assert.True(condition: evaluator.ArmTraceAll(maxEvaluations: 1024));
        _ = evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL
        );

        var observed = 0L;
        var phasesFired = 0;

        // Every rule is swept once whether or not it binds a key, so its setup is paid once.
        foreach (var line in lines) {
            observed += line.Cost.Setup.Units;
        }
        foreach (var entry in evaluator.TraceCaptured) {
            var cost = lines.Single(predicate: line => string.Equals(
                a: line.Name,
                b: entry.Rule,
                comparisonType: StringComparison.Ordinal
            )).Cost;

            observed += cost.Check.Units;
            if (entry.GateOpen) {
                observed += cost.Effects.Units;
                if (entry.Rule.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "phase"
                )) {
                    phasesFired++;
                }
            }
        }

        return (observed, admitted.Units, phasesFired);
    }

    [Fact]
    public void ObservedWorkNeverExceedsTheAdmittedWorkOnAnyEnumeratedWorld() {
        var worlds = 0;
        var twoPhasesInOneTick = 0;

        for (var position = 0; (position <= Phases); position++) {
            for (var target = 0; (target < Phases); target++) {
                for (var hand = 0; (hand <= 2); hand++) {
                    for (var flag = 0L; (flag < Phases); flag++) {
                        for (var third = 0L; (third <= 1L); third++) {
                            var rules = new List<Rule> {
                                Phase(value: 0),
                                Phase(value: 1),
                                Phase(value: 2),
                                Sweep(),
                            };

                            rules.Insert(
                                index: position,
                                item: Writer(target: target)
                            );

                            var (observed, admitted, phasesFired) = Run(
                                flag: flag,
                                hand: hand,
                                rules: rules,
                                third: third
                            );

                            Assert.True(
                                condition: (observed <= admitted),
                                userMessage: $"writer at {position} to {target}, hand {hand}, flag {flag}, third {third}: observed {observed} exceeds admitted {admitted}"
                            );
                            worlds++;
                            if (phasesFired >= 2) {
                                twoPhasesInOneTick++;
                            }
                        }
                    }
                }
            }
        }

        // The enumeration reaches the case the writer allowance exists for, so the inequality is not held only by
        // worlds in which one phase fires.
        Assert.True(
            condition: (twoPhasesInOneTick > 0),
            userMessage: $"no world of {worlds} fired two phase rules in one tick"
        );
    }
    [Fact]
    public void WithoutAWriterTheAdmittedWorkIsExactlyTheCostliestObservedTick() {
        var costliest = 0L;
        var admittedOnce = 0L;

        for (var flag = 0L; (flag < Phases); flag++) {
            var (observed, admitted, _) = Run(
                flag: flag,
                hand: 2,
                rules: [Phase(value: 0), Phase(value: 1), Phase(value: 2)],
                third: 0L
            );

            Assert.True(condition: (observed <= admitted));
            admittedOnce = admitted;
            costliest = Math.Max(
                val1: costliest,
                val2: observed
            );
        }

        Assert.Equal(
            admittedOnce,
            costliest
        );
    }
}
