namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain SquareGrid = new(
        Key: "square-grid",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] SquareGridCases() => [
        Case(
            id: "integer.square-coordinate",
            run: () => Laws.SweptClaim(
                claim: Subjects.SquareCoordinateOperations,
                domain: SquareGrid,
                lawId: "integer.square-coordinate",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "integer.square-index",
            run: () => Laws.SweptClaim(
                claim: Subjects.SquareIndexOperations,
                domain: SquareGrid,
                lawId: "integer.square-index",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "integer.square-grid-boundaries",
            run: () => Laws.Claim(
                claim: Subjects.SquareGridBoundaries,
                lawId: "integer.square-grid-boundaries"
            )
        ),
        Case(
            id: "deep.square-coordinate",
            run: () => Laws.SweptClaim(
                claim: Subjects.SquareCoordinateOperations,
                domain: SquareGrid,
                lawId: "deep.square-coordinate",
                tier: Tier.Deep,
                width: 2
            )
        ),
        Case(
            id: "deep.square-index",
            run: () => Laws.SweptClaim(
                claim: Subjects.SquareIndexOperations,
                domain: SquareGrid,
                lawId: "deep.square-index",
                tier: Tier.Deep,
                width: 2
            )
        ),
    ];
}
