namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // A Sweep's chain reach is the control polygon's own max distance from the local origin plus the largest radius
    // the sweep's profile can reach plus the strand orbit radius — the same sum SdfSolidGeometry.SweepReach and
    // SdfProgramBuilder.Sweep's admitted-envelope bound use.
    private static float SweepShapeReachRadius(SdfInstruction instruction, int instructionIndex, SdfSweepCurve[] sweepCurves) =>
        (TryFindSweepCurve(
            curve: out var curve,
            instructionIndex: instructionIndex,
            sweepCurves: sweepCurves
        )
            ? SdfSolidGeometry.SweepReach(
                a: curve.A,
                b: curve.B,
                bulge: curve.Bulge,
                c: curve.C,
                radiusEnd: curve.RadiusEnd,
                radiusStart: curve.RadiusStart,
                strandOffset: instruction.Data0.W
            )
            : 0.0f
        );
    // Writes every Sweep curve's control points and per-strand radius endpoints into the side table appended after
    // the convex-polygon table — fixed 3-uvec4 stride per curve (A.xyz+radiusStart, B.xyz+radiusEnd, C.xyz+bulge), so
    // (unlike ConvexPolygon's variable vertex count) no per-entry count needs packing alongside the table offset.
    private void PackSweepCurves(int[] curveOffsets) {
        for (var curveIndex = 0; (curveIndex < m_sweepCurves.Length); curveIndex++) {
            var curve = m_sweepCurves[curveIndex];
            var entryBase = (curveOffsets[curveIndex] * WordsPerVector);

            WriteVector4(
                words: m_words,
                baseIndex: entryBase,
                w: curve.RadiusStart,
                x: curve.A.X,
                y: curve.A.Y,
                z: curve.A.Z
            );
            WriteVector4(
                words: m_words,
                baseIndex: (entryBase + WordsPerVector),
                w: curve.RadiusEnd,
                x: curve.B.X,
                y: curve.B.Y,
                z: curve.B.Z
            );
            WriteVector4(
                words: m_words,
                baseIndex: (entryBase + (2 * WordsPerVector)),
                w: curve.Bulge,
                x: curve.C.X,
                y: curve.C.Y,
                z: curve.C.Z
            );
        }
    }
    // A linear scan is fine: a program's Sweep count is small, and this runs at Build() time (or once at
    // SdfFieldEvaluator construction), never per query.
    private static bool TryFindSweepCurve(SdfSweepCurve[] sweepCurves, int instructionIndex, out SdfSweepCurve curve) {
        for (var index = 0; (index < sweepCurves.Length); index++) {
            if (sweepCurves[index].InstructionIndex == instructionIndex) {
                curve = sweepCurves[index];

                return true;
            }
        }

        curve = default;

        return false;
    }
}
