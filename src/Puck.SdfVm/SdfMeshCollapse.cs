using Puck.Abstractions.Jobs;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>
/// The bake's coarser levels of detail (<see cref="SdfMesh.Levels"/>): a merged mesh (<see cref="SdfMeshPlanarMerge"/>)
/// simplified by half-edge collapse, each collapse moving one place's vertices onto a neighbouring place's, so every
/// level draws vertices at the mesh's own places, each with a normal one of the mesh's own vertices has, and no position
/// is made or moved.
/// <para>Collapses are tried cheapest first by the quadric of the planes about each place, with the planes along creases
/// and material edges weighted so those simplify last, and are taken only where every vertex of the mesh's own triangles
/// stays within the level's error of a triangle of its own material whose interpolated normal there is within the level's
/// angle of its own, every vertex and edge midpoint of a crease or material edge stays within it of that line as the
/// level draws it, and every new triangle's centroid and edge midpoints stay within it of the surface of their material
/// it replaces (with what that surface had strayed already), so a triangle no vertex needs, one cutting through a thin
/// rod, is measured too. Those distances are measured, never estimated, and the level records the largest
/// (<see cref="SdfMeshLevel.Error"/>), which the draws' level choice projects (<see cref="SdfMeshDetail"/>). A vertex whose
/// distance along the mesh to a sharp turn of its surface (a crease, or an edge whose ends' normals part by more than
/// <see cref="SharpDegrees"/>) is under the level's error keeps only the distance bound: where the level is drawn that
/// error is under a pixel, so the vertex's normal shades no pixel of its own (the side wall of a plate thinner than the
/// error, the rim of a leaf). A place on one crease slides only along it, with each side keeping its own vertices, and a
/// place on material edges only along them; a corner where three or more faces meet slides along any of its creases, each
/// side of the crease keeping its own vertices and each other face taking the neighbour's vertex whose normal lies within
/// the level's angle of its own, or, where the neighbour has none, a vertex made at the neighbour's place with its own
/// normal (<see cref="SdfMesh.VertexCount"/> grows by those; the mesh's own triangles name none of them). So a plate whose
/// thickness is under a level's error closes to a card whose two faces keep their own normals, and a box's corner stays
/// where it was until its faces' samples allow it to move. A place on the mesh's open edge, and one whose triangles do
/// not close one ring about it, never move, so neighbouring meshes keep meeting edge for edge. No collapse turns a
/// triangle to face away from its vertices' normals, which the opaque pass's back-face cull would show as a hole.</para>
/// <para>A mesh at least <see cref="ClusterExtent"/> long with enough triangles is drawn in clusters
/// (<see cref="SdfMesh.Clusters"/>): its own triangles are reordered cluster by cluster, every place two clusters share is
/// held for the first <see cref="ClusteredRuns"/> runs (one door, the collapser's kind of a place), so each of those levels
/// lies cluster by cluster too and any mix of them meets edge for edge, and the later runs collapse the mesh whole. The
/// levels and clusters are a pure function of the mesh, and presentation alone.</para>
/// </summary>
public static class SdfMeshCollapse {
    /// <summary>The share of a mesh's contour cell (<see cref="SdfMesh.BakeCell"/>, its largest side) each coarser level's
    /// vertices stay within, finest first: one level each doubling of the error from a quarter of the cell to 32 cells. A
    /// draw takes the coarsest level whose error projects under a pixel or a texel (<see cref="SdfMeshDetail"/>), so each
    /// copy of a shared mesh draws the level its own distance needs: a level every doubling leaves a draw at most twice
    /// as fine as its pixel asks, where a chain with wider steps draws a copy several times the distance of the one its
    /// level was made for at that level's triangles. The levels to two cells serve the views that resolve the mesh's own
    /// tier; the coarser ones serve views far smaller than the one its tier was demanded for (a far copy, a screen a few
    /// dozen pixels high, a shadow texel metres across).</summary>
    public static ReadOnlySpan<float> CellShares => [0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f];
    /// <summary>The angle, in degrees, each level's interpolated normal at a vertex's nearest point stays within of the
    /// vertex's own normal, finest first, one for each of <see cref="CellShares"/>: 2 degrees at a quarter cell, 6 at two
    /// cells, 15 at eight and 30 at 32, and between those geometric in the error.</summary>
    public static ReadOnlySpan<float> NormalDegrees => [2f, 2.88f, 4.16f, 6f, 9.49f, 15f, 21.2f, 30f];
    /// <summary>The normal angle, in degrees, the levels a view resolving the mesh's own tier takes stay within: the
    /// two-cell level's, which the field levels' witnesses hold too (<see cref="SdfMeshFieldLevels"/>).</summary>
    public static float ResolvedDegrees => NormalDegrees[(ResolvedLevels - 1)];
    /// <summary>The levels a view resolving the mesh's own tier takes, finest first (<see cref="CellShares"/>): those to two
    /// cells.</summary>
    public const int ResolvedLevels = 4;

    /// <summary>The most of the next finer level's triangles a level keeps and still is made: one that saves less is not
    /// worth its region words.</summary>
    public const float KeptShare = 0.75f;

    // How much heavier a crease's or a material edge's planes weigh than a face's, so those collapse last.
    private const double FeatureWeight = 8.0;
    // The least cosine between a collapsed triangle's normal and its own before: a collapse that turns a triangle further
    // is refused, so no triangle folds over.
    private const double FlipCosine = 0.2;
    // The most passes a level takes, and the least share of the places it still holds a pass must collapse for another to
    // follow.
    private const int MaxPasses = 48;
    private const int TailShare = 512;
    /// <summary>The angle, in degrees, by which the normals at a crease's copies or at an edge's two ends must part for the
    /// surface to turn sharply there: a vertex nearer such a turn, along the mesh, than a level's error keeps that level's
    /// distance bound but not its normal bound (<see cref="SdfMeshCollapse"/>).</summary>
    public const float SharpDegrees = 45f;

    /// <summary>Counts the levels the collapse ran, one an error budget a mesh was collapsed to.</summary>
    public static readonly WorkKind Runs = new(name: "sdf.collapse.runs", unit: "levels", workClass: WorkClass.Pacing);
    /// <summary>Counts the levels a mesh kept (<see cref="KeptShare"/>), a part of <see cref="Runs"/>.</summary>
    public static readonly WorkKind Kept = new(name: "sdf.collapse.kept", unit: "levels", workClass: WorkClass.Pacing);
    /// <summary>Counts the collapse's passes, each of which prices every place that may move.</summary>
    public static readonly WorkKind Passes = new(name: "sdf.collapse.passes", unit: "passes", workClass: WorkClass.Pacing);
    /// <summary>Counts the places those passes priced.</summary>
    public static readonly WorkKind Priced = new(name: "sdf.collapse.priced", unit: "places", workClass: WorkClass.Pacing);
    /// <summary>Counts the collapses tried: an edge measured against every bound a level holds, refused or taken.</summary>
    public static readonly WorkKind Tried = new(name: "sdf.collapse.tried", unit: "edges", workClass: WorkClass.Pacing);
    /// <summary>Counts the collapses taken, a part of <see cref="Tried"/>.</summary>
    public static readonly WorkKind Taken = new(name: "sdf.collapse.taken", unit: "edges", workClass: WorkClass.Pacing);
    /// <summary>Counts the candidates measured side by side ahead of their pass's turn, a part of <see cref="Priced"/>.</summary>
    public static readonly WorkKind Speculated = new(name: "sdf.collapse.speculated", unit: "places", workClass: WorkClass.Pacing);
    /// <summary>Counts the speculated candidates an earlier commit of their block reached, which their turn measured
    /// again, a part of <see cref="Speculated"/>.</summary>
    public static readonly WorkKind Redone = new(name: "sdf.collapse.redone", unit: "places", workClass: WorkClass.Pacing);
    /// <summary>Counts the places a pass left out because every target's kept refusal still stood (the collapser's quiet
    /// places), beside <see cref="Priced"/>, the places it listed.</summary>
    public static readonly WorkKind Quiet = new(name: "sdf.collapse.quiet", unit: "places", workClass: WorkClass.Pacing);
    /// <summary>Counts the surface samples the collapses tried listed from their source fans to measure.</summary>
    public static readonly WorkKind Listed = new(name: "sdf.collapse.listed", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Counts the surface samples the collapses tried measured against their laid-out triangles, speculated
    /// measures included.</summary>
    public static readonly WorkKind Measured = new(name: "sdf.collapse.measured", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Gets the collapse's work, process-wide: what building every baked mesh's levels costs, counted, never timed.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.collapse", kinds: [Runs, Kept, Passes, Priced, Quiet, Tried, Taken, Speculated, Redone, Listed, Measured]);

    /// <summary>Returns a mesh with its coarser levels of detail.</summary>
    /// <param name="mesh">The merged mesh.</param>
    /// <param name="preserveFeatures">Limits crease and material-edge error to the largest bake-cell dimension divided
    /// by 8, while face interiors use the ordinary level budget. A mesh without detectable features retains its base surface.</param>
    /// <returns>The mesh with <see cref="SdfMesh.Levels"/> set, its vertices followed by those its levels made (its own
    /// triangles and indices unchanged); <paramref name="mesh"/> itself when it carries no vertex normals, no contour cell,
    /// members (whose draws no host box bounds, so none chooses a level), or no level saves enough
    /// (<see cref="KeptShare"/>).</returns>
    /// <param name="owner">The workspace owner charged for the collapse's tables, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    /// <param name="allocator">The native allocator the collapse's lists come from, so a background bake's scratch never reaches the collector, or <see langword="null"/> to rent them.</param>
    public static SdfMesh Levels(SdfMesh mesh, bool preserveFeatures = false, IAllocationObserver? owner = null, IAllocator? allocator = null) {
        ArgumentNullException.ThrowIfNull(argument: mesh);

        var cell = MathF.Max(x: mesh.BakeCell.X, y: MathF.Max(x: mesh.BakeCell.Y, y: mesh.BakeCell.Z));

        if (!mesh.HasNormals || !mesh.Members.IsEmpty || !(cell > 0f) || (mesh.TriangleCount < 4)) {
            return mesh;
        }

        // A mesh whose features are preserved (lettering) makes only the levels a view resolving its tier takes: its
        // strokes are what it shows, which the coarser levels' wider normal bounds would let merge.
        var levelCount = (preserveFeatures ? ResolvedLevels : CellShares.Length);
        var errors = new double[levelCount];
        var cosines = new double[levelCount];

        for (var level = 0; (level < levelCount); level++) {
            errors[level] = (CellShares[level] * cell);
            cosines[level] = Math.Cos(d: ((NormalDegrees[level] * Math.PI) / 180.0));
        }

        // A mesh large enough to stand partly near and partly far is drawn in clusters: its own triangles reordered cluster
        // by cluster, which every clustered level keeps (Collapser.Run lists alive triangles in their order).
        using var scope = new RentedScope(allocator: allocator, owner: owner);
        var clusterCount = 0;
        int[]? order = null;
        var clusterOf = (preserveFeatures ? null : Partition(clusters: out clusterCount, mesh: mesh, order: out order, scope: scope));

        if ((clusterOf is not null) && (order is not null)) { mesh = Reordered(mesh: mesh, order: order); }

        var collapser = new Collapser(clusterOf: clusterOf, cosines: cosines, errors: errors, mesh: mesh, preserveFeatures: preserveFeatures, scope: scope);

        try {
            return Collapsed(clusterCount: clusterCount, clusterOf: clusterOf, collapser: collapser, levelCount: levelCount, mesh: mesh, preserveFeatures: preserveFeatures);
        } finally {
            collapser.Release();
        }
    }

    // Runs a collapser's levels and returns the mesh that carries those it kept: the mesh itself when none is kept.
    private static SdfMesh Collapsed(Collapser collapser, int clusterCount, int[]? clusterOf, int levelCount, SdfMesh mesh, bool preserveFeatures) {
        if (preserveFeatures && !collapser.HasFeatures) { return mesh; }
        var levels = new List<SdfMeshLevel>(capacity: CellShares.Length);
        var kept = mesh.TriangleCount;
        var runs = new List<int>();

        if (clusterOf is not null) { collapser.CountClusters(into: runs); }
        for (var level = 0; (level < levelCount); level++) {
            // Past the clustered runs the seams the clusters share move too: those levels are drawn whole.
            if ((clusterOf is not null) && (level == ClusteredRuns)) { collapser.ReleaseSeams(); }

            var made = collapser.Run(kept: kept, level: level);

            ProcessWork.Count(kind: Runs);
            if (made is null) {
                continue;
            }
            ProcessWork.Count(kind: Kept);

            // A sample later tracked to a nearer triangle can measure a coarser level nearer than a finer one; each bound
            // holds, and the larger holds for both.
            levels.Add(item: ((levels.Count == 0) ? made : (made with { Error = MathF.Max(x: made.Error, y: levels[^1].Error) })));
            kept = made.TriangleCount;
            if ((clusterOf is not null) && (level < ClusteredRuns)) { collapser.CountClusters(into: runs); }
        }

        collapser.CountSamples();
        if (levels.Count == 0) { return mesh; }

        var clusters = ((clusterOf is null) ? null : collapser.Clusters(clusters: clusterCount, runs: runs));

        if (collapser.MadeVertices == 0) { return (mesh with { Clusters = clusters, Levels = levels }); }

        // Every made vertex is kept, whichever levels name it, past the mesh's own vertices, which alone its own triangles name.
        using var held = mesh.Hold();
        var positions = RentedScope.Allocate<Vector3>(mesh.Products, mesh.VertexCount + collapser.MadeVertices);
        var normals = RentedScope.Allocate<Vector3>(mesh.Products, positions.Length);

        held.Positions.Span.CopyTo(destination: positions.Span);
        held.Normals.Span.CopyTo(destination: normals.Span);
        collapser.WriteMade(positions: positions.Span[mesh.VertexCount..], normals: normals.Span[mesh.VertexCount..]);

        return new SdfMesh(positions: positions, indices: Carried(mesh: mesh, plane: held.Indices), normals: normals, triangleMaterials: Carried(mesh: mesh, plane: held.TriangleMaterials)) {
            Products = mesh.Products,
            BakeCell = mesh.BakeCell,
            Clusters = clusters,
            Encloses = mesh.Encloses,
            Levels = levels,
        };
    }

    // A plane of the mesh the levelled mesh carries unchanged: the mesh's own array, shared, when nothing lets it go (an
    // array the collector owns, of a mesh that never restores its planes); else a copy that outlives the hold.
    private static ReadOnlyMemory<uint> Carried(SdfMesh mesh, ReadOnlyMemory<uint> plane) =>
        ((mesh.Products is not null || (!mesh.LetsGoPlanes && !RentedScope.RequiresOwner(plane))) ? plane : plane.ToArray());

    /// <summary>The collapse runs whose levels a clustered mesh draws cluster by cluster, from the finest (a quarter and
    /// half a cell, <see cref="CellShares"/>): the levels a draw near enough to hold some of its triangles near and some
    /// far takes. Their seams stay where they are, which costs those levels the seam's vertices (a few percent of a tree's
    /// at eight clusters; a third run, at one cell, cost the forest more than its clusters saved); past them a mesh is
    /// collapsed and drawn whole.</summary>
    public const int ClusteredRuns = 2;
    /// <summary>The longest extent, in object units, past which a mesh is drawn in clusters: a mesh shorter than it
    /// spans too little depth from any eye far enough to need no level finer than another part of it.</summary>
    public const float ClusterExtent = 4f;
    /// <summary>The fewest triangles of its own a cluster holds: a mesh is split while its largest cluster holds twice
    /// this many and is longer than <see cref="ClusterSide"/>, up to <see cref="SdfMeshClusters.MostClusters"/>, which
    /// gives a forest tree of about 300,000 triangles eight.</summary>
    public const int ClusterTriangles = 32768;
    /// <summary>The shortest a cluster's longest extent is split at, in object units.</summary>
    public const float ClusterSide = 1.5f;

    // A mesh's clusters: its triangles split at the median of their centroids along the longest extent of the largest
    // cluster, again and again, while that cluster holds at least twice ClusterTriangles and is longer than ClusterSide;
    // null for a mesh shorter than ClusterExtent or too small to split. order lists the triangles cluster by cluster, each
    // cluster's in their own order, and the result names each reordered triangle's cluster.
    private static int[]? Partition(SdfMesh mesh, RentedScope scope, out int clusters, out int[]? order) {
        clusters = 0;
        order = null;

        var extent = (mesh.PositionHigh - mesh.PositionLow);

        if ((MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z)) < ClusterExtent) || (mesh.TriangleCount < (2 * ClusterTriangles))) { return null; }
        using var held = mesh.Hold();
        var positions = held.Positions.Span;
        var indices = held.Indices.Span;
        var triangles = mesh.TriangleCount;
        var centroids = scope.Buffer<Vector3>(length: triangles).GetSpan()[..triangles];
        var sorted = new int[triangles];
        var keys = scope.Buffer<float>(length: triangles).GetSpan();
        var places = scope.Buffer<int>(length: triangles).GetSpan();
        var split = scope.Buffer<int>(length: triangles).GetSpan();

        for (var triangle = 0; (triangle < triangles); triangle++) {
            centroids[triangle] = (((positions[((int)indices[(3 * triangle)])] + positions[((int)indices[((3 * triangle) + 1)])]) + positions[((int)indices[((3 * triangle) + 2)])]) / 3f);
            sorted[triangle] = triangle;
        }

        // Each cluster a run of `sorted` between consecutive starts, split in place.
        Span<int> starts = stackalloc int[(SdfMeshClusters.MostClusters + 1)];
        var count = 1;

        starts[0] = 0;
        starts[1] = triangles;
        while (count < SdfMeshClusters.MostClusters) {
            var largest = -1;
            var axis = 0;

            for (var cluster = 0; (cluster < count); cluster++) {
                var holds = (starts[(cluster + 1)] - starts[cluster]);

                if ((holds < (2 * ClusterTriangles)) || ((largest >= 0) && (holds <= (starts[(largest + 1)] - starts[largest])))) { continue; }
                var low = new Vector3(value: float.PositiveInfinity);
                var high = new Vector3(value: float.NegativeInfinity);

                for (var at = starts[cluster]; (at < starts[(cluster + 1)]); at++) {
                    low = Vector3.Min(value1: low, value2: centroids[sorted[at]]);
                    high = Vector3.Max(value1: high, value2: centroids[sorted[at]]);
                }
                var size = (high - low);
                var longest = MathF.Max(x: size.X, y: MathF.Max(x: size.Y, y: size.Z));

                if (longest < ClusterSide) { continue; }
                largest = cluster;
                axis = ((size.X == longest) ? 0 : ((size.Y == longest) ? 1 : 2));
            }
            if (largest < 0) { break; }

            var start = starts[largest];
            var length = (starts[(largest + 1)] - start);
            var run = sorted.AsSpan(start: start, length: length);

            // The median by the axis (the sort draws on no randomness, so a run splits the same way every time); each half
            // then keeps its triangles in their own order.
            for (var at = 0; (at < length); at++) {
                keys[at] = centroids[run[at]][axis];
                places[at] = at;
            }
            keys[..length].Sort(items: places[..length]);
            var middle = (length / 2);

            places[..middle].Sort();
            places[middle..length].Sort();
            for (var at = 0; (at < length); at++) { split[at] = run[places[at]]; }
            split[..length].CopyTo(destination: run);
            for (var after = (count + 1); (after > (largest + 1)); after--) { starts[after] = starts[(after - 1)]; }
            starts[(largest + 1)] = (start + middle);
            count++;
        }
        if (count < 2) { return null; }

        clusters = count;
        order = sorted;
        var clusterOf = new int[triangles];

        for (var cluster = 0; (cluster < clusters); cluster++) {
            for (var at = starts[cluster]; (at < starts[(cluster + 1)]); at++) { clusterOf[at] = cluster; }
        }
        return clusterOf;
    }
    // A mesh with its own triangles (indices and triangle materials) in a new order, every other plane as it was.
    private static SdfMesh Reordered(SdfMesh mesh, int[] order) {
        using var held = mesh.Hold();
        var indices = held.Indices.Span;
        var materials = held.TriangleMaterials.Span;
        var reordered = RentedScope.Allocate<uint>(mesh.Products, indices.Length);
        var reorderedMaterials = RentedScope.Allocate<uint>(mesh.Products, materials.Length);

        for (var at = 0; (at < order.Length); at++) {
            var triangle = order[at];

            indices.Slice(start: (3 * triangle), length: 3).CopyTo(destination: reordered.Span[(3 * at)..]);
            if (!materials.IsEmpty) { reorderedMaterials.Span[at] = materials[triangle]; }
        }

        return new SdfMesh(positions: RentedScope.Copy<Vector3>(mesh.Products, held.Positions.Span), indices: reordered, normals: RentedScope.Copy<Vector3>(mesh.Products, held.Normals.Span), triangleMaterials: reorderedMaterials) {
            Products = mesh.Products,
            BakeCell = mesh.BakeCell,
            BakedSites = mesh.BakedSites,
            Encloses = mesh.Encloses,
        };
    }

