namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain SquareGrid = new(
        Key: "square-grid",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] SquareGridCases() => [
        SweptCase(
            claim: Subjects.SquareCoordinateOperations,
            domain: SquareGrid,
            id: "integer.square-coordinate",
            width: 2
        ),
        SweptCase(
            claim: Subjects.SquareIndexOperations,
            domain: SquareGrid,
            id: "integer.square-index",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.SquareGridBoundaries,
            id: "integer.square-grid-boundaries"
        ),
        SweptCase(
            claim: Subjects.SquareCoordinateOperations,
            domain: SquareGrid,
            id: "deep.square-coordinate",
            width: 2
        ),
        SweptCase(
            claim: Subjects.SquareIndexOperations,
            domain: SquareGrid,
            id: "deep.square-index",
            width: 2
        ),
    ];
}
