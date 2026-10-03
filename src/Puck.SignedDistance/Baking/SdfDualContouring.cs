using Puck.Maths;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// A surface extracted from an <see cref="SdfBakeGrid"/>: one vertex per surface patch of each cell the surface crosses,
/// and one quad per sign-changing lattice edge joining the vertices of the four cells around it. Positions and normals
/// are world-space doubles; quads list four vertex indices, wound counter-clockwise seen from outside.
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
/// Extracts a <see cref="SdfSurface"/> by manifold dual contouring: a vertex per surface patch of a cell, and one quad
/// per sign-changing lattice edge joining the patch vertices of the four cells around it. A cell's patches are the
/// connected pieces of the marching-cubes surface inside it, so a cell two sheets cross, as a plate about a cell thick
/// crosses its cells, holds one vertex for each sheet, and no edge of the mesh is shared by more than two quads. A
/// patch is found from the cell's eight corner signs alone: the sign-changing cube edges are joined by the segments
/// that marching squares draws across each cube face, and an ambiguous face, whose diagonal corners agree, is cut so
/// that it separates its inside corners. The rule reads nothing but the face's four signs, so the two cells that share a
/// face agree on it and the mesh has no crack. Each sign-changing edge of a patch contributes the point where the linear
/// interpolation of its corner values crosses zero and the field's gradient there; the patch's vertex is the point
/// nearest, in the least-squares sense, to every such tangent plane, drawn weakly toward the crossings' mean so a flat
/// or single-edge patch stays determined, and clamped into the cell. The vertex's normal is the field's gradient at the
/// vertex. Placing a vertex costs six evaluations per crossing, which keeps sharp edges and thin features that a mean
/// of the crossings rounds off.
/// </summary>
internal static class SdfDualContouring {
    // The most patches one cell holds: the four inside corners of an alternating cell are each cut off alone.
    private const int MaximumPatches = 4;

    // The twelve edges of a cell as corner-offset pairs, each offset packed as x | y << 1 | z << 2.
    private static readonly (int A, int B)[] CellEdges = [
        (0, 1), (2, 3), (4, 5), (6, 7),
        (0, 2), (1, 3), (4, 6), (5, 7),
        (0, 4), (1, 5), (2, 6), (3, 7),
    ];
    // For every corner-sign mask, the patch each cube edge belongs to (-1 for an edge that does not change sign), and how
    // many patches the mask has. Patches are numbered by their lowest edge, so the numbering is the same on every
    // machine.
    private static readonly sbyte[][] PatchOfEdge = new sbyte[256][];
    private static readonly int[] PatchCount = new int[256];

    static SdfDualContouring() {
        for (var mask = 0; (mask < 256); mask++) {
            (PatchOfEdge[mask], PatchCount[mask]) = Patches(mask: mask);
        }
    }

