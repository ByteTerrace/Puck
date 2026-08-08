using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Runs every declared law case as a theory row, one test per case, named by its id and tagged with its tier trait.
/// Tier selection is declarative: the project's default runsettings exclude Deep and Bench, and each tier has a
/// committed <c>*.runsettings</c> whose <c>TestCaseFilter</c> selects it (<c>dotnet test --settings …</c>).
/// This is also where a law failure is RAISED for the frontier's green gate (see
/// <see cref="Frontier.AdvanceAndPersist"/>): every law-side failure mode passes through this one frame, and no
/// narrower one sees them all.
/// </summary>
public sealed class LawTests {
    /// <summary>The theory data: one row per law case, tier-tagged and id-named.</summary>
    /// <returns>The rows.</returns>
    public static IEnumerable<ITheoryDataRow> Cases() =>
        LawRegistry.All.Select(selector: static lawCase =>
            new TheoryDataRow<string>(p1: lawCase.Id)
                .WithTrait(name: "tier", value: lawCase.Tier.ToString())
                .WithTestDisplayName(testDisplayName: lawCase.Id));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Law(string id) {
        var lawCase = LawRegistry.ById[id];

        // Every law-side failure mode flows through THIS frame and no narrower one. Laws.Fail's Assert.Fail throws, so
        // a counterexample aborts the combinator's loop and the case body here; Laws.Claim asserts directly rather than
        // through Fail; the shape gate below is asserted here; and an unexpected throw from a subject, an oracle or a
        // domain enumerator never reaches Laws.Fail at all. Raising the signal inside Laws would miss three of those
        // four, which is why it is raised here. A dynamic skip is not a failure and passes through untouched.
        try {
            // The leg declaration is checked against the combinators that actually ran, here, in the same test — so the
            // check cannot be made vacuous by ordering or by xUnit parallelism.
            var observed = LawShapes.Observe(run: lawCase.Run);

            Assert.True(condition: (LegLedger.ShapeViolation(lawCase: lawCase, observed: observed) is null), userMessage: $"{id} {LegLedger.ShapeViolation(lawCase: lawCase, observed: observed)}");
        } catch (Exception exception) when (exception is not Xunit.Sdk.SkipException) {
            LedgerState.RecordLawFailure();

            throw;
        }

        LedgerState.RecordLaw(tier: lawCase.Tier);
    }
}
