using System.Numerics;
using Puck.Abstractions.Memory;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// A surface extracted from an <see cref="ISdfContourLattice"/>: one vertex per surface patch of each cell the surface crosses,
/// and one quad per sign-changing lattice edge joining the vertices of the four cells around it. Positions and normals
/// are world space, placed in doubles and rounded once to single precision; quads list four vertex indices, wound
/// counter-clockwise seen from outside.
/// </summary>
internal sealed class SdfSurface {
    public required float[] Normals { get; init; }
    public required float[] Positions { get; init; }
    public int QuadCount => (Quads.Length / 4);
    public required int[] Quads { get; init; }
    // Per quad, the lattice corner at the outside end of the sign-changing edge the quad crosses: the field there is
    // nearest the surface the edge crosses, so its winning shape is that surface's, where the inside end of a shell thinner
    // than a cell can lie in the shape beneath.
    public required int[] QuadCorners { get; init; }
    // Per quad, the lattice edge it crosses as a signed axis (SdfContourFacing): the direction from the edge's inside
    // sample to its outside one, which every triangle of the quad faces along.
    public required sbyte[] QuadAxes { get; init; }
    // Per vertex, three components: the way the lattice says its patch faces (SdfContourFacing.Patch), a merged vertex's
    // the sum of its fine vertices'.
    public required float[] Patches { get; init; }
    // Per vertex, whether it lies on a crease or corner of the surface (Place's feature): empty where no vertex does.
    public bool[] Features { get; init; } = [];
    // Per vertex, whether its crossings' tangent planes drew it away from their mean (Place), as to a crease or to the
    // point the planes of a face rounded within its cell share, or it merges a refined cell's fine vertices, which
    // interpolate that cell's coarse corners (Merge): a place no crossing pins to the surface. Empty where none is.
    public bool[] Solved { get; init; } = [];
    // Per vertex, whether it lies inside its cell beside a curved convex crease past it (Place's `beside`): cut off the
    // crease, it may lie inside the solid, and its quads turn across the crease. Empty where none does.
    public bool[] Beside { get; init; } = [];
    public int VertexCount => (Positions.Length / 3);
}
/// <summary>The sampled lattice a surface is contoured from (<see cref="SdfDualContouring"/>): cells along each axis, each
/// corner's sign and value, and the field's normal at any point inside it. Values need only a consistent scale, since a
/// crossing is the ratio of two of them.</summary>
internal interface ISdfContourLattice {
    int CellsX { get; }
    int CellsY { get; }
    int CellsZ { get; }
    // The owner the contour's rented scratch is charged to for as long as the contour holds it, or null for unmetered
    // offline work. Every rental of the extraction goes through it, so a bake's account sees the contour's workspace.
    IAllocationObserver? Workspace { get; }
    // The native allocator the contour's lists and tables come from, so a background bake's largest scratch never reaches
    // the collector, or null for offline work, whose scratch is rented from RentedArrays.
    IAllocator? Allocator { get; }
    double CellSize { get; }
    int Corner(int x, int y, int z);
    bool Inside(int corner);
    double Value(int corner, int x, int y, int z);
    double Crossing(int x, int y, int z, int axis, int length, out int index);
    (double X, double Y, double Z) World(double x, double y, double z);
    bool TryNormal(double x, double y, double z, out double nx, out double ny, out double nz);
    // The unit normal at the crossing (wx, wy, wz) of the lattice edge `length` cells long along `axis` from corner
    // (x, y, z), and the field's value there in world units, the distance the surface lies behind the crossing along the
    // normal: zero where the crossing is taken to lie on the surface.
    bool TryCrossingNormal(int index, int length, double wx, double wy, double wz, out double nx, out double ny, out double nz, out double offset);
    // How far past its cell, in cells, a sharp feature its patch's tangent planes all meet at may lie: a feature whose
    // crossings this cell sees and its neighbour, which holds it, does not. A straight one keeps its vertex there, a curved
    // convex crease moves onto the neighbour's vertex (SdfDualContouring.HandOff). Zero clamps every vertex into
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
    // Crossed fine cells, ordered by coarse z/y/x then fine z/y/x. Corner is the low sample's linear index,
    // with CellsX + 1 and CellsY + 1 sample strides; Mask holds the eight signs after sheet closure.
    ReadOnlySpan<long> CrossingKeys { get; }
    ReadOnlySpan<SdfCrossedCell> CrossedFineCells { get; }
    // Fine cells along each axis of a coarse cell, at least two; the lattice's cells along each axis are a multiple of it.
    int Refinement { get; }
    // Whether the coarse cell at coarse coordinates (x, y, z) contours its fine cells. Every fine sample on the faces of
    // a cell that is not refined must be the multilinear interpolation of that cell's coarse corners, so a measured fine
    // sample lies only on refined cells.
    bool Refined(int x, int y, int z);
    // Whether a refined coarse cell's fine samples were measured. A refined cell that was not merges the fine vertices
    // of each connected piece of its surface into one, so it adds no detail its coarse corners lack.
    bool Measured(int x, int y, int z);
    // A measured coarse cell's stride: the fine cells along each axis each of its own cells spans, a power of two dividing
    // Refinement. A cell of stride one keeps its fine vertices; one of a larger stride merges the fine vertices of each
    // connected piece of its surface inside each of its own cells into one, as an unmeasured refined cell merges its whole
    // cell's.
    int Stride(int x, int y, int z);
    // Whether the coarse sample at coarse coordinates (x, y, z) lies inside: Inside of its corner, read without the
    // fine corner's coordinates.
    bool CoarseInside(int x, int y, int z);
}
internal readonly record struct SdfCrossedCell(int Corner, byte Mask);
/// <summary>
/// Extracts a <see cref="SdfSurface"/> by manifold dual contouring: a vertex per surface patch of a cell, and one quad
/// per sign-changing lattice edge joining the patch vertices of the four cells around it. A cell's patches are the
/// connected pieces of the marching-cubes surface inside it, so a cell two sheets cross, as a plate about a cell thick
/// crosses its cells, holds one vertex for each sheet, and no edge of the mesh is shared by more than two quads. A
/// patch is found from the cell's eight corner signs alone: the sign-changing cube edges are joined by the segments
/// that marching squares draws across each cube face, and an ambiguous face, whose diagonal corners agree, is cut so
/// that it separates its inside corners. The rule reads nothing but the face's four signs, so the two cells that share a
/// face agree on it and the mesh has no crack. Each sign-changing edge of a patch contributes its lattice's refined
/// zero crossing and the field's gradient there; the patch's vertex is the point
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
    // How near, in cells, a convex crease past a cell lies for the cell to keep it where its in-cell point lies within
    // NearPlane of both faces (Place): its vertex already lies on the crease, at the cell's face.
    private const double TouchedCrease = 0.05;
    // The mean-point bias a feature past its cell is solved with (Place): small enough to leave the planes it meets, large
    // enough to fix the point along their common null space.
    private const double FeatureBias = 1.0e-4;
    // The mean-point bias a vertex is first solved with (Place): it keeps a flat or single-edge patch's point determined.
    private const double MeanBias = 0.05;
    // How near, in cells, a crossing's tangent plane passes to a vertex for the vertex to take its normal (NearNormal).
    private const double NearPlane = 0.1;
    // How near, in fine cells, a merged piece's crossing planes pass to its feature point, root mean square (MergedFeature).
    private const double MergedResidual = 0.05;
    // The cosine within which every crossing of a flat patch faces its first (fifteen degrees).
    private const double FlatCosine = 0.966;
    // The cosine of the least turn between two crossings' planes that makes their meeting line a crease (thirty degrees);
    // planes that turn less (a curved face's) meet along a line far from the surface.
    private const double CreaseCosine = 0.866;
    // How far, in cells, a vertex lies from its crossings' mean for the contour to record it as placed by its planes
    // (SdfSurface.Solved).
    private const double SolvedDrift = 0.05;
    // The passes Settle makes over a contour's quads: a vertex returned for one quad can leave a neighbouring quad, which
    // faced out about the vertex's placed point, to settle in the next.
    private const int SettleRounds = 4;
    // How far, in cells, a vertex no feature pins keeps from a face of its cell its patch crosses (Clear): past the
    // rounding of a placed vertex, short of what a crease a cell's face cuts would show as a bevel.
    private const double CrossedClear = 0.05;

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
        // The workspace is the scope's, released once the surface, which the result owns, is copied out.
        using var scope = new RentedScope(allocator: grid.Allocator, owner: grid.Workspace);
        var vertexOfPatch = scope.Buffer<int>(length: (cellCount * MaximumPatches));
        var maskOfCell = scope.Buffer<byte>(length: cellCount);
        using var positions = new OwnedBuffer<double>(allocator: grid.Allocator, observer: grid.Workspace);
        using var normals = new OwnedBuffer<double>(allocator: grid.Allocator, observer: grid.Workspace);
        using var features = new OwnedBuffer<bool>(allocator: grid.Allocator, observer: grid.Workspace);
        using var solveds = new OwnedBuffer<bool>(allocator: grid.Allocator, observer: grid.Workspace);
        using var besides = new OwnedBuffer<bool>(allocator: grid.Allocator, observer: grid.Workspace);
        // Per vertex, its crossings' mean in world space, where Settle returns a vertex whose quads cannot face out.
        using var anchors = new OwnedBuffer<double>(allocator: grid.Allocator, observer: grid.Workspace);
        // Per vertex, the way the lattice says its patch faces (SdfContourFacing.Patch).
        using var patches = new OwnedBuffer<double>(allocator: grid.Allocator, observer: grid.Workspace);
        // Per vertex whose crease lies past its cell, the crease's point in lattice coordinates (HandOff).
        using var handoffs = new OwnedBuffer<(int Vertex, double X, double Y, double Z)>(allocator: grid.Allocator, observer: grid.Workspace);

        vertexOfPatch.GetSpan().Fill(value: -1);
        maskOfCell.GetSpan().Clear();

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

                        var (px, py, pz) = Place(anchor: out var anchor, beside: out var beside, beyond: out var beyond, crossingNormal: out var crossingNormal, edges: edges, feature: out var feature, flat: out var flat, grid: grid, solved: out var solved, x: x, y: y, z: z);
                        var (wx, wy, wz) = grid.World(x: (x + px), y: (y + py), z: (z + pz));
                        var (ax, ay, az) = grid.World(x: (x + anchor.X), y: (y + anchor.Y), z: (z + anchor.Z));
                        var (tx, ty, tz) = SdfContourFacing.Patch(edges: CellEdges, mask: mask, patch: edges);

                        anchors.Add(item: ax);
                        anchors.Add(item: ay);
                        anchors.Add(item: az);
                        patches.Add(item: tx);
                        patches.Add(item: ty);
                        patches.Add(item: tz);
                        var (nx, ny, nz) = VertexNormal(crossingNormal: crossingNormal, crossings: (feature || flat), grid: grid, x: wx, y: wy, z: wz);

                        if (beyond is var (bx, by, bz)) {
                            handoffs.Add(item: ((positions.Length / 3), (x + bx), (y + by), (z + bz)));
                        }
                        vertexOfPatch[((cell * MaximumPatches) + patch)] = (positions.Length / 3);
                        positions.Add(item: wx);
                        positions.Add(item: wy);
                        positions.Add(item: wz);
                        normals.Add(item: nx);
                        normals.Add(item: ny);
                        normals.Add(item: nz);
                        features.Add(item: feature);
                        solveds.Add(item: solved);
                        besides.Add(item: beside);
                    }
                }
            }
        }

        HandOff(besides: besides.GetSpan(), features: features.GetSpan(), grid: grid, handoffs: handoffs.GetSpan(), positions: positions.GetSpan(), solveds: solveds.GetSpan(), target: (x, y, z) => {
            var (tx, ty, tz) = (((int)Math.Floor(x)), ((int)Math.Floor(y)), ((int)Math.Floor(z)));

            if ((tx < 0) || (ty < 0) || (tz < 0) || (tx >= cellsX) || (ty >= cellsY) || (tz >= cellsZ)) {
                return -1;
            }

            var index = (tx + (cellsX * (ty + (cellsY * tz))));

            return ((PatchCount[maskOfCell[index]] == 1) ? vertexOfPatch[(index * MaximumPatches)] : -1);
        });

        var (quads, quadCorners, quadAxes) = Connect(grid: grid, maskOfCell: maskOfCell, vertexOfPatch: vertexOfPatch);

        Settle(anchors: anchors.GetSpan(), axes: quadAxes, besides: besides.GetSpan(), features: features.GetSpan(), patches: patches.GetSpan(), positions: positions.GetSpan(), quads: quads, solveds: solveds.GetSpan());

        return new SdfSurface {
            Beside = besides.ToArray(),
            Features = features.ToArray(),
            Solved = solveds.ToArray(),
            Normals = Narrowed(values: normals.GetSpan()),
            Patches = Narrowed(values: patches.GetSpan()),
            Positions = Narrowed(values: positions.GetSpan()),
            QuadAxes = quadAxes,
            QuadCorners = quadCorners,
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
        // The workspace is the scope's, released once the merged surface, which the result owns, is built.
        using var scope = new RentedScope(allocator: grid.Allocator, owner: grid.Workspace);
        var refined = scope.Buffer<bool>(length: coarseCount);
        var coarseMasks = scope.Buffer<byte>(length: coarseCount);
        var coarseVertices = scope.Buffer<int>(length: coarseCount);
        var crossed = grid.CrossedFineCells;
        // Per crossed fine cell, by its fine key, its corner-sign mask and its first vertex.
        using var fineCells = new OwnedLongMap<(byte Mask, int Vertex)>(allocator: grid.Allocator, capacity: crossed.Length, observer: grid.Workspace);
        // Every crossed fine cell places at least one vertex, and few place more, so the lists start near their final size
        // rather than growing by doubling from empty, which rents and returns an array at each step.
        var vertexEstimate = (crossed.Length + (crossed.Length / 4) + 64);
        using var positions = new OwnedBuffer<double>(allocator: grid.Allocator, capacity: (3 * vertexEstimate), observer: grid.Workspace);
        using var normals = new OwnedBuffer<double>(allocator: grid.Allocator, capacity: (3 * vertexEstimate), observer: grid.Workspace);
        // Per vertex, the block whose fine vertices it merges with (OwnerOf, Block), or -1.
        using var owners = new OwnedBuffer<int>(allocator: grid.Allocator, capacity: vertexEstimate, observer: grid.Workspace);
        // Per merging block, its low corner in fine lattice coordinates and its fine cells along each axis: an unmeasured
        // refined coarse cell, numbered by its coarse index, then the stride cells of measured cells of a stride past one,
        // numbered after the coarse cells as they are first met.
        using var strideBlocks = new OwnedLongMap<int>(allocator: grid.Allocator, observer: grid.Workspace);
        var blocks = new List<(int X, int Y, int Z, int Size)>();
        using var features = new OwnedBuffer<bool>(allocator: grid.Allocator, capacity: vertexEstimate, observer: grid.Workspace);
        using var solveds = new OwnedBuffer<bool>(allocator: grid.Allocator, capacity: vertexEstimate, observer: grid.Workspace);
        using var besides = new OwnedBuffer<bool>(allocator: grid.Allocator, capacity: vertexEstimate, observer: grid.Workspace);
        // Per vertex, its crossings' mean in world space (Settle).
        using var anchors = new OwnedBuffer<double>(allocator: grid.Allocator, capacity: (3 * vertexEstimate), observer: grid.Workspace);
        // Per vertex, the way the lattice says its patch faces (SdfContourFacing.Patch).
        using var patches = new OwnedBuffer<double>(allocator: grid.Allocator, capacity: (3 * vertexEstimate), observer: grid.Workspace);
        // Every merged fine vertex's crossings' tangent planes in world space (Place's worldPlanes), back to back in vertex
        // order, and per vertex where its planes start and end there: -1 for a vertex that records none.
        using var planeData = new OwnedBuffer<double>(allocator: grid.Allocator, capacity: (grid.CrossingNormals ? (8 * vertexEstimate) : 0), observer: grid.Workspace);
        using var planeStarts = new OwnedBuffer<int>(allocator: grid.Allocator, capacity: vertexEstimate, observer: grid.Workspace);
        using var planeEnds = new OwnedBuffer<int>(allocator: grid.Allocator, capacity: vertexEstimate, observer: grid.Workspace);
        // Per vertex whose crease lies past its cell, the crease's point in fine lattice coordinates (HandOff).
        using var handoffs = new OwnedBuffer<(int Vertex, double X, double Y, double Z)>(allocator: grid.Allocator, observer: grid.Workspace);

        coarseMasks.GetSpan().Clear();
        coarseVertices.GetSpan().Fill(value: -1);

        long FineKey(int x, int y, int z) => (x + (((long)cells[0]) * (y + (((long)cells[1]) * z))));
        int CoarseIndex(int x, int y, int z) => (x + (coarse[0] * (y + (coarse[1] * z))));
        int CoarseMask(int x, int y, int z) {
            var mask = 0;

            for (var offset = 0; (offset < 8); offset++) {
                if (grid.CoarseInside(x: (x + (offset & 1)), y: (y + ((offset >> 1) & 1)), z: (z + ((offset >> 2) & 1)))) {
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
                var records = ((owner >= 0) && grid.CrossingNormals);
                var planeStart = planeData.Length;
                var (px, py, pz) = Place(anchor: out var anchor, beside: out var beside, beyond: out var beyond, crossingNormal: out var crossingNormal, edges: edges, feature: out var feature, flat: out var flat, grid: grid, solved: out var solved, stride: stride, worldPlanes: (records ? planeData : null), x: x, y: y, z: z);
                var (wx, wy, wz) = grid.World(x: (x + (px * stride)), y: (y + (py * stride)), z: (z + (pz * stride)));
                var (ax, ay, az) = grid.World(x: (x + (anchor.X * stride)), y: (y + (anchor.Y * stride)), z: (z + (anchor.Z * stride)));
                var (tx, ty, tz) = SdfContourFacing.Patch(edges: CellEdges, mask: mask, patch: edges);

                anchors.Add(item: ax);
                anchors.Add(item: ay);
                anchors.Add(item: az);
                patches.Add(item: tx);
                patches.Add(item: ty);
                patches.Add(item: tz);
                var (nx, ny, nz) = VertexNormal(crossingNormal: crossingNormal, crossings: (feature || flat), grid: grid, x: wx, y: wy, z: wz);

                // A vertex its cell merges hands no crease over: its piece does, as one vertex (Merge).
                if ((owner < 0) && (beyond is var (bx, by, bz))) {
                    handoffs.Add(item: ((positions.Length / 3), (x + (bx * stride)), (y + (by * stride)), (z + (bz * stride))));
                }
                planeStarts.Add(item: (records ? planeStart : -1));
                planeEnds.Add(item: planeData.Length);
                positions.Add(item: wx);
                positions.Add(item: wy);
                positions.Add(item: wz);
                normals.Add(item: nx);
                normals.Add(item: ny);
                normals.Add(item: nz);
                owners.Add(item: owner);
                features.Add(item: feature);
                solveds.Add(item: solved);
                besides.Add(item: beside);
            }
        }

        var nextCrossed = 0;
        for (var cz = 0; (cz < coarse[2]); cz++) {
            for (var cy = 0; (cy < coarse[1]); cy++) {
                for (var cx = 0; (cx < coarse[0]); cx++) {
                    var index = CoarseIndex(x: cx, y: cy, z: cz);

                    refined[index] = grid.Refined(x: cx, y: cy, z: cz);

                    if (!refined[index]) {
                        var mask = CoarseMask(x: cx, y: cy, z: cz);

                        if ((mask != 0) && (mask != 0xFF)) {
                            coarseMasks[index] = ((byte)mask);
                            coarseVertices[index] = (positions.Length / 3);
                            AddVertices(mask: mask, owner: -1, stride: factor, x: (cx * factor), y: (cy * factor), z: (cz * factor));
                        }

                        continue;
                    }

                    // The crossing stage already read every fine cell's signs. Reuse its sparse topology in exactly
                    // the vertex order the dense scan used, including coarse cells interleaved with refined ones.
                    for (; (nextCrossed < crossed.Length); nextCrossed++) {
                        var crossedCell = crossed[nextCrossed];
                        var fx = (crossedCell.Corner % (cells[0] + 1));
                        var fy = ((crossedCell.Corner / (cells[0] + 1)) % (cells[1] + 1));
                        var fz = (crossedCell.Corner / ((cells[0] + 1) * (cells[1] + 1)));

                        if (((fx / factor) != cx) || ((fy / factor) != cy) || ((fz / factor) != cz)) { break; }
                        var key = FineKey(x: fx, y: fy, z: fz);

                        fineCells.Add(key: key, value: (crossedCell.Mask, (positions.Length / 3)));
                        AddVertices(mask: crossedCell.Mask, owner: OwnerOf(cx: cx, cy: cy, cz: cz, fx: fx, fy: fy, fz: fz, index: index), stride: 1, x: fx, y: fy, z: fz);
                    }
                }
            }
        }

        // The block a fine vertex of refined coarse cell (cx, cy, cz) at fine cell (fx, fy, fz) merges into: none in a
        // measured cell of stride one, its stride cell in a measured cell of a larger stride, the whole coarse cell in an
        // unmeasured one.
        int OwnerOf(int cx, int cy, int cz, int fx, int fy, int fz, int index) {
            if (!grid.Measured(x: cx, y: cy, z: cz)) { return index; }

            var size = grid.Stride(x: cx, y: cy, z: cz);

            if (size <= 1) { return -1; }

            var (bx, by, bz) = (((fx / size) * size), ((fy / size) * size), ((fz / size) * size));
            var key = ((((((long)bz * (cells[1] + 1L)) + by) * (cells[0] + 1L)) + bx) * 8L) + BitOperations.Log2(value: ((uint)size));

            if (!strideBlocks.TryGetValue(key: key, value: out var owner)) {
                owner = (coarseCount + blocks.Count);
                strideBlocks.Add(key: key, value: owner);
                blocks.Add(item: (bx, by, bz, size));
            }

            return owner;
        }
        // The block an owner names (OwnerOf): its low corner in fine lattice coordinates and its fine cells a side.
        (int X, int Y, int Z, int Size) Block(int owner) {
            if (owner >= coarseCount) { return blocks[(owner - coarseCount)]; }

            var (bx, by, bz) = ((owner % coarse[0]), ((owner / coarse[0]) % coarse[1]), (owner / (coarse[0] * coarse[1])));

            return ((bx * factor), (by * factor), (bz * factor), factor);
        }

        // The vertex of the single-patch cell holding a point in fine lattice coordinates, at the resolution that cell
        // contours at, or -1.
        int CellVertex(double x, double y, double z) {
            var (fx, fy, fz) = (((int)Math.Floor(x)), ((int)Math.Floor(y)), ((int)Math.Floor(z)));

            if ((fx < 0) || (fy < 0) || (fz < 0) || (fx >= cells[0]) || (fy >= cells[1]) || (fz >= cells[2])) {
                return -1;
            }

            var index = CoarseIndex(x: (fx / factor), y: (fy / factor), z: (fz / factor));

            if (!refined[index]) {
                return ((PatchCount[coarseMasks[index]] == 1) ? coarseVertices[index] : -1);
            }

            var key = FineKey(x: fx, y: fy, z: fz);

            return ((fineCells.TryGetValue(key: key, value: out var fine) && (PatchCount[fine.Mask] == 1)) ? fine.Vertex : -1);
        }

        using var quads = new OwnedBuffer<int>(allocator: grid.Allocator, observer: grid.Workspace);
        using var quadCorners = new OwnedBuffer<int>(allocator: grid.Allocator, observer: grid.Workspace);
        using var quadAxes = new OwnedBuffer<sbyte>(allocator: grid.Allocator, observer: grid.Workspace);
        Span<int> ring = stackalloc int[4];
        Span<int> at = stackalloc int[3];
        Span<int> cell = stackalloc int[3];
        Span<int> home = stackalloc int[3];

        void Emit(Span<int> around, int axis, bool firstInside, int firstCorner, int secondCorner) {
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
            quadAxes.Add(item: SdfContourFacing.Axis(axis: axis, rising: firstInside));
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
                    var t = Crossing(index: out _, a: a, b: b, grid: grid, stride: factor, x: (cx * factor), y: (cy * factor), z: (cz * factor));
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
                            Emit(around: ring, axis: axis, firstCorner: firstCorner, firstInside: first, secondCorner: secondCorner);
                        }
                    }
                }
            }
        }

        // The fine edges a refined cell touches: every sign-changing edge of a crossed fine cell, once, in ascending key
        // order.
        foreach (var crossingKey in grid.CrossingKeys) {
            if ((crossingKey & 1L) != 0) { continue; }
            var axis = (int)((crossingKey >> 1) % 3);
            var startCorner = (int)(crossingKey / 6);
            var row = startCorner / (cells[0] + 1);
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            at[0] = startCorner % (cells[0] + 1);
            at[1] = row % (cells[1] + 1);
            at[2] = row / (cells[1] + 1);

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

                    if (fineCells.TryGetValue(key: key, value: out var fine)) {
                        var patch = PatchOfEdge[fine.Mask][EdgeIndex(axis: axis, u: (at[u] - cell[u]), v: (at[v] - cell[v]))];

                        vertex = ((patch < 0) ? -1 : (fine.Vertex + patch));
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
                Emit(around: ring, axis: axis, firstCorner: firstCorner, firstInside: first, secondCorner: secondCorner);
            }
        }

        // A piece whose merged vertex the settle had to return holds what one vertex cannot: the merge runs again with
        // those pieces split at their creases (SplitAtCreases).
        var surface = Merge(anchors: anchors, besides: besides, block: Block, patches: patches, cellVertex: CellVertex, features: features, grid: grid, handoffs: handoffs, normals: normals, owners: owners, positions: positions, quadAxes: quadAxes, quadCorners: quadCorners, quads: quads, solveds: solveds, planeData: planeData, planeStarts: planeStarts, planeEnds: planeEnds, returned: out var returned, unheld: null);

        return ((returned is null) ? surface : Merge(anchors: anchors, besides: besides, block: Block, patches: patches, cellVertex: CellVertex, features: features, grid: grid, handoffs: handoffs, normals: normals, owners: owners, positions: positions, quadAxes: quadAxes, quadCorners: quadCorners, quads: quads, solveds: solveds, planeData: planeData, planeStarts: planeStarts, planeEnds: planeEnds, returned: out _, unheld: returned));
    }
    // The point of a merged piece every one of its fine vertices' tangent planes `planes` (world space, nx, ny, nz, d
    // each) passes through, within MergedResidual of a fine cell root mean square, solved toward the piece's mean along the
    // planes' common null space, with how far past the piece's block `block` (its low corner in fine lattice coordinates
    // and its fine cells a side) it lies, in blocks (zero within it); null where the planes do not meet at one point.
    // Where the point is a convex crease up to FeatureReach past the block, `Inside` is the point the piece's vertex takes instead: the mean-biased point, which a straight crease's planes
    // meet too, as Place keeps it; else the point inside the block (CellSolve.Inside), the crease handed to the neighbour
    // (`HandOff`). Null otherwise. The planes are solved in the block's unit coordinates, as Place solves a cell's; `local`
    // is the caller's scratch for them there, exactly as long as `planes`.
    private static (double X, double Y, double Z, double Past, (double X, double Y, double Z)? Inside, bool HandOff, bool Beside)? MergedFeature<TLattice>(TLattice grid, (double X, double Y, double Z) mean, (int X, int Y, int Z, int Size) block, ReadOnlySpan<double> planes, Span<double> local) where TLattice : ISdfRefinedContourLattice {
        var factor = block.Size;
        var low = grid.World(x: block.X, y: block.Y, z: block.Z);
        var high = grid.World(x: (block.X + factor), y: (block.Y + factor), z: (block.Z + factor));
        var (ex, ey, ez) = ((high.X - low.X), (high.Y - low.Y), (high.Z - low.Z));
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
        var (mx, my, mz) = (((mean.X - low.X) / ex), ((mean.Y - low.Y) / ey), ((mean.Z - low.Z) / ez));

        for (var plane = 0; (plane < planes.Length); plane += 4) {
            // n · (low + e q) = d, with n scaled by e and the plane normalised.
            var (nx, ny, nz) = ((planes[plane] * ex), (planes[(plane + 1)] * ey), (planes[(plane + 2)] * ez));
            var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));
            var d = ((planes[(plane + 3)] - (((planes[plane] * low.X) + (planes[(plane + 1)] * low.Y)) + (planes[(plane + 2)] * low.Z))) / length);

            (nx, ny, nz) = ((nx / length), (ny / length), (nz / length));
            (local[plane], local[(plane + 1)], local[(plane + 2)], local[(plane + 3)]) = (nx, ny, nz, d);
            a00 += (nx * nx); a01 += (nx * ny); a02 += (nx * nz);
            a11 += (ny * ny); a12 += (ny * nz); a22 += (nz * nz);
            b0 += (nx * d); b1 += (ny * d); b2 += (nz * d);
        }

        var cell = new CellSolve(A00: a00, A01: a01, A02: a02, A11: a11, A12: a12, A22: a22, B0: b0, B1: b1, B2: b2, Mean: (mx, my, mz));

        if (!cell.Solve(bias: FeatureBias, x: out var x, y: out var y, z: out var z)) {
            return null;
        }

        var residual = 0.0;

        for (var plane = 0; (plane < local.Length); plane += 4) {
            var distance = ((((local[plane] * x) + (local[(plane + 1)] * y)) + (local[(plane + 2)] * z)) - local[(plane + 3)]);

            residual += (distance * distance);
        }

        var tolerance = (MergedResidual / factor);

        if (!(residual <= ((tolerance * tolerance) * (local.Length / 4)))) {
            return null;
        }

        var past = Math.Max(val1: 0.0, val2: (Math.Max(val1: Math.Abs(value: (x - 0.5)), val2: Math.Max(val1: Math.Abs(value: (y - 0.5)), val2: Math.Abs(value: (z - 0.5)))) - 0.5));
        var (faceA, faceB) = Faces(planes: local);
        (double X, double Y, double Z)? inside = null;
        var (handOff, beside) = (false, false);

        // A convex crease past the cell, curved or straight, is the neighbour's that holds it, as Place keeps a cell's: the
        // piece's vertex stays inside the cell beside it. The mean-biased point past the cell sits a few hundredths of a
        // cell off the crease the neighbour's vertex lies on, a sliver strip between two rows of crease vertices.
        if ((past > 0.0) && (past <= grid.FeatureReach) && (faceA >= 0) && ConvexCrease(faceA: faceA, faceB: faceB, planes: local, point: (mx, my, mz)) &&
            cell.Solve(bias: MeanBias, x: out var sx, y: out var sy, z: out var sz)) {
            var (ix, iy, iz) = cell.Inside(high: (1.0, 1.0, 1.0), low: (0.0, 0.0, 0.0), planes: local, x: sx, y: sy, z: sz);

            beside = true;
            handOff = !OnBoth(faceA: faceA, faceB: faceB, planes: local, point: (ix, iy, iz));
            if (handOff) {
                (ix, iy, iz) = OwnSide(crease: (x, y, z), faceA: faceA, faceB: faceB, held: (ix, iy, iz), mean: (mx, my, mz), planes: local);
            }
            inside = ((low.X + (ix * ex)), (low.Y + (iy * ey)), (low.Z + (iz * ez)));
        }

        return ((low.X + (x * ex)), (low.Y + (y * ey)), (low.Z + (z * ez)), past, inside, handOff, beside);
    }
    // Merges the fine vertices of each connected piece of a block's surface into one (an unmeasured refined cell, or a
    // stride cell of a measured cell of a stride past one; `block` names each), at the feature point their crossings'
    // planes meet at (MergedFeature), inside the block where a convex crease lies past it, or at their mean, with their
    // mean normal, and drops the quads that collapse to fewer than three vertices. Two fine vertices of one block are one
    // piece when a quad joins them.
    // A vertex's planes are planeData[planeStarts[v]..planeEnds[v]], or none where planeStarts[v] is negative. The pieces
    // `unheld` names by their roots (fine vertex indices) split at their creases first (SplitAtCreases), which gives
    // their fine vertices new owners in `owners`; `returned` names, the same way, every piece whose merged vertex the
    // settle returned, or is null where it returned none.
    private static SdfSurface Merge<TLattice>(TLattice grid, OwnedBuffer<double> positions, OwnedBuffer<double> normals, OwnedBuffer<double> anchors, OwnedBuffer<double> patches, OwnedBuffer<bool> features, OwnedBuffer<bool> solveds, OwnedBuffer<int> owners, OwnedBuffer<int> quads, OwnedBuffer<int> quadCorners, OwnedBuffer<sbyte> quadAxes, OwnedBuffer<double> planeData, OwnedBuffer<int> planeStarts, OwnedBuffer<int> planeEnds, OwnedBuffer<bool> besides, OwnedBuffer<(int Vertex, double X, double Y, double Z)> handoffs, Func<double, double, double, int> cellVertex, Func<int, (int X, int Y, int Z, int Size)> block, IReadOnlySet<int>? unheld, out HashSet<int>? returned) where TLattice : ISdfRefinedContourLattice {
        var vertexCount = owners.Length;
        var faces = SplitAtCreases(block: block, grid: grid, owners: owners, planeData: planeData, planeEnds: planeEnds, planeStarts: planeStarts, positions: positions, quads: quads, unheld: unheld);

        if (faces.Count > 0) {
            var whole = block;

            block = owner => whole(faces.BlockOf(owner: owner));
        }

        using var pieceSets = Pieces(owners: owners, quads: quads, workspace: grid.Workspace);

        int Root(int vertex) => pieceSets.Find(element: vertex);

        // A merged vertex is an unowned vertex or the root of a piece, so the merged arrays are sized exactly once and
        // filled in place, rather than grown as lists and copied.
        var mergedCount = 0;

        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            if ((owners[vertex] < 0) || (Root(vertex: vertex) == vertex)) { mergedCount++; }
        }

        using var scope = new RentedScope(allocator: grid.Allocator, owner: grid.Workspace);
        var remap = scope.Buffer<int>(length: vertexCount);
        // Per merged vertex, by its index: a piece's sums over its fine vertices, its root and whether any of them lies
        // on a crease, and where its fine vertices' tangent planes lie in `piecePlanes`. A merged index is numbered as its
        // piece is first met in vertex order, so walking the indices visits the pieces in that order.
        var pieces = scope.Buffer<Piece>(length: mergedCount);
        var mergedPositions = scope.Buffer<double>(length: (3 * mergedCount));
        var mergedNormals = scope.Buffer<double>(length: (3 * mergedCount));
        var mergedFeatures = new bool[mergedCount];
        var mergedSolved = new bool[mergedCount];
        var mergedBesides = new bool[mergedCount];
        // Per merged vertex, where Settle returns it: an unowned vertex's own crossings' mean, a piece's fine vertices'
        // mean of theirs.
        var mergedAnchors = scope.Buffer<double>(length: (3 * mergedCount));
        var mergedPatches = scope.Buffer<double>(length: (3 * mergedCount));
        var merged = 0;

        remap.GetSpan().Fill(value: -1);
        pieces.GetSpan().Clear();
        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            var root = ((owners[vertex] >= 0) ? Root(vertex: vertex) : vertex);

            if (remap[root] < 0) {
                remap[root] = merged;
                for (var axis = 0; (axis < 3); axis++) {
                    mergedPositions[((3 * merged) + axis)] = positions[((3 * root) + axis)];
                    mergedNormals[((3 * merged) + axis)] = normals[((3 * root) + axis)];
                    mergedAnchors[((3 * merged) + axis)] = anchors[((3 * root) + axis)];
                    mergedPatches[((3 * merged) + axis)] = patches[((3 * root) + axis)];
                }
                mergedFeatures[merged] = ((owners[root] < 0) && features[root]);
                mergedSolved[merged] = ((owners[root] < 0) && solveds[root]);
                mergedBesides[merged] = ((owners[root] < 0) && besides[root]);
                merged++;
            }
            remap[vertex] = remap[root];
            if (owners[vertex] < 0) { continue; }

            ref var piece = ref pieces[remap[root]];

            piece.Root = root;
            piece.Count++;
            (piece.X, piece.Y, piece.Z) = ((piece.X + positions[(3 * vertex)]), (piece.Y + positions[((3 * vertex) + 1)]), (piece.Z + positions[((3 * vertex) + 2)]));
            (piece.AnchorX, piece.AnchorY, piece.AnchorZ) = ((piece.AnchorX + anchors[(3 * vertex)]), (piece.AnchorY + anchors[((3 * vertex) + 1)]), (piece.AnchorZ + anchors[((3 * vertex) + 2)]));
            (piece.PatchX, piece.PatchY, piece.PatchZ) = ((piece.PatchX + patches[(3 * vertex)]), (piece.PatchY + patches[((3 * vertex) + 1)]), (piece.PatchZ + patches[((3 * vertex) + 2)]));
            (piece.NormalX, piece.NormalY, piece.NormalZ) = ((piece.NormalX + normals[(3 * vertex)]), (piece.NormalY + normals[((3 * vertex) + 1)]), (piece.NormalZ + normals[((3 * vertex) + 2)]));
            piece.Feature |= features[vertex];
            // The piece's fine crossings' tangent planes, for its feature point, gathered below in vertex order: a face
            // a crease split off its piece keeps only that face's.
            if (planeStarts[vertex] >= 0) {
                piece.HasPlanes = true;
                piece.PlaneEnd += faces.Keep(destination: [], owner: owners[vertex], planes: planeData.GetSpan()[planeStarts[vertex]..planeEnds[vertex]]);
            }
        }

        // Each piece's planes back to back, in its fine vertices' order: PlaneStart is where its run begins, and PlaneEnd
        // its end once the gather has filled it.
        var planeTotal = 0;

        for (var index = 0; (index < merged); index++) {
            ref var piece = ref pieces[index];
            var count = piece.PlaneEnd;

            piece.PlaneStart = piece.PlaneEnd = planeTotal;
            planeTotal += count;
        }

        var piecePlanes = scope.Buffer<double>(length: planeTotal);

        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            if ((owners[vertex] < 0) || (planeStarts[vertex] < 0)) { continue; }

            ref var piece = ref pieces[remap[vertex]];

            piece.PlaneEnd += faces.Keep(destination: piecePlanes.GetSpan()[piece.PlaneEnd..], owner: owners[vertex], planes: planeData.GetSpan()[planeStarts[vertex]..planeEnds[vertex]]);
        }

        // One scratch for every piece's planes in its block's unit coordinates (MergedFeature), as long as the longest run.
        var longestRun = 0;

        for (var index = 0; (index < merged); index++) { longestRun = Math.Max(val1: longestRun, val2: (pieces[index].PlaneEnd - pieces[index].PlaneStart)); }
        var localPlanes = scope.Buffer<double>(length: longestRun);
        using var mergedHandoffs = new OwnedBuffer<(int Vertex, double X, double Y, double Z)>(allocator: grid.Allocator, capacity: handoffs.Length, observer: grid.Workspace);
        var origin = grid.World(x: 0.0, y: 0.0, z: 0.0);
        var (unitX, unitY, unitZ) = grid.World(x: 1.0, y: 1.0, z: 1.0);
        var unit = (X: (unitX - origin.X), Y: (unitY - origin.Y), Z: (unitZ - origin.Z));

        foreach (var (vertex, x, y, z) in handoffs.GetSpan()) {
            mergedHandoffs.Add(item: (remap[vertex], x, y, z));
        }
        for (var index = 0; (index < merged); index++) {
            var piece = pieces[index];

            if (piece.Count == 0) { continue; }

            var root = piece.Root;
            var at = (3 * index);
            var (x, y, z) = ((piece.X / piece.Count), (piece.Y / piece.Count), (piece.Z / piece.Count));

            // A lattice whose fine vertices lie on its features (CrossingNormals) keeps a sharp edge or corner through a
            // merged piece: the vertex is the point every fine vertex's tangent plane passes through, where one lies in the
            // coarse cell, and the vertices' mean otherwise.
            // A convex crease past the coarse cell, up to FeatureReach of it, is the neighbour's to hold (HandOff), and the
            // piece's vertex stays inside the cell, as Place keeps a cell's.
            var meeting = (((piece.Count > 1) && piece.HasPlanes) ? MergedFeature(grid: grid, mean: (x, y, z), block: block(owners[root]), planes: piecePlanes.GetSpan().Slice(start: piece.PlaneStart, length: (piece.PlaneEnd - piece.PlaneStart)), local: localPlanes.GetSpan().Slice(start: 0, length: (piece.PlaneEnd - piece.PlaneStart))) : null);
            var feature = ((meeting is var (fx, fy, fz, past, _, _, _) && (past <= 0.0)) ? (fx, fy, fz) : ((double X, double Y, double Z)?)null);

            if ((feature is null) && (meeting is (var bx, var by, var bz, _, { } inside, var handOff, var besideCrease)) && piece.Feature && !faces.Splits(owner: owners[root])) {
                if (handOff) {
                    mergedHandoffs.Add(item: (index, ((bx - origin.X) / unit.X), ((by - origin.Y) / unit.Y), ((bz - origin.Z) / unit.Z)));
                }
                feature = inside;
                mergedBesides[index] = besideCrease;
            }
            (x, y, z) = (feature ?? (x, y, z));
            // A face split off a crease lies on that face alone, whatever crease its fine vertices touched.
            mergedFeatures[index] = (!faces.Splits(owner: owners[root]) && ((feature is not null) ? piece.Feature : ((piece.Count == 1) && features[root])));
            mergedSolved[index] = true;

            mergedPositions[at] = x;
            mergedPositions[(at + 1)] = y;
            mergedPositions[(at + 2)] = z;
            mergedAnchors[at] = (piece.AnchorX / piece.Count);
            mergedAnchors[(at + 1)] = (piece.AnchorY / piece.Count);
            mergedAnchors[(at + 2)] = (piece.AnchorZ / piece.Count);
            (mergedPatches[at], mergedPatches[(at + 1)], mergedPatches[(at + 2)]) = (piece.PatchX, piece.PatchY, piece.PatchZ);

            // The merged vertex's normal is its fine vertices' mean normal.
            var (sx, sy, sz) = (piece.NormalX, piece.NormalY, piece.NormalZ);
            var length = Math.Sqrt(d: (((sx * sx) + (sy * sy)) + (sz * sz)));

            if (length > 1e-12) {
                mergedNormals[at] = (sx / length);
                mergedNormals[(at + 1)] = (sy / length);
                mergedNormals[(at + 2)] = (sz / length);
            }
        }

        HandOff(besides: mergedBesides, features: mergedFeatures, grid: grid, handoffs: mergedHandoffs.GetSpan(), positions: mergedPositions.GetSpan(), solveds: mergedSolved, target: (x, y, z) => {
            var vertex = cellVertex(x, y, z);

            return ((vertex < 0) ? -1 : remap[vertex]);
        });

        // The vertices a quad keeps once its merged corners collapse, or zero for a quad that collapses to fewer than three.
        int Collapse(int quad, Span<int> distinct) {
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

            return (((count < 3) || ((count == 4) && ((distinct[0] == distinct[2]) || (distinct[1] == distinct[3])))) ? 0 : count);
        }

        Span<int> distinct = stackalloc int[4];
        var keptQuads = 0;

        for (var quad = 0; (quad < quadCorners.Length); quad++) {
            if (Collapse(quad: quad, distinct: distinct) > 0) { keptQuads++; }
        }

        var mergedQuads = new int[(4 * keptQuads)];
        var mergedCorners = new int[keptQuads];
        var mergedAxes = new sbyte[keptQuads];

        keptQuads = 0;
        for (var quad = 0; (quad < quadCorners.Length); quad++) {
            var count = Collapse(quad: quad, distinct: distinct);

            if (count == 0) { continue; }
            mergedQuads[(4 * keptQuads)] = distinct[0];
            mergedQuads[((4 * keptQuads) + 1)] = distinct[1];
            mergedQuads[((4 * keptQuads) + 2)] = distinct[2];
            mergedQuads[((4 * keptQuads) + 3)] = distinct[((count == 4) ? 3 : 2)];
            mergedCorners[keptQuads] = quadCorners[quad];
            mergedAxes[keptQuads] = quadAxes[quad];
            keptQuads++;
        }

        using var settled = new OwnedBuffer<int>(allocator: grid.Allocator, observer: grid.Workspace);

        Settle(anchors: mergedAnchors.GetSpan(), axes: mergedAxes, besides: mergedBesides, features: mergedFeatures, patches: mergedPatches.GetSpan(), positions: mergedPositions.GetSpan(), quads: mergedQuads, returned: settled, solveds: mergedSolved);
        returned = null;
        foreach (var vertex in settled.GetSpan()) {
            if (pieces[vertex].Count > 1) { (returned ??= []).Add(item: pieces[vertex].Root); }
        }

        return new SdfSurface {
            Features = mergedFeatures,
            Beside = mergedBesides,
            Solved = mergedSolved,
            Normals = Narrowed(values: mergedNormals.GetSpan()),
            Patches = Narrowed(values: mergedPatches.GetSpan()),
            Positions = Narrowed(values: mergedPositions.GetSpan()),
            QuadAxes = mergedAxes,
            QuadCorners = mergedCorners,
            Quads = mergedQuads,
        };
    }
    // The pieces of the blocks' surfaces: two fine vertices of one owner are one piece where a quad edge joins them.
    private static SdfDisjointSet Pieces(OwnedBuffer<int> owners, OwnedBuffer<int> quads, IAllocationObserver? workspace) {
        var sets = new SdfDisjointSet(count: owners.Length, owner: workspace);

        for (var quad = 0; (quad < quads.Length); quad += 4) {
            for (var corner = 0; (corner < 4); corner++) {
                var a = quads[(quad + corner)];
                var b = quads[(quad + ((corner + 1) & 3))];

                if ((owners[a] >= 0) && (owners[a] == owners[b])) {
                    sets.Union(first: a, second: b);
                }
            }
        }

        return sets;
    }
    // Splits each piece `unheld` names (by its root) whose fine crossings' planes meet along a crease past its block
    // into one piece per face, giving each face's fine vertices an owner of its own (CreaseFaces), and returns those
    // owners; none where `unheld` is null. Merge names the pieces whose merged vertex its settle returned. A block's
    // piece merges into one vertex, which lies on the crease where the crease lies in the block (MergedFeature); a piece
    // holding both faces of a crease the block does not hold has no point that lies on both, and its one vertex,
    // wherever it is put, lies on one face (or between them, inside the solid) while its quads reach the other: past the
    // fine vertices the neighbouring cells place between the block and the crease, it turns their shared triangles over,
    // so the settle returns it to its crossings' mean, inside the solid, and the field check then moves it onto the
    // nearer face, where the triangles toward the other face turn against the field. Where two solids touch along a line
    // (an XOR's pinch), the block beside the line sees both faces of one solid's wedge, whose edge is the line. A face's
    // piece keeps its vertex on that face; the quads that joined the two faces' fine vertices join the two faces'
    // vertices across the block, short of the crease the cells holding it place their vertices on. A fine vertex belongs
    // to the face most of its crossings' planes face nearer, or, crossing neither, to the face whose plane passes nearer.
    private static CreaseFaces SplitAtCreases<TLattice>(TLattice grid, OwnedBuffer<int> owners, OwnedBuffer<int> quads, OwnedBuffer<double> positions, OwnedBuffer<double> planeData, OwnedBuffer<int> planeStarts, OwnedBuffer<int> planeEnds, Func<int, (int X, int Y, int Z, int Size)> block, IReadOnlySet<int>? unheld) where TLattice : ISdfRefinedContourLattice {
        var vertexCount = owners.Length;
        var first = 0;

        for (var vertex = 0; (vertex < vertexCount); vertex++) { first = Math.Max(val1: first, val2: (owners[vertex] + 1)); }

        var faces = new CreaseFaces(first: first);

        if ((unheld is null) || !grid.CrossingNormals) { return faces; }

        using var sets = Pieces(owners: owners, quads: quads, workspace: grid.Workspace);
        // Each piece's fine vertices back to back, pieces by their roots' order, each in vertex order.
        using var scope = new RentedScope(allocator: grid.Allocator, owner: grid.Workspace);
        var starts = scope.Buffer<int>(length: (vertexCount + 1));
        var members = scope.Buffer<int>(length: vertexCount);

        starts.GetSpan().Clear();
        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            if (owners[vertex] >= 0) { starts[(sets.Find(element: vertex) + 1)]++; }
        }
        for (var root = 0; (root < vertexCount); root++) { starts[(root + 1)] += starts[root]; }
        var fill = scope.Buffer<int>(length: vertexCount);

        starts.GetSpan().Slice(start: 0, length: vertexCount).CopyTo(destination: fill.GetSpan());
        for (var vertex = 0; (vertex < vertexCount); vertex++) {
            if (owners[vertex] >= 0) { members[fill[sets.Find(element: vertex)]++] = vertex; }
        }

        using var planes = new OwnedBuffer<double>(allocator: grid.Allocator, observer: grid.Workspace);
        using var local = new OwnedBuffer<double>(allocator: grid.Allocator, observer: grid.Workspace);

        for (var root = 0; (root < vertexCount); root++) {
            var piece = members.GetSpan().Slice(start: starts[root], length: (starts[(root + 1)] - starts[root]));

            if ((piece.Length < 2) || !unheld.Contains(item: root)) { continue; }

            var (mx, my, mz) = (0.0, 0.0, 0.0);

            planes.Clear();
            foreach (var vertex in piece) {
                (mx, my, mz) = ((mx + positions[(3 * vertex)]), (my + positions[((3 * vertex) + 1)]), (mz + positions[((3 * vertex) + 2)]));
                if (planeStarts[vertex] >= 0) { planes.AddRange(items: planeData.GetSpan()[planeStarts[vertex]..planeEnds[vertex]]); }
            }
            if (planes.Length == 0) { continue; }
            local.Clear();
            for (var at = 0; (at < planes.Length); at++) { local.Add(item: 0.0); }

            var owner = owners[root];
            var meeting = MergedFeature(block: block(owner), grid: grid, local: local.GetSpan(), mean: ((mx / piece.Length), (my / piece.Length), (mz / piece.Length)), planes: planes.GetSpan());

            if (meeting is not { Past: > 0.0 }) { continue; }

            var (faceA, faceB) = Faces(planes: local.GetSpan());

            if (faceA < 0) { continue; }

            var all = planes.GetSpan();
            var a = (X: all[(4 * faceA)], Y: all[((4 * faceA) + 1)], Z: all[((4 * faceA) + 2)], D: all[((4 * faceA) + 3)]);
            var b = (X: all[(4 * faceB)], Y: all[((4 * faceB) + 1)], Z: all[((4 * faceB) + 2)], D: all[((4 * faceB) + 3)]);
            var (onA, onB) = (0, 0);

            // Each fine vertex's face, held as its sign in `fill` (free once the members are listed): on A, or on B.
            foreach (var vertex in piece) {
                var votes = 0;

                for (var plane = Math.Max(val1: 0, val2: planeStarts[vertex]); (planeStarts[vertex] >= 0) && (plane < planeEnds[vertex]); plane += 4) {
                    var (nx, ny, nz) = (planeData[plane], planeData[(plane + 1)], planeData[(plane + 2)]);

                    votes += ((((nx * a.X) + (ny * a.Y)) + (nz * a.Z)) >= (((nx * b.X) + (ny * b.Y)) + (nz * b.Z))) ? 1 : -1;
                }
                if (votes == 0) {
                    var (px, py, pz) = (positions[(3 * vertex)], positions[((3 * vertex) + 1)], positions[((3 * vertex) + 2)]);

                    votes = (Math.Abs(value: ((((a.X * px) + (a.Y * py)) + (a.Z * pz)) - a.D)) <= Math.Abs(value: ((((b.X * px) + (b.Y * py)) + (b.Z * pz)) - b.D))) ? 1 : -1;
                }
                fill[vertex] = votes;
                (onA, onB) = ((votes > 0) ? ((onA + 1), onB) : (onA, (onB + 1)));
            }
            if ((onA == 0) || (onB == 0)) { continue; }

            var ownerA = faces.Add(block: owner, face: (a.X, a.Y, a.Z), other: (b.X, b.Y, b.Z));
            var ownerB = faces.Add(block: owner, face: (b.X, b.Y, b.Z), other: (a.X, a.Y, a.Z));

            foreach (var vertex in piece) { owners[vertex] = ((fill[vertex] > 0) ? ownerA : ownerB); }
        }

        return faces;
    }
    // The owners SplitAtCreases gives the faces it splits off their pieces, numbered from the first number past every
    // block's owner: per face, the block it stands in, its normal and the other face's, by which its fine vertices'
    // crossings' planes are kept for its vertex (a plane facing nearer the other face is that face's).
    private sealed class CreaseFaces(int first) {
        private readonly List<(int Block, (double X, double Y, double Z) Face, (double X, double Y, double Z) Other)> m_faces = [];

        public int Count => m_faces.Count;

        public int Add(int block, (double X, double Y, double Z) face, (double X, double Y, double Z) other) {
            m_faces.Add(item: (block, face, other));

            return (first + m_faces.Count - 1);
        }
        // The block an owner stands in: its own, or for a face split off a piece, that piece's.
        public int BlockOf(int owner) => (Splits(owner: owner) ? m_faces[(owner - first)].Block : owner);
        public bool Splits(int owner) => (owner >= first);
        // The planes of `planes` (nx, ny, nz, d each) a vertex of `owner` keeps, all of them or a split face's own, copied
        // to `destination` unless it is empty, and how many values they hold.
        public int Keep(int owner, ReadOnlySpan<double> planes, Span<double> destination) {
            var kept = 0;

            for (var plane = 0; (plane < planes.Length); plane += 4) {
                if (Splits(owner: owner) && !Keeps(owner: owner, plane: planes.Slice(start: plane, length: 4))) { continue; }
                if (!destination.IsEmpty) { planes.Slice(start: plane, length: 4).CopyTo(destination: destination[kept..]); }
                kept += 4;
            }

            return kept;
        }
        private bool Keeps(int owner, ReadOnlySpan<double> plane) {
            var (_, face, other) = m_faces[(owner - first)];

            return ((((plane[0] * face.X) + (plane[1] * face.Y)) + (plane[2] * face.Z)) >= (((plane[0] * other.X) + (plane[1] * other.Y)) + (plane[2] * other.Z)));
        }
    }
    // The values rounded to single precision, in an array of exactly their count.
    private static float[] Narrowed(ReadOnlySpan<double> values) {
        var narrowed = new float[values.Length];

        for (var index = 0; (index < values.Length); index++) { narrowed[index] = ((float)values[index]); }
        return narrowed;
    }
    // One merged vertex's piece, while Merge gathers it: its fine vertices' count and summed positions, crossings' means,
    // patch facings and normals, its root vertex, whether any fine vertex lies on a crease or records tangent planes, and
    // its planes' run in the gathered planes.
    private struct Piece {
        public int Count;
        public int Root;
        public double X, Y, Z;
        public double AnchorX, AnchorY, AnchorZ;
        public double PatchX, PatchY, PatchZ;
        public double NormalX, NormalY, NormalZ;
        public bool Feature;
        public bool HasPlanes;
        public int PlaneStart;
        public int PlaneEnd;
    }
    // Moves each convex crease a vertex's planes meet past its cell (Place's `beyond`, or a merged piece's past its
    // coarse cell) onto the vertex of the cell that holds it, `target`'s answer for the point in lattice coordinates:
    // that vertex takes the mean of the points handed to it, which lie on the crease, and becomes a crease vertex. A
    // cell's vertex holds one surface patch, which the crease bounds, so the vertex still lies on its patch there. Where
    // a crease the samples step across diagonally (an acute rim's, a tilted circle's) lies past the cell that sees both
    // its faces, the cell that holds it sees only one, and without the move no vertex lies on the crease there: the mesh
    // cuts its corner in a notch. A target that holds a crease of its own, a cell of several patches and a cell the
    // surface misses take none. A vertex that hands its crease over stays inside its cell and keeps its
    // crease mark, since its quads turn across the crease.
    private static void HandOff<TLattice>(TLattice grid, Span<double> positions, Span<bool> features, Span<bool> solveds, Span<bool> besides, ReadOnlySpan<(int Vertex, double X, double Y, double Z)> handoffs, Func<double, double, double, int> target) where TLattice : ISdfContourLattice {
        if (handoffs.Length == 0) {
            return;
        }

        var sources = new HashSet<int>();

        foreach (var handoff in handoffs) {
            _ = sources.Add(item: handoff.Vertex);
        }

        var sums = new Dictionary<int, (double X, double Y, double Z, int Count)>();

        foreach (var (vertex, x, y, z) in handoffs) {
            var to = target(x, y, z);

            if ((to < 0) || (to == vertex) || (features[to] && !sources.Contains(item: to))) {
                continue;
            }

            var (wx, wy, wz) = grid.World(x: x, y: y, z: z);
            var sum = sums.GetValueOrDefault(key: to);

            sums[to] = ((sum.X + wx), (sum.Y + wy), (sum.Z + wz), (sum.Count + 1));
        }
        foreach (var (to, sum) in sums) {
            positions[(3 * to)] = (sum.X / sum.Count);
            positions[((3 * to) + 1)] = (sum.Y / sum.Count);
            positions[((3 * to) + 2)] = (sum.Z / sum.Count);
            features[to] = true;
            solveds[to] = true;
            besides[to] = false;
        }
    }
    // Holds a contour to its lattice's orientation: a quad none of whose splits faces out (SdfContourFacing.Splits:
    // along the lattice edge it crosses, or along its corners' patches where it lies edge-on to that edge) has a vertex
    // placed past the region its quads allow or squashed into its neighbours' face, and the vertices its
    // planes, a hand-off or a merge moved return to their crossings' mean (`anchors`), which lies in the vertex's own
    // cell: the one of them whose return alone lets the quad face out, the farthest moved first, or all of them. The four
    // cells around a lattice edge lie in its four quadrants, so a quad whose vertices each lie in their own cell always
    // has a split that faces out; a merged piece's vertex stands for several cells and has no such region, so it is the
    // check alone that holds its triangles. A vertex returned is pinned by no crossing (`solveds`), so the bake's field
    // check moves it onto the surface, and is no crease; `returned`, when given, lists each vertex returned.
    private static void Settle(Span<double> positions, ReadOnlySpan<double> anchors, ReadOnlySpan<double> patches, ReadOnlySpan<int> quads, ReadOnlySpan<sbyte> axes, Span<bool> features, Span<bool> solveds, Span<bool> besides, OwnedBuffer<int>? returned = null) {
        Span<int> moved = stackalloc int[4];
        Span<double> held = stackalloc double[3];

        static bool Moved(Span<double> positions, ReadOnlySpan<double> anchors, int vertex) =>
            ((positions[(3 * vertex)] != anchors[(3 * vertex)]) || (positions[((3 * vertex) + 1)] != anchors[((3 * vertex) + 1)]) || (positions[((3 * vertex) + 2)] != anchors[((3 * vertex) + 2)]));
        static void Return(Span<double> positions, ReadOnlySpan<double> anchors, Span<bool> features, Span<bool> solveds, Span<bool> besides, int vertex) {
            anchors.Slice(start: (3 * vertex), length: 3).CopyTo(destination: positions[(3 * vertex)..]);
            features[vertex] = false;
            besides[vertex] = false;
            solveds[vertex] = true;
        }

        for (var round = 0; (round < SettleRounds); round++) {
            var any = false;

            for (var quad = 0; (quad < axes.Length); quad++) {
                var corners = quads.Slice(start: (4 * quad), length: 4);

                if (SdfContourFacing.Splits(axis: axes[quad], patches: patches, positions: positions, quad: corners) is not (false, false)) { continue; }

                var count = 0;

                for (var corner = 0; (corner < ((corners[2] == corners[3]) ? 3 : 4)); corner++) {
                    if (Moved(positions: positions, anchors: anchors, vertex: corners[corner])) { moved[count++] = corners[corner]; }
                }
                if (count == 0) { continue; }
                any = true;

                // The one vertex whose return lets the quad face out, the farthest from its crossings' mean first.
                var alone = -1;
                var farthest = -1.0;

                for (var index = 0; (index < count); index++) {
                    var vertex = moved[index];
                    var slot = positions.Slice(start: (3 * vertex), length: 3);
                    var (dx, dy, dz) = ((slot[0] - anchors[(3 * vertex)]), (slot[1] - anchors[((3 * vertex) + 1)]), (slot[2] - anchors[((3 * vertex) + 2)]));
                    var distance = (((dx * dx) + (dy * dy)) + (dz * dz));

                    if (distance <= farthest) { continue; }
                    slot.CopyTo(destination: held);
                    anchors.Slice(start: (3 * vertex), length: 3).CopyTo(destination: slot);
                    if (SdfContourFacing.Splits(axis: axes[quad], patches: patches, positions: positions, quad: corners) is not (false, false)) {
                        alone = vertex;
                        farthest = distance;
                    }
                    held.CopyTo(destination: slot);
                }
                if (alone >= 0) {
                    Return(positions: positions, anchors: anchors, features: features, solveds: solveds, besides: besides, vertex: alone);
                    returned?.Add(item: alone);
                    continue;
                }
                for (var index = 0; (index < count); index++) {
                    Return(positions: positions, anchors: anchors, features: features, solveds: solveds, besides: besides, vertex: moved[index]);
                    returned?.Add(item: moved[index]);
                }
            }
            if (!any) { break; }
        }
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
        using var patchSets = new SdfDisjointSet(count: CellEdges.Length);
        var changes = new bool[CellEdges.Length];

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            changes[edge] = ((((mask >> CellEdges[edge].A) ^ (mask >> CellEdges[edge].B)) & 1) != 0);
        }

        int Root(int edge) => patchSets.Find(element: edge);
        void Join(int first, int second) => patchSets.Union(first: first, second: second);

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
    // The lattice's crossing on a sign-changing edge, as a fraction from its first corner. A sampled lattice refines
    // this root along the edge before measuring its normal; moving a linear estimate along its normal can find another
    // face of the solid instead. A cell `stride` lattice cells wide reads its corners `stride` samples apart.
    private static double Crossing<TLattice>(TLattice grid, int x, int y, int z, int a, int b, out int index, int stride = 1) where TLattice : ISdfContourLattice {
        var (ax, ay, az) = Offset(corner: a);

        return grid.Crossing(index: out index, axis: BitOperations.TrailingZeroCount(value: (a ^ b)), length: stride, x: (x + (ax * stride)), y: (y + (ay * stride)), z: (z + (az * stride)));
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
    // `worldPlanes`, when given, takes the crossings' tangent planes in world space (nx, ny, nz, d each) for Merge.
    // `anchor` is the crossings' mean, which lies in the cell.
    private static (double X, double Y, double Z) Place<TLattice>(TLattice grid, int edges, int x, int y, int z, out (double X, double Y, double Z) crossingNormal, out bool feature, out bool flat, out bool solved, out (double X, double Y, double Z)? beyond, out bool beside, out (double X, double Y, double Z) anchor, int stride = 1, OwnedBuffer<double>? worldPlanes = null) where TLattice : ISdfContourLattice {
        double sxMean = 0, syMean = 0, szMean = 0;
        var crossings = 0;
        beyond = null;
        beside = false;
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
        Span<double> planes = stackalloc double[(4 * CellEdges.Length)];
        // Each plane's unit normal in world space, which the vertex normal reads (NearNormal).
        Span<double> crossingNormals = stackalloc double[(3 * CellEdges.Length)];
        var planeCount = 0;


        crossingNormal = (0.0, 0.0, 0.0);
        feature = false;
        solved = false;

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if ((edges & (1 << edge)) == 0) {
                continue;
            }

            var (a, b) = CellEdges[edge];
            var t = Crossing(index: out var crossingIndex, a: a, b: b, grid: grid, stride: stride, x: x, y: y, z: z);

            var (ax, ay, az) = Offset(corner: a);
            var (bx, by, bz) = Offset(corner: b);
            var px = (ax + (t * (bx - ax)));
            var py = (ay + (t * (by - ay)));
            var pz = (az + (t * (bz - az)));

            var (wx, wy, wz) = grid.World(x: (x + (px * stride)), y: (y + (py * stride)), z: (z + (pz * stride)));

            sxMean += px;
            syMean += py;
            szMean += pz;
            crossings++;
            if (!grid.TryCrossingNormal(
                index: crossingIndex,
                length: stride,
                nx: out var nx,
                ny: out var ny,
                nz: out var nz,
                offset: out var offset,
                wx: wx,
                wy: wy,
                wz: wz
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

                worldPlanes.AddRange(items: [nx, ny, nz, distance]);
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

        var mean = (X: sxMean / crossings, Y: syMean / crossings, Z: szMean / crossings);
        anchor = mean;
        var cell = new CellSolve(A00: a00, A01: a01, A02: a02, A11: a11, A12: a12, A22: a22, B0: b0, B1: b1, B2: b2, Mean: mean);
        var cellPlanes = planes[..(4 * planeCount)];
        var result = mean;
        var ownFace = -1;

        if (cell.Solve(bias: MeanBias, x: out var sx, y: out var sy, z: out var sz)) {
            var inside = ((sx >= 0.0) && (sx <= 1.0) && (sy >= 0.0) && (sy <= 1.0) && (sz >= 0.0) && (sz <= 1.0));
            var placed = false;

            // Where every plane passes through one point of the cell, the vertex is that point, solved again with a bias
            // only the planes' common null space (an edge's direction) feels: the mean-point bias draws it off its planes
            // toward the crossings' mean by a few hundredths of a cell, which along a crease between two faces leaves the
            // vertices stepping off the crease from cell to cell. Only a lattice whose crossings lie on the surface
            // (FeatureReach) can tell a point every plane passes through from planes that merely meet. A point that two of
            // its planes reach turned by more than the crease angle is a crease or corner of the surface (`feature`). The
            // point is taken inside the cell, or within FeatureResidual of it, as near as the planes are known to meet. A
            // convex crease up to FeatureReach past the cell, curved or straight, is the neighbour's that holds it
            // (`beyond`, HandOff), and this cell's vertex stays inside the cell (CellSolve.Inside, BesideCrease): one
            // taken past its cell lands beside the neighbour's and folds the quads they share into teeth, and one taken
            // at the mean-biased point past the cell sits a few hundredths of a cell off the crease the neighbour's lies
            // on, a sliver strip between two rows of crease vertices. A crease within TouchedCrease of the cell that the
            // point inside it already lies on stays this cell's: handed over, it would drag the neighbour's vertex along
            // the crease and leave the two a sliver apart. A curved face's crossings that turn more (a tube a cell or two
            // across) do not all pass through one point, and none of this applies.
            if ((grid.FeatureReach > 0.0) && cell.Solve(bias: FeatureBias, x: out var fx, y: out var fy, z: out var fz) &&
                Meets(x: fx, y: fy, z: fz, planes: cellPlanes)) {
                var (faceA, faceB) = Faces(planes: cellPlanes);
                var past = (Math.Max(val1: Math.Abs(value: (fx - 0.5)), val2: Math.Max(val1: Math.Abs(value: (fy - 0.5)), val2: Math.Abs(value: (fz - 0.5)))) - 0.5);

                feature = (faceA >= 0);
                // A crease farther off still marks the vertex, whose placement falls back below.
                if ((past <= 0.0) || (feature && (past <= FeatureResidual))) {
                    result = (fx, fy, fz);
                    placed = true;
                } else if (feature && (past <= grid.FeatureReach) && ConvexCrease(faceA: faceA, faceB: faceB, planes: cellPlanes, point: mean)) {
                    beside = true;
                    result = BesideCrease(crease: (fx, fy, fz), faceA: faceA, faceB: faceB, held: cell.Inside(high: (1.0, 1.0, 1.0), low: (0.0, 0.0, 0.0), planes: cellPlanes, x: sx, y: sy, z: sz), mean: mean, ownFace: out ownFace, planes: cellPlanes);
                    if ((ownFace < 0) || (past > TouchedCrease)) {
                        beyond = (fx, fy, fz);
                    }
                    placed = true;
                }
            }
            // A sharp feature whose faces' planes meet just past the cell (an edge the next cell holds but whose crossings
            // only this cell sees, a concave crease's) keeps the point they meet at, where clamping would cut the feature
            // off, while the point lies in the region the patch's quads allow (Within): past the cell only through a
            // face the patch does not cross. A point past a face the patch crosses lies among the cells its quads join
            // it to, where the vertex would turn those quads over, so this cell's vertex clamps, as it does for a point
            // the planes do not all pass through, which is no feature, and for a convex crease the neighbour holds
            // (`beyond`). A clamped vertex keeps clear of the faces its patch crosses (Clear).
            if (!placed) {
                var reaches = (!inside && (beyond is null) && (Math.Max(val1: Math.Abs(value: (sx - 0.5)), val2: Math.Max(val1: Math.Abs(value: (sy - 0.5)), val2: Math.Abs(value: (sz - 0.5)))) <= (0.5 + grid.FeatureReach)) &&
                    Meets(x: sx, y: sy, z: sz, planes: cellPlanes) && Within(edges: edges, x: sx, y: sy, z: sz));

                if (reaches) {
                    result = (sx, sy, sz);
                } else {
                    var (low, high) = Clear(edges: edges, mean: mean);

                    result = cell.Inside(high: high, low: low, planes: cellPlanes, x: sx, y: sy, z: sz);
                }
            }
        }

        // The crossings' mean lies on the surface where it is flat across the cell and within it where it curves; a vertex
        // the planes draw away from it (to their meeting point, or past the cell and back) may lie proud of a surface that
        // rounds within the cell.
        solved = (((((result.X - mean.X) * (result.X - mean.X)) + ((result.Y - mean.Y) * (result.Y - mean.Y))) + ((result.Z - mean.Z) * (result.Z - mean.Z))) > (SolvedDrift * SolvedDrift));
        if (ownFace >= 0) {
            var (ox, oy, oz) = (0.0, 0.0, 0.0);

            for (var plane = 0; (plane < planeCount); plane++) {
                if ((((planes[(4 * plane)] * planes[(4 * ownFace)]) + (planes[((4 * plane) + 1)] * planes[((4 * ownFace) + 1)])) + (planes[((4 * plane) + 2)] * planes[((4 * ownFace) + 2)])) >= CreaseCosine) {
                    (ox, oy, oz) = ((ox + crossingNormals[(3 * plane)]), (oy + crossingNormals[((3 * plane) + 1)]), (oz + crossingNormals[((3 * plane) + 2)]));
                }
            }
            crossingNormal = (ox, oy, oz);
        } else {
            crossingNormal = NearNormal(all: crossingNormal, normals: crossingNormals[..(3 * planeCount)], planes: planes[..(4 * planeCount)], point: result);
        }
        flat = Flat(normals: crossingNormals[..(3 * planeCount)]);

        return result;
    }
    // Whether a point in a cell's unit coordinates lies in the region the quads of the patch whose cube edges are `edges`
    // allow its vertex: behind every face of the cell that holds one of those edges running across it. A quad joins the
    // four cells around a sign-changing lattice edge, one in each quadrant about the edge's line, and faces out along the
    // edge while each vertex keeps to its own quadrant, so an edge bounds the vertex along the two axes it does not run
    // along, on its own side of the cell; a face the patch does not cross bounds nothing.
    private static bool Within(int edges, double x, double y, double z) {
        ReadOnlySpan<double> point = [x, y, z];

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if ((edges & (1 << edge)) == 0) { continue; }

            var (a, b) = CellEdges[edge];
            var along = BitOperations.TrailingZeroCount(value: (a ^ b));

            for (var axis = 0; (axis < 3); axis++) {
                if ((axis != along) && ((((a >> axis) & 1) == 0) ? (point[axis] < 0.0) : (point[axis] > 1.0))) { return false; }
            }
        }

        return true;
    }
    // The box of a cell's unit coordinates a vertex no feature pins keeps to (CellSolve.Inside): the cell, less
    // CrossedClear of it along each face that holds one of the patch's edges (`edges`) running across it, the faces
    // Within bounds it by. A vertex on such a face lies on the edge of its quadrant about that edge's line: the quad it
    // shares with the cell across the face has no width there, and which way its triangles turn is the two vertices'
    // rounding. A face the crossings' mean itself lies nearer than that keeps the mean's distance.
    private static ((double X, double Y, double Z) Low, (double X, double Y, double Z) High) Clear(int edges, (double X, double Y, double Z) mean) {
        Span<double> low = [0.0, 0.0, 0.0];
        Span<double> high = [1.0, 1.0, 1.0];
        ReadOnlySpan<double> held = [mean.X, mean.Y, mean.Z];

        for (var edge = 0; (edge < CellEdges.Length); edge++) {
            if ((edges & (1 << edge)) == 0) { continue; }

            var (a, b) = CellEdges[edge];
            var along = BitOperations.TrailingZeroCount(value: (a ^ b));

            for (var axis = 0; (axis < 3); axis++) {
                if (axis == along) { continue; }
                if (((a >> axis) & 1) == 0) {
                    low[axis] = Math.Min(val1: CrossedClear, val2: held[axis]);
                } else {
                    high[axis] = Math.Max(val1: (1.0 - CrossedClear), val2: held[axis]);
                }
            }
        }

        return ((low[0], low[1], low[2]), (high[0], high[1], high[2]));
    }
    // A cell's tangent planes as their normal equations (the six distinct entries A and the right-hand side B, in the
    // cell's unit coordinates) and its crossings' mean, which a bias on the diagonal draws the solution toward.
    private readonly record struct CellSolve(double A00, double A01, double A02, double A11, double A12, double A22, double B0, double B1, double B2, (double X, double Y, double Z) Mean) {
        // The least-squares point of the planes with `bias` on the diagonal drawing it toward the crossings' mean, by
        // Cramer's rule; false where the system is singular.
        public bool Solve(double bias, out double x, out double y, out double z) {
            var (m00, m11, m22) = ((A00 + bias), (A11 + bias), (A22 + bias));
            var (r0, r1, r2) = ((B0 + (bias * Mean.X)), (B1 + (bias * Mean.Y)), (B2 + (bias * Mean.Z)));
            var det = (((m00 * ((m11 * m22) - (A12 * A12))) - (A01 * ((A01 * m22) - (A12 * A02)))) + (A02 * ((A01 * A12) - (m11 * A02))));

            x = ((((r0 * ((m11 * m22) - (A12 * A12))) - (A01 * ((r1 * m22) - (A12 * r2)))) + (A02 * ((r1 * A12) - (m11 * r2)))) / det);
            y = ((((m00 * ((r1 * m22) - (A12 * r2))) - (r0 * ((A01 * m22) - (A12 * A02)))) + (A02 * ((A01 * r2) - (r1 * A02)))) / det);
            z = ((((m00 * ((m11 * r2) - (r1 * A12))) - (A01 * ((A01 * r2) - (r1 * A02)))) + (r0 * ((A01 * A12) - (m11 * A02)))) / det);

            return (Math.Abs(value: det) >= 1e-12);
        }
        // The point within the box `low` to `high` of the cell (the cell itself, or the box Clear keeps a vertex to) for a
        // solution (x, y, z) past it: of the least-squares point with each coordinate past
        // the box held at the box's face (Bounded) and the point the segment from the crossings' mean toward the solution
        // leaves the box at (Clip), the one nearer the planes. Clamping each coordinate on its own leaves the free ones
        // where the unconstrained solve put them, off the planes, and along an edge the cells cannot resolve steps the
        // vertices into a staircase aligned with the lattice; the segment keeps them near the surface the crossings sample,
        // and the held solve keeps a corner the cell's faces cut on the corner.
        public (double X, double Y, double Z) Inside(double x, double y, double z, ReadOnlySpan<double> planes, (double X, double Y, double Z) low, (double X, double Y, double Z) high) {
            var held = Bounded(high: high, low: low, x: x, y: y, z: z);
            var clipped = Clip(from: Mean, high: high, low: low, to: (x, y, z));

            return ((Residual(point: clipped, planes: planes) < Residual(point: held, planes: planes)) ? clipped : held);
        }
        private (double X, double Y, double Z) Bounded(double x, double y, double z, (double X, double Y, double Z) low, (double X, double Y, double Z) high) {
            Span<double> point = [x, y, z];
            Span<bool> held = [false, false, false];
            ReadOnlySpan<double> least = [low.X, low.Y, low.Z];
            ReadOnlySpan<double> most = [high.X, high.Y, high.Z];

            for (var round = 0; (round < 3); round++) {
                var any = false;

                for (var axis = 0; (axis < 3); axis++) {
                    if (!held[axis] && ((point[axis] < least[axis]) || (point[axis] > most[axis]))) {
                        point[axis] = Math.Clamp(value: point[axis], min: least[axis], max: most[axis]);
                        held[axis] = true;
                        any = true;
                    }
                }
                if (!any) {
                    break;
                }
                SolveHeld(held: held, point: point);
            }

            return (Math.Clamp(value: point[0], min: least[0], max: most[0]), Math.Clamp(value: point[1], min: least[1], max: most[1]), Math.Clamp(value: point[2], min: least[2], max: most[2]));
        }
        // Solves the mean-biased normal equations for the coordinates not `held`, the held ones fixed at their values in
        // `point`.
        private void SolveHeld(ReadOnlySpan<bool> held, Span<double> point) {
            Span<double> m = [(A00 + MeanBias), A01, A02, A01, (A11 + MeanBias), A12, A02, A12, (A22 + MeanBias)];
            Span<double> r = [(B0 + (MeanBias * Mean.X)), (B1 + (MeanBias * Mean.Y)), (B2 + (MeanBias * Mean.Z))];
            Span<int> free = stackalloc int[3];
            var count = 0;

            for (var axis = 0; (axis < 3); axis++) {
                if (held[axis]) {
                    for (var row = 0; (row < 3); row++) {
                        r[row] -= (m[((3 * row) + axis)] * point[axis]);
                    }
                } else {
                    free[count++] = axis;
                }
            }
            if (count == 1) {
                var a = free[0];

                point[a] = (r[a] / m[((3 * a) + a)]);
            } else if (count == 2) {
                var (a, b) = (free[0], free[1]);
                var (p, q, t) = (m[((3 * a) + a)], m[((3 * a) + b)], m[((3 * b) + b)]);
                var det = ((p * t) - (q * q));

                if (Math.Abs(value: det) > 1e-12) {
                    point[a] = (((r[a] * t) - (q * r[b])) / det);
                    point[b] = (((p * r[b]) - (q * r[a])) / det);
                }
            }
        }
        private static double Residual((double X, double Y, double Z) point, ReadOnlySpan<double> planes) {
            var sum = 0.0;

            for (var plane = 0; (plane < planes.Length); plane += 4) {
                var distance = ((((planes[plane] * point.X) + (planes[(plane + 1)] * point.Y)) + (planes[(plane + 2)] * point.Z)) - planes[(plane + 3)]);

                sum += (distance * distance);
            }

            return sum;
        }
        // The point the segment `from` to `to` leaves the box `low` to `high` at, or `to` within it; `from` lies in the box.
        public static (double X, double Y, double Z) Clip((double X, double Y, double Z) from, (double X, double Y, double Z) to, (double X, double Y, double Z) low, (double X, double Y, double Z) high) {
            var t = 1.0;

            t = Math.Min(val1: t, val2: Reach(from: from.X, high: high.X, low: low.X, to: to.X));
            t = Math.Min(val1: t, val2: Reach(from: from.Y, high: high.Y, low: low.Y, to: to.Y));
            t = Math.Min(val1: t, val2: Reach(from: from.Z, high: high.Z, low: low.Z, to: to.Z));

            return ((from.X + (t * (to.X - from.X))), (from.Y + (t * (to.Y - from.Y))), (from.Z + (t * (to.Z - from.Z))));

            static double Reach(double from, double to, double low, double high) => ((to > high) ? ((high - from) / (to - from)) : ((to < low) ? ((from - low) / (from - to)) : 1.0));
        }
    }
    // The point of a cell beside a convex crease past it (Place), from the cell's in-cell point `held`
    // (CellSolve.Inside): where `held` lies within NearPlane of both faces, a few hundredths of a cell inside the solid
    // beside the crease vertex the neighbour holds (HandOff), it moves onto the plane of the face the crossings'
    // mean lies nearer (`ownFace`, whose crossings give the vertex its normal), so the strip between the two vertices lies
    // on that face rather than folding across the corner; else it keeps to the crossings' side of the crease (OwnSide).
    private static (double X, double Y, double Z) BesideCrease((double X, double Y, double Z) crease, (double X, double Y, double Z) held, (double X, double Y, double Z) mean, int faceA, int faceB, ReadOnlySpan<double> planes, out int ownFace) {
        if (!OnBoth(faceA: faceA, faceB: faceB, planes: planes, point: held)) {
            ownFace = -1;

            return OwnSide(crease: crease, faceA: faceA, faceB: faceB, held: held, mean: mean, planes: planes);
        }

        ownFace = ((Off(face: faceA, planes: planes, point: mean) <= Off(face: faceB, planes: planes, point: mean)) ? faceA : faceB);

        var (nx, ny, nz, d) = (planes[(4 * ownFace)], planes[((4 * ownFace) + 1)], planes[((4 * ownFace) + 2)], planes[((4 * ownFace) + 3)]);
        var off = ((((nx * held.X) + (ny * held.Y)) + (nz * held.Z)) - d);

        return (Math.Clamp(value: (held.X - (off * nx)), min: 0.0, max: 1.0), Math.Clamp(value: (held.Y - (off * ny)), min: 0.0, max: 1.0), Math.Clamp(value: (held.Z - (off * nz)), min: 0.0, max: 1.0));
    }
    // The in-cell point `held` (CellSolve.Inside) of a cell beside a convex crease past it, kept on the side of the crease
    // the cell's crossings hold: where it lies clearly nearer one face's plane (by SolvedDrift) and the crossings' mean
    // clearly nearer the other's (by NearPlane), it has crossed past the neighbour's vertex on the crease and folds the
    // quads they share, and the point is instead where the segment from the mean toward the crease leaves the cell.
    private static (double X, double Y, double Z) OwnSide((double X, double Y, double Z) crease, (double X, double Y, double Z) held, (double X, double Y, double Z) mean, int faceA, int faceB, ReadOnlySpan<double> planes) {
        var (meanA, meanB) = (Off(face: faceA, planes: planes, point: mean), Off(face: faceB, planes: planes, point: mean));
        var (heldA, heldB) = (Off(face: faceA, planes: planes, point: held), Off(face: faceB, planes: planes, point: held));

        return ((((meanA <= meanB) != (heldA <= heldB)) && (Math.Abs(value: (meanA - meanB)) > NearPlane) && (Math.Abs(value: (heldA - heldB)) > SolvedDrift))
            ? CellSolve.Clip(from: mean, high: (1.0, 1.0, 1.0), low: (0.0, 0.0, 0.0), to: crease)
            : held);
    }
    // How far plane `face` of `planes` (nx, ny, nz, d each) passes from the point.
    private static double Off(int face, ReadOnlySpan<double> planes, (double X, double Y, double Z) point) =>
        Math.Abs(value: ((((planes[(4 * face)] * point.X) + (planes[((4 * face) + 1)] * point.Y)) + (planes[((4 * face) + 2)] * point.Z)) - planes[((4 * face) + 3)]));
    // Whether planes `faceA` and `faceB` of `planes` both pass within NearPlane of the point, the distance at which a
    // vertex lies on a plane for its normal (NearNormal): a point on both lies on their crease.
    private static bool OnBoth((double X, double Y, double Z) point, int faceA, int faceB, ReadOnlySpan<double> planes) {
        foreach (var face in (ReadOnlySpan<int>)[faceA, faceB]) {
            if (Math.Abs(value: ((((planes[(4 * face)] * point.X) + (planes[((4 * face) + 1)] * point.Y)) + (planes[((4 * face) + 2)] * point.Z)) - planes[((4 * face) + 3)])) > NearPlane) {
                return false;
            }
        }

        return true;
    }
    // Whether every plane (nx, ny, nz, d) passes within FeatureResidual of the point.
    private static bool Meets(double x, double y, double z, ReadOnlySpan<double> planes) {
        for (var plane = 0; (plane < planes.Length); plane += 4) {
            if (Math.Abs(value: ((((planes[plane] * x) + (planes[(plane + 1)] * y)) + (planes[(plane + 2)] * z)) - planes[(plane + 3)])) > FeatureResidual) {
                return false;
            }
        }

        return true;
    }
    // The two of `planes` (nx, ny, nz, d each) that turn apart most, a crease's two faces, where any two turn by more
    // than the crease angle (CreaseCosine); (-1, -1) where none do.
    private static (int A, int B) Faces(ReadOnlySpan<double> planes) {
        var turn = CreaseCosine;
        var faces = (A: -1, B: -1);

        for (var first = 0; ((4 * first) < planes.Length); first++) {
            for (var second = (first + 1); ((4 * second) < planes.Length); second++) {
                var cosine = (((planes[(4 * first)] * planes[(4 * second)]) + (planes[((4 * first) + 1)] * planes[((4 * second) + 1)])) + (planes[((4 * first) + 2)] * planes[((4 * second) + 2)]));

                if (cosine < turn) {
                    (turn, faces) = (cosine, (first, second));
                }
            }
        }

        return faces;
    }
    // Whether every plane faces within the crease angle of plane `faceA` or plane `faceB`, and the crossings' mean
    // `point` lies behind every plane: the planes meet along a convex crease between two faces, whose crossings each lie
    // inside the other face's plane. A third face's planes would meet at a corner, and a concave crease's crossings lie in
    // front of the other face.
    private static bool ConvexCrease((double X, double Y, double Z) point, int faceA, int faceB, ReadOnlySpan<double> planes) {
        for (var plane = 0; (plane < planes.Length); plane += 4) {
            var towardA = (((planes[plane] * planes[(4 * faceA)]) + (planes[(plane + 1)] * planes[((4 * faceA) + 1)])) + (planes[(plane + 2)] * planes[((4 * faceA) + 2)]));
            var towardB = (((planes[plane] * planes[(4 * faceB)]) + (planes[(plane + 1)] * planes[((4 * faceB) + 1)])) + (planes[(plane + 2)] * planes[((4 * faceB) + 2)]));

            if (((towardA < CreaseCosine) && (towardB < CreaseCosine)) ||
                (((((planes[plane] * point.X) + (planes[(plane + 1)] * point.Y)) + (planes[(plane + 2)] * point.Z)) - planes[(plane + 3)]) > 0.0)) {
                return false;
            }
        }

        return true;
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
    private static (int[] Quads, int[] QuadCorners, sbyte[] QuadAxes) Connect<TLattice>(TLattice grid, OwnedBuffer<byte> maskOfCell, OwnedBuffer<int> vertexOfPatch) where TLattice : ISdfContourLattice {
        Span<int> cells = [grid.CellsX, grid.CellsY, grid.CellsZ];
        using var quads = new OwnedBuffer<int>(allocator: grid.Allocator, observer: grid.Workspace);
        using var quadCorners = new OwnedBuffer<int>(allocator: grid.Allocator, observer: grid.Workspace);
        using var quadAxes = new OwnedBuffer<sbyte>(allocator: grid.Allocator, observer: grid.Workspace);
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
                        quadAxes.Add(item: SdfContourFacing.Axis(axis: axis, rising: first));
                    }
                }
            }
        }

        return (quads.ToArray(), quadCorners.ToArray(), quadAxes.ToArray());
    }
}
