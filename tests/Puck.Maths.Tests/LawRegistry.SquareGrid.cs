namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain SquareGrid = new(Key: "square-grid", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static LawCase[] SquareGridCases() => [
        Case(id: "integer.square-coordinate", run: () => Laws.SweptClaim(lawId: "integer.square-coordinate", domain: SquareGrid, tier: Tier.Default, width: 2, claim: Subjects.SquareCoordinateOperations)),
        Case(id: "integer.square-index", run: () => Laws.SweptClaim(lawId: "integer.square-index", domain: SquareGrid, tier: Tier.Default, width: 2, claim: Subjects.SquareIndexOperations)),
        Case(id: "integer.square-grid-boundaries", run: () => Laws.Claim(lawId: "integer.square-grid-boundaries", claim: Subjects.SquareGridBoundaries)),
        Case(id: "deep.square-coordinate", run: () => Laws.SweptClaim(lawId: "deep.square-coordinate", domain: SquareGrid, tier: Tier.Deep, width: 2, claim: Subjects.SquareCoordinateOperations)),
        Case(id: "deep.square-index", run: () => Laws.SweptClaim(lawId: "deep.square-index", domain: SquareGrid, tier: Tier.Deep, width: 2, claim: Subjects.SquareIndexOperations)),
    ];
}
