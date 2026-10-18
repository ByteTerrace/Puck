using System.Numerics;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// A surface extracted from an <see cref="ISdfContourLattice"/>: one vertex per surface patch of each cell the surface crosses,
/// and one quad per sign-changing lattice edge joining the vertices of the four cells around it. Positions and normals
/// are world-space doubles; quads list four vertex indices, wound counter-clockwise seen from outside.
/// </summary>
internal sealed class SdfSurface {
    public required double[] Normals { get; init; }
    public required double[] Positions { get; init; }
    public int QuadCount => (Quads.Length / 4);
    public required int[] Quads { get; init; }
    // Per quad, the lattice corner at the outside end of the sign-changing edge the quad crosses: the field there is
    // nearest the surface the edge crosses, so its winning shape is that surface's, where the inside end of a shell thinner
    // than a cell can lie in the shape beneath.
    public required int[] QuadCorners { get; init; }
    // Per quad, the corner at the inside end of the same edge.
    public required int[] QuadInsideCorners { get; init; }
    // Per vertex, whether it lies on a crease or corner of the surface (Place's feature): empty where no vertex does.
    public bool[] Features { get; init; } = [];
    public int VertexCount => (Positions.Length / 3);
}
/// <summary>The sampled lattice a surface is contoured from (<see cref="SdfDualContouring"/>): cells along each axis, each
/// corner's sign and value, and the field's normal at any point inside it. Values need only a consistent scale, since a
/// crossing is the ratio of two of them.</summary>
internal interface ISdfContourLattice {
    int CellsX { get; }
    int CellsY { get; }
    int CellsZ { get; }
    double CellSize { get; }
    int Corner(int x, int y, int z);
    bool Inside(int corner);
    double Value(int corner, int x, int y, int z);
    (double X, double Y, double Z) World(double x, double y, double z);
    bool TryNormal(double x, double y, double z, out double nx, out double ny, out double nz);
    // The unit normal at the crossing (wx, wy, wz) of the lattice edge `length` cells long along `axis` from corner
    // (x, y, z), and the field's value there in world units, the distance the surface lies behind the crossing along the
    // normal: zero where the crossing is taken to lie on the surface.
    bool TryCrossingNormal(int x, int y, int z, int axis, int length, double wx, double wy, double wz, out double nx, out double ny, out double nz, out double offset);
    // How far past its cell, in cells, a vertex may sit where every tangent plane of its patch meets there: a sharp feature
    // whose crossings this cell sees and its neighbour, which holds the feature, does not. Zero clamps every vertex into
    // its cell; only a lattice whose crossings lie on the surface (TryCrossingNormal's offset) can tell a feature from
    // planes that meet off it.
    double FeatureReach { get; }
    // Each axis's cell size when they differ, or null for a cubic lattice, whose normals need no rescaling.
    (double X, double Y, double Z)? CellExtent { get; }
    // Whether a vertex's normal is the mean of its crossings' normals, which a lattice that measures the field's gradient
    // where each edge crosses the surface reads on the surface itself, rather than the field's gradient at the vertex
    // (TryNormal), which a lattice interpolates from samples off the surface: near a sharp edge or across a sheet thinner
    // than a cell, those samples' gradients point at the other face.
    bool CrossingNormals { get; }
}
/// <summary>A lattice contoured at two resolutions (<see cref="SdfDualContouring.ExtractRefined"/>): its cells are fine
/// cells, <see cref="Refinement"/> to a coarse cell along each axis, and a coarse cell that is not
/// <see cref="Refined"/> contours whole, from its eight coarse corners.</summary>
internal interface ISdfRefinedContourLattice : ISdfContourLattice {
    // Fine cells along each axis of a coarse cell, at least two; the lattice's cells along each axis are a multiple of it.
    int Refinement { get; }
    // Whether the coarse cell at coarse coordinates (x, y, z) contours its fine cells. Every fine sample on the faces of
    // a cell that is not refined must be the multilinear interpolation of that cell's coarse corners, so a measured fine
    // sample lies only on refined cells.
    bool Refined(int x, int y, int z);
    // Whether a refined coarse cell's fine samples were measured. A refined cell that was not merges the fine vertices
    // of each connected piece of its surface into one, so it adds no detail its coarse corners lack.
    bool Measured(int x, int y, int z);
    // Whether the coarse sample at coarse coordinates (x, y, z) lies inside: Inside of its corner, read without the
    // fine corner's coordinates.
    bool CoarseInside(int x, int y, int z);
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
    // How near, in cells, every tangent plane of a patch must pass to a point past its cell for the point to count as a
    // sharp feature the vertex keeps (Place, ISdfContourLattice.FeatureReach).
    private const double FeatureResidual = 0.01;
    // The mean-point bias a feature past its cell is solved with (Place): small enough to leave the planes it meets, large
    // enough to fix the point along their common null space.
    private const double FeatureBias = 1.0e-4;
    // How near, in cells, a crossing's tangent plane passes to a vertex for the vertex to take its normal (NearNormal).
    private const double NearPlane = 0.1;
    // How near, in fine cells, a merged piece's crossing planes pass to its feature point, root mean square (MergedFeature).
    private const double MergedResidual = 0.05;
    // The cosine within which every crossing of a flat patch faces its first (fifteen degrees).
    private const double FlatCosine = 0.966;
    // How far past its cell, in cells, a point every plane passes through stays a feature (Place, MergedFeature): a
    // crease whose crossings only this cell sees, held to a quarter cell, past which a concave crease's vertex folds the
    // quads it shares with its neighbours.
    private const double FeatureOvershoot = 0.25;
    // The cosine of the least turn between two crossings' planes that makes their meeting line a crease (thirty degrees);
    // planes that turn less (a curved face's) meet along a line far from the surface.
    private const double CreaseCosine = 0.866;

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

