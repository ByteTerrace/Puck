using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.SdfVm;

/// <summary>
/// The static triangles the mesh path's irradiance probes trace (<see cref="SdfMeshIrradiance"/>): every static mesh draw of
/// a frame, each triangle with its program material, under a two-level bounding volume hierarchy. Each distinct geometry (a
/// mesh's positions and indices) holds its own hierarchy in the mesh's frame, and the scene holds one over its draws' world
/// bounds, each draw carrying its transform and material base, so a ray enters a draw's geometry through its inverse
/// transform. A scene built from a previous one takes every geometry the previous scene held, its draws' and those it
/// retained (<see cref="RetainedTriangles"/>), and builds only geometry new to it, so a change of a few draws costs their
/// new geometry and the top hierarchy rather than every triangle. It answers the nearest hit along a ray and whether a ray
/// reaches a distance unblocked. Presentation only: the tracing is single-precision and nothing it answers reaches
/// simulation state.
/// </summary>
public sealed partial class SdfMeshIrradianceScene {
    /// <summary>The most triangles of geometry no draw of a scene uses that the scene keeps for a later scene built from
    /// it, the most recently drawn first: a responding placement toggled back takes its earlier geometry again.</summary>
    public const long RetainedTriangles = 8_000_000L;

    // Fill the packed leaf count before adding another pair of bounding boxes. Triangle tests still read the
    // original mesh vertices; denser leaves change storage and traversal work, not the nearest surface.
    private const int LeafTriangles = Node.MaxCount;
    private const int Bins = 12;
    // A node range at least this many primitives wide builds its two children in parallel, and a geometry at least
    // this many triangles wide prepares its triangles in parallel chunks of it.
    private const int ParallelPrimitives = 16_384;
    private const int StackDepth = 64;
    // The widest subtree of a geometry the top level indexes on its own (Geometry.Braids).
    private const int BraidTriangles = 1024;

    private readonly Instance[] m_instances;
    private readonly Entry[] m_entries;
    private readonly Node[] m_nodes;
    private readonly Dictionary<GeometryKey, Geometry> m_geometries;
    private readonly List<KeyValuePair<GeometryKey, Geometry>> m_retained;

    /// <summary>Gets the triangles the scene's draws hold, a geometry drawn twice counted twice.</summary>
    public long TriangleCount { get; }
    /// <summary>Gets the static draws the scene holds.</summary>
    public int DrawCount => m_instances.Length;
    /// <summary>Gets the distinct geometries the scene's draws hold.</summary>
    public int GeometryCount => m_geometries.Count;
    /// <summary>Gets the geometries the build of the scene built: those no previous scene held.</summary>
    public int GeometriesBuilt { get; }
    /// <summary>Gets the triangles of the geometries the build of the scene built.</summary>
    public long TrianglesBuilt { get; }
    /// <summary>Gets the geometries the scene took from the scene it was built from.</summary>
    public int GeometriesReused => (GeometryCount - GeometriesBuilt);
    /// <summary>Gets the triangles of the geometry the scene keeps for a later scene though no draw of its own uses it.</summary>
    public long TrianglesRetained { get; }

    // 28 bytes: the bounds, then one word holding the first index above the count's CountBits.
    private struct Node {
        private const int CountBits = 3;

        public Vector3 Low;
        public Vector3 High;
        private int m_firstAndCount;

        // A leaf's first primitive, or an inner node's first child (its second follows it).
        public readonly int First => (m_firstAndCount >> CountBits);
        // A leaf's primitive count; zero for an inner node.
        public readonly int Count => (m_firstAndCount & ((1 << CountBits) - 1));
        // The most primitives a leaf can hold.
        public const int MaxCount = ((1 << CountBits) - 1);

        public void Link(int first, int count) => m_firstAndCount = ((first << CountBits) | count);
    }

