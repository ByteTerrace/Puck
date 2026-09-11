using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // A Bezier point is in its control polygon's convex hull. Enclose that hull, then add the largest positive
    // profile radius, strand orbit and the margin SUBTRACTED by sdfSweep. Geometric containment alone is too small
    // to lower-bound that field. This argument does not depend on the approximate closest-t solver's choice.
    private static bool TryGetSweepBound(SdfInstruction instruction, int instructionIndex, SdfSweepCurve[] sweepCurves, out Vector3 center, out float radius) {
        center = Vector3.Zero;
        radius = 0f;

        if (!TryFindSweepCurve(sweepCurves: sweepCurves, instructionIndex: instructionIndex, curve: out var curve)) {
            return false;
        }

        var margin = SdfProgramBuilder.SweepConservativeMargin(
            bulge: curve.Bulge,
            strandOffset: instruction.Data0.W,
            twist: instruction.Data0.Z,
            radiusStart: curve.RadiusStart,
            radiusEnd: curve.RadiusEnd
        );

        // sdfSweep seeds its strand minimum with SDF_FAR_DISTANCE = 1e9 (float ULP 64). At margin <= 16,
        // subtracting the margin from that cap still rounds to 1e9, with slack for shader arithmetic. Larger margins
        // retain full evaluation. The scalar AND dual bound tests also require runningMin <= SDF_FAR_DISTANCE:
        // this sphere bounds the uncapped candidate, while the capped candidate cannot win under that condition.
        if (!(margin <= 16f)) {
            return false;
        }

        var low = Vector3.Min(curve.A, Vector3.Min(curve.B, curve.C));
        var high = Vector3.Max(curve.A, Vector3.Max(curve.B, curve.C));

        center = ((low * 0.5f) + (high * 0.5f));
        var hullRadius = MathF.Max(Vector3.Distance(center, curve.A), MathF.Max(Vector3.Distance(center, curve.B), Vector3.Distance(center, curve.C)));
        // A tight hull can sit far from the local origin. Cover interpolation/center rounding in those coordinates,
        // in addition to PackBounds' radius-relative padding; padding only the small hull misses that error scale.
        var coordinateSlack = (0.000001f * MathF.Max(curve.A.Length(), MathF.Max(curve.B.Length(), curve.C.Length())));

        radius = (hullRadius + MathF.Max(curve.RadiusStart, curve.RadiusEnd) + MathF.Max(curve.Bulge, 0f) + instruction.Data0.W + margin + coordinateSlack);

        return float.IsFinite(radius);
    }
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