    public static SdfSurface Extract<TLattice>(TLattice grid) where TLattice : ISdfContourLattice {
        var cellsX = grid.CellsX;
        var cellsY = grid.CellsY;
        var cellsZ = grid.CellsZ;
        var cellCount = ((cellsX * cellsY) * cellsZ);
        var vertexOfPatch = new int[(cellCount * MaximumPatches)];
        var maskOfCell = new byte[cellCount];
        var positions = new List<double>();
        var normals = new List<double>();
        var features = new List<bool>();

        Array.Fill(array: vertexOfPatch, value: -1);

        for (var z = 0; (z < cellsZ); z++) {
            for (var y = 0; (y < cellsY); y++) {
                for (var x = 0; (x < cellsX); x++) {
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

                    var cell = (x + (cellsX * (y + (cellsY * z))));
                    var patchOfEdge = PatchOfEdge[mask];

                    maskOfCell[cell] = ((byte)mask);

                    for (var patch = 0; (patch < PatchCount[mask]); patch++) {
                        var edges = 0;

                        for (var edge = 0; (edge < CellEdges.Length); edge++) {
                            if (patchOfEdge[edge] == patch) {
                                edges |= (1 << edge);
                            }
                        }

                        var (px, py, pz) = Place(crossingNormal: out var crossingNormal, edges: edges, feature: out var feature, flat: out var flat, grid: grid, x: x, y: y, z: z);
                        var (wx, wy, wz) = grid.World(x: (x + px), y: (y + py), z: (z + pz));
                        var (nx, ny, nz) = VertexNormal(crossingNormal: crossingNormal, crossings: (feature || flat), grid: grid, x: wx, y: wy, z: wz);

                        vertexOfPatch[((cell * MaximumPatches) + patch)] = (positions.Count / 3);
                        positions.Add(item: wx);
                        positions.Add(item: wy);
                        positions.Add(item: wz);
                        normals.Add(item: nx);
                        normals.Add(item: ny);
                        normals.Add(item: nz);
                        features.Add(item: feature);
                    }
                }
            }
        }

        var (quads, quadCorners, quadInsideCorners) = Connect(grid: grid, maskOfCell: maskOfCell, vertexOfPatch: vertexOfPatch);

        return new SdfSurface {
            Features = [.. features],
            Normals = [.. normals],
            Positions = [.. positions],
            QuadCorners = quadCorners,
            QuadInsideCorners = quadInsideCorners,
            Quads = quads,
        };
    }