    /// <summary>Builds the scene of a frame's static mesh draws: every draw that is not dynamic.</summary>
    /// <param name="draws">The frame's mesh draws.</param>
    /// <param name="previous">The scene the draws change, whose geometries the new scene takes rather than builds, or
    /// <see langword="null"/> to build every geometry.</param>
    /// <returns>The scene, empty when no static draw holds a triangle. An empty scene has no occluders and can still
    /// supply measured sky irradiance; it is distinct from a scene not built yet.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> is <see langword="null"/>.</exception>
    public static SdfMeshIrradianceScene Build(IReadOnlyList<SdfMeshDraw> draws, SdfMeshIrradianceScene? previous = null) {
        ArgumentNullException.ThrowIfNull(argument: draws);

        var geometries = new Dictionary<GeometryKey, Geometry>();
        var pending = new List<(GeometryKey Key, SdfMesh Mesh)>();

        foreach (var draw in draws) {
            if (!IsStatic(draw: in draw)) { continue; }

            var key = GeometryKey.Of(mesh: draw.Mesh);

            if (geometries.ContainsKey(key: key)) { continue; }
            if ((previous is not null) && previous.TryFind(key: key, geometry: out var held)) {
                geometries.Add(key: key, value: held);
                continue;
            }
            geometries.Add(key: key, value: null!);
            pending.Add(item: (key, draw.Mesh));
        }

        var built = new Geometry[pending.Count];

        // The largest first, so one long build does not start last.
        pending.Sort(comparison: static (a, b) => b.Mesh.TriangleCount.CompareTo(value: a.Mesh.TriangleCount));
        _ = Parallel.For(fromInclusive: 0, toExclusive: pending.Count, body: index => built[index] = Geometry.Build(mesh: pending[index].Mesh));

        var trianglesBuilt = 0L;

        for (var index = 0; (index < pending.Count); index++) {
            geometries[pending[index].Key] = built[index];
            trianglesBuilt += built[index].Count;
        }

        var instances = new List<Instance>(capacity: draws.Count);
        var materials = new HashSet<int>();
        var materialRuns = new HashSet<(ReadOnlyMemory<uint> Triangles, int Base)>();

        foreach (var draw in draws) {
            if (!IsStatic(draw: in draw)) { continue; }
            if (Instance.TryCreate(draw: in draw, geometry: geometries[GeometryKey.Of(mesh: draw.Mesh)], instance: out var instance)) {
                instances.Add(item: instance);
                if (materialRuns.Add(item: (draw.Mesh.TriangleMaterials, draw.Material))) {
                    AddMaterials(materials: materials, draw: in draw);
                }
            }
        }
        var order = materials.ToArray();

        Array.Sort(array: order);

        return new SdfMeshIrradianceScene(
            geometries: geometries,
            geometriesBuilt: pending.Count,
            instances: instances,
            retained: Retain(geometries: geometries, previous: previous),
            trianglesBuilt: trianglesBuilt
        ) { Materials = order };
    }
    /// <summary>Gets the program material ids the scene's triangles carry, in increasing order: the materials a ray that
    /// meets the scene reads, and the only ones whose values its bounce light depends on.</summary>
    public int[] Materials { get; private init; } = [];

    // Adds the program material ids a draw's triangles carry: its base, offset by each distinct triangle material.
    private static void AddMaterials(HashSet<int> materials, in SdfMeshDraw draw) {
        var triangles = draw.Mesh.TriangleMaterials.Span;

        if (triangles.IsEmpty) {
            _ = materials.Add(item: draw.Material);
            return;
        }

        var last = -1L;

        foreach (var material in triangles) {
            if (material == last) { continue; }
            last = material;
            _ = materials.Add(item: (draw.Material + ((int)material)));
        }
    }
    /// <summary>Returns whether a draw belongs to the static scene: one neither dynamic nor glass. Glass neither blocks
    /// nor bounces the probes' rays: light reaches what lies behind it untinted.</summary>
    /// <param name="draw">The draw.</param>
    /// <returns><see langword="true"/> for a static, opaque mesh draw.</returns>
    public static bool IsStatic(in SdfMeshDraw draw) => (!draw.IsDynamic && !draw.Glass);

    private SdfMeshIrradianceScene(Dictionary<GeometryKey, Geometry> geometries, List<Instance> instances, List<KeyValuePair<GeometryKey, Geometry>> retained, int geometriesBuilt, long trianglesBuilt) {
        var entries = new List<Entry>();
        var lows = new List<Vector3>();
        var highs = new List<Vector3>();
        var triangles = 0L;

        // The top level indexes each draw's braid subtrees, not its root, so overlapping draws separate as one hierarchy
        // over all their triangles would.
        for (var index = 0; (index < instances.Count); index++) {
            var instance = instances[index];

            triangles += instance.Geometry.Count;
            foreach (var braid in instance.Geometry.Braids) {
                ref readonly var node = ref instance.Geometry.Nodes[braid];

                if (instance.Identity) {
                    lows.Add(item: node.Low);
                    highs.Add(item: node.High);
                } else {
                    var (low, high) = Bounds(high: node.High, low: node.Low, matrix: instance.ObjectToWorld);

                    lows.Add(item: low);
                    highs.Add(item: high);
                }
                entries.Add(item: new Entry(Instance: index, Node: braid));
            }
        }

        var (nodes, order, _) = ((entries.Count == 0)
            ? (Array.Empty<Node>(), Array.Empty<int>(), Array.Empty<int>())
            : Hierarchy.Build(braid: 0, highs: [.. highs], leaf: 1, lows: [.. lows]));

        m_instances = [.. instances];
        m_entries = new Entry[entries.Count];
        for (var index = 0; (index < entries.Count); index++) {
            m_entries[index] = entries[order[index]];
        }
        m_nodes = nodes;
        m_geometries = geometries;
        m_retained = retained;
        GeometriesBuilt = geometriesBuilt;
        TriangleCount = triangles;
        TrianglesBuilt = trianglesBuilt;
        TrianglesRetained = retained.Sum(selector: static entry => (long)entry.Value.Count);
    }

