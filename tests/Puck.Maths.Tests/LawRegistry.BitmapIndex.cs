namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] BitmapIndexCases() => [
        ClaimCase(
            claim: BitmapIndexClaims.RangeVsScan,
            id: "bitmap-index.range-vs-scan"
        ),
    ];
}
