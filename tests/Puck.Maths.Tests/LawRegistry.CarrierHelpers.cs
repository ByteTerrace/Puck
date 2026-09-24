namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain VectorHelpers = new(
        Key: "vector-componentwise-helpers",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain TryArithmetic = new(
        Key: "integer-try-arithmetic",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] CarrierHelperCases() => [
        SweptCase(
            claim: Subjects.VectorComponentwiseHelpersMatchOracle,
            domain: VectorHelpers,
            id: "vector.componentwise-helpers-vs-oracle",
            width: 4
        ),
        ClaimCase(
            claim: Subjects.VectorUnitAxesAreExact,
            id: "vector.unit-axes-are-exact"
        ),
        Case(
            id: "integer.try-add-and-try-narrow-vs-exact",
            run: () => {
                Laws.SweptClaim(
                    claim: TryArithmeticClaims.AddAndNarrowMatchExactArithmetic,
                    domain: TryArithmetic,
                    lawId: "integer.try-add-and-try-narrow-vs-exact",
                    tier: Tier.Default,
                    width: 2
                );
                Laws.Claim(
                    claim: TryArithmeticClaims.SeamsAreExact,
                    lawId: "integer.try-add-and-try-narrow-vs-exact"
                );
            }
        ),
    ];
}
