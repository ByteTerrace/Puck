namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain IntervalArithmetic = new(
        Key: "interval-arithmetic",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain IntervalIsotonic = new(
        Key: "interval-isotonic",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain IntervalCircular = new(
        Key: "interval-circular",
        Block: 512,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain IntervalArcTangent = new(
        Key: "interval-arctangent",
        Block: 512,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain IntervalArcFunctions = new(
        Key: "interval-arc-functions",
        Block: 512,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] FixedIntervalCases() => [
        SweptCase(
            claim: Subjects.FixedIntervalArithmeticIsTheDirectedHull,
            domain: IntervalArithmetic,
            id: "interval.arithmetic-is-the-directed-hull",
            width: 4
        ),
        SweptCase(
            claim: Subjects.FixedIntervalIsInclusionIsotonic,
            domain: IntervalIsotonic,
            id: "interval.inclusion-isotonic",
            width: 4
        ),
        SweptCase(
            claim: Subjects.FixedIntervalCircularEnclosesTheSeries,
            domain: IntervalCircular,
            id: "interval.circular-encloses-the-series",
            width: 2
        ),
        SweptCase(
            claim: Subjects.FixedIntervalAtan2EnclosesTheSeries,
            domain: IntervalArcTangent,
            id: "interval.atan2-encloses-the-series",
            width: 4
        ),
        SweptCase(
            claim: Subjects.FixedIntervalArcFunctionsEncloseTheSeries,
            domain: IntervalArcFunctions,
            id: "interval.arc-functions-enclose-the-series",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.FixedIntervalEdges,
            id: "interval.edges"
        ),
    ];
}