    private bool TryFind(GeometryKey key, out Geometry geometry) {
        if (m_geometries.TryGetValue(key: key, value: out geometry!)) { return true; }
        foreach (var entry in m_retained) {
            if (entry.Key == key) {
                geometry = entry.Value;
                return true;
            }
        }

        return false;
    }
    // The geometries a new scene keeps though none of its draws uses them: the previous scene's own, then those it kept,
    // up to RetainedTriangles.
    private static List<KeyValuePair<GeometryKey, Geometry>> Retain(Dictionary<GeometryKey, Geometry> geometries, SdfMeshIrradianceScene? previous) {
        var retained = new List<KeyValuePair<GeometryKey, Geometry>>();

        if (previous is null) { return retained; }

        var budget = RetainedTriangles;

        foreach (var entry in previous.m_geometries.Concat(second: previous.m_retained)) {
            if (geometries.ContainsKey(key: entry.Key) || (entry.Value.Count > budget)) { continue; }
            retained.Add(item: entry);
            budget -= entry.Value.Count;
        }

        return retained;
    }

    /// <summary>Finds the nearest triangle a ray meets within a distance.</summary>
    /// <param name="origin">The ray's origin, in world space.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="maximum">The farthest distance to search, in metres.</param>
    /// <param name="distance">The hit's distance along the ray.</param>
    /// <param name="normal">The hit triangle's geometric unit normal in world space, wound as its mesh winds it.</param>
    /// <param name="material">The hit triangle's program material.</param>
    /// <returns><see langword="true"/> when the ray meets a triangle.</returns>
    public bool Intersect(Vector3 origin, Vector3 direction, float maximum, out float distance, out Vector3 normal, out int material) =>
        Intersect(origin, direction, maximum, out distance, out normal, out material, out _);
    /// <summary>Finds the nearest retained triangle and the uncertainty of its own contour lattice.</summary>
    /// <param name="origin">World-space ray origin.</param>
    /// <param name="direction">Unit ray direction.</param>
    /// <param name="maximum">Search distance in metres.</param>
    /// <param name="distance">Hit distance in metres.</param>
    /// <param name="normal">Geometric surface normal.</param>
    /// <param name="material">Program material.</param>
    /// <param name="bakeHalfDiagonal">Half of the hit draw's world-space contour cell diagonal; zero for authored triangles.</param>
    /// <returns>Whether a retained triangle was hit.</returns>
    public bool Intersect(Vector3 origin, Vector3 direction, float maximum, out float distance, out Vector3 normal, out int material, out float bakeHalfDiagonal) {
        var inverse = Reciprocal(direction: direction);
        var nearest = new DrawLeaves(any: false, direction: direction, entries: m_entries, instances: m_instances, inverse: inverse, origin: origin);

        distance = maximum;
        bakeHalfDiagonal = 0;
        if ((m_nodes.Length > 0) && Slab(low: m_nodes[0].Low, high: m_nodes[0].High, origin: origin, inverse: inverse, maximum: distance, entry: out _)) {
            Walk(distance: ref distance, inverse: inverse, leaves: ref nearest, nodes: m_nodes, origin: origin, start: 0);
        }
        if (nearest.Instance < 0) {
            normal = default;
            material = 0;
            return false;
        }

        ref readonly var hit = ref m_instances[nearest.Instance];

        normal = hit.NormalOf(triangle: nearest.Triangle);
        material = hit.MaterialOf(triangle: nearest.Triangle);
        bakeHalfDiagonal = hit.BakeHalfDiagonal;
        return true;
    }
    /// <summary>Returns whether a ray reaches a distance without meeting a triangle.</summary>
    /// <param name="origin">The ray's origin, in world space.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="maximum">The distance it must reach, in metres.</param>
    /// <returns><see langword="true"/> when nothing blocks the ray.</returns>
    public bool Unblocked(Vector3 origin, Vector3 direction, float maximum) {
        var inverse = Reciprocal(direction: direction);
        var any = new DrawLeaves(any: true, direction: direction, entries: m_entries, instances: m_instances, inverse: inverse, origin: origin);
        var distance = maximum;

        if ((m_nodes.Length > 0) && Slab(low: m_nodes[0].Low, high: m_nodes[0].High, origin: origin, inverse: inverse, maximum: distance, entry: out _)) {
            Walk(distance: ref distance, inverse: inverse, leaves: ref any, nodes: m_nodes, origin: origin, start: 0);
        }
        return (any.Instance < 0);
    }

