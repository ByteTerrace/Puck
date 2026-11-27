namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] FixedAffineFormCases() => [
        ClaimCase(
            claim: Subjects.FixedAffineFormsHoldThePointOperations,
            id: "affine.forms-hold-the-point-operations"
        ),
        ClaimCase(
            claim: Subjects.FixedAffineFormEdges,
            id: "affine.edges"
        ),
    ];
}
