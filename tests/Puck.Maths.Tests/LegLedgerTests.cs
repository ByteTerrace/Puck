using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// The leg gate. Every gate statement in this suite declares the legs it stands on, and this fact proves the
/// declarations exist and are well formed. It also claims the <c>leg-ledger.md</c> artifact for the run: the assembly
/// ledger regenerates the file only when this gate ran, so a filtered run can neither refresh nor half-write it.
/// </summary>
/// <remarks>
/// What this gate DOES check: every statement names at least one leg; every leg carries the text its kind requires (an
/// agreement names what it stands against, a shared-substrate leg names what is shared, a delegation or shared-exact
/// leg cites where the shared kernel is independently pinned, an in-tree-independent leg cites its envelope, a
/// transcription names the independent witness beside it, a relative
/// canary names an absolute sibling that RESOLVES to a real statement); a case's declaration matches the combinator it
/// actually ran, checked in <see cref="LawTests"/> against the shape the combinator reported, which includes requiring
/// an INDEPENDENT leg from every twin that ran a third-leg witness.
/// What it does NOT check: whether a leg is TRUE. Nothing here reads the bodies the strings describe, so a leg declared
/// classical that actually shares an algorithm passes. Detecting a lying classification is the adversarial review's
/// job.
/// </remarks>
public sealed class LegLedgerTests {
    [Fact]
    [Trait(name: "tier", value: "Default")]
    public void LawLegsAreDeclared() {
        LedgerState.RecordLawLegGate();

        var rows = LegLedger.LawRows();

        Assert.Equal(expected: LawRegistry.All.Count, actual: rows.Count);

        var violations = LegLedger.DeclarationViolations(rows: rows);

        Assert.True(condition: (violations.Count == 0), userMessage: $"{violations.Count} malformed leg declaration(s): {string.Join(separator: " ", values: violations.Take(count: 20))}");

        var unresolved = LegLedger.UnresolvedSiblings(rows: rows);

        Assert.True(condition: (unresolved.Count == 0), userMessage: $"{unresolved.Count} relative canary/canaries name an absolute sibling that does not resolve: {string.Join(separator: " ", values: unresolved.Take(count: 20))}");
    }
}