    // One mesh's collapses. A place is a position some vertices share (a crease's copies, one per face about it); a
    // collapse moves every vertex at one place onto a neighbouring place's vertices, keeping each triangle's own side of a
    // crease. Each vertex and material its triangles carry is a sample, tracked to the triangle it is measured to.
    // Collapses run in passes: each pass lists every place's triangles, and a place a collapse moved triangles onto takes
    // part in no other collapse that pass, since its list misses them.
    private sealed class Collapser : IJobPartitions {
        private const byte Free = 0;
        private const byte Locked = 2;
        private const byte Slides = 1;
        // The slide targets Kind keeps per place; a place with more recomputes them.
        private const int SlideTargets = 4;

        private readonly OwnedBuffer<int> m_placeOf;
        // Each place's position, in doubles.
        private readonly OwnedBuffer<Point> m_point;
        // The mesh's own vertices' normals; past them, each vertex a collapse made, at its place with its normal; past
        // those, the vertices the collapse being measured would make (Normal reads all three).
        private readonly OwnedBuffer<Vector3> m_normals;
        private readonly int m_ownVertices;
        private readonly List<(int Place, Vector3 Normal)> m_madeVertices = [];
        private List<Vector3> m_making = null!;
        // Each own vertex's distance along the mesh to the nearest sharp turn of its surface (FeatureDistances).
        private readonly OwnedBuffer<double> m_feature;
        private readonly bool m_waivesNormals;
        // The collapse being measured: the copies on each side of its edge at the place and the neighbour, and the place's
        // other copies with the neighbour's vertices they take (Map).
        private int m_fromFirst;
        private int m_toFirst;
        private int m_fromSecond;
        private int m_toSecond;
        private List<(int From, int To)> m_otherCopies = null!;
        private readonly OwnedBuffer<int> m_corners;
        // Each corner's place, m_placeOf of its vertex, which Commit keeps beside m_corners.
        private readonly OwnedBuffer<int> m_cornerPlace;
        private readonly OwnedBuffer<uint> m_materials;
        private readonly OwnedBuffer<bool> m_alive;
        private readonly OwnedBuffer<double> m_quadrics;
        private readonly int m_places;
        private readonly int m_triangles;
        private readonly bool m_carriesMaterials;

        private int m_aliveTriangles;

        // The samples: each one's vertex, material, distance and normal cosine at the triangle it is tracked to, and the
        // next sample tracked to the same triangle.
        private readonly OwnedBuffer<int> m_sampleVertex;
        private readonly int m_sampleCount;
        private readonly OwnedBuffer<uint> m_sampleMaterial;
        private readonly OwnedBuffer<double> m_sampleDistance;
        private readonly OwnedBuffer<int> m_sampleNext;
        private readonly OwnedBuffer<int> m_tracked;

        // The line samples: each crease or material edge vertex and each such edge's midpoint, tracked to the edge of the
        // line it lies nearest now, keyed by the edge's places; and each one's distance there. A surface sample can sit
        // inside a triangle that fills a notch in a face's outline at no distance, so the line itself is measured.
        private readonly OwnedBuffer<double> m_lineX;
        private readonly OwnedBuffer<double> m_lineY;
        private readonly OwnedBuffer<double> m_lineZ;
        private readonly OwnedBuffer<double> m_lineDistance;
        private readonly OwnedBuffer<int> m_lineNext;
        private readonly OwnedLongMap<(int First, int Last)> m_lines;

        // Each triangle's bound on how far it strays from the mesh's own surface (zero for the mesh's own triangles).
        private readonly OwnedBuffer<double> m_stray;
        // A pass's triangles about each place; the pass a collapse moved triangles onto a place (touched) or changed the
        // triangles about it (changed); what each place may do.
        private readonly OwnedBuffer<int> m_fanStart;
        private readonly OwnedBuffer<int> m_fan;
        private readonly OwnedBuffer<int> m_filled;
        private readonly OwnedBuffer<int> m_touched;
        private readonly OwnedBuffer<int> m_changed;
        // Rejected directed edges stay rejected until the fan the refusal read changes: the collapsing place's always, the
        // neighbour's for a link refusal, and the level's budget for a measured one. Commit stamps every old fan endpoint.
        // No cached result ever accepts a collapse.
        private readonly OwnedBuffer<int> m_revisions;

        // Keyed by the directed edge (from, to) and holding the two revisions it was refused at, each packed high then low.
        private readonly OwnedLongMap<long> m_rejected;

        private int m_revision;
        private int m_budget;
        // Each level's distance budget and least normal cosine, finest first: both loosen level by level, so a sample
        // the level's bounds refuse names the first level whose bounds could hold it (NeededLevel). The budget that
        // the latest measured refusal stands through (Pass records it).
        private readonly double[] m_errors;
        private readonly double[] m_cosines;
        private int m_refusedThrough;

        // Midpoint searches are shared only while PrepareFrom's source fan is unchanged. A search may stop once its
        // witness cannot raise the current triangle's bound; Next resumes it when another target needs a tighter bound.
        private Dictionary<(long Edge, uint Material), (double Distance, int Next)> m_reverseEdges = null!;

        private readonly OwnedBuffer<byte> m_kind;
        private readonly OwnedBuffer<int> m_slideTargets;
        private readonly OwnedBuffer<int> m_slideCounts;
        private readonly double m_lineLimit;
        private readonly OwnedBuffer<(double Cost, int Place)> m_candidateKeys;
        private readonly OwnedBuffer<int> m_candidatePlaces;

        // A pass's speculation (Speculate): the block a commit last stamped each place in, the block's number, each
        // candidate's speculated outcomes (its first record and their count, or -1 for a candidate the block did not
        // speculate), and the records themselves, a target, its outcome and the budget its refusal stands through.
        private readonly OwnedBuffer<int> m_blockStamp;
        private int m_block;
        // The quiet places (TryPlace): each one's revision and the budget its refusals stand through when it went quiet,
        // the cost a pass listed it at and the pass that left it out of its candidates; the woken places of this pass
        // still to take (Wake), cheapest first; and the candidate the pass is taking, whose key orders a woken place.
        private readonly OwnedBuffer<bool> m_quiet;
        private readonly OwnedBuffer<int> m_quietRevision;
        private readonly OwnedBuffer<int> m_quietThrough;
        private readonly OwnedBuffer<double> m_quietCost;
        private readonly OwnedBuffer<int> m_leftOut;
        private readonly OwnedBuffer<(double Cost, int Place)> m_woken;
        private int m_wokenCount;
        private (double Cost, int Place) m_taking;
        private readonly OwnedBuffer<int> m_specFirst;
        private readonly OwnedBuffer<int> m_specCount;
        private readonly OwnedBuffer<int> m_specTarget;
        private readonly OwnedBuffer<Outcome> m_specOutcome;
        private readonly OwnedBuffer<int> m_specThrough;
        private readonly List<int> m_speculating = [];
        private readonly ConcurrentStack<Collapser> m_workers = new();

        private int m_pass;

        // Scratch every collapse reuses, a workspace each collapser takes (Workspace). A speculating worker (the sharing
        // constructor) holds its own, so every method that measures a collapse writes only these and reads the shared
        // tables, which no measure writes.
        private Workspace m_work = null!;
        private List<int> m_from = null!;
        // The places about the prepared fan, which every neighbour's link test reads (PrepareFrom, Links).
        private List<int> m_neighbours = null!;
        // The edge's two triangles of the collapse TryCollapse took last, which CommitTaken commits.
        private int m_first;
        private int m_second;
        private List<int> m_to = null!;
        private List<int> m_targetPlaces = null!;
        private List<int> m_features = null!;
        private List<int> m_seams = null!;
        // Sorted targets in independent runs. A commit invalidates the old source fan and
        // the old target fan: every edge whose topology or endpoint quadric changed touches one of them.
        private readonly OwnedBuffer<(double Cost, int Place)> m_prices;
        private readonly OwnedBuffer<(int Start, int Capacity, int Count, bool Ready)> m_priceRuns;
        private List<int> m_samples = null!;
        private List<(int Triangle, double Distance)> m_choices = null!;
        private List<int> m_lineNeighbours = null!;
        private List<(int Sample, int Other, double Distance)> m_lineChoices = null!;
        private List<double> m_strays = null!;
        private List<Source> m_sources = null!;

        private int m_source;
        private int m_sourcesListed;
        // The samples this collapser listed and measured, which Levels adds to the process's work (CountSamples).
        private long m_listedSamples;
        private long m_measuredSamples;
        private readonly RentedScope? m_products;
        private bool m_linesListed;
        private int m_laidCount;

        // Lines' scratch: each fan triangle's corner and the places after it. The arrays grow in place of the workspace's,
        // which takes them back when the collapser gives it up.
        private int[] m_ringCorner = null!;
        private int[] m_ringNext = null!;
        private int[] m_ringAfter = null!;

        private int[] m_fanCorner = null!;
        private int[] m_fanNext = null!;
        private int[] m_fanAfter = null!;
        private int[] m_fanTwin = null!;

        // Scratch for Kind's current fan only; every endpoint it reads is cleared before indexing that fan.
        private readonly OwnedBuffer<(int Outgoing, int Incoming)> m_fanEdges;

        private Candidate[] m_measured = null!;
        private OriginalTriangle[] m_previous = null!;
        private int[] m_laid = null!;

        // The scratch of one collapser, refilled by each mesh's collapse and kept between them: a collapse takes a
        // workspace for its collapser and every speculating worker, and gives them back through Release, so the lists'
        // and arrays' growth is the process's, once, instead of every mesh's. A workspace holds what the last collapse
        // left in it, which Take empties; no member's contents carry from one collapse to the next.
        private sealed class Workspace {
            // The most entries a list may hold for the workspace to be kept: one that grew past it (a fan of many
            // thousands of samples) is left to the collector instead of held by the pool.
            private const int MostKept = (1 << 16);

