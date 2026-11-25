using Puck.Maths;
using System.Diagnostics.CodeAnalysis;
using Puck.Hosting.Jobs;
using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Memory;

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
public sealed partial class SdfMeshIrradianceScene : IDisposable {
    private int m_claimCount = 1;

    private readonly SdfMeshClaims m_meshClaims;

    /// <summary>Acquires a scene claim before an independently completing reader starts.</summary>
    public SdfMeshIrradianceScene Acquire() {
        var count = Volatile.Read(location: ref m_claimCount);

        while (true) {
            ObjectDisposedException.ThrowIf(condition: (count <= 0), instance: this);
            var observed = Interlocked.CompareExchange(comparand: count, location1: ref m_claimCount, value: (count + 1));

            if (observed == count) { return this; }
            count = observed;
        }
    }
    /// <summary>Releases one scene claim; the last reader releases the scene's mesh claims and its share of the scene's
    /// arrays, which go back to the pool once no scene shares them.</summary>
    public void Dispose() {
        var count = Interlocked.Decrement(location: ref m_claimCount);

        if (count == 0) {
            m_meshClaims.Dispose();
            m_content.Release();
            foreach (var geometry in m_geometries.Values) { geometry.Release(); }
            foreach (var entry in m_retained) { entry.Value.Release(); }
        } else if (count < 0) { throw new InvalidOperationException(message: "A scene claim was released twice."); }
    }

    internal SdfMeshIrradianceScene WithoutRetained() => ((m_retained.Count == 0) ? Acquire() : new(source: this));

    // What a GPU export of the scene holds (its draws, hierarchy and geometries): one for each built scene, and shared by a
    // scene trimmed of its retained geometry (WithoutRetained), which exports the same words. Its identity is the GPU
    // content's identity.
    private readonly Content m_content;

    /// <summary>Returns whether two scenes export the same GPU words: one is the other, or one is the other trimmed of the
    /// geometry it retained for a later build, so a scene uploaded for one serves the other without another upload.</summary>
    /// <param name="first">A scene, or <see langword="null"/>.</param>
    /// <param name="second">Another scene, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are non-null and share their GPU content.</returns>
    internal static bool SameGpuContent(SdfMeshIrradianceScene? first, SdfMeshIrradianceScene? second) =>
        ((first is not null) && (second is not null) && ReferenceEquals(objA: first.m_content, objB: second.m_content));

    // The arrays a built scene keeps (its draws, the top level's entries and its nodes), rented and counted, each array
    // possibly longer than its count. The one constructor fills them and nothing writes them after. Each scene sharing the
    // content holds one claim on it, taken in its constructor and released by its last scene claim; the last release
    // returns the arrays to the pool and leaves the content empty, so a reader that outlived every claim reads no scene
    // rather than another renter's words. Every reader of a scene therefore holds a scene claim (Acquire), as its mesh
    // claims already require.
    private sealed class Content {
        private int m_claims = 1;

        public readonly MemoryAccount.Owner Memory = MemoryAccount.Shared.CreateOwner(name: "sdf.mesh-gi.trace-scene", memoryClass: MemoryClass.Elastic);
        public Instance[] Instances = [];
        public int InstanceCount;
        public Entry[] Entries = [];
        public int EntryCount;
        public Node[] Nodes = [];
        public int NodeCount;

        public Content Acquire() {
            var count = Interlocked.Increment(location: ref m_claims);

            if (count <= 1) { throw new InvalidOperationException(message: "A scene's arrays were shared after their last release."); }
            return this;
        }
        public void Release() {
            var count = Interlocked.Decrement(location: ref m_claims);

            if (count < 0) { throw new InvalidOperationException(message: "A scene's arrays were released twice."); }
            if (count > 0) { return; }

            var (instances, entries, nodes) = (Instances, Entries, Nodes);

            (Instances, Entries, Nodes) = ([], [], []);
            (InstanceCount, EntryCount, NodeCount) = (0, 0, 0);
            RentedArrays.Return(array: instances, owner: Memory);
            RentedArrays.Return(array: entries, owner: Memory);
            RentedArrays.Return(array: nodes, owner: Memory);
            ReleaseStorage(Memory);
        }
    }

    private SdfMeshIrradianceScene(SdfMeshIrradianceScene source) {
        m_content = source.m_content.Acquire();
        m_geometries = source.m_geometries;
        m_retained = [];
        TriangleCount = source.TriangleCount;
        GeometriesBuilt = source.GeometriesBuilt;
        TrianglesBuilt = source.TrianglesBuilt;
        Materials = source.Materials;
        Counters = source.Counters;
        TopDepth = source.TopDepth;
        GeometryDepth = source.GeometryDepth;
        MedianSplits = source.MedianSplits;
        m_meshClaims = source.m_meshClaims.Clone();
        AcquireGeometries();
    }

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
    private const int StackDepth = SdfMeshProbeTrace.StackDepth;
    // The widest subtree of a geometry the top level indexes on its own (Geometry.Braids).
    private const int BraidTriangles = 1024;

    private readonly Dictionary<GeometryKey, Geometry> m_geometries;
    private readonly List<KeyValuePair<GeometryKey, Geometry>> m_retained;

    /// <summary>Gets the triangles the scene's draws hold, a geometry drawn twice counted twice.</summary>
    public long TriangleCount { get; }
    /// <summary>Gets the static draws the scene holds.</summary>
    public int DrawCount => m_content.InstanceCount;
    /// <summary>Gets the scene's top-level entries: one a braid of a static draw.</summary>
    public int EntryCount => m_content.EntryCount;
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
    /// <summary>Deepest top-level leaf, with the root at depth zero.</summary>
    public int TopDepth { get; }
    /// <summary>Deepest geometry leaf, with each geometry's root at depth zero.</summary>
    public int GeometryDepth { get; }
    /// <summary>Splits forced to the median to keep the hierarchy within the shared traversal stack.</summary>
    public int MedianSplits { get; }
    /// <summary>Traversal assertion counters, shared with the owning volume when built for presentation.</summary>
    public WorkCounterSet Counters { get; private init; } = null!;

    /// <summary>Gets the managed array bytes this scene's hierarchy holds, including retained geometry hierarchies and the
    /// whole of each array rented for the top level, its unused tail included. Excludes object headers, dictionaries,
    /// mesh arrays shared with the bake, and temporary build workspace.</summary>
    public long HierarchyBytes => m_content.Nodes.LongLength * System.Runtime.CompilerServices.Unsafe.SizeOf<Node>() +
        m_content.Entries.LongLength * System.Runtime.CompilerServices.Unsafe.SizeOf<Entry>() +
        m_content.Instances.LongLength * System.Runtime.CompilerServices.Unsafe.SizeOf<Instance>() + Materials.LongLength * sizeof(int) +
        m_geometries.Values.Sum(static geometry => geometry.HierarchyBytes) + m_retained.Sum(static entry => entry.Value.HierarchyBytes);

    // 28 bytes: the bounds, then one word holding the first index above the count's CountBits.
    internal struct Node {
        private const int CountBits = SdfMeshProbeTrace.NodeCountBits;

        public Vector3 Low;
        public Vector3 High;

        private int m_firstAndCount;

        // A leaf's first primitive, or an inner node's first child (its second follows it).
        public readonly int First => (m_firstAndCount >> CountBits);
        // A leaf's primitive count; zero for an inner node.
        public readonly int Count => m_firstAndCount & ((1 << CountBits) - 1);
        // The first index and count as one word, as a packed node keeps them.
        public readonly int LinkWord => m_firstAndCount;

        // The most primitives a leaf can hold.
        public const int MaxCount = ((1 << CountBits) - 1);

        public void Link(int first, int count) => m_firstAndCount = (first << CountBits) | count;
    }
    // 16 bytes: a geometry node's bounds as steps of its braid's frame (BraidFrame), then the link Node holds. The GPU
    // record is the same words (SdfMeshProbeTrace.PackedNodeWords), decoded by sdfProbePackedSlab.
    internal struct PackedNode {
        private ushort m_lowX, m_lowY, m_lowZ, m_highX, m_highY, m_highZ;
        private int m_firstAndCount;

        public readonly int First => (m_firstAndCount >> SdfMeshProbeTrace.NodeCountBits);
        public readonly int Count => m_firstAndCount & Node.MaxCount;

