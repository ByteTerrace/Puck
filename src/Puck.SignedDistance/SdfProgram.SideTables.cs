using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>Returns the values of the side-table entry an instruction names: a <see cref="SdfShapeType.ConvexPolygon"/>'s
    /// vertices (x, y each), a <see cref="SdfShapeType.Sweep"/>'s curve (A, B, C, then the start and end radius and the
    /// bulge) or a <see cref="SdfShapeType.Path"/>'s edges (A, B, then the radius at each). Such an instruction's
    /// <see cref="SdfInstruction.Data0"/> x lane holds where the program packed the entry, which differs between programs
    /// that carry the same shape; the entry's values do not.</summary>
    /// <param name="instructionIndex">The instruction's index in <see cref="Instructions"/>.</param>
    /// <returns>The entry's values, or empty for an instruction that names no side-table entry.</returns>
    public ReadOnlySpan<float> SideTableValues(int instructionIndex) {
        foreach (var (index, vertices) in m_convexPolygonProfiles) {
            if (index == instructionIndex) { return MemoryMarshal.Cast<System.Numerics.Vector2, float>(span: vertices); }
        }
        foreach (var curve in m_sweepCurves) {
            if (curve.InstructionIndex == instructionIndex) {
                return new float[] { curve.A.X, curve.A.Y, curve.A.Z, curve.B.X, curve.B.Y, curve.B.Z, curve.C.X, curve.C.Y, curve.C.Z, curve.RadiusStart, curve.RadiusEnd, curve.Bulge };
            }
        }
        foreach (var path in m_paths) {
            if (path.InstructionIndex == instructionIndex) { return MemoryMarshal.Cast<SdfPathEdge, float>(span: (SdfPathEdge[])path.Edges); }
        }

        return [];
    }
}