            public readonly List<Vector3> Making = [];
            public readonly List<(int From, int To)> OtherCopies = [];
            public readonly Dictionary<(long Edge, uint Material), (double Distance, int Next)> ReverseEdges = [];
            public readonly List<int> From = [];
            public readonly List<int> Neighbours = [];
            public readonly List<int> To = [];
            public readonly List<int> TargetPlaces = [];
            public readonly List<int> Features = [];
            public readonly List<int> Seams = [];
            public readonly List<int> Samples = [];
            public readonly List<(int Triangle, double Distance)> Choices = [];
            public readonly List<int> LineNeighbours = [];
            public readonly List<(int Sample, int Other, double Distance)> LineChoices = [];
            public readonly List<double> Strays = [];
            public readonly List<Source> Sources = [];
            public int[] RingCorner = new int[16];
            public int[] RingNext = new int[16];
            public int[] RingAfter = new int[16];
            public int[] FanCorner = new int[16];
            public int[] FanNext = new int[16];
            public int[] FanAfter = new int[16];
            public int[] FanTwin = new int[16];
            public Candidate[] Measured = new Candidate[16];
            public OriginalTriangle[] Previous = new OriginalTriangle[16];
            public int[] Laid = new int[16];

            public void Empty() {
                Making.Clear();
                OtherCopies.Clear();
                ReverseEdges.Clear();
                From.Clear();
                Neighbours.Clear();
                To.Clear();
                TargetPlaces.Clear();
                Features.Clear();
                Seams.Clear();
                Samples.Clear();
                Choices.Clear();
                LineNeighbours.Clear();
                LineChoices.Clear();
                Strays.Clear();
                Sources.Clear();
            }
            public bool Kept => ((Sources.Capacity <= MostKept) && (Samples.Capacity <= MostKept) && (Choices.Capacity <= MostKept) && (Measured.Length <= MostKept) && (ReverseEdges.Count <= MostKept));
        }

        private static readonly SharedPool<Workspace> Workspaces = new(create: static () => new Workspace());

        private void Take() {
            m_work = Workspaces.Rent();
            m_work.Empty();
            (m_making, m_otherCopies, m_reverseEdges) = (m_work.Making, m_work.OtherCopies, m_work.ReverseEdges);
            (m_from, m_neighbours, m_to, m_targetPlaces, m_features, m_seams) = (m_work.From, m_work.Neighbours, m_work.To, m_work.TargetPlaces, m_work.Features, m_work.Seams);
            (m_samples, m_choices, m_lineNeighbours, m_lineChoices, m_strays, m_sources) = (m_work.Samples, m_work.Choices, m_work.LineNeighbours, m_work.LineChoices, m_work.Strays, m_work.Sources);
            (m_ringCorner, m_ringNext, m_ringAfter) = (m_work.RingCorner, m_work.RingNext, m_work.RingAfter);
            (m_fanCorner, m_fanNext, m_fanAfter, m_fanTwin) = (m_work.FanCorner, m_work.FanNext, m_work.FanAfter, m_work.FanTwin);
            (m_measured, m_previous, m_laid) = (m_work.Measured, m_work.Previous, m_work.Laid);
        }
        // Gives this collapser's workspace, and every worker's, back to the pool once the collapse is over.
        public void Release() {
            foreach (var worker in m_workers) { worker.Give(); }
            Give();
        }
        private void Give() {
            var work = m_work;

            m_work = null!;
            (work.RingCorner, work.RingNext, work.RingAfter) = (m_ringCorner, m_ringNext, m_ringAfter);
            (work.FanCorner, work.FanNext, work.FanAfter, work.FanTwin) = (m_fanCorner, m_fanNext, m_fanAfter, m_fanTwin);
            (work.Measured, work.Previous, work.Laid) = (m_measured, m_previous, m_laid);
            if (work.Kept) { Workspaces.Return(item: work); }
        }

        public bool HasFeatures => (m_kind.GetSpan().Contains(value: Locked) || m_kind.GetSpan().Contains(value: Slides));

        // Each triangle's cluster (null for a mesh drawn whole), the clusters' count and boxes, which places their
        // triangles share, and whether those places are held: every clustered run holds them, and ReleaseSeams lets go.
        private readonly int[]? m_clusterOf;
        private readonly int m_clusters;
        private readonly Vector3[] m_clusterLow = [];
        private readonly Vector3[] m_clusterHigh = [];
        private readonly OwnedBuffer<bool> m_seam;
        private bool m_seamsHeld;

        // Appends each cluster's triangles the latest level draws (its own, before the first run), cluster by cluster.
        public void CountClusters(List<int> into) {
            var start = into.Count;

            for (var cluster = 0; (cluster < m_clusters); cluster++) { into.Add(item: 0); }
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if ((m_pass == 0) || m_alive[triangle]) { into[(start + m_clusterOf![triangle])]++; }
            }
        }
        // The clusters' boxes, from the mesh's own triangles, and the runs CountClusters appended.
        public SdfMeshClusters Clusters(int clusters, List<int> runs) => new(high: m_clusterHigh[..clusters], low: m_clusterLow[..clusters], triangles: [.. runs]);
        // Lets the seams the clusters share move: the levels after are collapsed and drawn whole.
        public void ReleaseSeams() {
            m_seamsHeld = false;
            Fans();
            for (var place = 0; (place < m_places); place++) {
                if (m_seam[place]) { m_kind[place] = KindOf(penalize: false, place: place); }
            }
        }

        // Every table is rented through the scope, which the caller disposes: a rented array can be longer than asked for, so
        // each read is bounded by the place, triangle or sample count, never the array's length.
        public Collapser(SdfMesh mesh, bool preserveFeatures, RentedScope scope, double[] errors, double[] cosines, int[]? clusterOf) {
            Take();
            m_lines = scope.LongMap<(int First, int Last)>();
            m_products = mesh.Products;
            m_errors = errors;
            m_cosines = cosines;
            m_lineLimit = (preserveFeatures ? (Math.Max(val1: mesh.BakeCell.X, val2: Math.Max(val1: mesh.BakeCell.Y, val2: mesh.BakeCell.Z)) / 8.0) : double.PositiveInfinity);
            using var held = mesh.Hold();
            var positions = held.Positions.Span;
            var indices = held.Indices.Span;
            var materials = held.TriangleMaterials.Span;
            var normals = held.Normals.Span;
            var vertices = positions.Length;
            var sites = mesh.Sites();
            m_placeOf = scope.Buffer<int>(length: vertices);
            for (var vertex = 0; vertex < vertices; vertex++) {
                m_placeOf[vertex] = sites.Span[vertex] == vertex ? m_places++ : m_placeOf[sites.Span[vertex]];
            }
            m_point = scope.Buffer<Point>(length: m_places);
            for (var vertex = 0; (vertex < vertices); vertex++) {
                m_point[m_placeOf[vertex]] = new Point { X = positions[vertex].X, Y = positions[vertex].Y, Z = positions[vertex].Z };
            }

            m_normals = scope.Buffer<Vector3>(length: normals.Length);
            normals.CopyTo(destination: m_normals.GetSpan());
            m_ownVertices = normals.Length;
            // Lettering's strokes are as narrow as the levels it makes, so its normals keep their bound everywhere.
            m_waivesNormals = !preserveFeatures;
            m_carriesMaterials = !materials.IsEmpty;
            m_triangles = mesh.TriangleCount;
            m_corners = scope.Buffer<int>(length: (3 * m_triangles));
            m_cornerPlace = scope.Buffer<int>(length: (3 * m_triangles));
            m_materials = scope.Buffer<uint>(length: m_triangles);
            m_alive = scope.Buffer<bool>(length: m_triangles);
            for (var corner = 0; (corner < (3 * m_triangles)); corner++) {
                m_corners[corner] = ((int)indices[corner]);
                m_cornerPlace[corner] = m_placeOf[m_corners[corner]];
            }
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                m_materials[triangle] = (materials.IsEmpty ? 0u : materials[triangle]);
                m_alive[triangle] = Distinct(triangle: triangle);
                m_aliveTriangles += (m_alive[triangle] ? 1 : 0);
            }
            m_seam = scope.Buffer<bool>(length: m_places, clear: true);
            if (clusterOf is not null) {
                // A place the triangles of two clusters share is a seam, which stays put while the levels are clustered.
                var first = scope.Buffer<int>(length: m_places);

                first.GetSpan()[..m_places].Fill(value: -1);
                m_clusterOf = clusterOf;
                foreach (var cluster in clusterOf) { m_clusters = Math.Max(val1: m_clusters, val2: (cluster + 1)); }
                m_clusterLow = new Vector3[m_clusters];
                m_clusterHigh = new Vector3[m_clusters];
                Array.Fill(array: m_clusterLow, value: new Vector3(value: float.PositiveInfinity));
                Array.Fill(array: m_clusterHigh, value: new Vector3(value: float.NegativeInfinity));
                for (var corner = 0; (corner < (3 * m_triangles)); corner++) {
                    var cluster = clusterOf[(corner / 3)];
                    var place = m_cornerPlace[corner];
                    var point = positions[m_corners[corner]];

                    m_clusterLow[cluster] = Vector3.Min(value1: m_clusterLow[cluster], value2: point);
                    m_clusterHigh[cluster] = Vector3.Max(value1: m_clusterHigh[cluster], value2: point);
                    if (first[place] < 0) { first[place] = cluster; } else if (first[place] != cluster) { m_seam[place] = true; }
                }
                m_seamsHeld = true;
            }

            m_rejected = scope.LongMap<long>(capacity: m_places);
            m_fanStart = scope.Buffer<int>(length: (m_places + 1));
            m_fan = scope.Buffer<int>(length: (3 * m_triangles));
            m_filled = scope.Buffer<int>(length: m_places);
            m_touched = scope.Buffer<int>(length: m_places, clear: true);
            m_changed = scope.Buffer<int>(length: m_places, clear: true);
            m_revisions = scope.Buffer<int>(length: m_places, clear: true);
            m_fanEdges = scope.Buffer<(int, int)>(length: m_places);
            m_blockStamp = scope.Buffer<int>(length: m_places, clear: true);
            m_quiet = scope.Buffer<bool>(length: m_places, clear: true);
            m_quietRevision = scope.Buffer<int>(length: m_places);
            m_quietThrough = scope.Buffer<int>(length: m_places);
            m_quietCost = scope.Buffer<double>(length: m_places);
            m_leftOut = scope.Buffer<int>(length: m_places, clear: true);
            m_woken = scope.Buffer<(double, int)>(length: m_places);
            m_kind = scope.Buffer<byte>(length: m_places, clear: true);
            m_slideTargets = scope.Buffer<int>(length: (SlideTargets * m_places), clear: true);
            m_slideCounts = scope.Buffer<int>(length: m_places, clear: true);
            m_prices = scope.RentList<(double Cost, int Place)>(capacity: (3 * m_triangles));
            m_priceRuns = scope.Buffer<(int, int, int, bool)>(length: m_places, clear: true);
            m_candidateKeys = scope.Buffer<(double, int)>(length: m_places);
            m_candidatePlaces = scope.Buffer<int>(length: m_places);
            m_specFirst = scope.Buffer<int>(length: SpeculativeBlock);
            m_specCount = scope.Buffer<int>(length: SpeculativeBlock);
            m_specTarget = scope.RentList<int>(capacity: (8 * SpeculativeBlock));
            m_specOutcome = scope.RentList<Outcome>(capacity: (8 * SpeculativeBlock));
            m_specThrough = scope.RentList<int>(capacity: (8 * SpeculativeBlock));
            m_quadrics = scope.Buffer<double>(length: (10 * m_places), clear: true);
            m_tracked = scope.Buffer<int>(length: m_triangles);
            m_stray = scope.Buffer<double>(length: m_triangles, clear: true);
            m_tracked.GetSpan().Fill(value: -1);
            m_lineX = scope.RentList<double>(capacity: ((m_places / 4) + 16));
            m_lineY = scope.RentList<double>(capacity: ((m_places / 4) + 16));
            m_lineZ = scope.RentList<double>(capacity: ((m_places / 4) + 16));
            m_lineDistance = scope.RentList<double>(capacity: ((m_places / 4) + 16));
            m_lineNext = scope.RentList<int>(capacity: ((m_places / 4) + 16));

            // A vertex's first material is a sample at once; only a vertex whose triangles carry a second material asks the
            // set, so the samples keep their first-corner order.
            var firstMaterial = scope.Buffer<uint>(length: vertices);
            var hasMaterial = scope.Buffer<bool>(length: vertices, clear: true);
            var sampleOf = new HashSet<(int Vertex, uint Material)>();
            var sampleTriangles = scope.RentList<int>(capacity: vertices);
            var sampleVertices = scope.RentList<int>(capacity: vertices);

            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                var material = m_materials[triangle];