    public static SdfSurface Extract(SdfBakeField field, SdfBakeGrid grid) {
        var cells = grid.Cells;
        var cellCount = ((cells * cells) * cells);
        var vertexOfPatch = new int[(cellCount * MaximumPatches)];
        var maskOfCell = new byte[cellCount];
        var positions = new List<double>();
        var normals = new List<double>();
        var epsilon = FixedQ4816.FromRawBits(value: Math.Max(
            val1: 1L,
            val2: (grid.CellSizeFixed.Value / 4L)
        ));

        Array.Fill(array: vertexOfPatch, value: -1);

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

                    var cell = (x + (cells * (y + (cells * z))));
                    var patchOfEdge = PatchOfEdge[mask];

                    maskOfCell[cell] = ((byte)mask);

                    for (var patch = 0; (patch < PatchCount[mask]); patch++) {
                        var edges = 0;

                        for (var edge = 0; (edge < CellEdges.Length); edge++) {
                            if (patchOfEdge[edge] == patch) {
                                edges |= (1 << edge);
                            }
                        }

                        var (px, py, pz) = Place(edges: edges, epsilon: epsilon, field: field, grid: grid, x: x, y: y, z: z);
                        var (wx, wy, wz) = grid.World(x: (x + px), y: (y + py), z: (z + pz));
                        var point = new FixedVector3(
                            X: FixedQ4816.FromDouble(value: wx),
                            Y: FixedQ4816.FromDouble(value: wy),
                            Z: FixedQ4816.FromDouble(value: wz)
                        );

                        if (!field.TryNormal(epsilon: epsilon, normal: out var normal, point: point)) {
                            normal = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
                        }

                        vertexOfPatch[((cell * MaximumPatches) + patch)] = (positions.Count / 3);
                        positions.Add(item: wx);
                        positions.Add(item: wy);
                        positions.Add(item: wz);
                        normals.Add(item: ((double)normal.X));
                        normals.Add(item: ((double)normal.Y));
                        normals.Add(item: ((double)normal.Z));
                    }
                }
            }
        }

        return new SdfSurface {
            CellSize = grid.CellSize,
            Normals = [.. normals],
            Positions = [.. positions],
            Quads = Connect(grid: grid, maskOfCell: maskOfCell, vertexOfPatch: vertexOfPatch),
        };
    }

    private static (int X, int Y, int Z) Offset(int corner) =>
        (corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
    // Whether a cube edge lies on one face of the cube: the face whose corners all have bit `axis` equal to `side`.
    private static bool OnFace(int edge, int axis, int side) =>
        ((((CellEdges[edge].A >> axis) & 1) == side) && (((CellEdges[edge].B >> axis) & 1) == side));
    // The patches of a corner-sign mask: the sign-changing cube edges joined by the marching-squares segments of the six
    // faces, each connected piece one patch.
    private static (sbyte[] PatchOfEdge, int Count) Patches(int mask) {
        var parent = new int[CellEdges.Length];
        var changes = new bool[CellEdges.Length];

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            parent[edge] = edge;
            changes[edge] = ((((mask >> CellEdges[edge].A) ^ (mask >> CellEdges[edge].B)) & 1) != 0);
        }

        int Root(int edge) {
            while (parent[edge] != edge) {
                edge = parent[edge];
            }

            return edge;
        }

        void Join(int first, int second) {
            var a = Root(edge: first);
            var b = Root(edge: second);

            parent[Math.Max(val1: a, val2: b)] = Math.Min(val1: a, val2: b);
        }

        var onFace = new int[4];

        for (var axis = 0; (axis < 3); axis++) {
            for (var side = 0; (side < 2); side++) {
                var count = 0;

                for (var edge = 0; (edge < CellEdges.Length); edge++) {
                    if (changes[edge] && OnFace(axis: axis, edge: edge, side: side)) {
                        onFace[count++] = edge;
                    }
                }

                if (count == 2) {
                    Join(first: onFace[0], second: onFace[1]);
                } else if (count == 4) {
                    // An ambiguous face: each inside corner joins the two sign-changing edges that meet at it.
                    for (var corner = 0; (corner < 8); corner++) {
                        if (
                            (((corner >> axis) & 1) != side) ||
                            (((mask >> corner) & 1) == 0)
                        ) {
                            continue;
                        }

                        var first = -1;

                        for (var edge = 0; (edge < CellEdges.Length); edge++) {
                            if (
                                !changes[edge] ||
                                !OnFace(axis: axis, edge: edge, side: side) ||
                                ((CellEdges[edge].A != corner) && (CellEdges[edge].B != corner))
                            ) {
                                continue;
                            }

                            if (first < 0) {
                                first = edge;
                            } else {
                                Join(first: first, second: edge);
                            }
                        }
                    }
                }
            }
        }

        var patchOfEdge = new sbyte[CellEdges.Length];
        var numbers = new sbyte[CellEdges.Length];
        var patches = 0;

        Array.Fill(array: numbers, value: ((sbyte)-1));

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if (!changes[edge]) {
                patchOfEdge[edge] = -1;

                continue;
            }

            var root = Root(edge: edge);

            if (numbers[root] < 0) {
                numbers[root] = ((sbyte)patches++);
            }

            patchOfEdge[edge] = numbers[root];
        }

        return (patchOfEdge, patches);
    }
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
    private static (double X, double Y, double Z) Mean(SdfBakeGrid grid, int edges, int x, int y, int z) {
        double sx = 0.0, sy = 0.0, sz = 0.0;
        var count = 0;

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if ((edges & (1 << edge)) == 0) {
                continue;
            }

            var (a, b) = CellEdges[edge];
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
    // The vertex of the patch whose cube edges are `edges` (one bit an edge), in cell-local coordinates: the least-squares
    // point of the crossings' tangent planes, solved by Cramer's rule over the 3x3 normal equations with the mean-point
    // bias on the diagonal.
    private static (double X, double Y, double Z) Place(SdfBakeField field, SdfBakeGrid grid, int edges, int x, int y, int z, FixedQ4816 epsilon) {
        var mean = Mean(edges: edges, grid: grid, x: x, y: y, z: z);
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if ((edges & (1 << edge)) == 0) {
                continue;
            }

            var (a, b) = CellEdges[edge];
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
    // The cube edge of a cell that a lattice edge along `axis` is, from the edge's offsets (u, v) within the cell along the
    // two axes after `axis` in x, y, z order: the x edges are 0 to 3, the y edges 4 to 7 and the z edges 8 to 11, in
    // CellEdges order, each ordered by its offsets in the two axes it does not run along.
    private static int EdgeIndex(int axis, int u, int v) => (axis switch {
        0 => (u + (2 * v)),
        1 => (4 + (v + (2 * u))),
        _ => (8 + (u + (2 * v))),
    });
    // One quad per sign-changing lattice edge whose four surrounding cells all lie in the lattice. The cells around an
    // edge along axis a are visited counter-clockwise about +a, which faces +a; the quad is reversed when the edge runs
    // from outside to inside, so every quad faces out of the surface. Each cell lends the vertex of the patch the edge
    // belongs to.
    private static int[] Connect(SdfBakeGrid grid, byte[] maskOfCell, int[] vertexOfPatch) {
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

                            var index = (cell[0] + (cells * (cell[1] + (cells * cell[2]))));
                            var patch = PatchOfEdge[maskOfCell[index]][EdgeIndex(axis: axis, u: (j - cell[u]), v: (k - cell[v]))];

                            ring[corner] = ((patch < 0) ? -1 : vertexOfPatch[((index * MaximumPatches) + patch)]);
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
