using System.Numerics;

using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

// Shared nearest-point primitive for the offline bake audit, the retained live scene and the trace judge.
internal static class SdfMeshTriangle {
    // A triangle's three corners.
    public static (Vector3 A, Vector3 B, Vector3 C) Corners(ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positions, int triangle) =>
        (positions[((int)indices[(3 * triangle)])], positions[((int)indices[((3 * triangle) + 1)])], positions[((int)indices[((3 * triangle) + 2)])]);
    // The point of a triangle nearest p, by the triangle's Voronoi regions.
    public static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        var ab = (b - a);
        var ac = (c - a);
        var ap = (p - a);
        var d1 = Vector3.Dot(vector1: ab, vector2: ap);
        var d2 = Vector3.Dot(vector1: ac, vector2: ap);

        if ((d1 <= 0f) && (d2 <= 0f)) { return a; }

        var bp = (p - b);
        var d3 = Vector3.Dot(vector1: ab, vector2: bp);
        var d4 = Vector3.Dot(vector1: ac, vector2: bp);

        if ((d3 >= 0f) && (d4 <= d3)) { return b; }

        var vc = ((d1 * d4) - (d3 * d2));

        if ((vc <= 0f) && (d1 >= 0f) && (d3 <= 0f)) { return (a + (ab * (d1 / (d1 - d3)))); }

        var cp = (p - c);
        var d5 = Vector3.Dot(vector1: ab, vector2: cp);
        var d6 = Vector3.Dot(vector1: ac, vector2: cp);

        if ((d6 >= 0f) && (d5 <= d6)) { return c; }

        var vb = ((d5 * d2) - (d1 * d6));

        if ((vb <= 0f) && (d2 >= 0f) && (d6 <= 0f)) { return (a + (ac * (d2 / (d2 - d6)))); }

        var va = ((d3 * d6) - (d5 * d4));

        if ((va <= 0f) && ((d4 - d3) >= 0f) && ((d5 - d6) >= 0f)) { return (b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))); }

        var denominator = (1f / ((va + vb) + vc));

        return ((a + (ab * (vb * denominator))) + (ac * (vc * denominator)));
    }
}
// A mesh's triangles bucketed by the cells of a uniform grid their boxes overlap, which answers the distance from a point
// to the nearest triangle within a reach. It keeps triangle numbers only: each query is handed the positions and indices
// it was built from. The buckets are one flat run of triangle numbers with each occupied cell's start, found through one
// open-addressed map from the cell's index in the mesh's cell box, all rented storage the grid returns when disposed: the
// trace judge builds two for every floor it checks, and a list per cell made most of a bake's small-object garbage.
// Within a cell its triangles stand in ascending order, so a query visits what a list per cell gave it.
internal sealed class SdfMeshTriangleGrid : IDisposable {
    private readonly float m_cellSize;
    private readonly (int X, int Y, int Z) m_low;
    private readonly (int X, int Y, int Z) m_high;
    private readonly long m_spanX;
    private readonly long m_spanY;
    private readonly OwnedLongMap<int> m_cells;
    // Each occupied cell's first triangle in m_triangles, by the slot m_cells maps the cell to, then the run's end.
    private int[] m_starts;
    private int[] m_triangles;