                for (var corner = 0; (corner < 3); corner++) {
                    var vertex = m_corners[((3 * triangle) + corner)];
                    var fresh = !hasMaterial[vertex];

                    if (fresh) {
                        hasMaterial[vertex] = true;
                        firstMaterial[vertex] = material;
                    } else if (firstMaterial[vertex] != material) {
                        fresh = sampleOf.Add(item: (vertex, material));
                    }
                    if (fresh) {
                        sampleTriangles.Add(item: triangle);
                        sampleVertices.Add(item: vertex);
                    }
                }
            }

            var samples = sampleTriangles.Length;

            m_sampleCount = samples;
            m_sampleVertex = scope.Buffer<int>(length: samples);
            sampleVertices.GetSpan().CopyTo(destination: m_sampleVertex.GetSpan());
            m_sampleMaterial = scope.Buffer<uint>(length: samples);
            m_sampleDistance = scope.Buffer<double>(length: samples, clear: true);
            m_sampleNext = scope.Buffer<int>(length: samples);
            for (var sample = 0; (sample < samples); sample++) {
                m_sampleMaterial[sample] = m_materials[sampleTriangles[sample]];
                Track(sample: sample, triangle: sampleTriangles[sample]);
            }

            Fans();
            for (var place = 0; (place < m_places); place++) {
                m_kind[place] = Kind(penalize: true, place: place);
                Lines(fan: Alive(fan: m_from, place: place), into: m_lineNeighbours, place: place);
                for (var at = 0; (at < m_lineNeighbours.Count); at++) {
                    var other = m_lineNeighbours[at];

                    if (at == 0) { AddLine(a: place, b: other, x: m_point[place].X, y: m_point[place].Y, z: m_point[place].Z); }
                    if (place < other) { AddLine(a: place, b: other, x: (0.5 * (m_point[place].X + m_point[other].X)), y: (0.5 * (m_point[place].Y + m_point[other].Y)), z: (0.5 * (m_point[place].Z + m_point[other].Z))); }
                }
            }
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (m_alive[triangle]) { AddFace(triangle: triangle); }
            }
            m_feature = scope.Buffer<double>(length: m_places);
            if (m_waivesNormals) { FeatureDistances(scope: scope); }
        }
        // A worker for Speculate: every table is the shared collapser's, read and never written, and every scratch field
        // (the lists and arrays a measure fills) is the worker's own, so workers measure side by side.
        private Collapser(Collapser shared) {
            Take();
            m_placeOf = shared.m_placeOf;
            m_point = shared.m_point;
            m_normals = shared.m_normals;
            m_ownVertices = shared.m_ownVertices;
            m_madeVertices = shared.m_madeVertices;
            m_feature = shared.m_feature;
            m_waivesNormals = shared.m_waivesNormals;
            m_corners = shared.m_corners;
            m_cornerPlace = shared.m_cornerPlace;
            m_materials = shared.m_materials;
            m_alive = shared.m_alive;
            m_quadrics = shared.m_quadrics;
            m_places = shared.m_places;
            m_triangles = shared.m_triangles;
            m_carriesMaterials = shared.m_carriesMaterials;
            m_sampleVertex = shared.m_sampleVertex;
            m_sampleCount = shared.m_sampleCount;
            m_sampleMaterial = shared.m_sampleMaterial;
            m_sampleDistance = shared.m_sampleDistance;
            m_sampleNext = shared.m_sampleNext;
            m_tracked = shared.m_tracked;
            m_lineX = shared.m_lineX;
            m_lineY = shared.m_lineY;
            m_lineZ = shared.m_lineZ;
            m_lineDistance = shared.m_lineDistance;
            m_lineNext = shared.m_lineNext;
            m_products = shared.m_products;
            m_lines = shared.m_lines;
            m_stray = shared.m_stray;
            m_fanStart = shared.m_fanStart;
            m_fan = shared.m_fan;
            m_filled = shared.m_filled;
            m_touched = shared.m_touched;
            m_changed = shared.m_changed;
            m_revisions = shared.m_revisions;
            m_rejected = shared.m_rejected;
            m_errors = shared.m_errors;
            m_cosines = shared.m_cosines;
            m_kind = shared.m_kind;
            m_slideTargets = shared.m_slideTargets;
            m_slideCounts = shared.m_slideCounts;
            m_lineLimit = shared.m_lineLimit;
            m_candidateKeys = shared.m_candidateKeys;
            m_candidatePlaces = shared.m_candidatePlaces;
            m_blockStamp = shared.m_blockStamp;
            m_specFirst = shared.m_specFirst;
            m_specCount = shared.m_specCount;
            m_specTarget = shared.m_specTarget;
            m_specOutcome = shared.m_specOutcome;
            m_specThrough = shared.m_specThrough;
            m_quiet = shared.m_quiet;
            m_quietRevision = shared.m_quietRevision;
            m_quietThrough = shared.m_quietThrough;
            m_quietCost = shared.m_quietCost;
            m_leftOut = shared.m_leftOut;
            m_woken = shared.m_woken;
            m_prices = shared.m_prices;
            m_priceRuns = shared.m_priceRuns;
            m_fanEdges = shared.m_fanEdges;
            m_clusterOf = shared.m_clusterOf;
            m_clusters = shared.m_clusters;
            m_clusterLow = shared.m_clusterLow;
            m_clusterHigh = shared.m_clusterHigh;
            m_seam = shared.m_seam;
            m_seamsHeld = shared.m_seamsHeld;
        }

        /// <summary>Gets the vertices the collapses made past the mesh's own.</summary>
        public int MadeVertices => m_madeVertices.Count;

        // Writes each made vertex's position (its place's) and normal, in the order the vertex ids past the mesh's own name them.
        public void WriteMade(Span<Vector3> positions, Span<Vector3> normals) {
            for (var at = 0; (at < m_madeVertices.Count); at++) {
                var (place, normal) = m_madeVertices[at];

                positions[at] = new Vector3(x: ((float)m_point[place].X), y: ((float)m_point[place].Y), z: ((float)m_point[place].Z));
                normals[at] = normal;
            }
        }

        // Each place's distance along the mesh's edges to the nearest place where its surface turns sharply: one whose copies'
        // normals part by more than SharpDegrees, or an end of an edge whose two ends' normals do. Dijkstra from every such
        // place over the mesh's own edges, the places finalized in order of distance.
        private void FeatureDistances(RentedScope scope) {
            var sharp = Math.Cos(d: ((SharpDegrees * Math.PI) / 180.0));
            var distances = m_feature.GetSpan()[..m_places];
            var heap = scope.Buffer<(double Distance, int Place)>(length: ((6 * m_triangles) + m_places)).GetSpan();
            var count = 0;

            distances.Fill(value: double.PositiveInfinity);
            for (var place = 0; (place < m_places); place++) {
                var first = -1;

                foreach (var triangle in Alive(fan: m_from, place: place)) {
                    var copy = m_corners[CornerAt(place: place, triangle: triangle)];

                    if (first < 0) { first = copy; continue; }
                    if ((copy != first) && Parts(a: m_normals[first], b: m_normals[copy], sharp: sharp)) {
                        distances[place] = 0.0;
                        BinaryHeap.Push(count: ref count, heap: heap, item: (0.0, place), order: default(ItemThenIndexOrder<double>));
                        break;
                    }
                }
            }
            for (var corner = 0; (corner < (3 * m_triangles)); corner++) {
                var other = NextCorner(corner: corner);

                if (!m_alive[(corner / 3)] || !Parts(a: m_normals[m_corners[corner]], b: m_normals[m_corners[other]], sharp: sharp)) { continue; }
                foreach (var place in (ReadOnlySpan<int>)[m_cornerPlace[corner], m_cornerPlace[other]]) {
                    if (distances[place] == 0.0) { continue; }
                    distances[place] = 0.0;
                    BinaryHeap.Push(count: ref count, heap: heap, item: (0.0, place), order: default(ItemThenIndexOrder<double>));
                }
            }
            while (count > 0) {
                var (distance, place) = BinaryHeap.Pop(count: ref count, heap: heap, order: default(ItemThenIndexOrder<double>));

                if (distance > distances[place]) { continue; }
                foreach (var triangle in Alive(fan: m_from, place: place)) {
                    var (_, next, after) = Around(place: place, triangle: triangle);

                    foreach (var other in (ReadOnlySpan<int>)[next, after]) {
                        var (dx, dy, dz) = ((m_point[place].X - m_point[other].X), (m_point[place].Y - m_point[other].Y), (m_point[place].Z - m_point[other].Z));
                        var through = (distance + Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz))));

                        if (through < distances[other]) {
                            distances[other] = through;
                            BinaryHeap.Push(count: ref count, heap: heap, item: (through, other), order: default(ItemThenIndexOrder<double>));
                        }
                    }
                }
            }
        }
        // Whether two normals part by more than the angle whose cosine is sharp.
        private static bool Parts(Vector3 a, Vector3 b, double sharp) => (Vector3.Dot(vector1: a, vector2: b) < (sharp * a.Length() * b.Length()));

        // Collapses until no pass takes enough within the level's bounds, then returns the level the alive triangles make,
        // or null, with no plane laid out, when they are more than KeptShare of `kept`, the triangles of the level before.
        // Levels run in order, each from the state the finer one left: the budget a refusal is keyed by is the level plus one.
        public SdfMeshLevel? Run(int kept, int level) {
            var error = m_errors[level];
            var cosine = m_cosines[level];

            // A refusal of the shape or the link reads no budget and stands into the next level while its revisions do;
            // a measured refusal stands through the budgets its measure showed cannot take it.
            m_budget = (level + 1);
            for (var pass = 0; (pass < MaxPasses); pass++) {
                // The constructor's lists and kinds stand for the first pass; later, a place no collapse changed keeps its
                // triangles and its kind.
                if (m_pass > 0) {
                    Fans();
                    for (var place = 0; (place < m_places); place++) {
                        if ((m_touched[place] == m_pass) || (m_changed[place] == m_pass)) { m_kind[place] = Kind(penalize: false, place: place); }
                    }
                }

                var taken = Pass(cosine: cosine, error: error);

                if ((taken == 0) || (taken < (m_aliveTriangles / (2 * TailShare)))) { break; }
            }

            if (m_aliveTriangles > (KeptShare * kept)) { return null; }

            var indices = RentedScope.Allocate<uint>(m_products, 3 * m_aliveTriangles);
            var materials = RentedScope.Allocate<uint>(m_products, m_carriesMaterials ? m_aliveTriangles : 0);
            var at = 0;

            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                indices.Span[(3 * at)] = ((uint)m_corners[(3 * triangle)]);
                indices.Span[((3 * at) + 1)] = ((uint)m_corners[((3 * triangle) + 1)]);
                indices.Span[((3 * at) + 2)] = ((uint)m_corners[((3 * triangle) + 2)]);
                if (m_carriesMaterials) { materials.Span[at] = m_materials[triangle]; }
                at++;
            }

            var largest = 0.0;

            foreach (var distance in m_sampleDistance.GetSpan().Slice(start: 0, length: m_sampleCount)) {
                largest = Math.Max(val1: largest, val2: distance);
            }
            foreach (var distance in m_lineDistance.GetSpan()) {
                largest = Math.Max(val1: largest, val2: distance);
            }
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                largest = (m_alive[triangle] ? Math.Max(val1: largest, val2: m_stray[triangle]) : largest);
            }

            return new SdfMeshLevel(Error: ((float)largest), Indices: indices, TriangleMaterials: materials);
        }

        private int Pass(double error, double cosine) {
            m_pass++;

            var count = 0;
            var quiet = 0L;

            m_wokenCount = 0;
            for (var place = 0; (place < m_places); place++) {
                if (m_kind[place] == Locked) { continue; }

                var prices = Prices(place);
                var cost = (prices.IsEmpty ? double.PositiveInfinity : prices[0].Cost);

                // A quiet place still quiet would skip every target by its kept refusals: the pass leaves it out, and a
                // commit that moves its revision wakes it at its turn (Wake).
                if (m_quiet[place] && (m_quietRevision[place] == m_revisions[place]) && (m_budget <= m_quietThrough[place])) {
                    m_quietCost[place] = cost;
                    m_leftOut[place] = m_pass;
                    quiet++;
                    continue;
                }
                m_quiet[place] = false;
                m_candidateKeys[count] = (cost, place);
                count++;
            }

            // Candidates are taken in one total order, cheapest first and the lower place among equal costs, so where a
            // candidate stands never depends on which other places are listed (a woken place's turn, Wake).
            var keys = m_candidateKeys.GetSpan()[..count];

            keys.Sort();
            for (var at = 0; (at < count); at++) { m_candidatePlaces[at] = keys[at].Place; }

            var taken = 0;
            var tried = 0L;
            var speculated = 0L;
            var redone = 0L;
            // A pass with enough places is taken in blocks whose candidates are measured side by side first (Speculate);
            // any other runs every candidate in order, as a block of one does.
            var parallel = (count >= SpeculativeCandidates);

            for (var start = 0; (start < count); start += SpeculativeBlock) {
                var end = Math.Min(val1: count, val2: (start + SpeculativeBlock));
                // A block speculates only while other threads could help, since its caller checks and partly repeats
                // what they measured; either way the pass takes the same collapses, so only the speculated and redone
                // counts follow the machine's load.
                var speculating = (parallel && !PoolFork.Saturated);

                if (speculating) {
                    speculated += Speculate(cosine: cosine, end: end, error: error, start: start);
                }
                for (var at = start; (at < end); at++) {
                    var place = m_candidatePlaces[at];

                    taken += TakeWoken(before: m_candidateKeys[at], cosine: cosine, error: error, tried: ref tried);
                    m_taking = m_candidateKeys[at];

                    var first = (speculating ? m_specFirst[(at - start)] : -1);
                    var clean = ((first >= 0) && Clean(place: place));

                    redone += (((first >= 0) && !clean) ? 1 : 0);
                    if (!clean) {
                        if (m_touched[place] == m_pass) { continue; }
                        if ((m_changed[place] == m_pass) && ((m_kind[place] = Kind(penalize: false, place: place)) == Locked)) { continue; }
                    }
                    taken += TryPlace(cosine: cosine, error: error, place: place, records: (clean ? first : 0), recordsEnd: (clean ? (first + m_specCount[(at - start)]) : 0), tried: ref tried);
                }
            }
            taken += TakeWoken(before: (double.PositiveInfinity, int.MaxValue), cosine: cosine, error: error, tried: ref tried);

            ProcessWork.Count(kind: Passes);
            ProcessWork.Add(amount: count, kind: Priced);
            ProcessWork.Add(amount: tried, kind: Tried);
            ProcessWork.Add(amount: taken, kind: Taken);
            ProcessWork.Add(amount: speculated, kind: Speculated);
            ProcessWork.Add(amount: redone, kind: Redone);
            ProcessWork.Add(amount: quiet, kind: Quiet);
            return taken;
        }

        // Quiet places: a place whose last turn skipped or refused every target, each refusal kept by the place's own
        // revision and the level's budget alone (no link refusal, no unkept refusal, no target skipped as touched), skips
        // every target again until its revision moves or the budget passes what its measured refusals stand through.
        //
        // Invariant: a pass that leaves quiet places out takes exactly the collapses and refusals a pass listing them
        // takes. At its turn a listed quiet place tries nothing and writes nothing, and only Commit moves a revision; a
        // commit that moves a left-out place's revision wakes it (Wake), and the pass takes it at the turn its listed
        // key would have had, the order being total (cost, then place), or not at all if that turn has passed, where the
        // listed place stood quiet. Enforced by the one revision writer, Commit, calling Wake, and by TryPlace alone
        // making a place quiet. Where it stops: a new input a kept refusal reads beyond the place's revision and the
        // budget (a link refusal reads the target's revision, so a place holding one never goes quiet) must wake or
        // disqualify the place here.

        // Takes the woken places whose turn comes before `before`, cheapest first.
        private int TakeWoken(in (double Cost, int Place) before, double error, double cosine, ref long tried) {
            var taken = 0;

            while ((m_wokenCount > 0) && (m_woken[0].CompareTo(other: before) < 0)) {
                var key = BinaryHeap.Pop(count: ref m_wokenCount, heap: m_woken.GetSpan(), order: default(ItemThenIndexOrder<double>));
                var place = key.Place;

                m_taking = key;
                if (m_touched[place] == m_pass) { continue; }
                if ((m_changed[place] == m_pass) && ((m_kind[place] = Kind(penalize: false, place: place)) == Locked)) { continue; }
                taken += TryPlace(cosine: cosine, error: error, place: place, records: 0, recordsEnd: 0, tried: ref tried);
            }

            return taken;
        }
        // Wakes a quiet place whose revision a commit moved: one the pass left out takes its turn later in the pass when
        // that turn is still to come.
        private void Wake(int place) {
            if (!m_quiet[place]) { return; }
            m_quiet[place] = false;
            if (m_leftOut[place] != m_pass) { return; }
            m_leftOut[place] = 0;

            var key = (m_quietCost[place], place);

            if (key.CompareTo(other: m_taking) > 0) { BinaryHeap.Push(count: ref m_wokenCount, heap: m_woken.GetSpan(), item: key, order: default(ItemThenIndexOrder<double>)); }
        }
        // Tries a place's targets cheapest first, taking the first collapse every bound holds and keeping each refusal
        // until what it read changes; returns 1 when it took one, and makes the place quiet when every refusal it kept
        // stands on its own revision and the budget alone. The records between `records` and `recordsEnd` are a clean
        // speculation's outcomes of this same loop (Speculate), taken in place of measuring again; the loop measures
        // every target past them, and the taken collapse, itself.
        private int TryPlace(int place, double error, double cosine, int records, int recordsEnd, ref long tried) {
            var prepared = false;
            var quiet = true;
            var through = int.MaxValue;

            foreach (var (_, target) in Prices(place)) {
                if (m_touched[target] == m_pass) { quiet = false; continue; }
                if (Refused(place, target)) {
                    _ = m_rejected.TryGetValue(key: PackedIntPair.Pack(high: place, low: target), value: out var rejected);

                    var low = PackedIntPair.Low(value: rejected);

                    quiet &= (low < 0);
                    through = ((low < AnyRevision) ? Math.Min(val1: through, val2: (AnyRevision - low)) : through);
                    continue;
                }
                Outcome outcome;

                if ((records < recordsEnd) && (m_specTarget[records] == target) && (m_specOutcome[records] != Outcome.Taken)) {
                    outcome = m_specOutcome[records];
                    m_refusedThrough = m_specThrough[records];
                    records++;
                } else {
                    records = recordsEnd;
                    if (!prepared) { PrepareFrom(place); prepared = true; }
                    outcome = TryCollapse(cosine: cosine, error: error, from: place, to: target);
                }
                tried++;
                if (outcome == Outcome.Taken) {
                    CommitTaken(from: place, to: target);
                    return 1;
                }
                // A refusal that read the neighbour's vertices (a corner's third face matched to one) is kept by no
                // revision; it is tried again.
                if (outcome == Outcome.RefusedUnkept) { quiet = false; continue; }
                // Only the link test reads the neighbour's fan; every other refusal reads the place's own fan, its
                // samples and lines, and the neighbour's fixed position, so it stands until the place's revision moves.
                m_rejected.Set(key: PackedIntPair.Pack(high: place, low: target), value: PackedIntPair.Pack(high: m_revisions[place], low: outcome switch {
                    Outcome.RefusedLinks => m_revisions[target],
                    Outcome.RefusedMeasure => MeasuredAt(budget: m_refusedThrough),
                    _ => AnyRevision,
                }));
                quiet &= (outcome != Outcome.RefusedLinks);
                through = ((outcome == Outcome.RefusedMeasure) ? Math.Min(val1: through, val2: m_refusedThrough) : through);
            }
            if (quiet) {
                m_quiet[place] = true;
                m_quietRevision[place] = m_revisions[place];
                m_quietThrough[place] = through;
            }

            return 0;
        }

        // The candidates a pass must hold before its blocks are speculated, and the candidates a block holds.
        private const int SpeculativeCandidates = 4096;
        private const int SpeculativeBlock = 1024;

        // Measures a block of a pass's candidates side by side before the pass takes them in order, recording each one's
        // outcomes (m_specFirst, m_specCount and the records) for TryPlace to take in place of measuring again.
        //
        // Invariant: a pass that speculates takes exactly the collapses, refusals and kept refusals a pass taking every
        // candidate in order takes, so the levels are the same function of the mesh. A speculation measures against the
        // tables as the block's earlier commits found them; it stands only where no commit of the block since wrote
        // anything it read, which Clean decides from the places Commit stamps. Enforced by the two doors: a worker reads
        // the shared tables and writes only its own scratch and its candidate's records (the sharing constructor, IJobPartitions.Run), and only Commit writes
        // what a measure reads, stamping every place of both fans it changes. Where it stops: a new write a commit makes
        // outside those places, or a new read a measure makes outside the place's fan, would let a stale speculation
        // stand, so either must stamp or check its places here. Returns the candidates speculated.
        private int Speculate(int start, int end, double error, double cosine) {
            m_block++;
            m_speculating.Clear();

            var records = 0;

            // Each candidate's preamble runs here, in order, as TryPlace's caller would run it: a candidate that a commit
            // of this block then reaches is stamped, so the pass runs its preamble again.
            for (var at = start; (at < end); at++) {
                var place = m_candidatePlaces[at];

                m_specFirst[(at - start)] = -1;
                if (m_touched[place] == m_pass) { continue; }
                if ((m_changed[place] == m_pass) && ((m_kind[place] = Kind(penalize: false, place: place)) == Locked)) { continue; }
                m_specFirst[(at - start)] = records;
                records += Prices(place).Length;
                m_speculating.Add(item: (at - start));
            }
            m_specTarget.Resize(length: records, clear: false, preserve: false);
            m_specOutcome.Resize(length: records, clear: false, preserve: false);
            m_specThrough.Resize(length: records, clear: false, preserve: false);
            (m_specStart, m_specError, m_specCosine) = (start, error, cosine);
            PoolFork.Run(body: this, partitions: PartitionRuns.Count(count: m_speculating.Count, run: SpeculativeRun));

            return m_speculating.Count;
        }
        // Adds the samples this collapser and its workers listed and measured to the process's work.
        public void CountSamples() {
            var (listed, measured) = (m_listedSamples, m_measuredSamples);

            foreach (var worker in m_workers) { (listed, measured) = ((listed + worker.m_listedSamples), (measured + worker.m_measuredSamples)); }
            ProcessWork.Add(amount: listed, kind: Listed);
            ProcessWork.Add(amount: measured, kind: Measured);
        }
        // The speculated candidates one partition of a block measures.
        private const int SpeculativeRun = 16;
        // The block Speculate is measuring: its first candidate and the level's bounds.
        private int m_specStart;
        private double m_specError;
        private double m_specCosine;

        // One partition of Speculate's block, on a worker: a collapser sharing this one's tables, with scratch of its own,
        // at this pass and budget, measures each of the partition's candidates.
        void IJobPartitions.Run(int partition) {
            var worker = (m_workers.TryPop(result: out var kept) ? kept : new Collapser(shared: this));
            var (first, end) = PartitionRuns.Of(count: m_speculating.Count, partition: partition, run: SpeculativeRun);

            worker.m_pass = m_pass;
            worker.m_budget = m_budget;
            try {
                for (var index = first; (index < end); index++) {
                    var slot = m_speculating[index];

                    m_specCount[slot] = worker.Evaluate(cosine: m_specCosine, error: m_specError, first: m_specFirst[slot], place: m_candidatePlaces[(m_specStart + slot)], owner: this);
                }
            } finally {
                m_workers.Push(item: worker);
            }
        }
        // Whether a speculated candidate's records still stand: no commit of the block stamped the place or a place of its
        // fan. Every target is a place of the fan, and a commit that changes a triangle stamps all its places, so a
        // changed fan, a changed target fan and every refusal, quadric, sample and line the measures read show here.
        private bool Clean(int place) {
            if (m_blockStamp[place] == m_block) { return false; }
            for (var at = m_fanStart[place]; (at < m_fanStart[(place + 1)]); at++) {
                var triangle = m_fan[at];

                if (!m_alive[triangle]) { continue; }
                for (var corner = (3 * triangle); (corner < ((3 * triangle) + 3)); corner++) {
                    if (m_blockStamp[m_cornerPlace[corner]] == m_block) { return false; }
                }
            }

            return true;
        }
        // On a worker: TryPlace's loop with nothing written but this worker's scratch and the candidate's records from
        // `first` on, stopping at a taken collapse, which the owner measures again and commits. Returns the records.
        private int Evaluate(Collapser owner, int place, int first, double error, double cosine) {
            var prepared = false;
            var count = 0;
            ref readonly var run = ref m_priceRuns[place];

            if (!run.Ready) { throw new InvalidOperationException(message: "A speculated place is priced before its block is measured."); }
            foreach (var (_, target) in m_prices.GetSpan().Slice(start: run.Start, length: run.Count)) {
                if (m_touched[target] == m_pass || Refused(place, target)) { continue; }
                if (!prepared) { PrepareFrom(place); prepared = true; }

                var outcome = TryCollapse(cosine: cosine, error: error, from: place, to: target);

                owner.m_specTarget[(first + count)] = target;
                owner.m_specOutcome[(first + count)] = outcome;
                owner.m_specThrough[(first + count)] = m_refusedThrough;
                count++;
                if (outcome == Outcome.Taken) { break; }
            }

            return count;
        }
        private ReadOnlySpan<(double Cost, int Place)> Prices(int place) {
            ref var run = ref m_priceRuns[place];
            if (!run.Ready) {
                Targets(place);
                var count = m_targetPlaces.Count;
                if (count > run.Capacity) {
                    run.Start = m_prices.Length;
                    run.Capacity = Math.Max(count, 2 * run.Capacity);
                    _ = m_prices.Extend(count: run.Capacity);
                }
                var prices = m_prices.GetSpan().Slice(run.Start, count);
                for (var index = 0; index < count; index++) {
                    var target = m_targetPlaces[index];
                    prices[index] = (Cost(place, target), target);
                }
                prices.Sort();
                run.Count = count;
                run.Ready = true;
            }
            return m_prices.GetSpan().Slice(run.Start, run.Count);
        }

        private bool Refused(int from, int to) {
            if (!m_rejected.TryGetValue(key: PackedIntPair.Pack(high: from, low: to), value: out var rejected) || (PackedIntPair.High(value: rejected) != m_revisions[from])) { return false; }

            var low = PackedIntPair.Low(value: rejected);

            // A measured refusal's word falls as its budget rises, so it stands while this level's budget is no higher.
            return ((low < AnyRevision) ? (MeasuredAt(budget: m_budget) >= low) : ((low == AnyRevision) || (low == m_revisions[to])));
        }
        // The neighbour word of a refusal the level's distance and normal budgets decided, standing through a budget:
        // below every other word.
        private static int MeasuredAt(int budget) => (AnyRevision - budget);

        // The neighbour revision a refusal that never read the neighbour's fan records; no revision is negative.
        private const int AnyRevision = -1;

        private enum Outcome : byte { Taken, Refused, RefusedLinks, RefusedMeasure, RefusedUnkept }

        // A refusal after the place's vertices were mapped: one whose mapping read the neighbour's vertices (a corner's
        // other faces, CopyAt) depends on the neighbour's fan, which no revision of the place guards, so it is not kept.
        private Outcome Refusal(Outcome outcome) => ((m_otherCopies.Count > 0) ? Outcome.RefusedUnkept : outcome);

        // Lists a place's alive triangles: the pass's list less those a collapse this pass removed.
        private List<int> Alive(int place, List<int> fan) {
            fan.Clear();
            for (var at = m_fanStart[place]; (at < m_fanStart[(place + 1)]); at++) {
                if (m_alive[m_fan[at]]) { fan.Add(item: m_fan[at]); }
            }

            return fan;
        }
        // Lists the places a place may collapse onto: any neighbour for a free place, those along its creases and material
        // edges for one that slides.
        private void Targets(int place) {
            m_targetPlaces.Clear();
            if (m_kind[place] == Slides) {
                // Kind ran on this fan when it last changed (Run, Pass), so its slide targets stand.
                var count = m_slideCounts[place];

                if (count < 0) {
                    _ = Kind(penalize: false, place: place);
                    m_targetPlaces.AddRange(collection: m_features);
                } else {
                    m_targetPlaces.AddRange(source: ((ReadOnlySpan<int>)m_slideTargets.GetSpan().Slice(start: (SlideTargets * place), length: count)));
                }

                return;
            }

            foreach (var triangle in Alive(fan: m_from, place: place)) {
                var (_, next, _) = Around(place: place, triangle: triangle);

                m_targetPlaces.Add(item: next);
            }
        }
        // Whether a triangle's three corners lie at three places.
        private bool Distinct(int triangle) {
            var a = m_cornerPlace[(3 * triangle)];
            var b = m_cornerPlace[((3 * triangle) + 1)];
            var c = m_cornerPlace[((3 * triangle) + 2)];

            return ((a != b) && (b != c) && (a != c));
        }
        // A triangle seen from one of its places: its corner there, then the places after it in winding order.
        private (int Corner, int Next, int After) Around(int place, int triangle) {
            var first = (3 * triangle);

            for (var corner = 0; (corner < 2); corner++) {
                if (m_cornerPlace[(first + corner)] == place) {
                    return ((first + corner), m_cornerPlace[(first + ((corner + 1) % 3))], m_cornerPlace[(first + ((corner + 2) % 3))]);
                }
            }

            return ((first + 2), m_cornerPlace[first], m_cornerPlace[(first + 1)]);
        }
        private int CornerAt(int place, int triangle) {
            var first = (3 * triangle);

            return ((m_cornerPlace[first] == place) ? first : ((m_cornerPlace[(first + 1)] == place) ? (first + 1) : (first + 2)));
        }
        private static int NextCorner(int corner) => (((corner % 3) == 2) ? (corner - 2) : (corner + 1));
        private static int PreviousCorner(int corner) => (((corner % 3) == 0) ? (corner + 2) : (corner - 1));
        // Lists each place's alive triangles.
        private void Fans() {
            m_fanStart.GetSpan().Clear();
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                for (var corner = 0; (corner < 3); corner++) {
                    m_fanStart[(m_cornerPlace[((3 * triangle) + corner)] + 1)]++;
                }
            }
            for (var place = 0; (place < m_places); place++) {
                m_fanStart[(place + 1)] += m_fanStart[place];
            }

            m_filled.GetSpan().Clear();
            for (var triangle = 0; (triangle < m_triangles); triangle++) {
                if (!m_alive[triangle]) { continue; }

                for (var corner = 0; (corner < 3); corner++) {
                    var place = m_cornerPlace[((3 * triangle) + corner)];

                    m_fan[(m_fanStart[place] + m_filled[place]++)] = triangle;
                }
            }
        }
        // A place's kind (KindOf), and locked wherever it is a seam the clusters share while the seams are held: the one
        // door every kind passes, so no clustered run moves a seam.
        private byte Kind(int place, bool penalize) {
            var kind = KindOf(penalize: penalize, place: place);

            return ((m_seamsHeld && m_seam[place]) ? Locked : kind);
        }
        // Sorts a place into free (on at most one crease or material edge, which ends there), sliding along its creases
        // and material edges, or locked where its triangles do not close one ring of edges two triangles share; lists the
        // far places of those edges (m_features) and, with `penalize`, adds the planes along them to its quadric. Which
        // slide keeps every side's vertices is the collapse's to judge.
        private byte KindOf(int place, bool penalize) {
            var fan = Alive(fan: m_from, place: place);
            var count = fan.Count;
            var seams = m_seams;

            m_features.Clear();
            seams.Clear();
            if (count < 3) { return Locked; }
            if (m_fanCorner.Length < count) {
                m_fanCorner = new int[(2 * count)];
                m_fanNext = new int[(2 * count)];
                m_fanAfter = new int[(2 * count)];
                m_fanTwin = new int[(2 * count)];
            }
            for (var at = 0; (at < count); at++) {
                (m_fanCorner[at], m_fanNext[at], m_fanAfter[at]) = Around(place: place, triangle: fan[at]);
                m_fanEdges[m_fanNext[at]] = (-1, -1);
                m_fanEdges[m_fanAfter[at]] = (-1, -1);
            }
            // Each neighbour must have exactly one outgoing and one incoming edge. Index those edges once rather
            // than searching the complete fan for every edge, then searching it again to walk the ring.
            for (var at = 0; (at < count); at++) {
                ref var next = ref m_fanEdges[m_fanNext[at]];
                ref var after = ref m_fanEdges[m_fanAfter[at]];

                next.Outgoing = ((next.Outgoing == -1) ? at : -2);
                after.Incoming = ((after.Incoming == -1) ? at : -2);
            }

            for (var at = 0; (at < count); at++) {
                var next = m_fanNext[at];
                var edge = m_fanEdges[next];
                // Keep the original edge order, including the penalty prefix before a malformed edge refuses the
                // fan. Those quadrics also price a locked place as a collapse target.
                if ((edge.Outgoing < 0) || (edge.Incoming < 0)) { return Locked; }
                var incoming = edge.Incoming;

                m_fanTwin[at] = incoming;

                var corner = m_fanCorner[at];
                var twinCorner = m_fanCorner[incoming];
                var feature = ((m_corners[corner] != m_corners[twinCorner]) || (m_corners[NextCorner(corner: corner)] != m_corners[PreviousCorner(corner: twinCorner)]) || (m_materials[fan[at]] != m_materials[fan[incoming]]));

                if (!feature) { continue; }

                if (penalize) {
                    AddEdge(other: next, place: place, triangle: fan[at]);
                    AddEdge(other: next, place: place, triangle: fan[incoming]);
                }
                if (m_corners[corner] != m_corners[twinCorner]) {
                    seams.Add(item: next);
                } else {
                    m_features.Add(item: next);
                }
            }
            if (!OneRing(count: count)) { return Locked; }

            // A place with several vertices slides only along its creases (its seams, two or more about a ring): each side of
            // the collapsed edge keeps its own vertices, and any other vertex takes a neighbour's by normal (Map).
            var copies = 1;

            for (var at = 1; (at < count); at++) {
                if (m_corners[m_fanCorner[at]] != m_corners[m_fanCorner[0]]) {
                    copies++;
                    break;
                }
            }
            if (copies > 1) {
                m_features.Clear();
                m_features.AddRange(collection: seams);

                return Slide(place: place);
            }

            return ((m_features.Count <= 1) ? Free : Slide(place: place));
        }
        // Keeps a sliding place's targets (m_features) for Targets, which reads them until the place's fan next changes.
        private byte Slide(int place) {
            var count = m_features.Count;

            m_slideCounts[place] = ((count <= SlideTargets) ? count : -1);
            if (count <= SlideTargets) { CollectionsMarshal.AsSpan(list: m_features).CopyTo(destination: m_slideTargets.GetSpan().Slice(start: (SlideTargets * place), length: SlideTargets)); }

            return Slides;
        }
        // Whether Kind's bijection closes one ring rather than several disjoint rings. Follow the incoming twins;
        // reversing the old outgoing walk visits the same cycle, without searching the fan at every step.
        private bool OneRing(int count) {
            var at = 0;
            var steps = 0;

            do {
                at = m_fanTwin[at];
                steps++;
            } while ((at > 0) && (steps <= count));

            return ((at == 0) && (steps == count));
        }
        // Tries to collapse one place onto a neighbour, taking it only where the mesh stays a closed ring of untwisted
        // triangles about the neighbour and every sample of the triangles it changes stays within the bounds.
        private Outcome TryCollapse(int from, int to, double error, double cosine) {
            var fan = m_from;
            var first = -1;
            var second = -1;
            var firstAt = -1;
            var secondAt = -1;

            for (var index = 0; (index < fan.Count); index++) {
                ref readonly var original = ref m_previous[index];

                if (original.Next == to) { (first, firstAt) = (fan[index], index); }
                if (original.After == to) { (second, secondAt) = (fan[index], index); }
            }
            if ((first < 0) || (second < 0)) { return Outcome.Refused; }

            var firstOpposite = m_previous[firstAt].After;
            var secondOpposite = m_previous[secondAt].Next;

            if (firstOpposite == secondOpposite) { return Outcome.Refused; }

            // Each side of the collapsed edge keeps its own vertices: a triangle about the place takes, at the neighbour,
            // the vertex the edge's triangle on its side has there.
            var fromFirst = m_corners[m_previous[firstAt].Corner];
            var toFirst = m_corners[CornerAt(place: to, triangle: first)];
            var fromSecond = m_corners[m_previous[secondAt].Corner];
            var toSecond = m_corners[CornerAt(place: to, triangle: second)];

            if ((fromFirst == fromSecond) && (toFirst != toSecond)) { return Outcome.Refused; }

            var (tx, ty, tz) = (m_point[to].X, m_point[to].Y, m_point[to].Z);

            m_fromFirst = fromFirst;
            m_toFirst = toFirst;
            m_fromSecond = fromSecond;
            m_toSecond = toSecond;
            m_otherCopies.Clear();
            m_making.Clear();
            for (var index = 0; (index < fan.Count); index++) {
                if ((index == firstAt) || (index == secondAt)) { continue; }

                ref readonly var original = ref m_previous[index];
                var triangle = original.Surface.Triangle;
                var corner = original.Corner;
                var vertex = m_corners[corner];

                if ((vertex != fromFirst) && (vertex != fromSecond) && (OtherCopy(vertex: vertex) < 0)) {
                    m_otherCopies.Add(item: (vertex, CopyAt(cosine: cosine, place: to, vertex: vertex)));
                }

                ref readonly var old = ref original.Surface;

                var (ax, ay, az, bx, by, bz, cx, cy, cz) = (old.Ax, old.Ay, old.Az, old.Bx, old.By, old.Bz, old.Ex, old.Ey, old.Ez);
                var (ox, oy, oz) = original.Face;
                var moved = (corner - (3 * triangle));

                if (moved == 0) { (ax, ay, az) = (tx, ty, tz); } else if (moved == 1) { (bx, by, bz) = (tx, ty, tz); } else { (cx, cy, cz) = (tx, ty, tz); }

                var (nx, ny, nz) = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz);
                var oldLength = original.Length;
                var newLength = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

                if (!(newLength > (1e-12 * oldLength)) || ((((ox * nx) + (oy * ny)) + (oz * nz)) < ((FlipCosine * oldLength) * newLength))) { return Outcome.Refused; }

                // The face keeps the side of its vertices' normals it had, or the opaque pass, which culls back faces,
                // would leave a hole where the shading faces the camera.
                var before = original.NormalSum;
                var after = Vector3.Zero;

                for (var other = (3 * triangle); (other < ((3 * triangle) + 3)); other++) {
                    after += Normal(vertex: ((other == corner) ? Map(vertex: vertex) : m_corners[other]));
                }
                if (((((ox * before.X) + (oy * before.Y)) + (oz * before.Z)) > 0.0) != ((((nx * after.X) + (ny * after.Y)) + (nz * after.Z)) > 0.0)) { return Refusal(outcome: Outcome.Refused); }
            }
            // The link test walks the neighbour's fan, so it follows the turn and facing tests over the place's own fan,
            // which refuse most collapses; its refusal reads no other copy, so it is kept by the neighbour's revision.
            if (!Links(firstOpposite: firstOpposite, from: from, secondOpposite: secondOpposite, to: to)) { return Outcome.RefusedLinks; }

            // Each measure that refuses names the budget its refusal stands through (m_refusedThrough).
            if (!Measure(cosine: cosine, error: error, fan: fan, first: first, from: from, fromFirst: fromFirst, second: second, to: to, toFirst: toFirst, toSecond: toSecond)) { return Refusal(outcome: Outcome.RefusedMeasure); }
            // The line check is the cheaper of the two, and neither changes what the other measures.
            if (!MeasureLines(error: Math.Min(val1: error, val2: m_lineLimit), from: from, to: to)) { return Refusal(outcome: Outcome.RefusedMeasure); }
            if (!MeasureReverse(error: error, fan: fan)) { return Refusal(outcome: Outcome.RefusedMeasure); }

            // A measure writes nothing the shared tables hold, so a worker can measure beside others; only the collapser
            // that owns the tables commits a taken collapse (CommitTaken).
            m_first = first;
            m_second = second;

            return Outcome.Taken;
        }
        // Commits the collapse TryCollapse took last, of a place onto a neighbour.
        private void CommitTaken(int from, int to) => Commit(fan: m_from, first: m_first, from: from, fromFirst: m_fromFirst, second: m_second, to: to, toFirst: m_toFirst, toSecond: m_toSecond);
        // The first place at or past `after + 1` in a span that holds a value, or -1.
        private static int NextIndex(ReadOnlySpan<int> span, int value, int after) {
            var rest = span[(after + 1)..].IndexOf(value: value);

            return ((rest < 0) ? -1 : (after + 1 + rest));
        }
        // Failed neighbours leave the source fan unchanged. Lay its old geometry out once for all their forward and
        // reverse checks; Pass stops trying neighbours immediately after the first commit changes it.
        private void PrepareFrom(int place) {
            var fan = Alive(fan: m_from, place: place);

            m_reverseEdges.Clear();
            // The source fan's lines and tracked samples are the same for every neighbour tried, so the first that needs
            // them lists them.
            m_linesListed = false;
            m_sourcesListed = 0;
            m_sources.Clear();
            m_source = place;

            if (m_measured.Length < fan.Count) {
                m_measured = new Candidate[(2 * fan.Count)];
                m_previous = new OriginalTriangle[(2 * fan.Count)];
                m_laid = new int[(2 * fan.Count)];
            }
            // Every neighbour's link test reads the place's neighbours.
            m_neighbours.Clear();
            for (var index = 0; (index < fan.Count); index++) {
                var triangle = fan[index];

                var (corner, next, after) = Around(place: place, triangle: triangle);
                var (ax, ay, az, bx, by, bz, ex, ey, ez) = Positions(triangle: triangle);
                var face = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: ex, cy: ey, cz: ez);
                var sum = Vector3.Zero;

                m_neighbours.Add(item: next);
                m_neighbours.Add(item: after);
                for (var at = (3 * triangle); (at < ((3 * triangle) + 3)); at++) { sum += Normal(vertex: m_corners[at]); }
                m_previous[index] = new OriginalTriangle {
                    Surface = new Candidate { Ax = ax, Ay = ay, Az = az, Bx = bx, By = by, Bz = bz, Ex = ex, Ey = ey, Ez = ez, Material = m_materials[triangle], Triangle = triangle },
                    Face = face,
                    Length = Math.Sqrt(d: (((face.Item1 * face.Item1) + (face.Item2 * face.Item2)) + (face.Item3 * face.Item3))),
                    NormalSum = sum,
                    Corner = corner,
                    Next = next,
                    After = after,
                };
                Bounds(candidate: ref m_previous[index].Surface);
            }
        }
        // Lists the next phase of the samples tracked to the prepared fan's triangles for Measure: the removed vertex's,
        // then every other. Most refusals are decided by the removed vertex's own samples, so the rest are listed only
        // once a measure has passed all of those, in the order a whole listing gives.
        private void ListSources() {
            var fan = m_from;
            var place = m_source;
            var phase = m_sourcesListed++;

            for (var index = 0; (index < fan.Count); index++) {
                for (var sample = m_tracked[fan[index]]; (sample >= 0); sample = m_sampleNext[sample]) {
                    var vertex = m_sampleVertex[sample];
                    var at = m_placeOf[vertex];

                    if ((at == place) != (phase == 0)) { continue; }
                    var own = m_normals[vertex];

                    m_listedSamples++;
                    m_sources.Add(item: new Source {
                        Sample = sample,
                        Index = index,
                        Vertex = vertex,
                        Px = m_point[at].X,
                        Py = m_point[at].Y,
                        Pz = m_point[at].Z,
                        Own = own,
                        OwnLength = Math.Sqrt(d: (((own.X * ((double)own.X)) + (own.Y * ((double)own.Y))) + (own.Z * ((double)own.Z)))),
                        Material = m_sampleMaterial[sample],
                        Feature = (m_waivesNormals ? m_feature[at] : double.PositiveInfinity),
                    });
                }
            }
        }

        // A source fan triangle as it stands: its geometry, face normal and its length, its corners' normal sum, and the
        // collapsing place's corner in it with the places after that corner in winding order.
        private struct OriginalTriangle {
            public Candidate Surface;
            public (double X, double Y, double Z) Face;
            public double Length;
            public Vector3 NormalSum;
            public int Corner, Next, After;
        }

        // Bounds how far each triangle Measure laid out strays from the mesh's own surface: its centroid's and its edge
        // midpoints' distance to the nearest triangle of its material about the collapsing place as it stands, plus that
        // triangle's own bound. A surface sample is measured to the nearest triangle alone, so a triangle no sample needs
        // (one cutting through a thin rod, its corners on opposite sides) is measured here. False when one strays past the
        // bound.
        private bool MeasureReverse(List<int> fan, double error) {
            m_strays.Clear();
            for (var at = 0; (at < m_laidCount); at++) {
                ref readonly var candidate = ref m_measured[at];
                var bound = 0.0;

                // Shared edges establish the maximum first. The centroid often needs only one witness below it.
                for (var order = 0; (order < 4); order++) {
                    var point = ((order + 1) % 4);

                    var (px, py, pz) = point switch {
                        0 => (candidate.Cx, candidate.Cy, candidate.Cz),
                        1 => ((0.5 * (candidate.Ax + candidate.Bx)), (0.5 * (candidate.Ay + candidate.By)), (0.5 * (candidate.Az + candidate.Bz))),
                        2 => ((0.5 * (candidate.Bx + candidate.Ex)), (0.5 * (candidate.By + candidate.Ey)), (0.5 * (candidate.Bz + candidate.Ez))),
                        _ => ((0.5 * (candidate.Ex + candidate.Ax)), (0.5 * (candidate.Ey + candidate.Ay)), (0.5 * (candidate.Ez + candidate.Az))),
                    };
                    var nearest = double.PositiveInfinity;
                    var edge = point switch {
                        1 => Key(a: candidate.Pa, b: candidate.Pb),
                        2 => Key(a: candidate.Pb, b: candidate.Pc),
                        _ => Key(a: candidate.Pc, b: candidate.Pa),
                    };
                    var key = (edge, candidate.Material);
                    var next = 0;

                    if ((point != 0) && m_reverseEdges.TryGetValue(key: key, value: out var cached)) {
                        nearest = cached.Distance;
                        next = cached.Next;
                    }

                    // An upper witness no greater than the maximum already measured cannot change that maximum.
                    // Otherwise finish the exact nearest search, resuming in the same order after any cached prefix.
                    for (var oldAt = next; ((nearest > bound) && (oldAt < fan.Count)); oldAt++) {
                        next = (oldAt + 1);
                        ref readonly var old = ref m_previous[oldAt].Surface;

                        if (old.Material != candidate.Material) { continue; }
                        var stray = m_stray[old.Triangle];

                        if (Beyond(candidate: in old, distance: (nearest - stray), px: px, py: py, pz: pz)) { continue; }

                        var (ax, ay, az, bx, by, bz, cx, cy, cz) = (old.Ax, old.Ay, old.Az, old.Bx, old.By, old.Bz, old.Ex, old.Ey, old.Ez);
                        var (u, v, w) = Closest(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz, px: px, py: py, pz: pz);
                        var (dx, dy, dz) = (((((u * ax) + (v * bx)) + (w * cx)) - px), ((((u * ay) + (v * by)) + (w * cy)) - py), ((((u * az) + (v * bz)) + (w * cz)) - pz));

                        nearest = Math.Min(val1: nearest, val2: (Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz))) + stray));
                    }

                    if (point != 0) { m_reverseEdges[key] = (nearest, next); }
                    bound = Math.Max(val1: bound, val2: nearest);
                    // The bound only grows. A later point cannot rescue a refused triangle. A point that refuses ran its
                    // search to the end, since each witness it met lay past the bound, so its nearest is exact.
                    if (!(bound <= error)) {
                        m_refusedThrough = NeededLevel(distance: nearest, limit: double.PositiveInfinity);
                        return false;
                    }
                }

                m_strays.Add(item: bound);
            }

            return true;
        }
        // Lists the far places of a place's crease and material edges: the edges whose two triangles keep different
        // vertices at either end or carry different materials. The place's triangles close one ring of edges two
        // triangles share wherever it can move, so each edge has its twin.
        private void Lines(int place, List<int> fan, List<int> into) {
            var count = fan.Count;

            into.Clear();
            if (m_ringCorner.Length < count) {
                m_ringCorner = new int[(2 * count)];
                m_ringNext = new int[(2 * count)];
                m_ringAfter = new int[(2 * count)];
            }
            for (var at = 0; (at < count); at++) {
                (m_ringCorner[at], m_ringNext[at], m_ringAfter[at]) = Around(place: place, triangle: fan[at]);
            }
            // Each edge's twins are the fan's triangles whose place after the corner is the edge's far place, in fan
            // order: one on a manifold edge, every one on a non-manifold edge. A fan is a few triangles, so the search
            // reads only the fan, never a table as long as the mesh's places, and workers measure side by side.
            var afters = m_ringAfter.AsSpan(start: 0, length: count);

            for (var at = 0; (at < count); at++) {
                var next = m_ringNext[at];

                for (var twin = afters.IndexOf(value: next); (twin >= 0); twin = NextIndex(span: afters, value: next, after: twin)) {
                    Line(at: at, fan: fan, into: into, twin: twin);
                }
            }
        }
        private void Line(int at, int twin, List<int> fan, List<int> into) {
            var (corner, twinCorner) = (m_ringCorner[at], m_ringCorner[twin]);

            if ((m_corners[corner] != m_corners[twinCorner]) || (m_corners[NextCorner(corner: corner)] != m_corners[PreviousCorner(corner: twinCorner)]) || (m_materials[fan[at]] != m_materials[fan[twin]])) {
                into.Add(item: m_ringNext[at]);
            }
        }
        private static long Key(int a, int b) => PackedIntPair.Pack(high: Math.Min(val1: a, val2: b), low: Math.Max(val1: a, val2: b));
        private void AddLine(int a, int b, double x, double y, double z) {
            var sample = m_lineX.Length;

            m_lineNext.Add(item: -1);
            AppendLine(a: a, b: b, sample: sample);
            m_lineX.Add(item: x);
            m_lineY.Add(item: y);
            m_lineZ.Add(item: z);
            m_lineDistance.Add(item: 0.0);
        }
        // A sample belongs to one edge. Relink it in append order, retaining the measurement and tie order without
        // allocating and copying a new per-edge sample list whenever a collapse moves the edge.
        private void AppendLine(int a, int b, int sample) {
            var key = Key(a: a, b: b);
            var first = sample;

            if (m_lines.TryGetValue(key: key, value: out var samples)) {
                first = samples.First;
                m_lineNext[samples.Last] = sample;
            }
            m_lineNext[sample] = -1;
            m_lines.Set(key, (first, sample));
        }
        // Measures every line sample on an edge the collapsing place's lines (PrepareFrom's m_lineNeighbours) leave against
        // the edges they become, from the neighbour to each of the place's other line places, choosing the nearest within
        // the bound; false when one has none.
        private bool MeasureLines(int from, int to, double error) {
            m_lineChoices.Clear();
            if (!m_linesListed) {
                Lines(fan: m_from, into: m_lineNeighbours, place: from);
                m_linesListed = true;
            }
            foreach (var line in m_lineNeighbours) {
                if (!m_lines.TryGetValue(key: Key(a: from, b: line), value: out var samples)) { continue; }

                for (var sample = samples.First; (sample >= 0); sample = m_lineNext[sample]) {
                    var bestOther = -1;
                    var bestDistance = double.PositiveInfinity;

                    foreach (var other in m_lineNeighbours) {
                        if (other == to) { continue; }

                        var distance = Segment(a: to, b: other, sample: sample);

                        if (distance < bestDistance) {
                            bestOther = other;
                            bestDistance = distance;
                        }
                    }
                    if (!(bestDistance <= error)) {
                        m_refusedThrough = NeededLevel(distance: bestDistance, limit: m_lineLimit);
                        return false;
                    }

                    m_lineChoices.Add(item: (sample, bestOther, bestDistance));
                }
            }

            return true;
        }
        // Moves the measured line samples onto the edges they now lie on.
        private void CommitLines(int from, int to) {
            foreach (var line in m_lineNeighbours) {
                _ = m_lines.Remove(key: Key(a: from, b: line));
            }
            foreach (var (sample, other, distance) in m_lineChoices) {
                AppendLine(a: to, b: other, sample: sample);
                m_lineDistance[sample] = distance;
            }
        }
        // A line sample's distance to the segment between two places.
        private double Segment(int a, int b, int sample) {
            var (ax, ay, az) = (m_point[a].X, m_point[a].Y, m_point[a].Z);
            var (ex, ey, ez) = ((m_point[b].X - ax), (m_point[b].Y - ay), (m_point[b].Z - az));
            var (px, py, pz) = ((m_lineX[sample] - ax), (m_lineY[sample] - ay), (m_lineZ[sample] - az));
            var length = (((ex * ex) + (ey * ey)) + (ez * ez));
            var along = ((length > 0.0) ? Math.Clamp(max: 1.0, min: 0.0, value: ((((px * ex) + (py * ey)) + (pz * ez)) / length)) : 0.0);

            var (dx, dy, dz) = ((px - (along * ex)), (py - (along * ey)), (pz - (along * ez)));

            return Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz)));
        }
        // Whether the two places share no neighbour but the edge's two opposite places, so the collapse pinches nothing.
        private bool Links(int from, int to, int firstOpposite, int secondOpposite) {
            var neighbours = CollectionsMarshal.AsSpan(list: m_neighbours);

            foreach (var triangle in Alive(fan: m_to, place: to)) {
                var (_, next, after) = Around(place: to, triangle: triangle);

                if ((next != from) && (next != firstOpposite) && (next != secondOpposite) && neighbours.Contains(value: next)) { return false; }
                if ((after != from) && (after != firstOpposite) && (after != secondOpposite) && neighbours.Contains(value: after)) { return false; }
            }

            return true;
        }
        // The vertex a triangle about the collapsing place takes at the neighbour: the one on its side of the collapsed edge,
        // or for a vertex on neither side, the one TryCollapse chose for it (CopyAt). The one door every remapped corner,
        // laid-out triangle and facing check reads.
        private int Map(int vertex) {
            if (vertex == m_fromFirst) { return m_toFirst; }
            if (vertex == m_fromSecond) { return m_toSecond; }

            var copy = OtherCopy(vertex: vertex);

            return ((copy < 0) ? m_toSecond : copy);
        }
        private int OtherCopy(int vertex) {
            foreach (var (from, to) in m_otherCopies) {
                if (from == vertex) { return to; }
            }
            return -1;
        }
        // The neighbour's vertex a corner's third face takes: the one at the place whose normal lies nearest the vertex's
        // within the level's angle, or else a vertex made at the place with the vertex's own normal, which Commit keeps
        // (m_making). Either way the face's shading there turns by no more than the level allows before Measure checks it.
        private int CopyAt(int place, int vertex, double cosine) {
            var own = Normal(vertex: vertex);
            var ownLength = own.Length();
            var best = -1;
            var bestCosine = cosine;

            foreach (var triangle in Alive(fan: m_to, place: place)) {
                var copy = m_corners[CornerAt(place: place, triangle: triangle)];
                var normal = Normal(vertex: copy);
                var length = (((double)ownLength) * normal.Length());
                var cos = ((length > 0.0) ? (Vector3.Dot(vector1: own, vector2: normal) / length) : -1.0);

                if (cos >= bestCosine) {
                    best = copy;
                    bestCosine = cos;
                }
            }
            if (best >= 0) { return best; }
            m_making.Add(item: own);
            return ((m_ownVertices + m_madeVertices.Count + m_making.Count) - 1);
        }
        // A vertex's normal: one of the mesh's own, one a collapse made, or one the collapse being measured would make.
        private Vector3 Normal(int vertex) {
            if (vertex < m_ownVertices) { return m_normals[vertex]; }

            var made = (vertex - m_ownVertices);

            return ((made < m_madeVertices.Count) ? m_madeVertices[made].Normal : m_making[(made - m_madeVertices.Count)]);
        }
        // Measures every sample tracked to a triangle about the collapsing place against the triangles that stay, as they
        // will be, choosing for each the nearest of its material within the bounds; false when one has none.
        private bool Measure(List<int> fan, int from, int to, int first, int second, int fromFirst, int toFirst, int toSecond, double error, double cosine) {
            m_samples.Clear();
            m_choices.Clear();

            var candidates = Lay(fan: fan, first: first, from: from, fromFirst: fromFirst, second: second, to: to, toFirst: toFirst, toSecond: toSecond);

            m_laidCount = candidates;
            // The removed vertex is often the first sample that refuses a target, so its samples are listed and measured
            // first, then all remaining samples; each still chooses its exact nearest admissible witness independently of
            // the others.
            for (var next = 0; ; next++) {
                while ((next == m_sources.Count) && (m_sourcesListed < 2)) { ListSources(); }
                if (next >= m_sources.Count) { break; }

                ref readonly var source = ref CollectionsMarshal.AsSpan(list: m_sources)[next];

                m_measuredSamples++;
                // A sample's own triangle, where it stays, is measured first: it is most often the nearest, and the
                // others then fall to the bound on the nearest yet.
                var laid = m_laid[source.Index];

                // An original vertex retained by its tracked triangle has exactly its original position, normal and
                // material. Its own corner is a zero-distance witness without a nearest search.
                if ((laid >= 0) && (source.Own != Vector3.Zero)) {
                    ref readonly var retained = ref m_measured[laid];

                    if ((source.Vertex == retained.Va) || (source.Vertex == retained.Vb) || (source.Vertex == retained.Vc)) {
                        m_samples.Add(item: source.Sample);
                        m_choices.Add(item: (retained.Triangle, 0.0));
                        continue;
                    }
                }

                var best = -1;
                var bestDistance = double.PositiveInfinity;
                var bestCosine = -1.0;

                if (laid >= 0) {
                    Offer(at: laid, best: ref best, bestCosine: ref bestCosine, bestDistance: ref bestDistance, cosine: cosine, error: error, source: in source);
                }
                for (var at = 0; (at < candidates); at++) {
                    if (at != laid) {
                        Offer(at: at, best: ref best, bestCosine: ref bestCosine, bestDistance: ref bestDistance, cosine: cosine, error: error, source: in source);
                    }
                }
                if (best < 0) {
                    m_refusedThrough = NeededLevel(candidates: candidates, source: in source);
                    return false;
                }

                m_samples.Add(item: source.Sample);
                m_choices.Add(item: (m_measured[best].Triangle, bestDistance));
            }

            return true;
        }

        // The budget a refusal stands through: the first later level whose bounds a refused measure's exact answer meets, so
        // every level before it refuses the collapse on that witness alone, whatever the others measure; the chain's last
        // budget where none does. Every input a measure reads is the place's own fan, its samples and lines and the
        // neighbour's fixed position, which the refusal's place revision guards; only the level's bounds vary, and they
        // loosen level by level. A level's budget is its index plus one, so a refusal standing through budget k stands at
        // every level before level k.
        private int NeededLevel(double distance, double limit, double cosine = 1.0, double feature = double.PositiveInfinity) {
            for (var level = m_budget; (level < m_errors.Length); level++) {
                if ((distance <= Math.Min(val1: m_errors[level], val2: limit)) && Shades(cosine: cosine, error: m_errors[level], feature: feature, least: m_cosines[level])) { return level; }
            }
            return m_errors.Length;
        }
        // Whether a sample's normal meets a level's bound: within its angle, or waived where the sample lies nearer a sharp
        // turn of the surface than the level's error (FeatureDistances).
        private static bool Shades(double cosine, double least, double feature, double error) => ((cosine >= least) || (feature < error));
        // NeededLevel of a surface sample no laid-out triangle took: the earliest level any triangle of its material meets,
        // each measured to the whole triangle (Offer's test with no bound to stop at).
        private int NeededLevel(int candidates, in Source source) {
            var needed = m_errors.Length;

            for (var at = 0; ((at < candidates) && (needed > m_budget)); at++) {
                ref readonly var candidate = ref m_measured[at];

                if (candidate.Material != source.Material) { continue; }

                var (distance, cos) = Nearest(candidate: in candidate, limit: double.PositiveInfinity, own: source.Own, ownLength: source.OwnLength, px: source.Px, py: source.Py, pz: source.Pz);

                needed = Math.Min(val1: needed, val2: NeededLevel(cosine: cos, distance: distance, feature: source.Feature, limit: double.PositiveInfinity));
            }
            return needed;
        }
        // A sample tracked to a triangle of the source fan, laid out for Measure: the fan index of its triangle, its vertex
        // and place's position, its normal and that normal's length, its material, and its place's distance along the mesh
        // to a sharp turn (infinite where no level waives a normal).
        private struct Source {
            public int Sample, Index, Vertex;
            public double Px, Py, Pz, OwnLength, Feature;
            public Vector3 Own;
            public uint Material;
        }

        // Measures a sample against one laid-out triangle of its material, keeping it where it is the nearest within the
        // bounds yet. A triangle wholly beyond the bound, or farther than the nearest yet, is not measured.
        private void Offer(int at, in Source source, double error, double cosine, ref int best, ref double bestDistance, ref double bestCosine) {
            ref readonly var candidate = ref m_measured[at];

            if (candidate.Material != source.Material) { return; }

            var (px, py, pz) = (source.Px, source.Py, source.Pz);

            if (Beyond(candidate: in candidate, px: px, py: py, pz: pz, distance: Math.Min(val1: error, val2: bestDistance))) { return; }

            var (sx, sy, sz) = ((px - candidate.Cx), (py - candidate.Cy), (pz - candidate.Cz));
            var reach = (Math.Sqrt(d: (((sx * sx) + (sy * sy)) + (sz * sz))) - candidate.Radius);

            if ((reach > error) || (reach > bestDistance)) { return; }

            var (distance, cos) = Nearest(candidate: in candidate, limit: Math.Min(val1: error, val2: bestDistance), own: source.Own, ownLength: source.OwnLength, px: px, py: py, pz: pz);

            if ((distance <= error) && Shades(cosine: cos, error: error, feature: source.Feature, least: cosine) && ((distance < bestDistance) || ((distance == bestDistance) && (cos > bestCosine)))) {
                best = at;
                bestDistance = distance;
                bestCosine = cos;
            }
        }
        // Lays out the triangles that stay about the collapsing place as they will be, once for every sample measured
        // against them, and returns their count.
        private int Lay(List<int> fan, int from, int to, int first, int second, int fromFirst, int toFirst, int toSecond) {
            var count = 0;

            for (var index = 0; (index < fan.Count); index++) {
                var triangle = fan[index];

                m_laid[index] = (((triangle == first) || (triangle == second)) ? -1 : count);
                if (m_laid[index] < 0) { continue; }

                var corner = (3 * triangle);

                var (a, b, c) = (m_corners[corner], m_corners[(corner + 1)], m_corners[(corner + 2)]);
                var (pa, pb, pc) = (m_cornerPlace[corner], m_cornerPlace[(corner + 1)], m_cornerPlace[(corner + 2)]);

                // A mapped vertex is the neighbour's, at its place.
                if (pa == from) { (a, pa) = (Map(vertex: a), to); }
                if (pb == from) { (b, pb) = (Map(vertex: b), to); }
                if (pc == from) { (c, pc) = (Map(vertex: c), to); }

                var (cx, cy, cz) = ((((m_point[pa].X + m_point[pb].X) + m_point[pc].X) / 3.0), (((m_point[pa].Y + m_point[pb].Y) + m_point[pc].Y) / 3.0), (((m_point[pa].Z + m_point[pb].Z) + m_point[pc].Z) / 3.0));
                var radius = 0.0;
                // The square root is monotonic and correctly rounded, so the root of the largest square is the largest root.

                foreach (var p in ((ReadOnlySpan<int>)[pa, pb, pc])) {
                    var (dx, dy, dz) = ((m_point[p].X - cx), (m_point[p].Y - cy), (m_point[p].Z - cz));

                    radius = Math.Max(val1: radius, val2: (((dx * dx) + (dy * dy)) + (dz * dz)));
                }
                radius = Math.Sqrt(d: radius);

                m_measured[count++] = new Candidate {
                    Ax = m_point[pa].X,
                    Ay = m_point[pa].Y,
                    Az = m_point[pa].Z,
                    Bx = m_point[pb].X,
                    By = m_point[pb].Y,
                    Bz = m_point[pb].Z,
                    Ex = m_point[pc].X,
                    Ey = m_point[pc].Y,
                    Ez = m_point[pc].Z,
                    Cx = cx,
                    Cy = cy,
                    Cz = cz,
                    Radius = (radius * (1.0 + 1e-9)),
                    Pa = pa,
                    Pb = pb,
                    Pc = pc,
                    Va = a,
                    Vb = b,
                    Vc = c,
                    Na = Normal(vertex: a),
                    Nb = Normal(vertex: b),
                    Nc = Normal(vertex: c),
                    Material = m_materials[triangle],
                    Triangle = triangle,
                };
                Bounds(candidate: ref m_measured[(count - 1)]);
            }

            return count;
        }
        // A box distance is a lower bound only. Leave a double-rounding margin and still use the exact measurement for
        // every candidate that can improve it; the three refusal checks and their recorded distances stay unchanged.
        private static bool Beyond(in Candidate candidate, double px, double py, double pz, double distance) {
            var dx = Math.Max(val1: 0.0, val2: Math.Max(val1: (candidate.LowX - px), val2: (px - candidate.HighX)));
            var dy = Math.Max(val1: 0.0, val2: Math.Max(val1: (candidate.LowY - py), val2: (py - candidate.HighY)));
            var dz = Math.Max(val1: 0.0, val2: Math.Max(val1: (candidate.LowZ - pz), val2: (pz - candidate.HighZ)));
            var margin = (1e-12 * (((1.0 + Math.Abs(value: px)) + Math.Abs(value: py)) + Math.Abs(value: pz)));
            var reach = (Math.Max(val1: 0.0, val2: distance) + margin);

            return ((((dx * dx) + (dy * dy)) + (dz * dz)) > (reach * reach));
        }
        private static void Bounds(ref Candidate candidate) {
            candidate.LowX = Math.Min(val1: candidate.Ax, val2: Math.Min(val1: candidate.Bx, val2: candidate.Ex));
            candidate.LowY = Math.Min(val1: candidate.Ay, val2: Math.Min(val1: candidate.By, val2: candidate.Ey));
            candidate.LowZ = Math.Min(val1: candidate.Az, val2: Math.Min(val1: candidate.Bz, val2: candidate.Ez));
            candidate.HighX = Math.Max(val1: candidate.Ax, val2: Math.Max(val1: candidate.Bx, val2: candidate.Ex));
            candidate.HighY = Math.Max(val1: candidate.Ay, val2: Math.Max(val1: candidate.By, val2: candidate.Ey));
            candidate.HighZ = Math.Max(val1: candidate.Az, val2: Math.Max(val1: candidate.Bz, val2: candidate.Ez));
        }
        // A sample's distance to a laid-out triangle, and the cosine between its own normal and the triangle's interpolated
        // normal at the nearest point.
        private static (double Distance, double Cosine) Nearest(in Candidate candidate, double px, double py, double pz, Vector3 own, double ownLength, double limit) {
            var (u, v, w) = Closest(ax: candidate.Ax, ay: candidate.Ay, az: candidate.Az, bx: candidate.Bx, by: candidate.By, bz: candidate.Bz, cx: candidate.Ex, cy: candidate.Ey, cz: candidate.Ez, px: px, py: py, pz: pz);
            var dx = ((((u * candidate.Ax) + (v * candidate.Bx)) + (w * candidate.Ex)) - px);
            var dy = ((((u * candidate.Ay) + (v * candidate.By)) + (w * candidate.Ey)) - py);
            var dz = ((((u * candidate.Az) + (v * candidate.Bz)) + (w * candidate.Ez)) - pz);

            var distance = Math.Sqrt(d: (((dx * dx) + (dy * dy)) + (dz * dz)));

            if (distance > limit) { return (distance, -1.0); }

            var (na, nb, nc) = (candidate.Na, candidate.Nb, candidate.Nc);
            var nx = (((u * na.X) + (v * nb.X)) + (w * nc.X));
            var ny = (((u * na.Y) + (v * nb.Y)) + (w * nc.Y));
            var nz = (((u * na.Z) + (v * nb.Z)) + (w * nc.Z));
            var length = (Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz))) * ownLength);
            var cosine = ((length > 0.0) ? ((((nx * own.X) + (ny * own.Y)) + (nz * own.Z)) / length) : -1.0);

            return (distance, cosine);
        }
        // Takes a collapse: the edge's two triangles go, the rest about the place take the neighbour's vertices, every
        // measured sample is tracked to its chosen triangle, and the neighbour takes the place's planes.
        private void Commit(List<int> fan, int from, int to, int first, int second, int fromFirst, int toFirst, int toSecond) {
            m_revision++;
            // The target gains the source quadric even for neighbours outside the source fan. Every place of both fans is
            // stamped with the block: they are the places of every triangle, sample, line, quadric and revision the commit
            // writes, so a speculated measure that read none of them read what the commit left as it was (Clean).
            for (var at = m_fanStart[to]; at < m_fanStart[to + 1]; at++) {
                var triangle = m_fan[at];
                if (!m_alive[triangle]) { continue; }
                for (var corner = 3 * triangle; corner < 3 * triangle + 3; corner++) {
                    m_priceRuns[m_cornerPlace[corner]].Ready = false;
                    m_blockStamp[m_cornerPlace[corner]] = m_block;
                }
            }
            foreach (var triangle in fan) {
                for (var corner = (3 * triangle); (corner < ((3 * triangle) + 3)); corner++) {
                    m_priceRuns[m_cornerPlace[corner]].Ready = false;
                    m_revisions[m_cornerPlace[corner]] = m_revision;
                    Wake(place: m_cornerPlace[corner]);
                    m_blockStamp[m_cornerPlace[corner]] = m_block;
                }
                m_tracked[triangle] = -1;
            }

            foreach (var normal in m_making) { m_madeVertices.Add(item: (to, normal)); }
            m_making.Clear();
            m_alive[first] = false;
            m_alive[second] = false;
            m_aliveTriangles -= 2;
            m_touched[from] = m_pass;
            m_touched[to] = m_pass;
            foreach (var triangle in fan) {
                if (!m_alive[triangle]) { continue; }

                var corner = CornerAt(place: from, triangle: triangle);

                m_corners[corner] = Map(vertex: m_corners[corner]);
                m_cornerPlace[corner] = to;

                var (_, next, after) = Around(place: to, triangle: triangle);

                m_changed[next] = m_pass;
                m_changed[after] = m_pass;
            }
            for (var index = 0; (index < m_samples.Count); index++) {
                var (triangle, distance) = m_choices[index];

                m_sampleDistance[m_samples[index]] = distance;
                Track(sample: m_samples[index], triangle: triangle);
            }
            CommitLines(from: from, to: to);
            for (var at = 0; (at < m_strays.Count); at++) {
                m_stray[m_measured[at].Triangle] = m_strays[at];
            }
            for (var term = 0; (term < 10); term++) {
                m_quadrics[((10 * to) + term)] += m_quadrics[((10 * from) + term)];
            }
        }
        private void Track(int sample, int triangle) {
            m_sampleNext[sample] = m_tracked[triangle];
            m_tracked[triangle] = sample;
        }
        private (double, double, double, double, double, double, double, double, double) Positions(int triangle) {
            var a = m_cornerPlace[(3 * triangle)];
            var b = m_cornerPlace[((3 * triangle) + 1)];
            var c = m_cornerPlace[((3 * triangle) + 2)];

            return (m_point[a].X, m_point[a].Y, m_point[a].Z, m_point[b].X, m_point[b].Y, m_point[b].Z, m_point[c].X, m_point[c].Y, m_point[c].Z);
        }
        private static (double X, double Y, double Z) Cross(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz) {
            var (ux, uy, uz) = ((bx - ax), (by - ay), (bz - az));
            var (vx, vy, vz) = ((cx - ax), (cy - ay), (cz - az));

            return (((uy * vz) - (uz * vy)), ((uz * vx) - (ux * vz)), ((ux * vy) - (uy * vx)));
        }
        // The cost of moving a place's planes to a neighbour: the summed squared distances of both places' planes there.
        private double Cost(int from, int to) {
            var (x, y, z) = (m_point[to].X, m_point[to].Y, m_point[to].Z);

            return (Evaluate(place: from, x: x, y: y, z: z) + Evaluate(place: to, x: x, y: y, z: z));
        }
        private double Evaluate(int place, double x, double y, double z) {
            var q = m_quadrics.GetSpan().Slice(start: (10 * place), length: 10);

            return (((((((((((q[0] * x) * x) + (2.0 * ((q[1] * x) * y))) + (2.0 * ((q[2] * x) * z))) + (2.0 * (q[3] * x))) + ((q[4] * y) * y)) + (2.0 * ((q[5] * y) * z))) + (2.0 * (q[6] * y))) + ((q[7] * z) * z)) + (2.0 * (q[8] * z))) + q[9]);
        }
        // Adds a plane, through a point with a unit normal and a weight, to a place's quadric.
        private void AddPlane(int place, double nx, double ny, double nz, double px, double py, double pz, double weight) {
            var d = -(((nx * px) + (ny * py)) + (nz * pz));
            var q = m_quadrics.GetSpan().Slice(start: (10 * place), length: 10);

            q[0] += (weight * (nx * nx));
            q[1] += (weight * (nx * ny));
            q[2] += (weight * (nx * nz));
            q[3] += (weight * (nx * d));
            q[4] += (weight * (ny * ny));
            q[5] += (weight * (ny * nz));
            q[6] += (weight * (ny * d));
            q[7] += (weight * (nz * nz));
            q[8] += (weight * (nz * d));
            q[9] += (weight * (d * d));
        }
        // Adds a triangle's plane, weighted by its area, to each of its places.
        private void AddFace(int triangle) {
            var (ax, ay, az, bx, by, bz, cx, cy, cz) = Positions(triangle: triangle);
            var (nx, ny, nz) = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz);
            var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

            if (!(length > 0.0)) { return; }

            for (var corner = 0; (corner < 3); corner++) {
                AddPlane(nx: (nx / length), ny: (ny / length), nz: (nz / length), place: m_cornerPlace[((3 * triangle) + corner)], px: ax, py: ay, pz: az, weight: (0.5 * length));
            }
        }
        // Adds the plane through an edge standing on one of its triangles to one end's quadric, weighted heavier than a face
        // of the edge's length squared, so moving the end off the edge costs more than moving it across a face.
        private void AddEdge(int place, int other, int triangle) {
            var (ax, ay, az, bx, by, bz, cx, cy, cz) = Positions(triangle: triangle);
            var (fx, fy, fz) = Cross(ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz, cx: cx, cy: cy, cz: cz);
            var (ex, ey, ez) = ((m_point[other].X - m_point[place].X), (m_point[other].Y - m_point[place].Y), (m_point[other].Z - m_point[place].Z));
            var (nx, ny, nz) = (((ey * fz) - (ez * fy)), ((ez * fx) - (ex * fz)), ((ex * fy) - (ey * fx)));
            var length = Math.Sqrt(d: (((nx * nx) + (ny * ny)) + (nz * nz)));

            if (!(length > 0.0)) { return; }

            AddPlane(nx: (nx / length), ny: (ny / length), nz: (nz / length), place: place, px: m_point[place].X, py: m_point[place].Y, pz: m_point[place].Z, weight: (FeatureWeight * (((ex * ex) + (ey * ey)) + (ez * ez))));
        }
        // The barycentric weights of the point of a triangle nearest a point (Ericson, Real-Time Collision Detection 5.1.5).
        private static (double U, double V, double W) Closest(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, double px, double py, double pz) {
            var (abx, aby, abz) = ((bx - ax), (by - ay), (bz - az));
            var (acx, acy, acz) = ((cx - ax), (cy - ay), (cz - az));
            var (apx, apy, apz) = ((px - ax), (py - ay), (pz - az));
            var d1 = (((abx * apx) + (aby * apy)) + (abz * apz));
            var d2 = (((acx * apx) + (acy * apy)) + (acz * apz));

            if ((d1 <= 0.0) && (d2 <= 0.0)) { return (1.0, 0.0, 0.0); }

            var (bpx, bpy, bpz) = ((px - bx), (py - by), (pz - bz));
            var d3 = (((abx * bpx) + (aby * bpy)) + (abz * bpz));
            var d4 = (((acx * bpx) + (acy * bpy)) + (acz * bpz));

            if ((d3 >= 0.0) && (d4 <= d3)) { return (0.0, 1.0, 0.0); }

            var vc = ((d1 * d4) - (d3 * d2));

            if ((vc <= 0.0) && (d1 >= 0.0) && (d3 <= 0.0)) {
                var v = (d1 / (d1 - d3));

                return ((1.0 - v), v, 0.0);
            }

            var (cpx, cpy, cpz) = ((px - cx), (py - cy), (pz - cz));
            var d5 = (((abx * cpx) + (aby * cpy)) + (abz * cpz));
            var d6 = (((acx * cpx) + (acy * cpy)) + (acz * cpz));

            if ((d6 >= 0.0) && (d5 <= d6)) { return (0.0, 0.0, 1.0); }

            var vb = ((d5 * d2) - (d1 * d6));

            if ((vb <= 0.0) && (d2 >= 0.0) && (d6 <= 0.0)) {
                var w = (d2 / (d2 - d6));

                return ((1.0 - w), 0.0, w);
            }

            var va = ((d3 * d6) - (d5 * d4));

            if ((va <= 0.0) && ((d4 - d3) >= 0.0) && ((d5 - d6) >= 0.0)) {
                var w = ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

                return (0.0, (1.0 - w), w);
            }

            var denominator = (1.0 / ((va + vb) + vc));
            var inner = (vb * denominator);
            var outer = (vc * denominator);

            return (((1.0 - inner) - outer), inner, outer);
        }

        // A triangle about a collapsing place as it will be: its corners, its centroid and the radius about it that holds
        // them, its corners' normals and its material.
        private struct Point {
            public double X, Y, Z;
        }
        private struct Candidate {
            public double Ax, Ay, Az, Bx, By, Bz, Ex, Ey, Ez, Cx, Cy, Cz, Radius;
            public double LowX, LowY, LowZ, HighX, HighY, HighZ;
            public int Pa, Pb, Pc, Va, Vb, Vc;
            public Vector3 Na, Nb, Nc;
            public uint Material;
            public int Triangle;
        }
    }
}
