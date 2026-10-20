namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] VectorFunctionsCases() => [
        ClaimCase(
            claim: VectorFunctionsClaims.IsFiniteVsOracle,
            id: "vector-functions.is-finite-vs-oracle"
        ),
        ClaimCase(
            claim: VectorFunctionsClaims.AngleWrapsRangeAndCongruence,
            id: "vector-functions.angle-wraps-range-and-congruence"
        ),
        ClaimCase(
            claim: VectorFunctionsClaims.EasingAndFract,
            id: "vector-functions.easing-and-fract"
        ),
        ClaimCase(
            claim: VectorFunctionsClaims.ShortestArcAndPerpendicular,
            id: "vector-functions.shortest-arc-and-perpendicular"
        ),
        ClaimCase(
            claim: VectorFunctionsClaims.AttitudeReadbacks,
            id: "vector-functions.attitude-readbacks"
        ),
        ClaimCase(
            claim: VectorFunctionsClaims.UnormPackingVsExact,
            id: "vector-functions.unorm-packing-vs-exact"
        ),
    ];
}
