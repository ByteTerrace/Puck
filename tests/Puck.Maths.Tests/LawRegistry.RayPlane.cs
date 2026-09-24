namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain RayPlane = new(
        Key: "vector-ray-plane",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] RayPlaneCases() => [
        SweptCase(
            claim: Subjects.RayPlaneMatchesOracle,
            domain: RayPlane,
            id: "vector.ray-plane-vs-oracle",
            width: 6
        ),
        ClaimCase(
            claim: Subjects.RayPlaneKnownHits,
            id: "vector.ray-plane-known-hits"
        ),
    ];
}
