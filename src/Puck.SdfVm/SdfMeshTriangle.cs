using System.Numerics;

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
// it was built from.
internal sealed class SdfMeshTriangleGrid {
    private readonly float m_cellSize;
    private readonly Dictionary<(int, int, int), List<int>> m_cells = [];

    public SdfMeshTriangleGrid(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float cellSize) {
        m_cellSize = cellSize;
        for (var triangle = 0; (triangle < (indices.Length / 3)); triangle++) {
            var (a, b, c) = SdfMeshTriangle.Corners(indices: indices, positions: positions, triangle: triangle);
            var low = Cell(point: Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c)));
            var high = Cell(point: Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c)));

            for (var z = low.Z; (z <= high.Z); z++) {
                for (var y = low.Y; (y <= high.Y); y++) {
                    for (var x = low.X; (x <= high.X); x++) {
                        if (!m_cells.TryGetValue(key: (x, y, z), value: out var held)) {
                            held = [];
                            m_cells.Add(key: (x, y, z), value: held);
                        }
                        held.Add(item: triangle);
                    }
                }
            }
        }
    }

    // The distance to the nearest triangle within the reach, or the reach when none lies within it.
    public float Nearest(Vector3 point, float reach, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) {
        var low = Cell(point: (point - new Vector3(value: reach)));
        var high = Cell(point: (point + new Vector3(value: reach)));
        var nearest = reach;

        for (var z = low.Z; (z <= high.Z); z++) {
            for (var y = low.Y; (y <= high.Y); y++) {
                for (var x = low.X; (x <= high.X); x++) {
                    if (!m_cells.TryGetValue(key: (x, y, z), value: out var held)) { continue; }
                    foreach (var triangle in held) {
                        var (a, b, c) = SdfMeshTriangle.Corners(indices: indices, positions: positions, triangle: triangle);

                        nearest = MathF.Min(x: nearest, y: Vector3.Distance(value1: point, value2: SdfMeshTriangle.Closest(a: a, b: b, c: c, p: point)));
                    }
                }
            }
        }

        return nearest;
    }

    private (int X, int Y, int Z) Cell(Vector3 point) => SdfMeshLattice.CellOf(cell: new Vector3(value: m_cellSize), point: point);
}