        public static PackedNode Of(in Node node, in BraidFrame frame) => new() {
            m_lowX = BraidFrame.Below(value: node.Low.X, low: frame.Low.X, step: frame.Step.X),
            m_lowY = BraidFrame.Below(value: node.Low.Y, low: frame.Low.Y, step: frame.Step.Y),
            m_lowZ = BraidFrame.Below(value: node.Low.Z, low: frame.Low.Z, step: frame.Step.Z),
            m_highX = BraidFrame.Above(value: node.High.X, low: frame.Low.X, step: frame.Step.X),
            m_highY = BraidFrame.Above(value: node.High.Y, low: frame.Low.Y, step: frame.Step.Y),
            m_highZ = BraidFrame.Above(value: node.High.Z, low: frame.Low.Z, step: frame.Step.Z),
            m_firstAndCount = node.LinkWord,
        };

        // The record's first three words, each two steps with the first in the low half.
        public readonly uint Word(int index) => index switch {
            0 => (m_lowX | ((uint)m_lowY << 16)),
            1 => (m_lowZ | ((uint)m_highX << 16)),
            _ => (m_highY | ((uint)m_highZ << 16)),
        };
        public readonly Vector3 LowIn(in BraidFrame frame) => frame.Decode(steps: new Vector3(x: m_lowX, y: m_lowY, z: m_lowZ));
        public readonly Vector3 HighIn(in BraidFrame frame) => frame.Decode(steps: new Vector3(x: m_highX, y: m_highY, z: m_highZ));
    }
    // A braid subtree's frame: its root's low corner and a step per axis such that PackedSteps steps reach at least its
    // root's high corner. A bound decodes to low + steps * step, the same float expression on the GPU (sdfProbePackedSlab,
    // marked precise), so both walks read one box.
    //
    // Invariant: a decoded low is at most the node's true low and a decoded high at least its true high, so a packed
    // node never admits less than the float node it packs; Below and Above, the only encoders, check each step against
    // that same decode.
    internal readonly record struct BraidFrame(Vector3 Low, Vector3 Step) {
        public static BraidFrame Over(Vector3 low, Vector3 high) {
            var step = Vector3.Max(value1: ((high - low) / SdfMeshProbeTrace.PackedSteps), value2: Vector3.Zero);

            return new(Low: low, Step: new Vector3(x: Reaching(high: high.X, low: low.X, step: step.X), y: Reaching(high: high.Y, low: low.Y, step: step.Y), z: Reaching(high: high.Z, low: low.Z, step: step.Z)));
        }
        public Vector3 Decode(Vector3 steps) => (Low + (steps * Step));
        public Vector3 High => Decode(steps: new Vector3(value: SdfMeshProbeTrace.PackedSteps));

        // One axis's step, raised until the last step reaches the high bound in the decode's own float arithmetic.
        private static float Reaching(float low, float high, float step) {
            while ((low + (SdfMeshProbeTrace.PackedSteps * step)) < high) { step = MathF.BitIncrement(x: step); }
            return step;
        }
        // The largest step count whose decode lies at or below the value.
        public static ushort Below(float value, float low, float step) {
            if (!(step > 0f)) { return 0; }
            var steps = Math.Clamp(value: (int)MathF.Floor(x: ((value - low) / step)), min: 0, max: SdfMeshProbeTrace.PackedSteps);

            while ((steps > 0) && ((low + (steps * step)) > value)) { steps--; }
            return (ushort)steps;
        }
        // The smallest step count whose decode lies at or above the value.
        public static ushort Above(float value, float low, float step) {
            if (!(step > 0f)) { return 0; }
            var steps = Math.Clamp(value: (int)MathF.Ceiling(x: ((value - low) / step)), min: 0, max: SdfMeshProbeTrace.PackedSteps);

            while ((steps < SdfMeshProbeTrace.PackedSteps) && ((low + (steps * step)) < value)) { steps++; }
            return (ushort)steps;
        }
    }

    /// <summary>Builds the scene of a frame's static mesh draws: every draw that is not dynamic.</summary>
    /// <param name="draws">The frame's mesh draws.</param>
    /// <param name="previous">The scene the draws change, whose geometries the new scene takes rather than builds, or
    /// <see langword="null"/> to build every geometry.</param>
    /// <param name="counters">The owning volume's traversal counters, or null for a scene-local counter set.</param>
    /// <returns>The scene, empty when no static draw holds a triangle. An empty scene has no occluders and can still
    /// supply measured sky irradiance; it is distinct from a scene not built yet.</returns>
    /// <param name="traceError">The most a traced surface may stand from a mesh's own triangles, in the mesh's object
    /// units: each mesh traces its coarsest level whose measured error is within it (<see cref="SdfMeshLevel.Error"/>),
    /// or its own triangles. Zero, the default, traces every mesh's own triangles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> is <see langword="null"/>.</exception>
    public static SdfMeshIrradianceScene Build(IReadOnlyList<SdfMeshDraw> draws, SdfMeshIrradianceScene? previous = null, WorkCounterSet? counters = null, float traceError = 0f) {
        ArgumentNullException.ThrowIfNull(argument: draws);

        var geometries = new Dictionary<GeometryKey, Geometry>();
        var pending = new List<(GeometryKey Key, SdfMesh Mesh)>();
        List<Geometry>? borrowed = null;
        var statics = 0;

        // Indexed: an interface list's enumerator is an allocation.
        for (var index = 0; (index < draws.Count); index++) {
            var draw = draws[index];

            if (!draw.IsStatic) { continue; }
            statics++;

            var key = GeometryKey.Of(error: traceError, mesh: draw.Mesh);

            if (geometries.ContainsKey(key: key)) { continue; }
            if ((previous is not null) && previous.TryFind(geometry: out var held, key: key)) {
                geometries.Add(key: key, value: held);
                continue;
            }
            // Another scene of the same mesh (a second residency of the world, whose draws the mesh region store
            // hands the same meshes) built its geometry already: the build takes a claim on it, which it gives back
            // once the scene holds its own.
            if (Geometry.TryAcquireShared(key: key, geometry: out var shared)) {
                geometries.Add(key: key, value: shared);
                (borrowed ??= []).Add(item: shared);
                continue;
            }
            geometries.Add(key: key, value: null!);
            pending.Add(item: (key, draw.Mesh));
        }

        var built = new Geometry[pending.Count];

        var workspace = MemoryAccount.Shared.CreateOwner(name: "sdf.mesh-gi.scene-build", memoryClass: MemoryClass.Workspace);
        var content = new Content();
        try {
            // The largest first, so one long build does not start last.
            pending.Sort(comparison: static (a, b) => b.Mesh.TriangleCount.CompareTo(value: a.Mesh.TriangleCount));
            _ = Parallel.For(fromInclusive: 0, toExclusive: pending.Count, body: index => built[index] = Geometry.Build(key: pending[index].Key, mesh: pending[index].Mesh, workspace: workspace));

            var trianglesBuilt = 0L;

            for (var index = 0; (index < pending.Count); index++) {
                geometries[pending[index].Key] = built[index];
                trianglesBuilt += built[index].Count;
                Geometry.Share(key: pending[index].Key, geometry: built[index]);
            }

            // The scene's draws go straight into the array it keeps, rented at the static draws' count.
            content.Instances = RentedArrays.Rent<Instance>(minimumLength: statics, owner: content.Memory);

            var materials = new HashSet<int>();
            var materialRuns = new HashSet<(ReadOnlyMemory<uint> Triangles, int Base)>();
            for (var index = 0; (index < draws.Count); index++) {
                var draw = draws[index];

                if (!draw.IsStatic) { continue; }

                var geometry = geometries[GeometryKey.Of(error: traceError, mesh: draw.Mesh)];
                var triangleMaterials = geometry.MaterialsOf(mesh: draw.Mesh);

                if (Instance.TryCreate(draw: in draw, geometry: geometry, instance: out var instance, triangleMaterials: triangleMaterials)) {
                    content.Instances[content.InstanceCount++] = instance;
                    if (materialRuns.Add(item: (triangleMaterials, draw.Material))) {
                        AddMaterials(draw: in draw, materials: materials, triangles: triangleMaterials.Span);
                    }
                }
            }
            var order = content.Memory.AllocateArray<int>(materials.Count);
            materials.CopyTo(order);

            Array.Sort(array: order);

            return new SdfMeshIrradianceScene(
                content: content, workspace: workspace,
                geometries: geometries,
                draws: draws,
                geometriesBuilt: pending.Count,
                retained: Retain(geometries: geometries, previous: previous),
                trianglesBuilt: trianglesBuilt
            ) { Materials = order, Counters = counters ?? new(SdfMeshIrradiance.SourceName, SdfMeshIrradiance.Kinds) };
        } catch {
            content.Release();
            throw;
        } finally {
            foreach (var geometry in built) { geometry?.Release(); }
            if (borrowed is not null) {
                foreach (var geometry in borrowed) { geometry.Release(); }
            }
            ReleaseStorage(workspace);
        }
    }

