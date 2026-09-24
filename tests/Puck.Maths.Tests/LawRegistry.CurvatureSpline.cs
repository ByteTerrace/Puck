namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] CurvatureSplineCases() => [
        ClaimCase(
            claim: Subjects.CurvatureSplineEndpointCurvatureOracle,
            id: "curvature-spline.endpoint-curvature-oracle"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineG2Joint,
            id: "curvature-spline.g2-joint"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineArcLengthTable,
            id: "curvature-spline.arc-length-table"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineEvaluateContinuityAndTotality,
            id: "curvature-spline.evaluate-continuity-and-totality"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineEvaluateRawStationBoundaries,
            id: "curvature-spline.evaluate-raw-station-boundaries"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineDeterministicRecompile,
            id: "curvature-spline.deterministic-recompile"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineDeterministicMultiRootPick,
            id: "curvature-spline.deterministic-multi-root-pick"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineDegenerateBranches,
            id: "curvature-spline.degenerate-branches"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineRefusalLadder,
            id: "curvature-spline.refusal-ladder"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineArcStationOracle,
            id: "curvature-spline.arc-station-oracle"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineCarrierExtremes,
            id: "curvature-spline.carrier-extremes"
        ),
        ClaimCase(
            claim: Subjects.CurvatureSplineEvaluateCurvature,
            id: "curvature-spline.evaluate-curvature"
        ),
    ];
}
