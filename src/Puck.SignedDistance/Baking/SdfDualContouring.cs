using Puck.Maths;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// A surface extracted from an <see cref="SdfBakeGrid"/>: one vertex per cell the surface crosses, and one quad per
/// sign-changing lattice edge joining the vertices of the four cells around it. Positions and normals are world-space
/// doubles; quads list four vertex indices, wound counter-clockwise seen from outside.
/// </summary>
internal sealed class SdfSurface {
    public required double CellSize { get; init; }
    public required double[] Normals { get; init; }
    public required double[] Positions { get; init; }
    public int QuadCount => (Quads.Length / 4);
    public required int[] Quads { get; init; }
    public int VertexCount => (Positions.Length / 3);
}
/// <summary>
/// Extracts a <see cref="SdfSurface"/> by dual contouring. Each sign-changing edge of a crossed cell contributes the
/// point where the linear interpolation of its corner values crosses zero and the field's gradient there; the cell's
/// vertex is the point nearest, in the least-squares sense, to every such tangent plane, drawn weakly toward the
/// crossings' mean so a flat or single-edge cell stays determined, and clamped into the cell. The vertex's normal is
/// the field's gradient at the vertex. Placing a vertex costs six evaluations per crossing, which keeps sharp edges
/// and thin features that a mean of the crossings rounds off.
/// </summary>
internal static class SdfDualContouring {
    // The twelve edges of a cell as corner-offset pairs, each offset packed as x | y << 1 | z << 2.
    private static readonly (int A, int B)[] CellEdges = [
        (0, 1), (2, 3), (4, 5), (6, 7),
        (0, 2), (1, 3), (4, 6), (5, 7),
        (0, 4), (1, 5), (2, 6), (3, 7),
    ];