    // The nodes under start a ray's slab test admits, nearer child first, each leaf handed to the leaves; a leaf that
    // answers true ends the walk. A hit shortens distance, which prunes the nodes behind it. The caller has admitted
    // start: the top level's root by its own test, a braid subtree by the top-level leaf that names it.
    [SkipLocalsInit]
    private static void Walk<TLeaves>(Node[] nodes, int start, Vector3 origin, Vector3 inverse, ref float distance, ref TLeaves leaves) where TLeaves : struct, ILeaves {
        Span<int> stack = stackalloc int[StackDepth];
        var top = 0;

        stack[top++] = start;

        while (top > 0) {
            ref readonly var node = ref nodes[stack[--top]];

            if (node.Count > 0) {
                if (leaves.Visit(first: node.First, count: node.Count, distance: ref distance)) { return; }
                continue;
            }

            var left = node.First;
            var right = (left + 1);
            var hitLeft = Slab(low: nodes[left].Low, high: nodes[left].High, origin: origin, inverse: inverse, maximum: distance, entry: out var leftEntry);
            var hitRight = Slab(low: nodes[right].Low, high: nodes[right].High, origin: origin, inverse: inverse, maximum: distance, entry: out var rightEntry);

            if (hitLeft && hitRight) {
                // The nearer child is visited first: it pops after the farther one is pushed.
                if (leftEntry <= rightEntry) {
                    stack[top++] = right;
                    stack[top++] = left;
                } else {
                    stack[top++] = left;
                    stack[top++] = right;
                }
            } else if (hitLeft) {
                stack[top++] = left;
            } else if (hitRight) {
                stack[top++] = right;
            }
            if (top > (stack.Length - 2)) { return; }
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Reciprocal(Vector3 direction) => (Vector3.One / direction);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Slab(Vector3 low, Vector3 high, Vector3 origin, Vector3 inverse, float maximum, out float entry) {
        var t0 = ((low - origin) * inverse);
        var t1 = ((high - origin) * inverse);
        var near = Vector3.Min(value1: t0, value2: t1);
        var far = Vector3.Max(value1: t0, value2: t1);

        entry = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));

        var exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, maximum));

        return (entry <= exit);
    }

    // What a walk does at a leaf: the top level enters each draw's geometry, a geometry tests its triangles.
    private interface ILeaves {
        bool Visit(int first, int count, ref float distance);
    }

    // The top level's leaves: a braid subtree of a draw's geometry each, walked by the ray in the geometry's frame. A ray
    // carried through an affine transform keeps its parameter, so distances compare across draws.
    private struct DrawLeaves(Entry[] entries, Instance[] instances, Vector3 origin, Vector3 direction, Vector3 inverse, bool any) : ILeaves {
        public int Instance = -1;
        public int Triangle = -1;

        public bool Visit(int first, int count, ref float distance) {
            for (var index = first; (index < (first + count)); index++) {
                var entry = entries[index];
                ref readonly var instance = ref instances[entry.Instance];
                var local = (instance.Identity ? direction : Vector3.TransformNormal(normal: direction, matrix: instance.WorldToObject));
                var triangles = new TriangleLeaves(any: any, direction: local, geometry: instance.Geometry, origin: (instance.Identity ? origin : Vector3.Transform(position: origin, matrix: instance.WorldToObject)));

                Walk(distance: ref distance, inverse: (instance.Identity ? inverse : Reciprocal(direction: local)), leaves: ref triangles, nodes: instance.Geometry.Nodes, origin: triangles.Origin, start: entry.Node);
                if (triangles.Hit >= 0) {
                    Instance = entry.Instance;
                    Triangle = triangles.Hit;
                    if (any) { return true; }
                }
            }

            return false;
        }
    }

    // A geometry's leaves: its triangles, Möller-Trumbore and two-sided, since a probe inside geometry must see the back
    // faces it would otherwise leak through.
    private struct TriangleLeaves(Geometry geometry, Vector3 origin, Vector3 direction, bool any) : ILeaves {
        public readonly Vector3 Origin = origin;
        public readonly Vector3 Direction = direction;
        public int Hit = -1;

        public bool Visit(int first, int count, ref float distance) {
            var positions = geometry.Positions.Span;
            var indices = geometry.Indices.Span;
            var triangles = geometry.Triangles;

            for (var index = first; (index < (first + count)); index++) {
                Geometry.Corner(e1: out var e1, e2: out var e2, indices: indices, positions: positions, triangle: triangles[index], v0: out var v0);
                if (HitTriangle(distance: out var t, e1: e1, e2: e2, maximum: distance, v0: v0)) {
                    distance = t;
                    Hit = index;
                    if (any) { return true; }
                }
            }

            return false;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly bool HitTriangle(Vector3 v0, Vector3 e1, Vector3 e2, float maximum, out float distance) {
            distance = 0f;

            var p = Vector3.Cross(vector1: Direction, vector2: e2);
            var determinant = Vector3.Dot(vector1: e1, vector2: p);

            if (MathF.Abs(determinant) < 1.0e-12f) { return false; }

            var inverse = (1f / determinant);
            var s = (Origin - v0);
            var u = (Vector3.Dot(vector1: s, vector2: p) * inverse);

            if ((u < 0f) || (u > 1f)) { return false; }

            var q = Vector3.Cross(vector1: s, vector2: e1);
            var v = (Vector3.Dot(vector1: Direction, vector2: q) * inverse);

            if ((v < 0f) || ((u + v) > 1f)) { return false; }

            distance = (Vector3.Dot(vector1: e2, vector2: q) * inverse);
            return ((distance > 1.0e-4f) && (distance < maximum));
        }
    }

    // A geometry's identity: its mesh's position and index buffers. A mesh renumbered into another program's materials
    // (SdfMeshLibrary) shares both, so its draws take the same geometry and read their own triangle materials.
    private readonly record struct GeometryKey(ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices) {
        public static GeometryKey Of(SdfMesh mesh) => new(Indices: mesh.Indices, Positions: mesh.Positions);
    }

    // The world bounds of a box in a draw's frame: its eight corners carried out through the draw's transform.
    private static (Vector3 Low, Vector3 High) Bounds(Matrix4x4 matrix, Vector3 low, Vector3 high) {
        var lowest = new Vector3(value: float.PositiveInfinity);
        var highest = new Vector3(value: float.NegativeInfinity);

        for (var corner = 0; (corner < 8); corner++) {
            var point = Vector3.Transform(
                matrix: matrix,
                position: new Vector3(
                    x: (((corner & 1) == 0) ? low.X : high.X),
                    y: (((corner & 2) == 0) ? low.Y : high.Y),
                    z: (((corner & 4) == 0) ? low.Z : high.Z)
                )
            );

            lowest = Vector3.Min(value1: lowest, value2: point);
            highest = Vector3.Max(value1: highest, value2: point);
        }

        return (lowest, highest);
    }

    // A top-level primitive: one braid subtree (its root node) of one draw's geometry.
    private readonly record struct Entry(int Instance, int Node);

    // One draw: its geometry, its transform and the transform's inverse (world to the mesh's frame), the matrix that
    // carries a normal of the mesh's frame to world space with the winding the transform gives it, its program material
    // base and its mesh's triangle materials.
    private readonly struct Instance {
        public required Geometry Geometry { get; init; }
        public required Matrix4x4 ObjectToWorld { get; init; }
        public required Matrix4x4 WorldToObject { get; init; }
        public required Matrix4x4 NormalToWorld { get; init; }
        public required bool Identity { get; init; }
        public required int Material { get; init; }
        public required ReadOnlyMemory<uint> TriangleMaterials { get; init; }
        public float BakeHalfDiagonal { get; init; }

        // A draw whose transform has no inverse (a zero scale) draws nothing a ray can meet.
        public static bool TryCreate(in SdfMeshDraw draw, Geometry geometry, out Instance instance) {
            var matrix = draw.ObjectToWorld;

            instance = default;
            if (!Matrix4x4.Invert(matrix: matrix, result: out var inverse)) { return false; }

            // Edges carried through the transform cross to det(M) times the normal carried by the inverse transpose.
            var normalToWorld = Matrix4x4.Transpose(matrix: inverse);

            if (matrix.GetDeterminant() < 0f) { normalToWorld *= -1f; }
            instance = new Instance {
                Geometry = geometry,
                Identity = matrix.IsIdentity,
                Material = draw.Material,
                NormalToWorld = normalToWorld,
                ObjectToWorld = matrix,
                TriangleMaterials = draw.Mesh.TriangleMaterials,
                WorldToObject = inverse,
                BakeHalfDiagonal = 0.5f * Vector3.TransformNormal(draw.Mesh.BakeCell, matrix).Length(),
            };
            return true;
        }
        public Vector3 NormalOf(int triangle) {
            Geometry.Corner(e1: out var e1, e2: out var e2, index: triangle, v0: out _);

            var normal = Vector3.Cross(vector1: e1, vector2: e2);

            if (!Identity) { normal = Vector3.TransformNormal(matrix: NormalToWorld, normal: normal); }

            var length = normal.Length();

            return ((length > 0f) ? (normal / length) : Vector3.UnitY);
        }
        public int MaterialOf(int triangle) =>
            (Material + (TriangleMaterials.IsEmpty ? 0 : (int)TriangleMaterials.Span[Geometry.Triangles[triangle]]));
    }

    // One geometry's triangles in its mesh's frame, in leaf order under its own hierarchy: the mesh triangle each leaf
    // position holds, read through the mesh's own positions and indices rather than a copy of them, and the hierarchy's
    // braid subtrees: the widest nodes at most BraidTriangles wide, which together hold every leaf position once. A
    // triangle whose box is loose about it (a long thin one a merged polygon leaves, SdfMeshPlanarMerge) holds several
    // leaf positions, each bounded by a piece of it, so a ray tests it only where it passes near.
    private sealed class Geometry {
        public required ReadOnlyMemory<Vector3> Positions { get; init; }
        public required ReadOnlyMemory<uint> Indices { get; init; }
        public required int[] Triangles { get; init; }
        public required Node[] Nodes { get; init; }
        public required int[] Braids { get; init; }
        public int Count => (Indices.Length / 3);

        // The leaf position's triangle as its first corner and its two edges from it, the terms the ray test reads.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Corner(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, int triangle, out Vector3 v0, out Vector3 e1, out Vector3 e2) {
            var first = (3 * triangle);

            v0 = positions[(int)indices[first]];
            e1 = (positions[(int)indices[(first + 1)]] - v0);
            e2 = (positions[(int)indices[(first + 2)]] - v0);
        }
        public void Corner(int index, out Vector3 v0, out Vector3 e1, out Vector3 e2) =>
            Corner(e1: out e1, e2: out e2, indices: Indices.Span, positions: Positions.Span, triangle: Triangles[index], v0: out v0);

        public static Geometry Build(SdfMesh mesh) {
            var count = mesh.TriangleCount;
            var chunks = (((count + ParallelPrimitives) - 1) / ParallelPrimitives);
            var pieces = new Pieces[chunks];

            // Each chunk's pieces in triangle order, joined in chunk order, so a geometry builds alike on every run.
            Chunked(count: count, body: (start, end) => {
                var positions = mesh.Positions.Span;
                var indices = mesh.Indices.Span;
                var own = new Pieces(capacity: (end - start));

                for (var index = start; (index < end); index++) {
                    own.Add(a: positions[(int)indices[(3 * index)]], b: positions[(int)indices[((3 * index) + 1)]], c: positions[(int)indices[((3 * index) + 2)]], depth: 0, triangle: index);
                }
                pieces[(start / ParallelPrimitives)] = own;
            });

            var total = pieces.Sum(selector: static piece => piece.Count);
            var lows = new Vector3[total];
            var highs = new Vector3[total];
            var owners = new int[total];
            var at = 0;

            foreach (var piece in pieces) {
                piece.CopyTo(at: at, highs: highs, lows: lows, owners: owners);
                at += piece.Count;
            }

            var (nodes, order, braids) = Hierarchy.Build(braid: BraidTriangles, highs: highs, leaf: LeafTriangles, lows: lows);
            var triangles = new int[order.Length];

            for (var index = 0; (index < order.Length); index++) {
                triangles[index] = owners[order[index]];
            }

            return new Geometry { Braids = braids, Indices = mesh.Indices, Nodes = nodes, Positions = mesh.Positions, Triangles = triangles };
        }
        private static void Chunked(int count, Action<int, int> body) {
            if (count < ParallelPrimitives) {
                body(arg1: 0, arg2: count);
                return;
            }
            _ = Parallel.For(fromInclusive: 0, toExclusive: (((count + ParallelPrimitives) - 1) / ParallelPrimitives), body: chunk => body(arg1: (chunk * ParallelPrimitives), arg2: Math.Min(val1: count, val2: ((chunk + 1) * ParallelPrimitives))));
        }

        // The boxes a run of triangles enters the hierarchy as, each with the triangle it bounds a piece of: a triangle
        // whose box's surface is more than LooseBox times its own area halves across its longest edge, down to MaxSplits
        // halvings, and a piece is boxed as its own triangle is. A flat right triangle along two axes, the tightest a box
        // holds, reads 4; a long thin triangle across a face reads its length over its width.
        private sealed class Pieces(int capacity) {
            private const float LooseBox = 64f;
            private const int MaxSplits = 6;

            private readonly List<Vector3> m_lows = new(capacity: capacity);
            private readonly List<Vector3> m_highs = new(capacity: capacity);
            private readonly List<int> m_owners = new(capacity: capacity);

            public int Count => m_owners.Count;

            public void Add(Vector3 a, Vector3 b, Vector3 c, int triangle, int depth) {
                var low = Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c));
                var high = Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c));
                var area = (0.5f * Vector3.Cross(vector1: (b - a), vector2: (c - a)).Length());
                var size = (high - low);
                var surface = (2f * (((size.X * size.Y) + (size.Y * size.Z)) + (size.Z * size.X)));

                if ((depth >= MaxSplits) || !(area > 0f) || (surface <= (LooseBox * area))) {
                    m_lows.Add(item: low);
                    m_highs.Add(item: high);
                    m_owners.Add(item: triangle);
                    return;
                }

                var (ab, bc, ca) = (Vector3.DistanceSquared(value1: a, value2: b), Vector3.DistanceSquared(value1: b, value2: c), Vector3.DistanceSquared(value1: c, value2: a));

                if ((ab >= bc) && (ab >= ca)) {
                    var middle = ((a + b) * 0.5f);

                    Add(a: a, b: middle, c: c, depth: (depth + 1), triangle: triangle);
                    Add(a: middle, b: b, c: c, depth: (depth + 1), triangle: triangle);
                } else if (bc >= ca) {
                    var middle = ((b + c) * 0.5f);

                    Add(a: a, b: b, c: middle, depth: (depth + 1), triangle: triangle);
                    Add(a: a, b: middle, c: c, depth: (depth + 1), triangle: triangle);
                } else {
                    var middle = ((c + a) * 0.5f);

                    Add(a: a, b: b, c: middle, depth: (depth + 1), triangle: triangle);
                    Add(a: middle, b: b, c: c, depth: (depth + 1), triangle: triangle);
                }
            }
            public void CopyTo(Vector3[] lows, Vector3[] highs, int[] owners, int at) {
                m_lows.CopyTo(array: lows, arrayIndex: at);
                m_highs.CopyTo(array: highs, arrayIndex: at);
                m_owners.CopyTo(array: owners, arrayIndex: at);
            }
        }
    }

    // A binned surface-area hierarchy over primitive bounds, its wide ranges' children built in parallel. Its nodes
    // are written into chunks allocated as the build reaches them, since the bound a range can need (2n - 1 nodes)
    // is several times what a build of leaves several primitives wide uses; the result is copied out at its size.
    private sealed class Hierarchy {
        private const int ChunkShift = 16;
        private const int ChunkMask = ((1 << ChunkShift) - 1);

        private readonly Vector3[] m_lows;
        private readonly Vector3[] m_highs;
        private readonly Vector3[] m_centroids;
        private readonly int[] m_order;
        private readonly Node[]?[] m_chunks;
        private readonly int m_capacity;
        private readonly int m_leaf;
        private readonly int m_braid;
        private readonly List<int> m_braids = [];
        private int m_nodeCount = 1;

        private Hierarchy(Vector3[] lows, Vector3[] highs, int leaf, int braid) {
            var count = lows.Length;

            m_lows = lows;
            m_highs = highs;
            m_leaf = leaf;
            m_braid = braid;
            m_centroids = new Vector3[count];
            m_order = new int[count];
            m_capacity = Math.Max(val1: 1, val2: ((2 * count) - 1));
            m_chunks = new Node[]?[(((m_capacity - 1) >> ChunkShift) + 1)];
            for (var index = 0; (index < count); index++) {
                m_centroids[index] = ((lows[index] + highs[index]) * 0.5f);
                m_order[index] = index;
            }
        }

        // The nodes, root first, the primitive each leaf position holds, and with a braid width the widest nodes at most
        // that many primitives wide.
        public static (Node[] Nodes, int[] Order, int[] Braids) Build(Vector3[] lows, Vector3[] highs, int leaf, int braid) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: leaf, other: Node.MaxCount);

            var hierarchy = new Hierarchy(braid: braid, highs: highs, leaf: leaf, lows: lows);

            hierarchy.BuildNode(count: lows.Length, first: 0, node: 0, wide: true);
            hierarchy.m_braids.Sort();
            return (hierarchy.CopyNodes(), hierarchy.m_order, [.. hierarchy.m_braids]);
        }
        // A node's storage, its chunk allocated by the first build task to reach it; the last chunk is as long as the
        // bound needs, so a small hierarchy allocates no more than it could use.
        private ref Node At(int node) {
            var index = (node >> ChunkShift);
            var chunk = Volatile.Read(location: ref m_chunks[index]);

            if (chunk is null) {
                lock (m_chunks) {
                    chunk = m_chunks[index];
                    if (chunk is null) {
                        chunk = new Node[Math.Min(val1: (1 << ChunkShift), val2: (m_capacity - (index << ChunkShift)))];
                        Volatile.Write(location: ref m_chunks[index], value: chunk);
                    }
                }
            }

            return ref chunk[(node & ChunkMask)];
        }
        private Node[] CopyNodes() {
            var nodes = new Node[m_nodeCount];

            for (var start = 0; (start < m_nodeCount); start += (1 << ChunkShift)) {
                m_chunks[(start >> ChunkShift)].AsSpan(length: Math.Min(val1: (1 << ChunkShift), val2: (m_nodeCount - start)), start: 0).CopyTo(destination: nodes.AsSpan(start: start));
            }

            return nodes;
        }

        // Builds a node over a range of the order; wide says its parent, if any, is wider than the braid width.
        private void BuildNode(int node, int first, int count, bool wide) {
            if (wide && (count <= m_braid)) {
                lock (m_braids) { m_braids.Add(item: node); }
            }

            var low = new Vector3(value: float.PositiveInfinity);
            var high = new Vector3(value: float.NegativeInfinity);
            var centroidLow = low;
            var centroidHigh = high;

            for (var index = first; (index < (first + count)); index++) {
                var primitive = m_order[index];

                low = Vector3.Min(value1: low, value2: m_lows[primitive]);
                high = Vector3.Max(value1: high, value2: m_highs[primitive]);
                centroidLow = Vector3.Min(value1: centroidLow, value2: m_centroids[primitive]);
                centroidHigh = Vector3.Max(value1: centroidHigh, value2: m_centroids[primitive]);
            }

            ref var built = ref At(node: node);

            built.Low = low;
            built.High = high;

            if (count <= m_leaf) {
                built.Link(count: count, first: first);
                return;
            }

            var mid = Split(centroidHigh: centroidHigh, centroidLow: centroidLow, count: count, first: first);
            var child = Interlocked.Add(location1: ref m_nodeCount, value: 2) - 2;

            built.Link(count: 0, first: child);
            var wider = (count > m_braid);

            if (count >= ParallelPrimitives) {
                BuildChildrenInParallel(child: child, count: count, first: first, mid: mid, wide: wider);
                return;
            }
            BuildNode(count: (mid - first), first: first, node: child, wide: wider);
            BuildNode(count: ((first + count) - mid), first: mid, node: (child + 1), wide: wider);
        }
        // Apart from BuildNode, since the closure its two branches share is made on entry to the method holding them, and
        // nearly every node builds its children in line.
        private void BuildChildrenInParallel(int child, int first, int count, int mid, bool wide) => Parallel.Invoke(
            () => BuildNode(count: (mid - first), first: first, node: child, wide: wide),
            () => BuildNode(count: ((first + count) - mid), first: mid, node: (child + 1), wide: wide)
        );
        // A binned surface-area split along the centroids' longest axis, which partitions the range in place; an even
        // split when the bins cannot separate them.
        private int Split(int first, int count, Vector3 centroidLow, Vector3 centroidHigh) {
            var extent = (centroidHigh - centroidLow);
            var axis = ((extent.X >= extent.Y) && (extent.X >= extent.Z)) ? 0 : ((extent.Y >= extent.Z) ? 1 : 2);
            var axisLow = Component(value: centroidLow, axis: axis);
            var axisExtent = Component(value: extent, axis: axis);
            var mid = (first + (count / 2));

            if (axisExtent <= 0f) { return mid; }

            Span<int> binCounts = stackalloc int[Bins];
            Span<Vector3> binLows = stackalloc Vector3[Bins];
            Span<Vector3> binHighs = stackalloc Vector3[Bins];

            binLows.Fill(value: new Vector3(value: float.PositiveInfinity));
            binHighs.Fill(value: new Vector3(value: float.NegativeInfinity));
            for (var index = first; (index < (first + count)); index++) {
                var primitive = m_order[index];
                var bin = BinOf(value: Component(value: m_centroids[primitive], axis: axis), low: axisLow, extent: axisExtent);

                binCounts[bin]++;
                binLows[bin] = Vector3.Min(value1: binLows[bin], value2: m_lows[primitive]);
                binHighs[bin] = Vector3.Max(value1: binHighs[bin], value2: m_highs[primitive]);
            }

            var bestCost = float.PositiveInfinity;
            var bestSplit = -1;

            for (var split = 1; (split < Bins); split++) {
                var leftCount = 0;
                var rightCount = 0;
                var leftLow = new Vector3(value: float.PositiveInfinity);
                var leftHigh = new Vector3(value: float.NegativeInfinity);
                var rightLow = leftLow;
                var rightHigh = leftHigh;

                for (var bin = 0; (bin < split); bin++) {
                    if (binCounts[bin] == 0) { continue; }
                    leftCount += binCounts[bin];
                    leftLow = Vector3.Min(value1: leftLow, value2: binLows[bin]);
                    leftHigh = Vector3.Max(value1: leftHigh, value2: binHighs[bin]);
                }
                for (var bin = split; (bin < Bins); bin++) {
                    if (binCounts[bin] == 0) { continue; }
                    rightCount += binCounts[bin];
                    rightLow = Vector3.Min(value1: rightLow, value2: binLows[bin]);
                    rightHigh = Vector3.Max(value1: rightHigh, value2: binHighs[bin]);
                }
                if ((leftCount == 0) || (rightCount == 0)) { continue; }

                var cost = ((leftCount * Area(low: leftLow, high: leftHigh)) + (rightCount * Area(low: rightLow, high: rightHigh)));

                if (cost < bestCost) {
                    bestCost = cost;
                    bestSplit = split;
                }
            }
            if (bestSplit <= 0) { return mid; }

            var left = first;
            var right = ((first + count) - 1);

            while (left <= right) {
                if (BinOf(value: Component(value: m_centroids[m_order[left]], axis: axis), low: axisLow, extent: axisExtent) < bestSplit) {
                    left++;
                } else {
                    (m_order[left], m_order[right]) = (m_order[right], m_order[left]);
                    right--;
                }
            }

            return (((left > first) && (left < (first + count))) ? left : mid);
        }
        private static int BinOf(float value, float low, float extent) =>
            Math.Clamp(value: (int)(((value - low) / extent) * Bins), min: 0, max: (Bins - 1));
        private static float Area(Vector3 low, Vector3 high) {
            var d = Vector3.Max(value1: (high - low), value2: Vector3.Zero);

            return ((d.X * d.Y) + (d.Y * d.Z) + (d.Z * d.X));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Component(Vector3 value, int axis) => ((axis == 0) ? value.X : ((axis == 1) ? value.Y : value.Z));
    }
}
