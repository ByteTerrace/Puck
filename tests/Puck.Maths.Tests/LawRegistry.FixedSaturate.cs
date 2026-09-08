namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain FixedSaturateDomain = new(
        Key: "fixed-saturate",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] FixedSaturateCases() => [
        Case(
            id: "core.fixed-saturate-clamps-to-the-extremes",
            run: () => Laws.SweptClaim(
                claim: FixedSaturateClaims.ClampsToTheExtremes,
                domain: FixedSaturateDomain,
                lawId: "core.fixed-saturate-clamps-to-the-extremes",
                tier: Tier.Default,
                width: 4
            )
        ),
    ];
}