    /// <summary>
    /// Extracts the surface of a lattice contoured at two resolutions: a refined coarse cell places a vertex per patch
    /// of each fine cell the surface crosses, and any other coarse cell places one per patch of its own eight corners,
    /// as <see cref="Extract"/> does at the coarse spacing. A quad joins the four cells around each sign-changing edge,
    /// the coarse edge where all four contour whole and each fine edge where any is refined; a cell that contours whole
    /// lends the vertex of the patch the fine edge's coarse edge belongs to, or, for a fine edge inside one of its faces,
    /// of the patch whose crossing on that face lies nearest. Where two of the four cells are one coarse cell, the quad
    /// is a triangle, listed with its last vertex twice. The mesh has no crack where the resolution changes, since every
    /// fine edge on a whole cell's face reads the samples that cell's corners interpolate.
    /// </summary>
    public static SdfSurface ExtractRefined<TLattice>(TLattice grid) where TLattice : ISdfRefinedContourLattice {
        var factor = grid.Refinement;
        int[] cells = [grid.CellsX, grid.CellsY, grid.CellsZ];
        int[] coarse = [(cells[0] / factor), (cells[1] / factor), (cells[2] / factor)];
        var coarseCount = ((coarse[0] * coarse[1]) * coarse[2]);
        var refined = new bool[coarseCount];
        var coarseMasks = new byte[coarseCount];
        var coarseVertices = new int[coarseCount];
        var fineMasks = new Dictionary<long, byte>();
        var fineVertices = new Dictionary<long, int>();
        var positions = new List<double>();
        var normals = new List<double>();
        // Per vertex, the unmeasured refined cell whose fine vertices it merges with, or -1.
        var owners = new List<int>();
        var features = new List<bool>();
        // Per merged fine vertex, its crossings' tangent planes in world space (Place's worldPlanes).
        var vertexPlanes = new Dictionary<int, double[]>();

        Array.Fill(array: coarseVertices, value: -1);

        long FineKey(int x, int y, int z) => (x + (((long)cells[0]) * (y + (((long)cells[1]) * z))));
        int CoarseIndex(int x, int y, int z) => (x + (coarse[0] * (y + (coarse[1] * z))));
        int MaskAt(int x, int y, int z, int stride) {
            var mask = 0;

            if (stride == factor) {
                for (var offset = 0; (offset < 8); offset++) {
                    if (grid.CoarseInside(x: ((x / factor) + (offset & 1)), y: ((y / factor) + ((offset >> 1) & 1)), z: ((z / factor) + ((offset >> 2) & 1)))) {
                        mask |= (1 << offset);
                    }
                }

                return mask;
            }

            for (var offset = 0; (offset < 8); offset++) {
                if (grid.Inside(corner: grid.Corner(x: (x + ((offset & 1) * stride)), y: (y + (((offset >> 1) & 1) * stride)), z: (z + (((offset >> 2) & 1) * stride))))) {
                    mask |= (1 << offset);
                }
            }

            return mask;
        }
        void AddVertices(int mask, int owner, int x, int y, int z, int stride) {
            var patchOfEdge = PatchOfEdge[mask];

            for (var patch = 0; (patch < PatchCount[mask]); patch++) {
                var edges = 0;

                for (var edge = 0; (edge < CellEdges.Length); edge++) {
                    if (patchOfEdge[edge] == patch) {
                        edges |= (1 << edge);
                    }
                }

                // A fine vertex its cell merges keeps its crossings' tangent planes for the merged vertex's placement.
                var planes = (((owner >= 0) && grid.CrossingNormals) ? new double[10] : null);
                var (px, py, pz) = Place(crossingNormal: out var crossingNormal, edges: edges, feature: out var feature, flat: out var flat, grid: grid, stride: stride, worldPlanes: planes, x: x, y: y, z: z);
                var (wx, wy, wz) = grid.World(x: (x + (px * stride)), y: (y + (py * stride)), z: (z + (pz * stride)));
                var (nx, ny, nz) = VertexNormal(crossingNormal: crossingNormal, crossings: (feature || flat), grid: grid, x: wx, y: wy, z: wz);

                if (planes is not null) {
                    vertexPlanes.Add(key: (positions.Count / 3), value: planes);
                }
                positions.Add(item: wx);
                positions.Add(item: wy);
                positions.Add(item: wz);
                normals.Add(item: nx);
                normals.Add(item: ny);
                normals.Add(item: nz);
                owners.Add(item: owner);
                features.Add(item: feature);
            }
        }

        for (var cz = 0; (cz < coarse[2]); cz++) {
            for (var cy = 0; (cy < coarse[1]); cy++) {
                for (var cx = 0; (cx < coarse[0]); cx++) {
                    var index = CoarseIndex(x: cx, y: cy, z: cz);

                    refined[index] = grid.Refined(x: cx, y: cy, z: cz);

                    if (!refined[index]) {
                        var mask = MaskAt(stride: factor, x: (cx * factor), y: (cy * factor), z: (cz * factor));

                        if ((mask != 0) && (mask != 0xFF)) {
                            coarseMasks[index] = ((byte)mask);
                            coarseVertices[index] = (positions.Count / 3);
                            AddVertices(mask: mask, owner: -1, stride: factor, x: (cx * factor), y: (cy * factor), z: (cz * factor));
                        }

                        continue;
                    }

                    for (var fz = (cz * factor); (fz < ((cz + 1) * factor)); fz++) {
                        for (var fy = (cy * factor); (fy < ((cy + 1) * factor)); fy++) {
                            for (var fx = (cx * factor); (fx < ((cx + 1) * factor)); fx++) {
                                var mask = MaskAt(stride: 1, x: fx, y: fy, z: fz);

                                if ((mask == 0) || (mask == 0xFF)) {
                                    continue;
                                }

                                var key = FineKey(x: fx, y: fy, z: fz);

                                fineMasks.Add(key: key, value: ((byte)mask));
                                fineVertices.Add(key: key, value: (positions.Count / 3));
                                AddVertices(mask: mask, owner: (grid.Measured(x: cx, y: cy, z: cz) ? -1 : index), stride: 1, x: fx, y: fy, z: fz);
                            }
                        }
                    }
                }
            }
        }

        var quads = new List<int>();
        var quadCorners = new List<int>();
        var quadInsideCorners = new List<int>();
        Span<int> ring = stackalloc int[4];
        Span<int> at = stackalloc int[3];
        Span<int> cell = stackalloc int[3];
        Span<int> home = stackalloc int[3];

        void Emit(Span<int> around, bool firstInside, int firstCorner, int secondCorner) {
            // The ring with its repeated neighbours dropped, in order around the edge.
            Span<int> distinct = stackalloc int[4];
            var count = 0;

            for (var corner = 0; (corner < 4); corner++) {
                var vertex = around[(firstInside ? corner : (3 - corner))];

                if ((count == 0) || (distinct[(count - 1)] != vertex)) {
                    distinct[count++] = vertex;
                }
            }
            if ((count > 1) && (distinct[(count - 1)] == distinct[0])) {
                count--;
            }
            if (count < 3) {
                return;
            }

            quads.Add(item: distinct[0]);
            quads.Add(item: distinct[1]);
            quads.Add(item: distinct[2]);
            quads.Add(item: distinct[((count == 4) ? 3 : 2)]);
            quadCorners.Add(item: (firstInside ? secondCorner : firstCorner));
            quadInsideCorners.Add(item: (firstInside ? firstCorner : secondCorner));
        }
        // The patch of a whole cell nearest a point in fine coordinates: among its sign-changing coarse edges on one face
        // (faceAxis, faceSide), or on any face when faceAxis is negative or the face holds none.
        int NearestPatch(int index, int cx, int cy, int cz, int faceAxis, int faceSide, double px, double py, double pz) {
            var mask = coarseMasks[index];
            var patchOfEdge = PatchOfEdge[mask];
            var best = -1;
            var bestDistance = double.PositiveInfinity;

            for (var pass = 0; (pass < 2) && (best < 0); pass++) {
                for (var edge = 0; (edge < CellEdges.Length); edge++) {
                    if ((patchOfEdge[edge] < 0) || ((pass == 0) && (faceAxis >= 0) && !OnFace(axis: faceAxis, edge: edge, side: faceSide))) {
                        continue;
                    }

                    var (a, b) = CellEdges[edge];
                    var t = Crossing(a: a, b: b, grid: grid, stride: factor, x: (cx * factor), y: (cy * factor), z: (cz * factor));
                    var (ax, ay, az) = Offset(corner: a);
                    var (bx, by, bz) = Offset(corner: b);
                    var dx = (((cx + ax + (t * (bx - ax))) * factor) - px);
                    var dy = (((cy + ay + (t * (by - ay))) * factor) - py);
                    var dz = (((cz + az + (t * (bz - az))) * factor) - pz);
                    var distance = (((dx * dx) + (dy * dy)) + (dz * dz));

                    if (distance < bestDistance) {
                        bestDistance = distance;
                        best = patchOfEdge[edge];
                    }
                }
            }

            return best;
        }

        // The coarse edges all four of whose cells contour whole.
        for (var axis = 0; (axis < 3); axis++) {
            var u = ((axis + 1) % 3);
            var v = ((axis + 2) % 3);

            for (var k = 1; (k < coarse[v]); k++) {
                for (var j = 1; (j < coarse[u]); j++) {
                    for (var i = 0; (i < coarse[axis]); i++) {
                        at[axis] = (i * factor);
                        at[u] = (j * factor);
                        at[v] = (k * factor);

                        var firstCorner = grid.Corner(x: at[0], y: at[1], z: at[2]);
                        var first = grid.CoarseInside(x: (at[0] / factor), y: (at[1] / factor), z: (at[2] / factor));

                        at[axis] = ((i + 1) * factor);

                        var secondCorner = grid.Corner(x: at[0], y: at[1], z: at[2]);

                        if (first == grid.CoarseInside(x: (at[0] / factor), y: (at[1] / factor), z: (at[2] / factor))) {
                            continue;
                        }

                        var complete = true;

                        for (var corner = 0; (corner < 4); corner++) {
                            cell[axis] = i;
                            cell[u] = (j - (((corner == 0) || (corner == 3)) ? 1 : 0));
                            cell[v] = (k - ((corner < 2) ? 1 : 0));

                            var index = CoarseIndex(x: cell[0], y: cell[1], z: cell[2]);

                            if (refined[index] || (coarseVertices[index] < 0)) {
                                complete = false;
                                break;
                            }

                            var patch = PatchOfEdge[coarseMasks[index]][EdgeIndex(axis: axis, u: (j - cell[u]), v: (k - cell[v]))];

                            ring[corner] = ((patch < 0) ? -1 : (coarseVertices[index] + patch));
                            complete &= (ring[corner] >= 0);
                        }

                        if (complete) {
                            Emit(around: ring, firstCorner: firstCorner, firstInside: first, secondCorner: secondCorner);
                        }
                    }
                }
            }
        }

        // The fine edges a refined cell touches: every sign-changing edge of a crossed fine cell, once, in a fixed order.
        var fineEdges = new List<long>();
        var seen = new HashSet<long>();

        foreach (var (key, mask) in fineMasks) {
            var fx = ((int)(key % cells[0]));
            var fy = ((int)((key / cells[0]) % cells[1]));
            var fz = ((int)(key / (((long)cells[0]) * cells[1])));

            for (var edge = 0; (edge < CellEdges.Length); edge++) {
                if (PatchOfEdge[mask][edge] < 0) {
                    continue;
                }

                var (a, _) = CellEdges[edge];
                var (ax, ay, az) = Offset(corner: a);
                var edgeAxis = (edge >> 2);
                // An edge's key: its lower sample in fine coordinates and its axis.
                var edgeKey = ((((((long)(fz + az)) << 21) | ((long)(fy + ay))) << 21) | ((long)(fx + ax))) * 3L + edgeAxis;

                if (seen.Add(item: edgeKey)) {
                    fineEdges.Add(item: edgeKey);
                }
            }
        }
        fineEdges.Sort();

        foreach (var edgeKey in fineEdges) {
            var axis = ((int)(edgeKey % 3L));
            var packed = (edgeKey / 3L);
            var u = ((axis + 1) % 3);
            var v = ((axis + 2) % 3);

            at[0] = ((int)(packed & 0x1FFFFF));
            at[1] = ((int)((packed >> 21) & 0x1FFFFF));
            at[2] = ((int)(packed >> 42));

            if ((at[u] < 1) || (at[u] >= cells[u]) || (at[v] < 1) || (at[v] >= cells[v])) {
                continue;
            }

            var firstCorner = grid.Corner(x: at[0], y: at[1], z: at[2]);
            var first = grid.Inside(corner: firstCorner);
            var firstValue = grid.Value(corner: firstCorner, x: at[0], y: at[1], z: at[2]);

            at[axis]++;

            var secondCorner = grid.Corner(x: at[0], y: at[1], z: at[2]);
            var secondValue = grid.Value(corner: secondCorner, x: at[0], y: at[1], z: at[2]);

            at[axis]--;

            if (first == grid.Inside(corner: secondCorner)) {
                continue;
            }

            var complete = true;

            for (var corner = 0; (corner < 4); corner++) {
                cell[axis] = at[axis];
                cell[u] = (at[u] - (((corner == 0) || (corner == 3)) ? 1 : 0));
                cell[v] = (at[v] - ((corner < 2) ? 1 : 0));
                home[0] = (cell[0] / factor);
                home[1] = (cell[1] / factor);
                home[2] = (cell[2] / factor);

                var index = CoarseIndex(x: home[0], y: home[1], z: home[2]);
                var vertex = -1;

                if (refined[index]) {
                    var key = FineKey(x: cell[0], y: cell[1], z: cell[2]);

                    if (fineMasks.TryGetValue(key: key, value: out var mask)) {
                        var patch = PatchOfEdge[mask][EdgeIndex(axis: axis, u: (at[u] - cell[u]), v: (at[v] - cell[v]))];

                        vertex = ((patch < 0) ? -1 : (fineVertices[key] + patch));
                    }
                } else if (coarseVertices[index] >= 0) {
                    var lu = (at[u] - (home[u] * factor));
                    var lv = (at[v] - (home[v] * factor));
                    var onU = ((lu == 0) || (lu == factor));
                    var onV = ((lv == 0) || (lv == factor));
                    var patch = -1;

                    if (onU && onV) {
                        patch = PatchOfEdge[coarseMasks[index]][EdgeIndex(axis: axis, u: (lu / factor), v: (lv / factor))];
                    }
                    if (patch < 0) {
                        var t = ((firstValue == secondValue) ? 0.5 : Math.Clamp(max: 1.0, min: 0.0, value: (firstValue / (firstValue - secondValue))));

                        patch = NearestPatch(
                            cx: home[0],
                            cy: home[1],
                            cz: home[2],
                            faceAxis: (onU ? u : (onV ? v : -1)),
                            faceSide: (onU ? (lu / factor) : (onV ? (lv / factor) : 0)),
                            index: index,
                            px: (at[0] + ((axis == 0) ? t : 0.0)),
                            py: (at[1] + ((axis == 1) ? t : 0.0)),
                            pz: (at[2] + ((axis == 2) ? t : 0.0))
                        );
                    }

                    vertex = ((patch < 0) ? -1 : (coarseVertices[index] + patch));
                }

                ring[corner] = vertex;
                complete &= (vertex >= 0);
            }

            if (complete) {
                Emit(around: ring, firstCorner: firstCorner, firstInside: first, secondCorner: secondCorner);
            }
        }

        return Merge(features: features, grid: grid, normals: normals, owners: owners, positions: positions, quadCorners: quadCorners, quadInsideCorners: quadInsideCorners, quads: quads, vertexPlanes: vertexPlanes);
    }
    // Merges the fine vertices of each connected piece of an unmeasured refined cell's surface into one, at the feature
    // point their crossings' planes meet at (MergedFeature) or else their mean, with their mean normal, and drops the quads
    // that collapse to fewer than three vertices. Two fine vertices of
    // one cell are one piece when a quad joins them.
    // The point of a merged piece every one of its fine vertices' tangent planes passes through (within FeatureResidual of
    // a fine cell, root mean square), solved toward the piece's mean along the planes' common null space, or null where
    // the planes do not meet at one point or it lies outside the piece's coarse cell `owner`. `planes` holds the normal
    // equations' six distinct entries, their right-hand side and the planes' summed squared offsets.
    private static (double X, double Y, double Z)? MergedFeature<TLattice>(TLattice grid, (double X, double Y, double Z) mean, int owner, double[] planes) where TLattice : ISdfRefinedContourLattice {
        var (a00, a01, a02, a11, a12, a22) = ((planes[0] + FeatureBias), planes[1], planes[2], (planes[3] + FeatureBias), planes[4], (planes[5] + FeatureBias));
        var (b0, b1, b2) = ((planes[6] + (FeatureBias * mean.X)), (planes[7] + (FeatureBias * mean.Y)), (planes[8] + (FeatureBias * mean.Z)));
        var det = (((a00 * ((a11 * a22) - (a12 * a12))) - (a01 * ((a01 * a22) - (a12 * a02)))) + (a02 * ((a01 * a12) - (a11 * a02))));

        // A singular system's point is not finite, and fails the residual below.
        var x = ((((b0 * ((a11 * a22) - (a12 * a12))) - (a01 * ((b1 * a22) - (a12 * b2)))) + (a02 * ((b1 * a12) - (a11 * b2)))) / det);
        var y = ((((a00 * ((b1 * a22) - (a12 * b2))) - (b0 * ((a01 * a22) - (a12 * a02)))) + (a02 * ((a01 * b2) - (b1 * a02)))) / det);
        var z = ((((a00 * ((a11 * b2) - (b1 * a12))) - (a01 * ((a01 * b2) - (b1 * a02)))) + (b0 * ((a01 * a12) - (a11 * a02)))) / det);
        // Σ (n·p - d)² expanded over the accumulated sums, without the bias.
        var residual = ((((((planes[0] * x * x) + (planes[3] * y * y)) + (planes[5] * z * z)) +
            (2.0 * (((planes[1] * x * y) + (planes[2] * x * z)) + (planes[4] * y * z)))) -
            (2.0 * (((planes[6] * x) + (planes[7] * y)) + (planes[8] * z)))) + planes[9]);
        var count = (planes[0] + planes[3] + planes[5]);
        var tolerance = (MergedResidual * grid.CellSize);

        var factor = grid.Refinement;
        var coarseX = (grid.CellsX / factor);
        var coarseY = (grid.CellsY / factor);
        var cx = (owner % coarseX);
        var cy = ((owner / coarseX) % coarseY);
        var cz = (owner / (coarseX * coarseY));
        var low = grid.World(x: (cx * factor), y: (cy * factor), z: (cz * factor));
        var high = grid.World(x: ((cx + 1) * factor), y: ((cy + 1) * factor), z: ((cz + 1) * factor));

        var (rx, ry, rz) = ((FeatureOvershoot * (high.X - low.X)), (FeatureOvershoot * (high.Y - low.Y)), (FeatureOvershoot * (high.Z - low.Z)));

        return (((Math.Abs(value: det) >= 1e-12) && (residual <= ((tolerance * tolerance) * count)) &&
            (x >= (low.X - rx)) && (x <= (high.X + rx)) && (y >= (low.Y - ry)) && (y <= (high.Y + ry)) && (z >= (low.Z - rz)) && (z <= (high.Z + rz)))
            ? (x, y, z)
            : null);
    }
    private static SdfSurface Merge<TLattice>(TLattice grid, List<double> positions, List<double> normals, List<bool> features, List<int> owners, List<int> quads, List<int> quadCorners, List<int> quadInsideCorners, Dictionary<int, double[]> vertexPlanes) where TLattice : ISdfRefinedContourLattice {
        var vertexCount = owners.Count;
        var parent = new int[vertexCount];

        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            parent[vertex] = vertex;
        }

