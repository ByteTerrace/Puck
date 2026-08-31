namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] CurvatureSplineCases() => [
        Case(
            id: "curvature-spline.endpoint-curvature-oracle",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineEndpointCurvatureOracle,
                lawId: "curvature-spline.endpoint-curvature-oracle"
            )
        ),
        Case(
            id: "curvature-spline.g2-joint",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineG2Joint,
                lawId: "curvature-spline.g2-joint"
            )
        ),
        Case(
            id: "curvature-spline.arc-length-table",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineArcLengthTable,
                lawId: "curvature-spline.arc-length-table"
            )
        ),
        Case(
            id: "curvature-spline.evaluate-continuity-and-totality",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineEvaluateContinuityAndTotality,
                lawId: "curvature-spline.evaluate-continuity-and-totality"
            )
        ),
        Case(
            id: "curvature-spline.evaluate-raw-station-boundaries",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineEvaluateRawStationBoundaries,
                lawId: "curvature-spline.evaluate-raw-station-boundaries"
            )
        ),
        Case(
            id: "curvature-spline.deterministic-recompile",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineDeterministicRecompile,
                lawId: "curvature-spline.deterministic-recompile"
            )
        ),
        Case(
            id: "curvature-spline.deterministic-multi-root-pick",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineDeterministicMultiRootPick,
                lawId: "curvature-spline.deterministic-multi-root-pick"
            )
        ),
        Case(
            id: "curvature-spline.degenerate-branches",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineDegenerateBranches,
                lawId: "curvature-spline.degenerate-branches"
            )
        ),
        Case(
            id: "curvature-spline.refusal-ladder",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineRefusalLadder,
                lawId: "curvature-spline.refusal-ladder"
            )
        ),
        Case(
            id: "curvature-spline.arc-station-oracle",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineArcStationOracle,
                lawId: "curvature-spline.arc-station-oracle"
            )
        ),
        Case(
            id: "curvature-spline.carrier-extremes",
            run: () => Laws.Claim(
                claim: Subjects.CurvatureSplineCarrierExtremes,
                lawId: "curvature-spline.carrier-extremes"
            )
        ),
    ];
}
