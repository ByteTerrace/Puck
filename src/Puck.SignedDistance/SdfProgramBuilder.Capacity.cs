namespace Puck.SignedDistance;

/// <summary>The buffer capacity a program needs, measured from a builder's emission without packing it
/// (<see cref="SdfProgramBuilder.MeasureCapacity"/>).</summary>
/// <param name="Words">The packed words to reserve: at least the <see cref="SdfProgram.PartCompilationWordCapacity"/>
/// of the program <see cref="SdfProgramBuilder.Build"/> would return from the same emission.</param>
/// <param name="Instances">The instances that program declares.</param>
public readonly record struct SdfProgramCapacity(int Words, int Instances);

public sealed partial class SdfProgramBuilder {
    /// <summary>Measures the capacity the program this builder holds would need, without building it. The segment count
    /// and every table the emission states (materials, instances, polygon profiles, sweep curves, paths, reserved path
    /// words) are exact; the counts only analysis decides are bounded above: world segments by the segments, and rigid
    /// leaves by two per shape (a leaf and its fold extension). The words are therefore never below the built
    /// program's <see cref="SdfProgram.PartCompilationWordCapacity"/>, through the one layout
    /// (<c>SdfProgram.Layout</c>) the program packs by. A capacity probe measures with it, so it allocates no packed
    /// stream and runs none of the build's analysis.</summary>
    /// <returns>The capacity.</returns>
    public SdfProgramCapacity MeasureCapacity() {
        var instructions = m_instructions.Count;
        // SegmentRanges' rule: a segment starts at the first instruction, at each ResetPoint, and at every instance's
        // first and end instruction.
        var boundaries = new bool[(instructions + 1)];

        foreach (var instance in m_instances) {
            boundaries[instance.First] = true;
            boundaries[instance.End] = true;
        }

        var segments = 0;
        var shapes = 0;

        for (var index = 0; (index < instructions); index++) {
            var op = m_instructions[index].Op;

            if ((index == 0) || (op == SdfOp.ResetPoint) || boundaries[index]) {
                segments++;
            }
            if (op == SdfOp.ShapeBlend) {
                shapes++;
            }
        }

        var convexPolygonVectors = 0;
        var pathEdges = 0;

        foreach (var (_, vertices) in m_convexPolygonProfiles) {
            convexPolygonVectors += ((vertices.Length + 1) / 2);
        }

        foreach (var path in m_paths) {
            pathEdges = checked((pathEdges + path.Edges.Count));
        }

        var layout = new SdfProgram.Layout(
            ConvexPolygonVectors: convexPolygonVectors,
            Instances: m_instances.Count,
            Instructions: instructions,
            Materials: m_materials.Count,
            Paints: m_materials.Sum(selector: material => (material.Paints?.Count ?? 0)),
            PathEdges: pathEdges,
            RigidLeaves: checked((2 * shapes)),
            Segments: segments,
            SweepCurves: m_sweepCurves.Count,
            WorldSegments: segments
        );

        return new SdfProgramCapacity(
            Instances: m_instances.Count,
            Words: layout.PartCompilationWordCapacity(reservedPathWords: m_reservedPathWords)
        );
    }
}
