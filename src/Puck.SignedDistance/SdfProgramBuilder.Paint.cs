using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    // Reads back the one primitive this builder holds as a paint's shape: its packed instruction, the side table it
    // names, and the scale transforms it rode. SdfPaintShape.Of emits a document primitive into a fresh builder through
    // SdfSolidGeometry.AppendScaledPrimitive and reads it here, so a paint packs every shape exactly as the field does.
    internal SdfPaintShape CapturePaintShape(float reach) {
        var shape = m_instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.ShapeBlend));
        var scale = m_instructions
            .Where(predicate: static instruction => (instruction.Op == SdfOp.Scale))
            .Aggregate(seed: Vector3.One, func: static (product, instruction) => (product * new Vector3(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z)));
        var table = new List<float>();

        foreach (var (_, vertices) in m_convexPolygonProfiles) {
            foreach (var vertex in vertices) {
                table.Add(item: vertex.X);
                table.Add(item: vertex.Y);
            }
            // The table offset is the paint's own, written when the program packs it; the count rides beside it.
            shape = shape with { Data0 = shape.Data0 with { X = BitConverter.UInt32BitsToSingle(value: (uint)vertices.Length) } };
        }
        foreach (var curve in m_sweepCurves) {
            table.AddRange(collection: [curve.A.X, curve.A.Y, curve.A.Z, curve.RadiusStart, curve.B.X, curve.B.Y, curve.B.Z, curve.RadiusEnd, curve.C.X, curve.C.Y, curve.C.Z, curve.Bulge]);
        }

        return new SdfPaintShape(
            Data0: shape.Data0,
            Data1: shape.Data1,
            Reach: reach,
            Scale: scale,
            Table: table.AsReadOnly(),
            Type: (SdfShapeType)shape.Shape
        );
    }
}
