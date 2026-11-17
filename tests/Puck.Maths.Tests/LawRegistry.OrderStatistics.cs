namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] OrderStatisticsCases() => [
        ClaimCase(
            claim: OrderStatisticsClaims.Quantile,
            id: "order-statistics.quantile-hand-computed"
        ),
    ];
}