        int Root(int vertex) {
            while (parent[vertex] != vertex) {
                parent[vertex] = parent[parent[vertex]];
                vertex = parent[vertex];
            }

            return vertex;
        }

        for (var quad = 0; (quad < quads.Count); quad += 4) {
            for (var corner = 0; (corner < 4); corner++) {
                var a = quads[(quad + corner)];
                var b = quads[(quad + ((corner + 1) & 3))];

                if ((owners[a] >= 0) && (owners[a] == owners[b])) {
                    var ra = Root(vertex: a);
                    var rb = Root(vertex: b);

                    parent[Math.Max(val1: ra, val2: rb)] = Math.Min(val1: ra, val2: rb);
                }
            }
        }

        var remap = new int[vertexCount];
        var sums = new Dictionary<int, (double X, double Y, double Z, int Count)>();
        var normalSums = new Dictionary<int, (double X, double Y, double Z)>();
        // Per piece, the normal equations of its fine vertices' tangent planes and the planes' summed squared offsets.
        var planeSums = new Dictionary<int, double[]>();
        var mergedPositions = new List<double>();
        var mergedNormals = new List<double>();
        var mergedFeatures = new List<bool>();
        // Per piece, whether any of its fine vertices lies on a crease.
        var pieceFeatures = new HashSet<int>();

