namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain HexagonalIndices = new(
        Key: "integer-hexagonal-index", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

    private static LawCase[] HexagonalIndexCases() => [
        Case(id: "integer.encoded-operations", run: () => Laws.SweptClaim(
            lawId: "integer.encoded-operations", domain: HexagonalIndices, tier: Tier.Default,
            width: 2, claim: Subjects.EncodedOperations)),
        Case(id: "integer.encoded-operations-boundaries", run: () => Laws.Claim(
            lawId: "integer.encoded-operations-boundaries", claim: Subjects.EncodedOperationsBoundaries)),
        Case(id: "deep.encoded-operations", run: () => Laws.SweptClaim(
            lawId: "deep.encoded-operations", domain: HexagonalIndices, tier: Tier.Deep,
            width: 2, claim: Subjects.EncodedOperations)),
        Case(id: "integer.hexagonal-index-perimeter", run: () => Laws.Claim(
            lawId: "integer.hexagonal-index-perimeter", claim: Subjects.HexagonalIndexPerimeter)),
        Case(id: "integer.hexagonal-index-continuity", run: () => Laws.SweptClaim(
            lawId: "integer.hexagonal-index-continuity", domain: HexagonalIndices, tier: Tier.Default,
            width: 2, claim: Subjects.HexagonalIndexContinuity)),
        Case(id: "integer.hexagonal-index-geometry", run: () => Laws.SweptClaim(
            lawId: "integer.hexagonal-index-geometry", domain: HexagonalIndices, tier: Tier.Default,
            width: 2, claim: Subjects.HexagonalIndexGeometry)),
        Case(id: "integer.hexagonal-index-arithmetic", run: () => Laws.SweptClaim(
            lawId: "integer.hexagonal-index-arithmetic", domain: HexagonalIndices, tier: Tier.Default,
            width: 2, claim: Subjects.HexagonalIndexArithmetic)),
        Case(id: "integer.hexagonal-index-boundaries", run: () => Laws.Claim(
            lawId: "integer.hexagonal-index-boundaries", claim: Subjects.HexagonalIndexBoundaries)),
        Case(id: "deep.hexagonal-index-geometry", run: () => Laws.SweptClaim(
            lawId: "deep.hexagonal-index-geometry", domain: HexagonalIndices, tier: Tier.Deep,
            width: 2, claim: Subjects.HexagonalIndexGeometry)),
    ];
}
