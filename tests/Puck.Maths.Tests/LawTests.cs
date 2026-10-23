using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Runs every declared law case as a theory row, one test per case, named by its id and tagged with its tier trait.
/// Tier selection is declarative: Deep and Exhaustive rows are explicit, so a plain run leaves them out, and the tier
/// trait selects one tier (<c>--explicit on --filter-trait tier=Deep</c>).
/// </summary>
public sealed class LawTests {
    /// <summary>The theory data: one row per law case, tier-tagged and id-named.</summary>
    /// <returns>The rows.</returns>
    public static IEnumerable<ITheoryDataRow> Cases() =>
        LawRegistry.All.Select(selector: static lawCase =>
            new TheoryDataRow<string>(p1: lawCase.Id)
                .WithTrait(
            name: "tier",
            value: lawCase.Tier.ToString()
        )
                .WithExplicit(@explicit: (lawCase.Tier is Tier.Deep or Tier.Exhaustive))
                .WithTestDisplayName(testDisplayName: lawCase.Id));
    [MemberData(nameof(Cases))]
    [Theory]
    public void Law(string id) {
        var lawCase = LawRegistry.ById[id];

        // The leg declaration is checked against the combinators that actually ran, here, in the same test — so the
        // check cannot be made vacuous by ordering or by xUnit parallelism.
        var observed = LawShapes.Observe(run: lawCase.Run);

        Assert.True(
            condition: (LawLegs.ShapeViolation(
                lawCase: lawCase,
                observed: observed
            ) is null),
            userMessage: $"{id} {LawLegs.ShapeViolation(
                lawCase: lawCase,
                observed: observed
            )}"
        );
    }
}
