namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain HexagonalIndices = new(
        Key: "integer-hexagonal-index",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] HexagonalIndexCases() => [
        SweptCase(
            claim: Subjects.EncodedOperations,
            domain: HexagonalIndices,
            id: "integer.encoded-operations",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.EncodedOperationsBoundaries,
            id: "integer.encoded-operations-boundaries"
        ),
        SweptCase(
            claim: Subjects.EncodedOperations,
            domain: HexagonalIndices,
            id: "deep.encoded-operations",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.HexagonalIndexPerimeter,
            id: "integer.hexagonal-index-perimeter"
        ),
        SweptCase(
            claim: Subjects.HexagonalIndexContinuity,
            domain: HexagonalIndices,
            id: "integer.hexagonal-index-continuity",
            width: 2
        ),
        SweptCase(
            claim: Subjects.HexagonalIndexGeometry,
            domain: HexagonalIndices,
            id: "integer.hexagonal-index-geometry",
            width: 2
        ),
        SweptCase(
            claim: Subjects.HexagonalIndexArithmetic,
            domain: HexagonalIndices,
            id: "integer.hexagonal-index-arithmetic",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.HexagonalIndexBoundaries,
            id: "integer.hexagonal-index-boundaries"
        ),
        SweptCase(
            claim: Subjects.HexagonalIndexGeometry,
            domain: HexagonalIndices,
            id: "deep.hexagonal-index-geometry",
            width: 2
        ),
    ];
}
