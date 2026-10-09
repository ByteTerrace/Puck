using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Laws for <c>CanaryCeilings.json</c> and <c>puck canary-ceilings</c> (<see cref="CanaryCeilingsLedger"/>): recording
/// round-trips; a recorded count that differs from its plan in either direction is drift; the shipped ledger equals
/// the plans of the shipped manifests; and two branches that each record a change collide in the ledger, or, when
/// they record the same change, merge to a ledger that no longer matches the combined plan.
/// </summary>
public sealed class CanaryCeilingsLedgerLawTests {
    private static readonly CanaryCeilingsLedger Base = new(
        Automatic: new CanaryCeiling(
            LegBudgetSeconds: 3000,
            WorldBoots: 78
        ),
        Merge: new CanaryCeiling(
            LegBudgetSeconds: 16000,
            WorldBoots: 300
        )
    );

    private static CanaryCeilingsLedger Grown(CanaryCeilingsLedger ledger, int boots, int seconds) => ledger with {
        Merge = new CanaryCeiling(
            LegBudgetSeconds: (ledger.Merge.LegBudgetSeconds + seconds),
            WorldBoots: (ledger.Merge.WorldBoots + boots)
        ),
    };
    private static IReadOnlyList<string> Check(CanaryCeilingsLedger recorded, CanaryCeilingsLedger planned) => recorded.Check(
        ledgerText: recorded.Render(),
        planned: planned
    );

    [Fact]
    public void ARecordedLedgerRoundTripsThroughItsOneSpelling() {
        var text = Base.Render();

        Assert.True(
            condition: CanaryCeilingsLedger.TryParse(
                error: out var error,
                json: text,
                ledger: out var parsed
            ),
            userMessage: error
        );
        Assert.Equal(
            actual: parsed,
            expected: Base
        );
        Assert.Equal(
            actual: parsed!.Render(),
            expected: text
        );
        Assert.EndsWith(
            actualString: text,
            expectedEndString: "}\n"
        );
        Assert.DoesNotContain(
            actualString: text,
            expectedSubstring: "\r"
        );
    }
    [Fact]
    public void AnUnknownOrMissingMemberIsRefusedRatherThanReadAsEmpty() {
        Assert.False(condition: CanaryCeilingsLedger.TryParse(
            error: out _,
            json: Base.Render().Replace(
                newValue: "\"extra\": 1,\n        \"merge\"",
                oldValue: "\"merge\""
            ),
            ledger: out _
        ));
        Assert.False(condition: CanaryCeilingsLedger.TryParse(
            error: out _,
            json: Base.Render().Replace(
                newValue: "\"worldBot\"",
                oldValue: "\"worldBoots\""
            ),
            ledger: out _
        ));
        Assert.False(condition: CanaryCeilingsLedger.TryParse(
            error: out _,
            json: "{}",
            ledger: out _
        ));
    }
    [Fact]
    public void ALedgerThatEqualsItsPlanHoldsAndAnyDifferenceIsDriftInEitherDirection() {
        Assert.Empty(collection: Check(
            planned: Base,
            recorded: Base
        ));

        var rise = Check(
            planned: Grown(
                boots: 3,
                ledger: Base,
                seconds: 0
            ),
            recorded: Base
        );

        Assert.Single(collection: rise);
        Assert.Contains(
            actualString: rise[0],
            expectedSubstring: "merge: plans 303 World boot(s) against 300 recorded (a rise)"
        );

        var fall = Check(
            planned: Grown(
                boots: 0,
                ledger: Base,
                seconds: -10
            ),
            recorded: Base
        );

        Assert.Single(collection: fall);
        Assert.Contains(
            actualString: fall[0],
            expectedSubstring: "merge: plans a 15990-second leg budget against 16000 recorded (a fall)"
        );
        Assert.Equal(
            expected: 4,
            actual: Check(
                planned: new CanaryCeilingsLedger(
                    Automatic: new CanaryCeiling(
                        LegBudgetSeconds: 1,
                        WorldBoots: 1
                    ),
                    Merge: new CanaryCeiling(
                        LegBudgetSeconds: 1,
                        WorldBoots: 1
                    )
                ),
                recorded: Base
            ).Count
        );
    }
    [Fact]
    public void ALedgerWhoseBytesDifferFromTheWriterIsDriftEvenWhenEveryCountHolds() {
        var problems = Base.Check(
            ledgerText: Base.Render().Replace(
                newValue: "\"worldBoots\":  78",
                oldValue: "\"worldBoots\": 78"
            ),
            planned: Base
        );

        Assert.Single(collection: problems);
        Assert.Contains(
            actualString: problems[0],
            expectedSubstring: "not canonical"
        );
    }
    [Fact]
    public void TheShippedLedgerIsExactlyThePlanOfTheShippedManifests() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(
            condition: CanaryCeilingsCommand.TryPlan(
                error: out var planError,
                planned: out var planned,
                repositoryRoot: repositoryRoot
            ),
            userMessage: planError
        );
        Assert.True(
            condition: CanaryCeilingsLedger.TryRead(
                error: out var readError,
                ledger: out var recorded,
                repositoryRoot: repositoryRoot,
                text: out var text
            ),
            userMessage: readError
        );
        Assert.Empty(collection: recorded!.Check(
            ledgerText: text,
            planned: planned!
        ));
    }
    [Fact]
    public void TwoBranchesThatRecordDifferentRisesCollideTextually() {
        var (conflicts, merged) = LedgerMergeProbe.Merge(
            baseText: Base.Render(),
            ours: Grown(
                boots: 3,
                ledger: Base,
                seconds: 30
            ).Render(),
            theirs: Grown(
                boots: 5,
                ledger: Base,
                seconds: 50
            ).Render()
        );

        Assert.True(condition: (conflicts > 0));
        Assert.Contains(
            actualString: merged,
            expectedSubstring: "<<<<<<<"
        );
    }
    [Fact]
    public void TwoBranchesThatRecordTheSameRiseMergeCleanlyToALedgerTheCombinedPlanFails() {
        var each = Grown(
            boots: 3,
            ledger: Base,
            seconds: 30
        );

        var (conflicts, merged) = LedgerMergeProbe.Merge(
            baseText: Base.Render(),
            ours: each.Render(),
            theirs: each.Render()
        );
        var combined = Grown(
            boots: 6,
            ledger: Base,
            seconds: 60
        );

        Assert.Equal(
            actual: conflicts,
            expected: 0
        );
        Assert.True(condition: CanaryCeilingsLedger.TryParse(
            error: out _,
            json: merged,
            ledger: out var mergedLedger
        ));
        Assert.Equal(
            expected: 2,
            actual: mergedLedger!.Check(
                ledgerText: merged,
                planned: combined
            ).Count
        );
    }
}