    public static SdfSurface Extract(SdfBakeField field, SdfBakeGrid grid) {
        var cells = grid.Cells;
        var vertexOfCell = new int[((cells * cells) * cells)];
        var positions = new List<double>();
        var normals = new List<double>();
        var epsilon = FixedQ4816.FromRawBits(value: Math.Max(
            val1: 1L,
            val2: (grid.CellSizeFixed.Value / 4L)
        ));

        Array.Fill(array: vertexOfCell, value: -1);

        for (var z = 0; (z < cells); z++) {
            for (var y = 0; (y < cells); y++) {
                for (var x = 0; (x < cells); x++) {
                    var mask = 0;

                    for (var offset = 0; (offset < 8); offset++) {
                        if (grid.Inside(corner: grid.Corner(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1))))) {
                            mask |= (1 << offset);
                        }
                    }

                    if (
                        (mask == 0) ||
                        (mask == 0xFF)
                    ) {
                        continue;
                    }

                    var (px, py, pz) = Place(epsilon: epsilon, field: field, grid: grid, mask: mask, x: x, y: y, z: z);
                    var (wx, wy, wz) = grid.World(x: (x + px), y: (y + py), z: (z + pz));
                    var point = new FixedVector3(
                        X: FixedQ4816.FromDouble(value: wx),
                        Y: FixedQ4816.FromDouble(value: wy),
                        Z: FixedQ4816.FromDouble(value: wz)
                    );

                    if (!field.TryNormal(epsilon: epsilon, normal: out var normal, point: point)) {
                        normal = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
                    }

                    vertexOfCell[(x + (cells * (y + (cells * z))))] = (positions.Count / 3);
                    positions.Add(item: wx);
                    positions.Add(item: wy);
                    positions.Add(item: wz);
                    normals.Add(item: ((double)normal.X));
                    normals.Add(item: ((double)normal.Y));
                    normals.Add(item: ((double)normal.Z));
                }
            }
        }

        return new SdfSurface {
            CellSize = grid.CellSize,
            Normals = [.. normals],
            Positions = [.. positions],
            Quads = Connect(grid: grid, vertexOfCell: vertexOfCell),
        };
    }

    private static (int X, int Y, int Z) Offset(int corner) =>
        (corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
    // The crossing on a sign-changing edge, as a fraction of the edge from its first corner.
    private static double Crossing(SdfBakeGrid grid, int x, int y, int z, int a, int b) {
        var (ax, ay, az) = Offset(corner: a);
        var (bx, by, bz) = Offset(corner: b);
        var va = ((double)grid.Value(corner: grid.Corner(x: (x + ax), y: (y + ay), z: (z + az)), x: (x + ax), y: (y + ay), z: (z + az)));
        var vb = ((double)grid.Value(corner: grid.Corner(x: (x + bx), y: (y + by), z: (z + bz)), x: (x + bx), y: (y + by), z: (z + bz)));
        var denominator = (va - vb);

        return ((denominator == 0.0)
            ? 0.5
            : Math.Clamp(max: 1.0, min: 0.0, value: (va / denominator)));
    }
    private static (double X, double Y, double Z) Mean(SdfBakeGrid grid, int mask, int x, int y, int z) {
        double sx = 0.0, sy = 0.0, sz = 0.0;
        var count = 0;

        foreach (var (a, b) in CellEdges) {
            if ((((mask >> a) ^ (mask >> b)) & 1) == 0) {
                continue;
            }

            var t = Crossing(a: a, b: b, grid: grid, x: x, y: y, z: z);

            var (ax, ay, az) = Offset(corner: a);
            var (bx, by, bz) = Offset(corner: b);

            sx += (ax + (t * (bx - ax)));
            sy += (ay + (t * (by - ay)));
            sz += (az + (t * (bz - az)));
            count++;
        }

        return ((sx / count), (sy / count), (sz / count));
    }
    // The vertex in cell-local coordinates: the least-squares point of the crossings' tangent planes, solved by Cramer's
    // rule over the 3x3 normal equations with the mean-point bias on the diagonal.
    private static (double X, double Y, double Z) Place(SdfBakeField field, SdfBakeGrid grid, int mask, int x, int y, int z, FixedQ4816 epsilon) {
        var mean = Mean(grid: grid, mask: mask, x: x, y: y, z: z);
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;

        foreach (var (a, b) in CellEdges) {
            if ((((mask >> a) ^ (mask >> b)) & 1) == 0) {
                continue;
            }

            var t = Crossing(a: a, b: b, grid: grid, x: x, y: y, z: z);

            var (ax, ay, az) = Offset(corner: a);
            var (bx, by, bz) = Offset(corner: b);
            var px = (ax + (t * (bx - ax)));
            var py = (ay + (t * (by - ay)));
            var pz = (az + (t * (bz - az)));

            var (wx, wy, wz) = grid.World(x: (x + px), y: (y + py), z: (z + pz));

            if (!field.TryNormal(
                epsilon: epsilon,
                normal: out var normal,
                point: new FixedVector3(X: FixedQ4816.FromDouble(value: wx), Y: FixedQ4816.FromDouble(value: wy), Z: FixedQ4816.FromDouble(value: wz))
            )) {
                continue;
            }

            var nx = ((double)normal.X);
            var ny = ((double)normal.Y);
            var nz = ((double)normal.Z);
            var d = (((nx * px) + (ny * py)) + (nz * pz));

            a00 += (nx * nx); a01 += (nx * ny); a02 += (nx * nz);
            a11 += (ny * ny); a12 += (ny * nz); a22 += (nz * nz);
            b0 += (nx * d); b1 += (ny * d); b2 += (nz * d);
        }

        const double Bias = 0.05;

        a00 += Bias; a11 += Bias; a22 += Bias;
        b0 += (Bias * mean.X); b1 += (Bias * mean.Y); b2 += (Bias * mean.Z);

        var det = (((a00 * ((a11 * a22) - (a12 * a12))) - (a01 * ((a01 * a22) - (a12 * a02)))) + (a02 * ((a01 * a12) - (a11 * a02))));

        if (Math.Abs(value: det) < 1e-12) {
            return mean;
        }

        var sx = ((((b0 * ((a11 * a22) - (a12 * a12))) - (a01 * ((b1 * a22) - (a12 * b2)))) + (a02 * ((b1 * a12) - (a11 * b2)))) / det);
        var sy = ((((a00 * ((b1 * a22) - (a12 * b2))) - (b0 * ((a01 * a22) - (a12 * a02)))) + (a02 * ((a01 * b2) - (b1 * a02)))) / det);
        var sz = ((((a00 * ((a11 * b2) - (b1 * a12))) - (a01 * ((a01 * b2) - (b1 * a02)))) + (b0 * ((a01 * a12) - (a11 * a02)))) / det);

        return (Math.Clamp(max: 1.0, min: 0.0, value: sx), Math.Clamp(max: 1.0, min: 0.0, value: sy), Math.Clamp(max: 1.0, min: 0.0, value: sz));
    }
    // One quad per sign-changing lattice edge whose four surrounding cells all lie in the lattice. The cells around an
    // edge along axis a are visited counter-clockwise about +a, which faces +a; the quad is reversed when the edge runs
    // from outside to inside, so every quad faces out of the surface.
    private static int[] Connect(SdfBakeGrid grid, int[] vertexOfCell) {
        var cells = grid.Cells;
        var quads = new List<int>();
        Span<int> ring = stackalloc int[4];
        Span<int> at = stackalloc int[3];
        Span<int> cell = stackalloc int[3];

        for (var axis = 0; (axis < 3); axis++) {
            var u = ((axis + 1) % 3);
            var v = ((axis + 2) % 3);

            for (var k = 0; (k <= cells); k++) {
                for (var j = 0; (j <= cells); j++) {
                    for (var i = 0; (i < cells); i++) {
                        at[axis] = i;
                        at[u] = j;
                        at[v] = k;

                        if (
                            (j < 1) || (j >= cells) ||
                            (k < 1) || (k >= cells)
                        ) {
                            continue;
                        }

                        var first = grid.Inside(corner: grid.Corner(x: at[0], y: at[1], z: at[2]));

                        at[axis] = (i + 1);

                        var second = grid.Inside(corner: grid.Corner(x: at[0], y: at[1], z: at[2]));

                        if (first == second) {
                            continue;
                        }

                        at[axis] = i;

                        var complete = true;

                        for (var corner = 0; (corner < 4); corner++) {
                            cell[axis] = i;
                            cell[u] = (j - (((corner == 0) || (corner == 3)) ? 1 : 0));
                            cell[v] = (k - ((corner < 2) ? 1 : 0));
                            ring[corner] = vertexOfCell[(cell[0] + (cells * (cell[1] + (cells * cell[2]))))];
                            complete &= (ring[corner] >= 0);
                        }

                        if (!complete) {
                            continue;
                        }

                        for (var corner = 0; (corner < 4); corner++) {
                            quads.Add(item: ring[(first ? corner : (3 - corner))]);
                        }
                    }
                }
            }
        }

        return [.. quads];
    }
}
