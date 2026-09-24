namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] VectorFunctionsCases() => [
        ClaimCase(
            claim: VectorFunctionsClaims.IsFiniteVsOracle,
            id: "vector-functions.is-finite-vs-oracle"
        ),
    ];
}
