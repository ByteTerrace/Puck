namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain HexagonalIndices = new(
        Key: "integer-hexagonal-index",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] HexagonalIndexCases() => [
        Case(
            id: "integer.encoded-operations",
            run: () => Laws.SweptClaim(
                claim: Subjects.EncodedOperations,
                domain: HexagonalIndices,
                lawId: "integer.encoded-operations",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "integer.encoded-operations-boundaries",
            run: () => Laws.Claim(
                claim: Subjects.EncodedOperationsBoundaries,
                lawId: "integer.encoded-operations-boundaries"
            )
        ),
        Case(
            id: "deep.encoded-operations",
            run: () => Laws.SweptClaim(
                claim: Subjects.EncodedOperations,
                domain: HexagonalIndices,
                lawId: "deep.encoded-operations",
                tier: Tier.Deep,
                width: 2
            )
        ),
        Case(
            id: "integer.hexagonal-index-perimeter",
            run: () => Laws.Claim(
                claim: Subjects.HexagonalIndexPerimeter,
                lawId: "integer.hexagonal-index-perimeter"
            )
        ),
        Case(
            id: "integer.hexagonal-index-continuity",
            run: () => Laws.SweptClaim(
                claim: Subjects.HexagonalIndexContinuity,
                domain: HexagonalIndices,
                lawId: "integer.hexagonal-index-continuity",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "integer.hexagonal-index-geometry",
            run: () => Laws.SweptClaim(
                claim: Subjects.HexagonalIndexGeometry,
                domain: HexagonalIndices,
                lawId: "integer.hexagonal-index-geometry",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "integer.hexagonal-index-arithmetic",
            run: () => Laws.SweptClaim(
                claim: Subjects.HexagonalIndexArithmetic,
                domain: HexagonalIndices,
                lawId: "integer.hexagonal-index-arithmetic",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "integer.hexagonal-index-boundaries",
            run: () => Laws.Claim(
                claim: Subjects.HexagonalIndexBoundaries,
                lawId: "integer.hexagonal-index-boundaries"
            )
        ),
        Case(
            id: "deep.hexagonal-index-geometry",
            run: () => Laws.SweptClaim(
                claim: Subjects.HexagonalIndexGeometry,
                domain: HexagonalIndices,
                lawId: "deep.hexagonal-index-geometry",
                tier: Tier.Deep,
                width: 2
            )
        ),
    ];
}
