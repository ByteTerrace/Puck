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
}