        Array.Fill(array: remap, value: -1);

        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            var root = ((owners[vertex] >= 0) ? Root(vertex: vertex) : vertex);

            if (owners[vertex] >= 0) {
                var sum = sums.GetValueOrDefault(key: root);

                sums[root] = ((sum.X + positions[(3 * vertex)]), (sum.Y + positions[((3 * vertex) + 1)]), (sum.Z + positions[((3 * vertex) + 2)]), (sum.Count + 1));

                var normalSum = normalSums.GetValueOrDefault(key: root);

                normalSums[root] = ((normalSum.X + normals[(3 * vertex)]), (normalSum.Y + normals[((3 * vertex) + 1)]), (normalSum.Z + normals[((3 * vertex) + 2)]));
                if (features[vertex]) { _ = pieceFeatures.Add(item: root); }

                // The piece's fine crossings' tangent planes, for its feature point.
                if (vertexPlanes.TryGetValue(key: vertex, value: out var planes)) {
                    if (!planeSums.TryGetValue(key: root, value: out var planeSum)) {
                        planeSum = new double[10];
                        planeSums.Add(key: root, value: planeSum);
                    }
                    for (var entry = 0; (entry < planeSum.Length); entry++) {
                        planeSum[entry] += planes[entry];
                    }
                }
            }
            if (remap[root] < 0) {
                remap[root] = (mergedPositions.Count / 3);
                for (var axis = 0; (axis < 3); axis++) {
                    mergedPositions.Add(item: positions[((3 * root) + axis)]);
                    mergedNormals.Add(item: normals[((3 * root) + axis)]);
                }
                mergedFeatures.Add(item: ((owners[root] < 0) && features[root]));
            }
            remap[vertex] = remap[root];
        }
        foreach (var (root, sum) in sums) {
            var at = (3 * remap[root]);
            var (x, y, z) = ((sum.X / sum.Count), (sum.Y / sum.Count), (sum.Z / sum.Count));

            // A lattice whose fine vertices lie on its features (CrossingNormals) keeps a sharp edge or corner through a
            // merged piece: the vertex is the point every fine vertex's tangent plane passes through, where one lies in the
            // coarse cell, and the vertices' mean otherwise.
            var feature = (((sum.Count > 1) && planeSums.TryGetValue(key: root, value: out var rootPlanes)) ? MergedFeature(grid: grid, mean: (x, y, z), owner: owners[root], planes: rootPlanes) : null);

            (x, y, z) = (feature ?? (x, y, z));
            mergedFeatures[remap[root]] = ((feature is not null) ? pieceFeatures.Contains(item: root) : ((sum.Count == 1) && features[root]));

            mergedPositions[at] = x;
            mergedPositions[(at + 1)] = y;
            mergedPositions[(at + 2)] = z;

            // The merged vertex's normal is its fine vertices' mean normal.
            var (sx, sy, sz) = normalSums[root];
            var length = Math.Sqrt(d: (((sx * sx) + (sy * sy)) + (sz * sz)));

            if (length > 1e-12) {
                mergedNormals[at] = (sx / length);
                mergedNormals[(at + 1)] = (sy / length);
                mergedNormals[(at + 2)] = (sz / length);
            }
        }

        var mergedQuads = new List<int>(capacity: quads.Count);
        var mergedCorners = new List<int>(capacity: quadCorners.Count);
        var mergedInsideCorners = new List<int>(capacity: quadInsideCorners.Count);
        Span<int> distinct = stackalloc int[4];

        for (var quad = 0; (quad < quadCorners.Count); quad++) {
            var count = 0;

            for (var corner = 0; (corner < 4); corner++) {
                var vertex = remap[quads[((4 * quad) + corner)]];

                if ((count == 0) || (distinct[(count - 1)] != vertex)) {
                    distinct[count++] = vertex;
                }
            }
            if ((count > 1) && (distinct[(count - 1)] == distinct[0])) {
                count--;
            }
            if ((count < 3) || ((count == 4) && ((distinct[0] == distinct[2]) || (distinct[1] == distinct[3])))) {
                continue;
            }

            mergedQuads.Add(item: distinct[0]);
            mergedQuads.Add(item: distinct[1]);
            mergedQuads.Add(item: distinct[2]);
            mergedQuads.Add(item: distinct[((count == 4) ? 3 : 2)]);
            mergedCorners.Add(item: quadCorners[quad]);
            mergedInsideCorners.Add(item: quadInsideCorners[quad]);
        }

        return new SdfSurface {
            Features = [.. mergedFeatures],
            Normals = [.. mergedNormals],
            Positions = [.. mergedPositions],
            QuadCorners = [.. mergedCorners],
            QuadInsideCorners = [.. mergedInsideCorners],
            Quads = [.. mergedQuads],
        };
    }
    /// <summary>Returns how many surface patches a cell with corner-sign mask <paramref name="mask"/> holds: more than
    /// one where separate sheets cross it.</summary>
    public static int PatchesOf(int mask) => PatchCount[mask];

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
    // The crossing on a sign-changing edge, as a fraction of the edge from its first corner. A cell `stride` lattice cells
    // wide reads its corners `stride` samples apart.
    private static double Crossing<TLattice>(TLattice grid, int x, int y, int z, int a, int b, int stride = 1) where TLattice : ISdfContourLattice {
        var (ax, ay, az) = Offset(corner: a);
        var (bx, by, bz) = Offset(corner: b);
        (ax, ay, az) = ((ax * stride), (ay * stride), (az * stride));
        (bx, by, bz) = ((bx * stride), (by * stride), (bz * stride));
        var va = ((double)grid.Value(corner: grid.Corner(x: (x + ax), y: (y + ay), z: (z + az)), x: (x + ax), y: (y + ay), z: (z + az)));
        var vb = ((double)grid.Value(corner: grid.Corner(x: (x + bx), y: (y + by), z: (z + bz)), x: (x + bx), y: (y + by), z: (z + bz)));
        var denominator = (va - vb);

        return ((denominator == 0.0)
            ? 0.5
            : Math.Clamp(max: 1.0, min: 0.0, value: (va / denominator)));
    }
    private static (double X, double Y, double Z) Mean<TLattice>(TLattice grid, int edges, int x, int y, int z, int stride = 1) where TLattice : ISdfContourLattice {
        double sx = 0.0, sy = 0.0, sz = 0.0;
        var count = 0;

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if ((edges & (1 << edge)) == 0) {
                continue;
            }

            var (a, b) = CellEdges[edge];
            var t = Crossing(a: a, b: b, grid: grid, stride: stride, x: x, y: y, z: z);

            var (ax, ay, az) = Offset(corner: a);
            var (bx, by, bz) = Offset(corner: b);

            sx += (ax + (t * (bx - ax)));
            sy += (ay + (t * (by - ay)));
            sz += (az + (t * (bz - az)));
            count++;
        }

        return ((sx / count), (sy / count), (sz / count));
    }
    // A vertex's unit normal: its crossings' mean normal where the lattice reads normals at its crossings
    // (ISdfContourLattice.CrossingNormals) and they do not cancel, the field's gradient at the vertex otherwise, and up
    // where neither has a direction.
    private static (double X, double Y, double Z) VertexNormal<TLattice>(TLattice grid, (double X, double Y, double Z) crossingNormal, bool crossings, double x, double y, double z) where TLattice : ISdfContourLattice {
        if (grid.CrossingNormals && crossings) {
            var length = Math.Sqrt(d: (((crossingNormal.X * crossingNormal.X) + (crossingNormal.Y * crossingNormal.Y)) + (crossingNormal.Z * crossingNormal.Z)));

            if (length > 1e-6) {
                return ((crossingNormal.X / length), (crossingNormal.Y / length), (crossingNormal.Z / length));
            }
        }

        return (grid.TryNormal(nx: out var nx, ny: out var ny, nz: out var nz, x: x, y: y, z: z) ? (nx, ny, nz) : (0.0, 1.0, 0.0));
    }
    // Whether every crossing faces within FlatCosine of the first: a cell crossed by one flat face alone.
    private static bool Flat(ReadOnlySpan<double> normals) {
        for (var plane = 3; (plane < normals.Length); plane += 3) {
            if ((((normals[0] * normals[plane]) + (normals[1] * normals[(plane + 1)])) + (normals[2] * normals[(plane + 2)])) < FlatCosine) {
                return false;
            }
        }

        return (normals.Length > 0);
    }    // The summed world normals of the crossings whose tangent planes pass within NearPlane of the vertex (cell
    // coordinates), the faces it lies on: a vertex on one face beside a crease takes that face's normal rather than a
    // blend with the crossings of the face across the crease, which its cell also holds. `all` sums every crossing's, for
    // a vertex no plane passes near.
    private static (double X, double Y, double Z) NearNormal((double X, double Y, double Z) all, ReadOnlySpan<double> normals, ReadOnlySpan<double> planes, (double X, double Y, double Z) point) {
        var (x, y, z) = (0.0, 0.0, 0.0);

        for (var plane = 0; ((4 * plane) < planes.Length); plane++) {
            var distance = ((((planes[(4 * plane)] * point.X) + (planes[((4 * plane) + 1)] * point.Y)) + (planes[((4 * plane) + 2)] * point.Z)) - planes[((4 * plane) + 3)]);

            if (Math.Abs(value: distance) <= NearPlane) {
                (x, y, z) = ((x + normals[(3 * plane)]), (y + normals[((3 * plane) + 1)]), (z + normals[((3 * plane) + 2)]));
            }
        }

        return ((((x * x) + (y * y)) + (z * z)) > 1e-12) ? (x, y, z) : all;
    }    // The vertex of the patch whose cube edges are `edges` (one bit an edge), in cell-local coordinates: the least-squares
    // point of the crossings' tangent planes, solved by Cramer's rule over the 3x3 normal equations with the mean-point
    // bias on the diagonal. A cell `stride` lattice cells wide (a coarse cell of a refined lattice) reads its corners
    // `stride` samples apart; the vertex is still in the cell's own unit coordinates. `crossingNormal` is the sum of the
    // crossings' unit world normals.
    // `worldPlanes`, when given, accumulates the crossings' tangent planes in world space as MergedFeature reads them.
    private static (double X, double Y, double Z) Place<TLattice>(TLattice grid, int edges, int x, int y, int z, out (double X, double Y, double Z) crossingNormal, out bool feature, out bool flat, int stride = 1, double[]? worldPlanes = null) where TLattice : ISdfContourLattice {
        var mean = Mean(edges: edges, grid: grid, stride: stride, x: x, y: y, z: z);
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
        Span<double> planes = stackalloc double[(4 * CellEdges.Length)];
        // Each plane's unit normal in world space, which the vertex normal reads (NearNormal).
        Span<double> crossingNormals = stackalloc double[(3 * CellEdges.Length)];
        var planeCount = 0;


        crossingNormal = (0.0, 0.0, 0.0);
        feature = false;

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if ((edges & (1 << edge)) == 0) {
                continue;
            }

            var (a, b) = CellEdges[edge];
            var t = Crossing(a: a, b: b, grid: grid, stride: stride, x: x, y: y, z: z);

            var (ax, ay, az) = Offset(corner: a);
            var (bx, by, bz) = Offset(corner: b);
            var px = (ax + (t * (bx - ax)));
            var py = (ay + (t * (by - ay)));
            var pz = (az + (t * (bz - az)));

            var (wx, wy, wz) = grid.World(x: (x + (px * stride)), y: (y + (py * stride)), z: (z + (pz * stride)));

            if (!grid.TryCrossingNormal(
                axis: BitOperations.TrailingZeroCount(value: (a ^ b)),
                length: stride,
                nx: out var nx,
                ny: out var ny,
                nz: out var nz,
                offset: out var offset,
                wx: wx,
                wy: wy,
                wz: wz,
                x: (x + (ax * stride)),
                y: (y + (ay * stride)),
                z: (z + (az * stride))
            )) {
                continue;
            }

            crossingNormal = ((crossingNormal.X + nx), (crossingNormal.Y + ny), (crossingNormal.Z + nz));
            crossingNormals[(3 * planeCount)] = nx;
            crossingNormals[((3 * planeCount) + 1)] = ny;
            crossingNormals[((3 * planeCount) + 2)] = nz;

            // The tangent plane passes through the surface, the crossing moved back along the normal by the field there,
            // in cell coordinates: a linear crossing between two samples whose values do not fall linearly (one outside a
            // convex edge, where the distance is to the edge) lies off the surface.
            if (offset != 0.0) {
                var (cx, cy, cz) = (grid.CellExtent ?? (grid.CellSize, grid.CellSize, grid.CellSize));

                px -= ((offset * nx) / (stride * cx));
                py -= ((offset * ny) / (stride * cy));
                pz -= ((offset * nz) / (stride * cz));
            }
            if (worldPlanes is not null) {
                var (onX, onY, onZ) = grid.World(x: (x + (px * stride)), y: (y + (py * stride)), z: (z + (pz * stride)));
                var distance = (((nx * onX) + (ny * onY)) + (nz * onZ));

                worldPlanes[0] += (nx * nx); worldPlanes[1] += (nx * ny); worldPlanes[2] += (nx * nz);
                worldPlanes[3] += (ny * ny); worldPlanes[4] += (ny * nz); worldPlanes[5] += (nz * nz);
                worldPlanes[6] += (nx * distance); worldPlanes[7] += (ny * distance); worldPlanes[8] += (nz * distance);
                worldPlanes[9] += (distance * distance);
            }

            // The tangent planes are solved in cell coordinates: a world normal there is scaled by each axis's cell size.
            if (grid.CellExtent is var (ex, ey, ez)) {
                (nx, ny, nz) = ((nx * ex), (ny * ey), (nz * ez));

                var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

                if (length <= 0.0) {
                    continue;
                }

                (nx, ny, nz) = ((nx / length), (ny / length), (nz / length));
            }

            var d = (((nx * px) + (ny * py)) + (nz * pz));


            planes[(4 * planeCount)] = nx;
            planes[((4 * planeCount) + 1)] = ny;
            planes[((4 * planeCount) + 2)] = nz;
            planes[((4 * planeCount) + 3)] = d;
            planeCount++;
            a00 += (nx * nx); a01 += (nx * ny); a02 += (nx * nz);
            a11 += (ny * ny); a12 += (ny * nz); a22 += (nz * nz);
            b0 += (nx * d); b1 += (ny * d); b2 += (nz * d);
        }

        const double Bias = 0.05;

        var result = mean;

        if (Solve(bias: Bias, x: out var sx, y: out var sy, z: out var sz)) {
            var inside = ((sx >= 0.0) && (sx <= 1.0) && (sy >= 0.0) && (sy <= 1.0) && (sz >= 0.0) && (sz <= 1.0));
            var placed = false;

            // Where every plane passes through one point of the cell, the vertex is that point, solved again with a bias
            // only the planes' common null space (an edge's direction) feels: the mean-point bias draws it off its planes
            // toward the crossings' mean by a few hundredths of a cell, which along a crease between two faces leaves the
            // vertices stepping off the crease from cell to cell. Only a lattice whose crossings lie on the surface
            // (FeatureReach) can tell a point every plane passes through from planes that merely meet. A point that two of
            // its planes reach turned by more than the crease angle is a crease or corner of the surface (`feature`), which
            // may lie up to FeatureOvershoot past the cell; planes that hardly turn (a curved face's) meet along a line far
            // from the surface, so their point stays in the cell. A curved face's crossings that turn more (a tube a cell
            // or two across) do not all pass through one point, and none of this applies.
            if ((grid.FeatureReach > 0.0) && Solve(bias: FeatureBias, x: out var fx, y: out var fy, z: out var fz) &&
                Meets(x: fx, y: fy, z: fz, planes: planes[..(4 * planeCount)])) {
                for (var first = 0; (first < planeCount) && !feature; first++) {
                    for (var second = (first + 1); (second < planeCount); second++) {
                        if ((((planes[(4 * first)] * planes[(4 * second)]) + (planes[((4 * first) + 1)] * planes[((4 * second) + 1)])) + (planes[((4 * first) + 2)] * planes[((4 * second) + 2)])) < CreaseCosine) {
                            feature = true;
                            break;
                        }
                    }
                }

                // A crease farther off still marks the vertex, whose placement falls back below.
                if (Math.Max(val1: Math.Abs(value: (fx - 0.5)), val2: Math.Max(val1: Math.Abs(value: (fy - 0.5)), val2: Math.Abs(value: (fz - 0.5)))) <= (0.5 + (feature ? FeatureOvershoot : 0.0))) {
                    result = (fx, fy, fz);
                    placed = true;
                }
            }
            // A sharp feature whose faces' planes meet just past the cell (an edge the next cell holds but whose crossings
            // only this cell sees) keeps the point they meet at, where clamping would cut the feature off; a point the
            // planes do not all pass through is no feature and clamps.
            if (!placed) {
                result = ((!inside && (Math.Max(val1: Math.Abs(value: (sx - 0.5)), val2: Math.Max(val1: Math.Abs(value: (sy - 0.5)), val2: Math.Abs(value: (sz - 0.5)))) <= (0.5 + grid.FeatureReach)) &&
                    Meets(x: sx, y: sy, z: sz, planes: planes[..(4 * planeCount)]))
                    ? (sx, sy, sz)
                    : (Math.Clamp(max: 1.0, min: 0.0, value: sx), Math.Clamp(max: 1.0, min: 0.0, value: sy), Math.Clamp(max: 1.0, min: 0.0, value: sz)));
            }
        }

        crossingNormal = NearNormal(all: crossingNormal, normals: crossingNormals[..(3 * planeCount)], planes: planes[..(4 * planeCount)], point: result);
        flat = Flat(normals: crossingNormals[..(3 * planeCount)]);

        return result;

        // Whether every plane (nx, ny, nz, d) passes within FeatureResidual of the point.
        static bool Meets(double x, double y, double z, ReadOnlySpan<double> planes) {
            for (var plane = 0; (plane < planes.Length); plane += 4) {
                if (Math.Abs(value: ((((planes[plane] * x) + (planes[(plane + 1)] * y)) + (planes[(plane + 2)] * z)) - planes[(plane + 3)])) > FeatureResidual) {
                    return false;
                }
            }

            return true;
        }

        // The least-squares point of the planes with `bias` on the diagonal drawing it toward the crossings' mean, by
        // Cramer's rule; false where the system is singular.
        bool Solve(double bias, out double x, out double y, out double z) {
            var (m00, m11, m22) = ((a00 + bias), (a11 + bias), (a22 + bias));
            var (r0, r1, r2) = ((b0 + (bias * mean.X)), (b1 + (bias * mean.Y)), (b2 + (bias * mean.Z)));
            var det = (((m00 * ((m11 * m22) - (a12 * a12))) - (a01 * ((a01 * m22) - (a12 * a02)))) + (a02 * ((a01 * a12) - (m11 * a02))));

            x = ((((r0 * ((m11 * m22) - (a12 * a12))) - (a01 * ((r1 * m22) - (a12 * r2)))) + (a02 * ((r1 * a12) - (m11 * r2)))) / det);
            y = ((((m00 * ((r1 * m22) - (a12 * r2))) - (r0 * ((a01 * m22) - (a12 * a02)))) + (a02 * ((a01 * r2) - (r1 * a02)))) / det);
            z = ((((m00 * ((m11 * r2) - (r1 * a12))) - (a01 * ((a01 * r2) - (r1 * a02)))) + (r0 * ((a01 * a12) - (m11 * a02)))) / det);

            return (Math.Abs(value: det) >= 1e-12);
        }
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
    private static (int[] Quads, int[] QuadCorners, int[] QuadInsideCorners) Connect<TLattice>(TLattice grid, byte[] maskOfCell, int[] vertexOfPatch) where TLattice : ISdfContourLattice {
        Span<int> cells = [grid.CellsX, grid.CellsY, grid.CellsZ];
        var quads = new List<int>();
        var quadCorners = new List<int>();
        var quadInsideCorners = new List<int>();
        Span<int> ring = stackalloc int[4];
        Span<int> at = stackalloc int[3];
        Span<int> cell = stackalloc int[3];

        for (var axis = 0; (axis < 3); axis++) {
            var u = ((axis + 1) % 3);
            var v = ((axis + 2) % 3);

            for (var k = 0; (k <= cells[v]); k++) {
                for (var j = 0; (j <= cells[u]); j++) {
                    for (var i = 0; (i < cells[axis]); i++) {
                        at[axis] = i;
                        at[u] = j;
                        at[v] = k;

                        if (
                            (j < 1) || (j >= cells[u]) ||
                            (k < 1) || (k >= cells[v])
                        ) {
                            continue;
                        }

                        var firstCorner = grid.Corner(x: at[0], y: at[1], z: at[2]);
                        var first = grid.Inside(corner: firstCorner);

                        at[axis] = (i + 1);

                        var secondCorner = grid.Corner(x: at[0], y: at[1], z: at[2]);
                        var second = grid.Inside(corner: secondCorner);

                        if (first == second) {
                            continue;
                        }

                        at[axis] = i;

                        var complete = true;

                        for (var corner = 0; (corner < 4); corner++) {
                            cell[axis] = i;
                            cell[u] = (j - (((corner == 0) || (corner == 3)) ? 1 : 0));
                            cell[v] = (k - ((corner < 2) ? 1 : 0));

                            var index = (cell[0] + (cells[0] * (cell[1] + (cells[1] * cell[2]))));
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

                        quadCorners.Add(item: (first ? secondCorner : firstCorner));
                        quadInsideCorners.Add(item: (first ? firstCorner : secondCorner));
                    }
                }
            }
        }

        return ([.. quads], [.. quadCorners], [.. quadInsideCorners]);
    }
}