    /// <summary>Gets the program material ids the scene's triangles carry, in increasing order: the materials a ray that
    /// meets the scene reads, and the only ones whose values its bounce light depends on.</summary>
    public int[] Materials { get; private init; } = [];

    // Adds the program material ids a draw's traced triangles carry: its base, offset by each distinct triangle material.
    private static void AddMaterials(HashSet<int> materials, in SdfMeshDraw draw, ReadOnlySpan<uint> triangles) {
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

    // Takes the content, whose draws Build filled, and fills its entries and nodes; Build releases it if this throws.
    private SdfMeshIrradianceScene(Content content, MemoryAccount.Owner workspace, Dictionary<GeometryKey, Geometry> geometries, IReadOnlyList<SdfMeshDraw> draws, List<KeyValuePair<GeometryKey, Geometry>> retained, int geometriesBuilt, long trianglesBuilt) {
        var instances = content.Instances.AsSpan(start: 0, length: content.InstanceCount);
        var count = 0;
        var triangles = 0L;
        var depth = 0;
        var medians = 0;

        foreach (ref readonly var instance in instances) { count += instance.Geometry.Braids.Length; }

        // Build workspace only, rented: the scene keeps none of these past its build.
        var entries = RentedArrays.Rent<Entry>(minimumLength: count, owner: workspace);
        var lows = RentedArrays.Rent<Vector3>(minimumLength: count, owner: workspace);
        var highs = RentedArrays.Rent<Vector3>(minimumLength: count, owner: workspace);
        var order = Array.Empty<int>();

        try {
            var at = 0;

            // The top level indexes each draw's braid subtrees, not its root, so overlapping draws separate as one
            // hierarchy over all their triangles would.
            for (var index = 0; (index < instances.Length); index++) {
                ref readonly var instance = ref instances[index];

                triangles += instance.Geometry.Count;
                var braids = instance.Geometry.Braids;

                for (var braid = 0; (braid < braids.Length); braid++) {
                    ref readonly var frame = ref instance.Geometry.Frames[braid];
                    var (low, high) = (frame.Low, frame.High);

                    (lows[at], highs[at]) = (instance.Identity ? (low, high) : Bounds(high: high, low: low, matrix: instance.ObjectToWorld));
                    entries[at++] = new Entry(Instance: index, Node: braids[braid], Braid: braid);
                }
            }
            if (count > 0) {
                (content.Nodes, content.NodeCount, order, _, depth, medians) = Hierarchy.Build(braid: 0, count: count, highs: highs, leaf: 1, lows: lows, name: "mesh GI static scene", rent: true, workspace: workspace, storage: content.Memory);
            }
            content.Entries = RentedArrays.Rent<Entry>(minimumLength: count, owner: content.Memory);
            for (var index = 0; (index < count); index++) {
                content.Entries[index] = entries[order[index]];
            }
            content.EntryCount = count;
        } finally {
            RentedArrays.Return(array: entries, owner: workspace);
            RentedArrays.Return(array: lows, owner: workspace);
            RentedArrays.Return(array: highs, owner: workspace);
            RentedArrays.Return(array: order, owner: workspace);
        }

        m_content = content;
        TopDepth = depth;
        GeometryDepth = geometries.Values.Select(static geometry => geometry.Depth).DefaultIfEmpty().Max();
        MedianSplits = medians + geometries.Values.Sum(static geometry => geometry.MedianSplits);
        m_geometries = geometries;
        m_retained = retained;
        GeometriesBuilt = geometriesBuilt;
        TriangleCount = triangles;
        TrianglesBuilt = trianglesBuilt;
        TrianglesRetained = retained.Sum(selector: static entry => ((long)entry.Value.Count));
        // The draws' meshes; each geometry, retained ones included, pins the mesh it reads itself (Geometry.Mesh).
        m_meshClaims = new SdfMeshClaims(meshes: draws.Where(predicate: static draw => draw.IsStatic).Select(selector: static draw => draw.Mesh));
        AcquireGeometries();
    }

    private void AcquireGeometries() {
        foreach (var geometry in m_geometries.Values) { geometry.Acquire(); }
        foreach (var entry in m_retained) { entry.Value.Acquire(); }
    }
    // All workers have joined or the last reader has released its claim. This also accounts for
    // partially constructed arrays abandoned when a parallel build was refused.
    private static void ReleaseStorage(MemoryAccount.Owner owner) {
        owner.Change(-owner.Bytes, MemoryBacking.Managed);
        owner.Dispose();
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
        Intersect(bakeHalfDiagonal: out _, direction: direction, distance: out distance, material: out material, maximum: maximum, normal: out normal, origin: origin);
    /// <summary>Finds the nearest retained triangle and the uncertainty of its own contour lattice.</summary>
    /// <param name="origin">World-space ray origin.</param>
    /// <param name="direction">Unit ray direction.</param>
    /// <param name="maximum">Search distance in metres.</param>
    /// <param name="distance">Hit distance in metres.</param>
    /// <param name="normal">Geometric surface normal.</param>
    /// <param name="material">Program material.</param>
    /// <param name="bakeHalfDiagonal">Half of the hit draw's world-space contour cell diagonal; zero for authored triangles.</param>
    /// <returns>Whether a retained triangle was hit.</returns>
    public bool Intersect(Vector3 origin, Vector3 direction, float maximum, out float distance, out Vector3 normal, out int material, out float bakeHalfDiagonal) =>
        Intersect(origin, direction, maximum, out distance, out normal, out material, out bakeHalfDiagonal, out _, out _);

    /// <summary>Finds the nearest hit, also returning its scene instance and original mesh triangle ordinal, or -1 for a miss.</summary>
    public bool Intersect(Vector3 origin, Vector3 direction, float maximum, out float distance, out Vector3 normal, out int material, out float bakeHalfDiagonal, out int instance, out int triangle) {
        var content = m_content;
        var inverse = Reciprocal(direction: direction);
        var nearest = new DrawLeaves(any: false, direction: direction, entries: content.Entries, instances: content.Instances, inverse: inverse, origin: origin, counters: Counters, toward: false);

        distance = maximum;
        bakeHalfDiagonal = 0;
        if ((content.NodeCount > 0) && Slab(low: content.Nodes[0].Low, high: content.Nodes[0].High, origin: origin, inverse: inverse, maximum: distance, entry: out _)) {
            Walk(distance: ref distance, inverse: inverse, leaves: ref nearest, nodes: new FloatNodes(nodes: content.Nodes), origin: origin, start: 0, counters: Counters);
        }
        instance = nearest.Instance;
        triangle = instance < 0 ? -1 : content.Instances[instance].Geometry.Triangles[nearest.Triangle];
        if (nearest.Instance < 0) {
            normal = default;
            material = 0;
            return false;
        }

        ref readonly var hit = ref content.Instances[nearest.Instance];

        normal = hit.NormalOf(triangle: nearest.Triangle);
        material = hit.MaterialOf(triangle: nearest.Triangle);
        bakeHalfDiagonal = hit.BakeHalfDiagonal;
        return true;
    }
    // A ray already outside a face of the scene and pointing away from that face cannot hit a caster.
    internal bool PointsOutside(Vector3 origin, Vector3 direction) {
        if (m_content.NodeCount == 0) { return true; }
        ref readonly var root = ref m_content.Nodes[0];
        for (var axis = 0; axis < 3; axis++) {
            if ((origin[axis] < root.Low[axis] && direction[axis] <= 0f) ||
                (origin[axis] > root.High[axis] && direction[axis] >= 0f)) { return true; }
        }
        return false;
    }

    /// <summary>Returns whether a ray reaches a distance without meeting a triangle.</summary>
    /// <param name="origin">The ray's origin, in world space.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="maximum">The distance it must reach, in metres.</param>
    /// <returns><see langword="true"/> when nothing blocks the ray.</returns>
    public bool Unblocked(Vector3 origin, Vector3 direction, float maximum) => Unblocked(direction: direction, maximum: maximum, origin: origin, toward: false);
    /// <summary>Returns whether a ray from a point reaches a point light: what blocks it is an open sheet it meets or a
    /// solid it leaves, a face of an enclosed solid that faces the light (<see cref="SdfMeshProbeTrace.SolidWord"/>). A
    /// solid that holds the light, its fixture, is entered and never left short of it, so it never hides the light,
    /// however deep in it the light sits; a solid between the point and the light is left on its far side and blocks it.
    /// The answer depends on the geometry and the light's position alone, never on its power.</summary>
    /// <param name="origin">The ray's origin, in world space, outside every solid.</param>
    /// <param name="direction">The ray's unit direction, toward the light.</param>
    /// <param name="maximum">The distance it must reach, in metres: the light's.</param>
    /// <returns><see langword="true"/> when nothing blocks the ray.</returns>
    public bool ReachesLight(Vector3 origin, Vector3 direction, float maximum) => Unblocked(direction: direction, maximum: maximum, origin: origin, toward: true);
    private bool Unblocked(Vector3 origin, Vector3 direction, float maximum, bool toward) {
        var content = m_content;
        var inverse = Reciprocal(direction: direction);
        var any = new DrawLeaves(any: true, direction: direction, entries: content.Entries, instances: content.Instances, inverse: inverse, origin: origin, counters: Counters, toward: toward);
        var distance = maximum;

        if ((content.NodeCount > 0) && Slab(low: content.Nodes[0].Low, high: content.Nodes[0].High, origin: origin, inverse: inverse, maximum: distance, entry: out _)) {
            Walk(distance: ref distance, inverse: inverse, leaves: ref any, nodes: new FloatNodes(nodes: content.Nodes), origin: origin, start: 0, counters: Counters);
        }
        return (any.Instance < 0);
    }

    // The nodes under start a ray's slab test admits, nearer child first, each leaf handed to the leaves; a leaf that
    // answers true ends the walk. A hit shortens distance, which prunes the nodes behind it. The caller has admitted
    // start: the top level's root by its own test, a braid subtree by the top-level leaf that names it.
    [SkipLocalsInit]
    private static void Walk<TNodes, TLeaves>(TNodes nodes, int start, Vector3 origin, Vector3 inverse, ref float distance, ref TLeaves leaves, WorkCounterSet counters) where TNodes : struct, INodes where TLeaves : struct, ILeaves {
        Span<int> stack = stackalloc int[StackDepth];
        var top = 0;

        stack[top++] = start;

        while (top > 0) {
            var node = stack[--top];
            var count = nodes.Count(node: node);

            if (count > 0) {
                if (leaves.Visit(first: nodes.First(node: node), count: count, distance: ref distance)) { return; }
                continue;
            }

            var left = nodes.First(node: node);
            var right = (left + 1);
            nodes.Box(high: out var leftHigh, low: out var leftLow, node: left);
            nodes.Box(high: out var rightHigh, low: out var rightLow, node: right);
            var hitLeft = Slab(low: leftLow, high: leftHigh, origin: origin, inverse: inverse, maximum: distance, entry: out var leftEntry);
            var hitRight = Slab(low: rightLow, high: rightHigh, origin: origin, inverse: inverse, maximum: distance, entry: out var rightEntry);

            if (top + (hitLeft ? 1 : 0) + (hitRight ? 1 : 0) > stack.Length) {
                counters.Count(SdfMeshIrradiance.TraversalFaults);
                throw new InvalidOperationException("mesh GI traversal exceeded its builder-certified stack depth.");
            }

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
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Reciprocal(Vector3 direction) => (Vector3.One / direction);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Slab(Vector3 low, Vector3 high, Vector3 origin, Vector3 inverse, float maximum, out float entry) {
        var t0 = ((low - origin) * inverse);
        var t1 = ((high - origin) * inverse);
        var near = Vector3.Min(value1: t0, value2: t1);
        var far = Vector3.Max(value1: t0, value2: t1);

        entry = MathF.Max(x: MathF.Max(x: near.X, y: near.Y), y: MathF.Max(x: near.Z, y: 0f));

        var exit = MathF.Min(x: MathF.Min(x: far.X, y: far.Y), y: MathF.Min(x: far.Z, y: maximum));

        return (entry <= exit);
    }

    // What a walk does at a leaf: the top level enters each draw's geometry, a geometry tests its triangles.
    private interface ILeaves {
        bool Visit(int first, int count, ref float distance);
    }
    // The nodes a walk reads: the top level's float nodes, or one braid of a geometry's packed nodes in its frame.
    private interface INodes {
        int Count(int node);
        int First(int node);
        void Box(int node, out Vector3 low, out Vector3 high);
    }
    private readonly struct FloatNodes(Node[] nodes) : INodes {
        public int Count(int node) => nodes[node].Count;
        public int First(int node) => nodes[node].First;
        public void Box(int node, out Vector3 low, out Vector3 high) {
            ref readonly var at = ref nodes[node];

            (low, high) = (at.Low, at.High);
        }
    }
    private readonly struct PackedNodes(PackedNode[] nodes, BraidFrame frame) : INodes {
        public int Count(int node) => nodes[node].Count;
        public int First(int node) => nodes[node].First;
        public void Box(int node, out Vector3 low, out Vector3 high) {
            ref readonly var at = ref nodes[node];

            (low, high) = (at.LowIn(frame: in frame), at.HighIn(frame: in frame));
        }
    }
    // The top level's leaves: a braid subtree of a draw's geometry each, walked by the ray in the geometry's frame. A ray
    // carried through an affine transform keeps its parameter, so distances compare across draws.
    // `toward`: the ray goes toward a point light, and a solid instance blocks it only where it leaves the solid.
    private struct DrawLeaves(Entry[] entries, Instance[] instances, Vector3 origin, Vector3 direction, Vector3 inverse, bool any, WorkCounterSet counters, bool toward) : ILeaves {
        public int Instance = -1;
        public int Triangle = -1;

        public bool Visit(int first, int count, ref float distance) {
            for (var index = first; (index < (first + count)); index++) {
                var entry = entries[index];
                ref readonly var instance = ref instances[entry.Instance];
                var local = (instance.Identity ? direction : Vector3.TransformNormal(normal: direction, matrix: instance.WorldToObject));
                var triangles = new TriangleLeaves(any: any, direction: local, geometry: instance.Geometry, origin: (instance.Identity ? origin : Vector3.Transform(position: origin, matrix: instance.WorldToObject)), leaving: (toward && instance.Solid));

                Walk(distance: ref distance, inverse: (instance.Identity ? inverse : Reciprocal(direction: local)), leaves: ref triangles, nodes: new PackedNodes(frame: instance.Geometry.Frames[entry.Braid], nodes: instance.Geometry.Nodes), origin: triangles.Origin, start: entry.Node, counters: counters);
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
    // faces it would otherwise leak through. `leaving` keeps only the faces the ray leaves through, those whose outward
    // normal (the winding's cross product in the mesh's frame) points along the ray. KEEP IN SYNC with sdfProbeWalk.
    private struct TriangleLeaves(Geometry geometry, Vector3 origin, Vector3 direction, bool any, bool leaving) : ILeaves {
        public readonly Vector3 Origin = origin;
        public readonly Vector3 Direction = direction;
        public int Hit = -1;

        public bool Visit(int first, int count, ref float distance) {
            var positions = geometry.Positions.Span;
            var indices = geometry.Indices.Span;
            var triangles = geometry.Triangles;

            for (var index = first; (index < (first + count)); index++) {
                Geometry.Corner(e1: out var e1, e2: out var e2, indices: indices, positions: positions, triangle: triangles[index], v0: out var v0);
                if (leaving && (Vector3.Dot(vector1: Vector3.Cross(vector1: e1, vector2: e2), vector2: Direction) <= 0f)) { continue; }
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

            if (MathF.Abs(x: determinant) < 1.0e-12f) { return false; }

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
    // A geometry's identity: the mesh whose positions and indices it traces (a mesh renumbered into another program's
    // materials names the mesh it was renumbered from, SdfMesh.GeometrySource, so its draws take the same geometry and
    // read their own triangle materials of that level) and the level it traces.
    internal readonly record struct GeometryKey(SdfMesh Source, int Level) {
        // The mesh's coarsest level whose measured error lies within `error` (SdfMeshLevel.Error, in the mesh's object
        // units; a level's vertices are the mesh's own, so it moves the surface by no more than that), else its own
        // triangles, level zero. Zero traces level zero always.
        public static GeometryKey Of(SdfMesh mesh, float error) {
            var levels = mesh.Levels;
            var source = (mesh.GeometrySource ?? mesh);

            for (var level = levels.Count; (level > 0); level--) {
                if (levels[(level - 1)].Error <= error) { return new(Level: level, Source: source); }
            }
            return new(Level: 0, Source: source);
        }

        // A mesh is one by reference: the record's value equality would compare its storage's views field by field.
        public bool Equals(GeometryKey other) => (ReferenceEquals(objA: Source, objB: other.Source) && (Level == other.Level));
        public override int GetHashCode() => HashCode.Combine(value1: RuntimeHelpers.GetHashCode(o: Source), value2: Level);
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
    // A top-level leaf: a draw, the root node of one of its geometry's braids, and that braid's ordinal (its frame).
    private readonly record struct Entry(int Instance, int Node, int Braid);
    // One draw: its geometry, its transform and the transform's inverse (world to the mesh's frame), the matrix that
    // carries a normal of the mesh's frame to world space with the winding the transform gives it, its program material
    // base and its mesh's triangle materials.
    private readonly struct Instance {
        public required SdfMesh Mesh { get; init; }
        public required Geometry Geometry { get; init; }
        public required Matrix4x4 ObjectToWorld { get; init; }
        public required Matrix4x4 WorldToObject { get; init; }
        public required Matrix4x4 NormalToWorld { get; init; }
        public required bool Identity { get; init; }
        // Whether the mesh encloses a solid, outward faces wound counter-clockwise through a transform that keeps the
        // winding (SdfMeshDraw.EnclosesSolid): a ray toward a light is blocked only where it leaves it (TriangleLeaves).
        public required bool Solid { get; init; }
        public required int Material { get; init; }
        public required ReadOnlyMemory<uint> TriangleMaterials { get; init; }
        // The contour's precision about a triangle: half the bake cell's diagonal, and the error of the level the geometry
        // traces in place of the mesh's own triangles, each carried out through the draw's transform.
        public float BakeHalfDiagonal { get; init; }

        // A draw whose transform has no inverse (a zero scale) draws nothing a ray can meet.
        public static bool TryCreate(in SdfMeshDraw draw, Geometry geometry, ReadOnlyMemory<uint> triangleMaterials, out Instance instance) {
            var matrix = draw.FixedPlacement;

            instance = default;
            if (!Matrix4x4.Invert(matrix: matrix, result: out var inverse)) { return false; }

            // Edges carried through the transform cross to det(M) times the normal carried by the inverse transpose.
            var normalToWorld = Matrix4x4.Transpose(matrix: inverse);

            if (matrix.GetDeterminant() < 0f) { normalToWorld *= -1f; }
            instance = new Instance {
                Mesh = draw.Mesh,
                Geometry = geometry,
                Identity = matrix.IsIdentity,
                Solid = draw.EnclosesSolid,
                Material = draw.Material,
                NormalToWorld = normalToWorld,
                ObjectToWorld = matrix,
                TriangleMaterials = triangleMaterials,
                WorldToObject = inverse,
                BakeHalfDiagonal = ((0.5f * Vector3.TransformNormal(draw.Mesh.BakeCell, matrix).Length()) +
                    (geometry.Error * MathF.Max(x: new Vector3(x: matrix.M11, y: matrix.M12, z: matrix.M13).Length(), y: MathF.Max(x: new Vector3(x: matrix.M21, y: matrix.M22, z: matrix.M23).Length(), y: new Vector3(x: matrix.M31, y: matrix.M32, z: matrix.M33).Length())))),
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
    internal sealed class Geometry(MemoryAccount.Owner storage) {
        // Every geometry a scene holds, by its mesh's buffers, across every residency in the process: a second scene of
        // a world (its meshes the same SdfMesh objects, which the mesh region store places once) takes the first's
        // geometry instead of building its hierarchy again. A geometry leaves at its last release.
        // Invariant: a claim is taken only on a geometry whose claims stand above zero (TryAcquireShared's exchange),
        // so a geometry whose last release has begun is never handed out, and its storage goes exactly once.
        private static readonly Dictionary<GeometryKey, Geometry> SharedGeometries = [];
        private static readonly Lock SharedGate = new();

        private int m_claims = 1;
        private GeometryKey? m_sharedAs;

        // Takes a claim on the geometry another scene built over the same mesh buffers; false when none stands.
        internal static bool TryAcquireShared(GeometryKey key, [NotNullWhen(true)] out Geometry? geometry) {
            lock (SharedGate) {
                if (SharedGeometries.TryGetValue(key: key, value: out geometry)) {
                    for (var claims = Volatile.Read(location: ref geometry.m_claims); (claims > 0); claims = Volatile.Read(location: ref geometry.m_claims)) {
                        if (Interlocked.CompareExchange(location1: ref geometry.m_claims, value: (claims + 1), comparand: claims) == claims) { return true; }
                    }
                }
            }
            geometry = null;
            return false;
        }
        // Offers a geometry a scene build just made to every later build over the same mesh buffers.
        internal static void Share(GeometryKey key, Geometry geometry) {
            lock (SharedGate) {
                geometry.m_sharedAs = key;
                SharedGeometries[key] = geometry;
            }
        }
        internal void Acquire() => Interlocked.Increment(ref m_claims);
        internal void Release() {
            if (Interlocked.Decrement(ref m_claims) != 0) { return; }
            if (m_sharedAs is { } key) {
                lock (SharedGate) {
                    if (SharedGeometries.TryGetValue(key: key, value: out var listed) && ReferenceEquals(objA: listed, objB: this)) { _ = SharedGeometries.Remove(key: key); }
                }
            }
            // A GPU placement can still use the geometry as an identity after its last scene reader leaves.
            Braids = [];
            Frames = [];
            Nodes = [];
            Triangles = [];
            lock (m_materialsGate) { m_materials.Clear(); }
            ReleaseStorage(storage);
            m_mesh.Release(cached: true);
        }

        public long HierarchyBytes => Nodes.LongLength * System.Runtime.CompilerServices.Unsafe.SizeOf<PackedNode>() +
            Frames.LongLength * System.Runtime.CompilerServices.Unsafe.SizeOf<BraidFrame>() + (Triangles.LongLength + Braids.LongLength) * sizeof(int);
        // The words the geometry's GPU block holds: its packed nodes, its triangle ordinals, then its braids' frames.
        public int GpuWords => checked(Nodes.Length * SdfMeshProbeTrace.PackedNodeWords + Triangles.Length + Frames.Length * SdfMeshProbeTrace.FrameWords);
        public int[] Braids { get; private set; } = [];
        // Each braid's frame, in the order of Braids; every node of a braid's subtree is packed in it.
        public BraidFrame[] Frames { get; private set; } = [];
        public int Count => (Indices.Length / 3);
        // The level of its mesh the geometry traces (zero for the mesh's own triangles), that level's first triangle in
        // the mesh's levelled numbering (the region lays each level's indices and materials after the finer levels',
        // SdfMeshRegion.WriteMesh), and the level's measured error in the mesh's object units.
        public int Level { get; init; }
        public int FirstTriangle { get; init; }
        public float Error { get; init; }
        // The traced level's indices: the mesh's own for level zero, else a copy in the geometry's storage, since a
        // published mesh lets its coarser levels' indices go once its words land (SdfMesh.Restore.cs).
        public required ReadOnlyMemory<uint> Indices { get; init; }
        // The mesh whose buffers the geometry reads. The geometry holds a claim on it from its making to its last release,
        // so every holder of a geometry claim, a build that borrowed it from another scene included (TryAcquireShared),
        // reads a mesh that stands: a second scene's draws may name another mesh of the same buffers (a renumbered
        // variant, SdfMesh.GeometrySource), whose claims never pin this one. A cached claim, so the draws' claims decide
        // whether the mesh counts as live.
        public required SdfMesh Mesh { get => m_mesh; init => m_mesh = value.Acquire(cached: true); }
        private readonly SdfMesh m_mesh = null!;
        public PackedNode[] Nodes { get; private set; } = [];
        public required ReadOnlyMemory<Vector3> Positions { get; init; }
        public int[] Triangles { get; private set; } = [];
        public int Depth { get; init; }
        public int MedianSplits { get; init; }

        // Each draw mesh's triangle materials of the traced level, copied into the geometry's storage the first time a scene
        // draws that mesh over it (its own mesh's at the build, a renumbered variant's at its first draw), so a scene's build
        // reads no mesh's planes for a geometry it reuses. A variant's entry holds the variant's object, never a claim.
        private readonly Dictionary<SdfMesh, ReadOnlyMemory<uint>> m_materials = new(comparer: ReferenceEqualityComparer.Instance);
        private readonly Lock m_materialsGate = new();

        public ReadOnlyMemory<uint> MaterialsOf(SdfMesh mesh) {
            lock (m_materialsGate) {
                if (m_materials.TryGetValue(key: mesh, value: out var held)) { return held; }
            }

            ReadOnlyMemory<uint> copied = default;

            using (var hold = mesh.Hold()) {
                var level = hold.LevelMaterials(level: Level).Span;

                if (!level.IsEmpty) {
                    var copy = storage.AllocateArray<uint>(level.Length);

                    level.CopyTo(destination: copy);
                    copied = copy;
                }
            }
            lock (m_materialsGate) {
                if (m_materials.TryGetValue(key: mesh, value: out var raced)) { return raced; }
                m_materials.Add(key: mesh, value: copied);
            }
            return copied;
        }

        // The leaf position's triangle as its first corner and its two edges from it, the terms the ray test reads.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Corner(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, int triangle, out Vector3 v0, out Vector3 e1, out Vector3 e2) {
            var first = (3 * triangle);

            v0 = positions[((int)indices[first])];
            e1 = (positions[((int)indices[(first + 1)])] - v0);
            e2 = (positions[((int)indices[(first + 2)])] - v0);
        }
        public void Corner(int index, out Vector3 v0, out Vector3 e1, out Vector3 e2) =>
            Corner(e1: out e1, e2: out e2, indices: Indices.Span, positions: Positions.Span, triangle: Triangles[index], v0: out v0);
        public static Geometry Build(GeometryKey key, SdfMesh mesh, MemoryAccount.Owner workspace) {
            var storage = MemoryAccount.Shared.CreateOwner(name: "sdf.mesh-gi.trace-scene", memoryClass: MemoryClass.Elastic);
            try { return Build(key, mesh, workspace, storage); }
            catch { ReleaseStorage(storage); throw; }
        }

        private static Geometry Build(GeometryKey key, SdfMesh mesh, MemoryAccount.Owner workspace, MemoryAccount.Owner storage) {
            var level = key.Level;
            var first = 0;

            for (var finer = 0; (finer < level); finer++) { first += ((finer == 0) ? mesh.TriangleCount : mesh.Levels[(finer - 1)].TriangleCount); }

            var error = ((level == 0) ? 0f : mesh.Levels[(level - 1)].Error);
            // The traced level's triangles over the vertices they reach, copied while a hold keeps the mesh's planes
            // resident: the geometry keeps none of the mesh's planes past its build, and a coarse level reaches a few of
            // its mesh's vertices.
            var (positionsCopy, tracedCopy, materialsCopy) = CompactCopy(level: level, mesh: mesh, storage: storage);
            ReadOnlyMemory<Vector3> compact = positionsCopy;
            ReadOnlyMemory<uint> traced = tracedCopy;
            var count = (traced.Length / 3);
            var chunks = count.CeilingDivide(divisor: ParallelPrimitives);
            var pieces = new Pieces[chunks];

            // Each chunk's pieces in triangle order, joined in chunk order, so a geometry builds alike on every run.
            Chunked(count: count, body: (start, end) => {
                var positions = compact.Span;
                var indices = traced.Span;
                var own = new Pieces(capacity: (end - start), workspace);

                for (var index = start; (index < end); index++) {
                    own.Add(a: positions[((int)indices[(3 * index)])], b: positions[((int)indices[((3 * index) + 1)])], c: positions[((int)indices[((3 * index) + 2)])], depth: 0, triangle: index);
                }
                pieces[(start / ParallelPrimitives)] = own;
            });

            var total = pieces.Sum(selector: static piece => piece.Count);
            // Build workspace only, rented: the hierarchy keeps none of these past its build.
            var lows = RentedArrays.Rent<Vector3>(minimumLength: total, owner: workspace);
            var highs = RentedArrays.Rent<Vector3>(minimumLength: total, owner: workspace);
            var owners = RentedArrays.Rent<int>(minimumLength: total, owner: workspace);

            try {
                var at = 0;

                foreach (var piece in pieces) {
                    piece.MoveTo(at: at, highs: highs, lows: lows, owners: owners);
                    at += piece.Count;
                }

                var (nodes, frames, order, braids, depth, medians) = Hierarchy.BuildPacked(workspace: workspace, storage: storage, braid: BraidTriangles, count: total, highs: highs, leaf: LeafTriangles, lows: lows, name: $"mesh GI geometry ({count} triangles)");

                try {
                    var triangles = storage.AllocateArray<int>(total);

                    for (var index = 0; (index < total); index++) {
                        triangles[index] = owners[order[index]];
                    }

                    var geometry = new Geometry(storage) { Braids = braids, Frames = frames, Indices = traced, Level = level, FirstTriangle = first, Error = error, Mesh = mesh, Nodes = nodes, Positions = compact, Triangles = triangles, Depth = depth, MedianSplits = medians };

                    geometry.m_materials.Add(key: mesh, value: materialsCopy);
                    return geometry;
                } finally {
                    RentedArrays.Return(array: order, owner: workspace);
                }
            } finally {
                RentedArrays.Return(array: lows, owner: workspace);
                RentedArrays.Return(array: highs, owner: workspace);
                RentedArrays.Return(array: owners, owner: workspace);
            }
        }

        // Copies a mesh level's triangles over the vertices they name, renumbered in first-use order, and its triangle
        // materials into the geometry's storage: none for a mesh whose store could not restore its planes.
        private static (Vector3[] Positions, uint[] Indices, uint[] Materials) CompactCopy(SdfMesh mesh, int level, MemoryAccount.Owner storage) {
            using var held = mesh.Hold();
            var indices = held.LevelIndices(level: level).Span;
            var positions = held.Positions.Span;
            var levelMaterials = held.LevelMaterials(level: level).Span;

            if (indices.IsEmpty) { return ([], [], []); }

            var materials = (levelMaterials.IsEmpty ? [] : storage.AllocateArray<uint>(levelMaterials.Length));

            levelMaterials.CopyTo(destination: materials);

            var compacted = storage.AllocateArray<uint>(indices.Length);

            using var remapLease = RentedArrays.Rent<int>(minimumLength: positions.Length, array: out var remap);
            using var sourcesLease = RentedArrays.Rent<int>(minimumLength: Math.Min(val1: indices.Length, val2: positions.Length), array: out var sources);
            var count = SdfMesh.CompactVertices(compacted: compacted, indices: indices, remap: remap.AsSpan(start: 0, length: positions.Length), sources: sources);
            var copy = storage.AllocateArray<Vector3>(count);

            for (var vertex = 0; (vertex < count); vertex++) { copy[vertex] = positions[sources[vertex]]; }
            return (copy, compacted, materials);
        }
        private static void Chunked(int count, Action<int, int> body) {
            if (count < ParallelPrimitives) {
                body(arg1: 0, arg2: count);
                return;
            }
            _ = Parallel.For(fromInclusive: 0, toExclusive: count.CeilingDivide(divisor: ParallelPrimitives), body: chunk => body(arg1: (chunk * ParallelPrimitives), arg2: Math.Min(val1: count, val2: ((chunk + 1) * ParallelPrimitives))));
        }

        // The boxes a run of triangles enters the hierarchy as, each with the triangle it bounds a piece of: a triangle
        // whose box's surface is more than LooseBox times its own area halves across its longest edge, down to MaxSplits
        // halvings, and a piece is boxed as its own triangle is. A flat right triangle along two axes, the tightest a box
        // holds, reads 4; a long thin triangle across a face reads its length over its width.
        // Its storage is rented, and goes back to the pool when MoveTo copies it out.
        private sealed class Pieces(int capacity, MemoryAccount.Owner workspace) {
            private const float LooseBox = 64f;
            private const int MaxSplits = 6;

            private Vector3[] m_lows = RentedArrays.Rent<Vector3>(minimumLength: capacity, owner: workspace);
            private Vector3[] m_highs = RentedArrays.Rent<Vector3>(minimumLength: capacity, owner: workspace);
            private int[] m_owners = RentedArrays.Rent<int>(minimumLength: capacity, owner: workspace);

            public int Count { get; private set; }
            private int Capacity => Math.Min(val1: m_owners.Length, val2: Math.Min(val1: m_lows.Length, val2: m_highs.Length));

            public void Add(Vector3 a, Vector3 b, Vector3 c, int triangle, int depth) {
                var low = Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c));
                var high = Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c));
                var area = (0.5f * Vector3.Cross(vector1: (b - a), vector2: (c - a)).Length());
                var size = (high - low);
                var surface = (2f * (((size.X * size.Y) + (size.Y * size.Z)) + (size.Z * size.X)));

                if ((depth >= MaxSplits) || !(area > 0f) || (surface <= (LooseBox * area))) {
                    if (Count == Capacity) { Grow(); }
                    m_lows[Count] = low;
                    m_highs[Count] = high;
                    m_owners[Count] = triangle;
                    Count++;
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
            public void MoveTo(Vector3[] lows, Vector3[] highs, int[] owners, int at) {
                m_lows.AsSpan(start: 0, length: Count).CopyTo(destination: lows.AsSpan(start: at));
                m_highs.AsSpan(start: 0, length: Count).CopyTo(destination: highs.AsSpan(start: at));
                m_owners.AsSpan(start: 0, length: Count).CopyTo(destination: owners.AsSpan(start: at));
                RentedArrays.Return(array: m_lows, owner: workspace);
                RentedArrays.Return(array: m_highs, owner: workspace);
                RentedArrays.Return(array: m_owners, owner: workspace);
                m_lows = m_highs = [];
                m_owners = [];
            }
            private void Grow() {
                var size = Math.Max(val1: 16, val2: (2 * Capacity));

                m_lows = RentedArrays.Grow(array: m_lows, keep: Count, minimumLength: size, owner: workspace);
                m_highs = RentedArrays.Grow(array: m_highs, keep: Count, minimumLength: size, owner: workspace);
                m_owners = RentedArrays.Grow(array: m_owners, keep: Count, minimumLength: size, owner: workspace);
            }
        }
    }
    // A binned surface-area hierarchy over primitive bounds, its wide ranges' children built in parallel. Its nodes
    // are written into chunks rented as the build reaches them, since the bound a range can need (2n - 1 nodes) is
    // several times what a build of leaves several primitives wide uses; the result is copied out at its size and the
    // chunks, like the centroids, go back to the pool, so a build leaves only what the scene keeps. The volume's change
    // trees (SdfMeshIrradiance.ChangeTree) build over the changed draws' boxes with it too.
    internal sealed class Hierarchy {
        private const int ChunkMask = ((1 << ChunkShift) - 1);
        private const int ChunkShift = 16;

        private readonly Vector3[] m_lows;
        private readonly Vector3[] m_highs;
        private readonly Vector3[] m_centroids;
        private readonly int[] m_order;
        private readonly Node[]?[] m_chunks;
        private readonly int m_capacity;
        private readonly int m_leaf;
        private readonly int m_braid;
        // Each braid's node and the first leaf position it holds: the position orders the braids, since a parallel build
        // numbers its nodes in whatever order its tasks reach them, and the top level that indexes the braids is built
        // over them in this order.
        private readonly List<(int First, int Node)> m_braids = [];

        private int m_nodeCount = 1;
        private int m_medianSplits;
        private string m_name = "";

        private readonly MemoryAccount.Owner? m_workspace;
        private readonly MemoryAccount.Owner? m_storage;

        private Hierarchy(MemoryAccount.Owner? workspace, MemoryAccount.Owner? storage, Vector3[] lows, Vector3[] highs, int count, int leaf, int braid) {
            m_workspace = workspace;
            m_storage = storage;
            m_lows = lows;
            m_highs = highs;
            m_leaf = leaf;
            m_braid = braid;
            m_centroids = RentedArrays.Rent<Vector3>(minimumLength: count, owner: workspace);
            m_order = RentedArrays.Rent<int>(minimumLength: count, owner: workspace);
            m_capacity = Math.Max(val1: 1, val2: ((2 * count) - 1));
            m_chunks = new Node[]?[(((m_capacity - 1) >> ChunkShift) + 1)];
            for (var index = 0; (index < count); index++) {
                m_centroids[index] = ((lows[index] + highs[index]) * 0.5f);
                m_order[index] = index;
            }
        }

        // The nodes, root first, and how many there are; the primitive each of the first count leaf positions holds, in an
        // array rented for the caller to return; and with a braid width the widest nodes at most that many primitives
        // wide, over the first count primitives of the bounds. The nodes are rented for the caller to return when rent
        // says so, and exactly as long as their count otherwise.
        public static (Node[] Nodes, int NodeCount, int[] Order, int[] Braids, int Depth, int MedianSplits) Build(Vector3[] lows, Vector3[] highs, int count, int leaf, int braid, string name, bool rent, MemoryAccount.Owner? workspace = null, MemoryAccount.Owner? storage = null) {
            Node[] nodes = [];
            var (nodeCount, order, braids, depth, medians) = Build(braid: braid, count: count, highs: highs, leaf: leaf, lows: lows, name: name, workspace: workspace, storage: storage, take: hierarchy => nodes = hierarchy.CopyNodes(rent: rent));

            return (nodes, nodeCount, order, braids, depth, medians);
        }
        // As Build, with each braid's subtree packed in that braid's frame straight from the build's chunks.
        public static (PackedNode[] Nodes, BraidFrame[] Frames, int[] Order, int[] Braids, int Depth, int MedianSplits) BuildPacked(MemoryAccount.Owner workspace, MemoryAccount.Owner storage, Vector3[] lows, Vector3[] highs, int count, int leaf, int braid, string name) {
            PackedNode[] nodes = [];
            BraidFrame[] frames = [];
            var (_, order, braids, depth, medians) = Build(braid: braid, count: count, highs: highs, leaf: leaf, lows: lows, name: name, workspace: workspace, storage: storage, take: hierarchy => (nodes, frames) = hierarchy.Pack());

            return (nodes, frames, order, braids, depth, medians);
        }
        private static (int NodeCount, int[] Order, int[] Braids, int Depth, int MedianSplits) Build(Vector3[] lows, Vector3[] highs, int count, int leaf, int braid, string name, MemoryAccount.Owner? workspace, MemoryAccount.Owner? storage, Action<Hierarchy> take) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: leaf, other: Node.MaxCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: count, other: Math.Min(val1: lows.Length, val2: highs.Length));

            var hierarchy = new Hierarchy(workspace: workspace, storage: storage, braid: braid, count: count, highs: highs, leaf: leaf, lows: lows) { m_name = name };
            var handed = false;

            try {
                var depth = hierarchy.BuildNode(count: count, first: 0, node: 0, wide: true, depth: 0);

                hierarchy.m_braids.Sort();
                take(hierarchy);
                handed = true;
                return (hierarchy.m_nodeCount, hierarchy.m_order, hierarchy.Braids, depth, hierarchy.m_medianSplits);
            } finally {
                hierarchy.ReturnWorkspace();
                if (!handed) { RentedArrays.Return(array: hierarchy.m_order, owner: workspace); }
            }
        }
        // The braids' root nodes, in the order of their first leaf positions.
        private int[] Braids {
            get {
                var braids = m_storage?.AllocateArray<int>(m_braids.Count) ?? new int[m_braids.Count];

                for (var index = 0; (index < braids.Length); index++) { braids[index] = m_braids[index].Node; }
                return braids;
            }
        }
        // Packs each braid's subtree in that braid's frame (its root's box); the nodes above the braids, which no walk
        // reads (the top level enters a geometry at its braids), are packed in the root's frame.
        private (PackedNode[] Nodes, BraidFrame[] Frames) Pack() {
            var packed = m_storage!.AllocateArray<PackedNode>(m_nodeCount);
            var frames = m_storage!.AllocateArray<BraidFrame>(m_braids.Count);
            Span<int> stack = stackalloc int[StackDepth];
            var whole = BraidFrame.Over(high: At(node: 0).High, low: At(node: 0).Low);

            for (var index = 0; (index < m_nodeCount); index++) { packed[index] = PackedNode.Of(frame: in whole, node: in At(node: index)); }
            for (var braid = 0; (braid < frames.Length); braid++) {
                var root = m_braids[braid].Node;
                var frame = frames[braid] = BraidFrame.Over(high: At(node: root).High, low: At(node: root).Low);
                var top = 0;

                stack[top++] = root;
                while (top > 0) {
                    var index = stack[--top];
                    ref readonly var node = ref At(node: index);

                    packed[index] = PackedNode.Of(frame: in frame, node: in node);
                    // Children follow their parent; an empty geometry's root is a leaf of no primitives.
                    if ((node.Count == 0) && (node.First > index)) {
                        stack[top++] = node.First;
                        stack[top++] = (node.First + 1);
                    }
                }
            }

            return (packed, frames);
        }

        // A node's storage, its chunk rented by the first build task to reach it.
        private ref Node At(int node) {
            var index = (node >> ChunkShift);
            var chunk = Volatile.Read(location: ref m_chunks[index]);

            if (chunk is null) {
                lock (m_chunks) {
                    chunk = m_chunks[index];
                    if (chunk is null) {
                        chunk = RentedArrays.Rent<Node>(minimumLength: Math.Min(val1: (1 << ChunkShift), val2: (m_capacity - (index << ChunkShift))), owner: m_workspace);
                        Volatile.Write(location: ref m_chunks[index], value: chunk);
                    }
                }
            }

            return ref chunk[node & ChunkMask];
        }
        // Every node below the count was written by the build before it ends, so a rented chunk's earlier contents are
        // never read.
        private Node[] CopyNodes(bool rent) {
            var nodes = (rent ? RentedArrays.Rent<Node>(minimumLength: m_nodeCount, owner: m_storage) : new Node[m_nodeCount]);

            for (var start = 0; (start < m_nodeCount); start += (1 << ChunkShift)) {
                m_chunks[(start >> ChunkShift)].AsSpan(length: Math.Min(val1: (1 << ChunkShift), val2: (m_nodeCount - start)), start: 0).CopyTo(destination: nodes.AsSpan(start: start));
            }

            return nodes;
        }
        private void ReturnWorkspace() {
            RentedArrays.Return(array: m_centroids, owner: m_workspace);
            for (var index = 0; (index < m_chunks.Length); index++) {
                if (m_chunks[index] is { } chunk) { RentedArrays.Return(array: chunk, owner: m_workspace); }
                m_chunks[index] = null;
            }
        }
        // Builds a node over a range of the order; wide says its parent, if any, is wider than the braid width.
        private int BuildNode(int node, int first, int count, bool wide, int depth) {
            var levelsLeft = StackDepth - 2 - depth;
            var leaves = ((long)count).CeilingDivide(divisor: m_leaf);
            var balanced = leaves <= 1 ? 0 : BitOperations.Log2((ulong)(leaves - 1)) + 1;
            if (levelsLeft < balanced) {
                throw new InvalidOperationException($"{m_name}: {count} primitives cannot fit within the mesh GI hierarchy depth bound {StackDepth - 2}.");
            }
            if (wide && (count <= m_braid)) {
                lock (m_braids) { m_braids.Add(item: (first, node)); }
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
                return depth;
            }

            // Once only a balanced tree fits, a surface-area split may no longer spend another level.
            var median = levelsLeft == balanced;
            int mid;
            if (median) {
                m_order.AsSpan(start: first, length: count).Sort(comparer: new CentroidOrder(Axis: LongestAxis(centroidHigh - centroidLow), Centroids: m_centroids));
                mid = first + count / 2;
                Interlocked.Increment(ref m_medianSplits);
            } else {
                mid = Split(centroidHigh: centroidHigh, centroidLow: centroidLow, count: count, first: first);
            }
            var child = (Interlocked.Add(location1: ref m_nodeCount, value: 2) - 2);

            built.Link(count: 0, first: child);
            var wider = (count > m_braid);

            if (count >= ParallelPrimitives) {
                return BuildChildrenInParallel(child: child, count: count, first: first, mid: mid, wide: wider, depth: depth + 1);
            }
            return Math.Max(BuildNode(count: (mid - first), first: first, node: child, wide: wider, depth: depth + 1),
                BuildNode(count: ((first + count) - mid), first: mid, node: (child + 1), wide: wider, depth: depth + 1));
        }
        // Apart from BuildNode, since the closure its two branches share is made on entry to the method holding them, and
        // nearly every node builds its children in line.
        private int BuildChildrenInParallel(int child, int first, int count, int mid, bool wide, int depth) {
            var left = 0;
            var right = 0;
            Parallel.Invoke(
                () => left = BuildNode(count: (mid - first), first: first, node: child, wide: wide, depth: depth),
                () => right = BuildNode(count: ((first + count) - mid), first: mid, node: (child + 1), wide: wide, depth: depth));
            return Math.Max(left, right);
        }
        // A binned surface-area split along the centroids' longest axis, which partitions the range in place; an even
        // split when the bins cannot separate them.
        private int Split(int first, int count, Vector3 centroidLow, Vector3 centroidHigh) {
            var extent = (centroidHigh - centroidLow);
            var axis = LongestAxis(extent);
            var axisLow = centroidLow[axis];
            var axisExtent = extent[axis];
            var mid = (first + (count / 2));

            if (axisExtent <= 0f) { return mid; }

            Span<int> binCounts = stackalloc int[Bins];
            Span<Vector3> binLows = stackalloc Vector3[Bins];
            Span<Vector3> binHighs = stackalloc Vector3[Bins];

            binLows.Fill(value: new Vector3(value: float.PositiveInfinity));
            binHighs.Fill(value: new Vector3(value: float.NegativeInfinity));
            for (var index = first; (index < (first + count)); index++) {
                var primitive = m_order[index];
                var bin = BinOf(value: m_centroids[primitive][axis], low: axisLow, extent: axisExtent);

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

                var cost = ((leftCount * Area(high: leftHigh, low: leftLow)) + (rightCount * Area(high: rightHigh, low: rightLow)));

                if (cost < bestCost) {
                    bestCost = cost;
                    bestSplit = split;
                }
            }
            if (bestSplit <= 0) { return mid; }

            var left = first;
            var right = ((first + count) - 1);

            while (left <= right) {
                if (BinOf(value: m_centroids[m_order[left]][axis], low: axisLow, extent: axisExtent) < bestSplit) {
                    left++;
                } else {
                    (m_order[left], m_order[right]) = (m_order[right], m_order[left]);
                    right--;
                }
            }

            return (((left > first) && (left < (first + count))) ? left : mid);
        }
        // Primitives by centroid along an axis, ties by index: a total order, so any sort leaves one arrangement. A value
        // type passed to the generic span sort, since a lambda here would put its closure on every BuildNode call: the
        // compiler merges a branch's closure scope into the method's.
        private readonly record struct CentroidOrder(Vector3[] Centroids, int Axis) : IComparer<int> {
            public int Compare(int x, int y) {
                var order = Centroids[x][Axis].CompareTo(value: Centroids[y][Axis]);

                return ((order != 0) ? order : x.CompareTo(value: y));
            }
        }
        private static int BinOf(float value, float low, float extent) =>
            Math.Clamp(max: (Bins - 1), min: 0, value: ((int)(((value - low) / extent) * Bins)));
        private static int LongestAxis(Vector3 extent) => extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        private static float Area(Vector3 low, Vector3 high) {
            var d = Vector3.Max(value1: (high - low), value2: Vector3.Zero);

            return (((d.X * d.Y) + (d.Y * d.Z)) + (d.Z * d.X));
        }
    }
}