    public SdfMeshTriangleGrid(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float cellSize) {
        m_cellSize = cellSize;

        var triangleCount = (indices.Length / 3);
        var low = Vector3.Zero;
        var high = Vector3.Zero;

        for (var triangle = 0; (triangle < triangleCount); triangle++) {
            var (a, b, c) = SdfMeshTriangle.Corners(indices: indices, positions: positions, triangle: triangle);
            var cornerLow = Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c));
            var cornerHigh = Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c));

            low = ((triangle == 0) ? cornerLow : Vector3.Min(value1: low, value2: cornerLow));
            high = ((triangle == 0) ? cornerHigh : Vector3.Max(value1: high, value2: cornerHigh));
        }
        m_low = Cell(point: low);
        m_high = Cell(point: high);
        m_spanX = (((long)m_high.X - m_low.X) + 1L);
        m_spanY = (((long)m_high.Y - m_low.Y) + 1L);
        m_cells = new OwnedLongMap<int>(allocator: null, capacity: triangleCount);

        // The first pass numbers each occupied cell in the order a triangle first reaches it and counts its triangles;
        // the counts become the runs' starts, and the second pass lays each triangle into its cells' runs.
        var counts = RentedArrays.Rent<int>(minimumLength: Math.Max(val1: 16, val2: triangleCount));
        var occupied = 0;
        var total = 0;

        try {
            for (var triangle = 0; (triangle < triangleCount); triangle++) {
                var (cellLow, cellHigh) = CellsOf(indices: indices, positions: positions, triangle: triangle);

                for (var z = cellLow.Z; (z <= cellHigh.Z); z++) {
                    for (var y = cellLow.Y; (y <= cellHigh.Y); y++) {
                        for (var x = cellLow.X; (x <= cellHigh.X); x++) {
                            var key = Key(x: x, y: y, z: z);

                            if (!m_cells.TryGetValue(key: key, value: out var slot)) {
                                slot = occupied++;
                                m_cells.Add(key: key, value: slot);
                                if (slot >= counts.Length) { counts = RentedArrays.Grow(array: counts, keep: slot, minimumLength: (2 * counts.Length)); }
                                counts[slot] = 0;
                            }
                            counts[slot]++;
                            total = checked(total + 1);
                        }
                    }
                }
            }

            m_starts = RentedArrays.Rent<int>(minimumLength: (occupied + 1));
            m_triangles = RentedArrays.Rent<int>(minimumLength: total);

            var at = 0;

            for (var slot = 0; (slot < occupied); slot++) {
                m_starts[slot] = at;
                at += counts[slot];
                counts[slot] = m_starts[slot];
            }
            m_starts[occupied] = at;
            for (var triangle = 0; (triangle < triangleCount); triangle++) {
                var (cellLow, cellHigh) = CellsOf(indices: indices, positions: positions, triangle: triangle);

                for (var z = cellLow.Z; (z <= cellHigh.Z); z++) {
                    for (var y = cellLow.Y; (y <= cellHigh.Y); y++) {
                        for (var x = cellLow.X; (x <= cellHigh.X); x++) {
                            _ = m_cells.TryGetValue(key: Key(x: x, y: y, z: z), value: out var slot);
                            m_triangles[counts[slot]++] = triangle;
                        }
                    }
                }
            }
        } finally {
            RentedArrays.Return(array: counts);
        }
    }

    // The distance to the nearest triangle within the reach, or the reach when none lies within it.
    public float Nearest(Vector3 point, float reach, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) {
        var low = Cell(point: (point - new Vector3(value: reach)));
        var high = Cell(point: (point + new Vector3(value: reach)));
        var nearest = reach;

        // No triangle reaches a cell outside the mesh's own cell box.
        for (var z = Math.Max(val1: low.Z, val2: m_low.Z); (z <= Math.Min(val1: high.Z, val2: m_high.Z)); z++) {
            for (var y = Math.Max(val1: low.Y, val2: m_low.Y); (y <= Math.Min(val1: high.Y, val2: m_high.Y)); y++) {
                for (var x = Math.Max(val1: low.X, val2: m_low.X); (x <= Math.Min(val1: high.X, val2: m_high.X)); x++) {
                    if (!m_cells.TryGetValue(key: Key(x: x, y: y, z: z), value: out var slot)) { continue; }
                    for (var at = m_starts[slot]; (at < m_starts[(slot + 1)]); at++) {
                        var (a, b, c) = SdfMeshTriangle.Corners(indices: indices, positions: positions, triangle: m_triangles[at]);

                        nearest = MathF.Min(x: nearest, y: Vector3.Distance(value1: point, value2: SdfMeshTriangle.Closest(a: a, b: b, c: c, p: point)));
                    }
                }
            }
        }

        return nearest;
    }

    // Returns the grid's storage to the pools it was rented from.
    public void Dispose() {
        m_cells.Dispose();
        RentedArrays.Return(array: m_starts);
        RentedArrays.Return(array: m_triangles);
        m_starts = [];
        m_triangles = [];
    }

    private ((int X, int Y, int Z) Low, (int X, int Y, int Z) High) CellsOf(ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positions, int triangle) {
        var (a, b, c) = SdfMeshTriangle.Corners(indices: indices, positions: positions, triangle: triangle);

        return (Cell(point: Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c))), Cell(point: Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c))));
    }
    // A cell's index in the mesh's cell box, which every triangle's cells lie inside.
    private long Key(int x, int y, int z) => ((x - (long)m_low.X) + (m_spanX * ((y - (long)m_low.Y) + (m_spanY * (z - (long)m_low.Z)))));
    private (int X, int Y, int Z) Cell(Vector3 point) => SdfMeshLattice.CellOf(cell: new Vector3(value: m_cellSize), point: point);
}
