using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The bake-time lighting check that admits a bake group's floor as the mesh the irradiance volume traces in place of
/// the tier its view draws (<see cref="SdfMeshDraw.Trace"/>): the floor stands for the group's geometry only where it
/// has lost no part, closed no hole and joined no two surfaces at the scale the finest probes resolve.
/// <para>The invariant it measures: every witness of each mesh lies within <see cref="Tolerance"/> of a triangle of the
/// other. A triangle's witnesses are its centroid where no edge is longer than the tolerance, and otherwise a grid of
/// the tolerance over it, since a floor's triangles and a planar-merged face's span many cells. A part the floor lost
/// leaves the finer mesh's witnesses far from it; a hole it closed or two surfaces it joined leaves its own bridge's
/// witnesses far from the finer mesh. The guarantee stops at the witness spacing and at what the finer mesh itself
/// resolves: the check compares two baked tiers, never the field.</para>
/// </summary>
internal static class SdfMeshTraceJudge {
    /// <summary>The distance, in metres, the check holds the two meshes to: a quarter of the finest probe spacing any
    /// indirect tier lays out (<see cref="SdfMeshIrradianceProbe.FinestSpacing"/>), the scale below which a probe's
    /// surface map cannot tell surfaces apart. A constant, so the bake, which runs the check, reads nothing of the
    /// volume's own code.</summary>
    public const float Tolerance = (0.25f * SdfMeshIrradianceProbe.FinestSpacing);
    /// <summary>The most witnesses one check measures over both directions; a pair past it is declined, since a check it
    /// cannot finish admits nothing.</summary>
    public const long MostWitnesses = 4_000_000L;

    /// <summary>Returns whether a floor stands for a finer mesh of the same group within <see cref="Tolerance"/> both
    /// ways. Reads both meshes' positions and level-zero indices, piece by piece; the caller holds a claim on each.</summary>
    /// <param name="floor">The group's mesh at its coarsest tier.</param>
    /// <param name="finer">A mesh of the same geometry at a finer tier, in the same frame.</param>
    /// <returns>Whether the floor is admitted; <see langword="false"/> also for a pair past <see cref="MostWitnesses"/>.</returns>
    public static bool Admits(SdfMesh floor, SdfMesh finer) {
        var (floorPositions, floorIndices) = Joined(mesh: floor);
        var (finerPositions, finerIndices) = Joined(mesh: finer);

        if ((floorIndices.Length == 0) != (finerIndices.Length == 0)) { return false; }
        if (floorIndices.Length == 0) { return true; }
        if ((Witnesses(indices: floorIndices, positions: floorPositions) + Witnesses(indices: finerIndices, positions: finerPositions)) > MostWitnesses) { return false; }

        return Within(from: (floorPositions, floorIndices), to: (finerPositions, finerIndices)) && Within(from: (finerPositions, finerIndices), to: (floorPositions, floorIndices));
    }
    // Whether every witness of one mesh lies within the tolerance of a triangle of another.
    private static bool Within((Vector3[] Positions, uint[] Indices) from, (Vector3[] Positions, uint[] Indices) to) {
        var grid = new SdfMeshTriangleGrid(cellSize: Tolerance, indices: to.Indices, positions: to.Positions);

        for (var triangle = 0; (triangle < (from.Indices.Length / 3)); triangle++) {
            var (a, b, c) = SdfMeshTriangle.Corners(indices: from.Indices, positions: from.Positions, triangle: triangle);
            var steps = StepsOf(a: a, b: b, c: c);

            // A triangle no wider than the tolerance is witnessed by its centroid, which every point of it lies within
            // the tolerance of; a wider one by a grid of the tolerance over it, corners included.
            if (steps == 0) {
                if (grid.Nearest(indices: to.Indices, point: ((a + b + c) / 3f), positions: to.Positions, reach: Tolerance) >= Tolerance) { return false; }
                continue;
            }
            for (var i = 0; (i <= steps); i++) {
                for (var j = 0; (j <= (steps - i)); j++) {
                    var witness = ((a + ((b - a) * (i / (float)steps))) + ((c - a) * (j / (float)steps)));

                    if (grid.Nearest(indices: to.Indices, point: witness, positions: to.Positions, reach: Tolerance) >= Tolerance) { return false; }
                }
            }
        }
        return true;
    }
    // The grid steps a triangle's witnesses take along its edges so that no two neighbours lie farther than the tolerance
    // apart: zero for a triangle no edge of which is longer than it.
    private static int StepsOf(Vector3 a, Vector3 b, Vector3 c) {
        var longest = MathF.Max(x: Vector3.Distance(value1: a, value2: b), y: MathF.Max(x: Vector3.Distance(value1: b, value2: c), y: Vector3.Distance(value1: c, value2: a)));

        return ((longest <= Tolerance) ? 0 : (int)MathF.Ceiling(x: (longest / Tolerance)));
    }
    // The witnesses a mesh's triangles take.
    private static long Witnesses(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) {
        var total = 0L;

        for (var triangle = 0; (triangle < (indices.Length / 3)); triangle++) {
            var (a, b, c) = SdfMeshTriangle.Corners(indices: indices, positions: positions, triangle: triangle);
            var steps = (long)StepsOf(a: a, b: b, c: c);

            total += (((steps + 1) * (steps + 2)) / 2);
        }
        return total;
    }
    // A mesh's pieces as one triangle list: a published chunked mesh keeps no joined indices.
    private static (Vector3[] Positions, uint[] Indices) Joined(SdfMesh mesh) {
        if (mesh.PieceCount == 1) { return (mesh.Positions.ToArray(), mesh.Indices.ToArray()); }

        var vertices = 0;
        var corners = 0;

        for (var piece = 0; (piece < mesh.PieceCount); piece++) {
            vertices += mesh.Piece(index: piece).Positions.Length;
            corners += mesh.Piece(index: piece).Indices.Length;
        }

        var positions = new Vector3[vertices];
        var indices = new uint[corners];
        var vertexAt = 0;
        var cornerAt = 0;

        for (var piece = 0; (piece < mesh.PieceCount); piece++) {
            var part = mesh.Piece(index: piece);

            part.Positions.Span.CopyTo(destination: positions.AsSpan(start: vertexAt));
            foreach (var index in part.Indices.Span) { indices[cornerAt++] = (index + (uint)vertexAt); }
            vertexAt += part.Positions.Length;
        }
        return (positions, indices);
    }}
