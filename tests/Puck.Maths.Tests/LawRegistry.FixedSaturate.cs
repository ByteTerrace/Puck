namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain FixedSaturateDomain = new(
        Key: "fixed-saturate",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] FixedSaturateCases() => [
        SweptCase(
            claim: FixedSaturateClaims.ClampsToTheExtremes,
            domain: FixedSaturateDomain,
            id: "core.fixed-saturate-clamps-to-the-extremes",
            width: 4
        ),
    ];
}
