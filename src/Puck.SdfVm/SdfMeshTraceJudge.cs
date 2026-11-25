using System.Numerics;

using Puck.Abstractions.Memory;

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

    // The identity of the judge's own code in the running build: every member Admits reaches, walked as the bake's
    // closure is. A verdict is kept under it, so a verdict an earlier judge gave is never read by a later one.
    private static readonly Lazy<UInt128> Code = new(valueFactory: static () => SdfMeshBakeClosure.Of(
        directory: AppContext.BaseDirectory,
        roots: [("Puck.SdfVm", typeof(SdfMeshTraceJudge).FullName!, nameof(Admits))]).Hash);

    /// <summary>Gets the identity of the judge's own code in the running build. A verdict is a function of the two
    /// meshes and the judge alone, so the mesh store keeps it beside the meshes, under their bake's identity, keyed by
    /// this and the two geometries (<see cref="SdfMeshLibrary.TraceVerdict"/>). The first read walks the running
    /// build's modules, so it is made off the frame's thread.</summary>
    public static UInt128 CodeIdentity => Code.Value;

    /// <summary>Returns whether a floor stands for a finer mesh of the same group within <see cref="Tolerance"/> both
    /// ways. Reads both meshes' positions and level-zero indices, piece by piece; the caller holds a claim on each.</summary>
    /// <param name="floor">The group's mesh at its coarsest tier.</param>
    /// <param name="finer">A mesh of the same geometry at a finer tier, in the same frame.</param>
    /// <returns>Whether the floor is admitted; <see langword="false"/> also for a pair past <see cref="MostWitnesses"/>.</returns>
    public static bool Admits(SdfMesh floor, SdfMesh finer) {
        // The holds keep both meshes' planes, which their pieces share, resident for the whole check.
        using var floorHeld = floor.Hold();
        using var finerHeld = finer.Hold();

        if (!floorHeld.Readable || !finerHeld.Readable) { return false; }

        using var floorJoined = new Joined(held: in floorHeld);
        using var finerJoined = new Joined(held: in finerHeld);
        var floorMesh = (floorJoined.Positions, floorJoined.Indices);
        var finerMesh = (finerJoined.Positions, finerJoined.Indices);

        if ((floorMesh.Indices.Length == 0) != (finerMesh.Indices.Length == 0)) { return false; }
        if (floorMesh.Indices.Length == 0) { return true; }
        if ((Witnesses(indices: floorMesh.Indices.Span, positions: floorMesh.Positions.Span) + Witnesses(indices: finerMesh.Indices.Span, positions: finerMesh.Positions.Span)) > MostWitnesses) { return false; }

        return Within(from: floorMesh, to: finerMesh) && Within(from: finerMesh, to: floorMesh);
    }
    // Whether every witness of one mesh lies within the tolerance of a triangle of another.
    private static bool Within((ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices) from, (ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices) to) {
        var toPositions = to.Positions.Span;
        var toIndices = to.Indices.Span;
        var fromPositions = from.Positions.Span;
        var fromIndices = from.Indices.Span;
        using var grid = new SdfMeshTriangleGrid(cellSize: Tolerance, indices: toIndices, positions: toPositions);

        for (var triangle = 0; (triangle < (fromIndices.Length / 3)); triangle++) {
            var (a, b, c) = SdfMeshTriangle.Corners(indices: fromIndices, positions: fromPositions, triangle: triangle);
            var steps = StepsOf(a: a, b: b, c: c);

            // A triangle no wider than the tolerance is witnessed by its centroid, which every point of it lies within
            // the tolerance of; a wider one by a grid of the tolerance over it, corners included.
            if (steps == 0) {
                if (grid.Nearest(indices: toIndices, point: ((a + b + c) / 3f), positions: toPositions, reach: Tolerance) >= Tolerance) { return false; }
                continue;
            }
            for (var i = 0; (i <= steps); i++) {
                for (var j = 0; (j <= (steps - i)); j++) {
                    var witness = ((a + ((b - a) * (i / (float)steps))) + ((c - a) * (j / (float)steps)));

                    if (grid.Nearest(indices: toIndices, point: witness, positions: toPositions, reach: Tolerance) >= Tolerance) { return false; }
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
    // A held mesh's pieces as one triangle list: a published chunked mesh keeps no joined indices. A one-piece mesh is
    // read where it lies, for as long as its hold stands; a chunked one is joined into rented arrays the check returns
    // when it ends, since a pair of tiers runs to tens of megabytes and a bake checks one pair per admitted floor.
    private readonly struct Joined : IDisposable {
        private readonly Vector3[]? m_positions;
        private readonly uint[]? m_indices;

        public ReadOnlyMemory<Vector3> Positions { get; }
        public ReadOnlyMemory<uint> Indices { get; }

        public Joined(in SdfMesh.Held held) {
            var mesh = held.Mesh;

            if (mesh.PieceCount == 1) {
                Positions = held.Positions;
                Indices = held.Indices;
                return;
            }

            var vertices = 0;
            var corners = 0;

            for (var piece = 0; (piece < mesh.PieceCount); piece++) {
                vertices += mesh.Piece(index: piece).VertexCount;
                corners += mesh.Piece(index: piece).IndexCount;
            }

            m_positions = RentedArrays.Rent<Vector3>(minimumLength: vertices);
            m_indices = RentedArrays.Rent<uint>(minimumLength: corners);

            var vertexAt = 0;
            var cornerAt = 0;

            for (var piece = 0; (piece < mesh.PieceCount); piece++) {
                using var part = mesh.Piece(index: piece).Hold();
                var positions = part.Positions.Span;

                positions.CopyTo(destination: m_positions.AsSpan(start: vertexAt));
                foreach (var index in part.Indices.Span) { m_indices[cornerAt++] = (index + (uint)vertexAt); }
                vertexAt += positions.Length;
            }
            Positions = m_positions.AsMemory(start: 0, length: vertices);
            Indices = m_indices.AsMemory(start: 0, length: corners);
        }

        public void Dispose() {
            if (m_positions is not null) { RentedArrays.Return(array: m_positions); }
            if (m_indices is not null) { RentedArrays.Return(array: m_indices); }
        }
    }
}
