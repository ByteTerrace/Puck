using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.SdfVm;

/// <summary>
/// The static triangles the mesh path's irradiance probes trace (<see cref="SdfMeshIrradiance"/>): every static mesh draw of
/// a frame in world space, each triangle with its program material, under a bounding volume hierarchy built on the thread
/// pool. It answers the nearest hit along a ray and whether a ray reaches a distance unblocked. Presentation only: the
/// tracing is single-precision and nothing it answers reaches simulation state.
/// </summary>
public sealed class SdfMeshIrradianceScene {
    // The most triangles a leaf holds, and the bins the surface-area split tries along an axis.
    private const int LeafTriangles = 4;
    private const int Bins = 12;

    private readonly Vector3[] m_v0;
    private readonly Vector3[] m_e1;
    private readonly Vector3[] m_e2;
    private readonly Vector3[] m_normals;
    private readonly int[] m_materials;
    private readonly Node[] m_nodes;
    private readonly int m_nodeCount;

    /// <summary>Gets the triangles the scene holds.</summary>
    public int TriangleCount => m_v0.Length;
    /// <summary>Gets the scene's bounds.</summary>
    public Vector3 Low { get; }
    /// <summary>Gets the scene's bounds.</summary>
    public Vector3 High { get; }

    private struct Node {
        public Vector3 Low;
        public Vector3 High;
        // A leaf's first triangle, or an inner node's first child (its second follows it).
        public int First;
        // A leaf's triangle count; zero for an inner node.
        public int Count;
    }

    /// <summary>Builds the scene of a frame's static mesh draws: every draw that is not dynamic.</summary>
    /// <param name="draws">The frame's mesh draws.</param>
    /// <returns>The scene, or <see langword="null"/> when no static draw holds a triangle.</returns>
    public static SdfMeshIrradianceScene? Build(IReadOnlyList<SdfMeshDraw> draws) {
        ArgumentNullException.ThrowIfNull(argument: draws);

        var count = 0;

        foreach (var draw in draws) {
            if (IsStatic(draw: draw)) { count += draw.Mesh.TriangleCount; }
        }
        if (count == 0) { return null; }

        var v0 = new Vector3[count];
        var e1 = new Vector3[count];
        var e2 = new Vector3[count];
        var normals = new Vector3[count];
        var materials = new int[count];
        var triangle = 0;

        foreach (var draw in draws) {
            if (!IsStatic(draw: draw)) { continue; }

            var positions = draw.Mesh.Positions.Span;
            var indices = draw.Mesh.Indices.Span;
            var triangleMaterials = draw.Mesh.TriangleMaterials.Span;
            var matrix = draw.ObjectToWorld;

            for (var index = 0; (index < draw.Mesh.TriangleCount); index++) {
                var a = Vector3.Transform(position: positions[(int)indices[(3 * index)]], matrix: matrix);
                var b = Vector3.Transform(position: positions[(int)indices[((3 * index) + 1)]], matrix: matrix);
                var c = Vector3.Transform(position: positions[(int)indices[((3 * index) + 2)]], matrix: matrix);
                var normal = Vector3.Cross(vector1: (b - a), vector2: (c - a));
                var length = normal.Length();

                v0[triangle] = a;
                e1[triangle] = (b - a);
                e2[triangle] = (c - a);
                normals[triangle] = ((length > 0f) ? (normal / length) : Vector3.UnitY);
                materials[triangle] = (draw.Material + (triangleMaterials.IsEmpty ? 0 : (int)triangleMaterials[index]));
                triangle++;
            }
        }

        return new SdfMeshIrradianceScene(v0: v0, e1: e1, e2: e2, normals: normals, materials: materials);
    }
    /// <summary>Returns whether a draw belongs to the static scene: one neither dynamic nor glass. Glass neither blocks
    /// nor bounces the probes' rays: light reaches what lies behind it untinted.</summary>
    /// <param name="draw">The draw.</param>
    /// <returns><see langword="true"/> for a static, opaque mesh draw.</returns>
    public static bool IsStatic(in SdfMeshDraw draw) => (!draw.IsDynamic && !draw.Glass);

    private SdfMeshIrradianceScene(Vector3[] v0, Vector3[] e1, Vector3[] e2, Vector3[] normals, int[] materials) {
        var count = v0.Length;
        var centroids = new Vector3[count];
        var lows = new Vector3[count];
        var highs = new Vector3[count];
        var order = new int[count];

        for (var index = 0; (index < count); index++) {
            var a = v0[index];
            var b = (a + e1[index]);
            var c = (a + e2[index]);

            lows[index] = Vector3.Min(value1: a, value2: Vector3.Min(value1: b, value2: c));
            highs[index] = Vector3.Max(value1: a, value2: Vector3.Max(value1: b, value2: c));
            centroids[index] = ((lows[index] + highs[index]) * 0.5f);
            order[index] = index;
        }

        var nodes = new Node[Math.Max(val1: 1, val2: ((2 * count) - 1))];
        var nodeCount = 1;

        BuildNode(node: 0, first: 0, count: count, order: order, lows: lows, highs: highs, centroids: centroids, nodes: nodes, nodeCount: ref nodeCount);

        // Reorder the triangles into leaf order so a leaf reads its triangles contiguously.
        m_v0 = new Vector3[count];
        m_e1 = new Vector3[count];
        m_e2 = new Vector3[count];
        m_normals = new Vector3[count];
        m_materials = new int[count];
        for (var index = 0; (index < count); index++) {
            var source = order[index];

            m_v0[index] = v0[source];
            m_e1[index] = e1[source];
            m_e2[index] = e2[source];
            m_normals[index] = normals[source];
            m_materials[index] = materials[source];
        }
        m_nodes = nodes;
        m_nodeCount = nodeCount;
        Low = nodes[0].Low;
        High = nodes[0].High;
    }

    private static void BuildNode(int node, int first, int count, int[] order, Vector3[] lows, Vector3[] highs, Vector3[] centroids, Node[] nodes, ref int nodeCount) {
        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);
        var centroidLow = low;
        var centroidHigh = high;

        for (var index = first; (index < (first + count)); index++) {
            var triangle = order[index];

            low = Vector3.Min(value1: low, value2: lows[triangle]);
            high = Vector3.Max(value1: high, value2: highs[triangle]);
            centroidLow = Vector3.Min(value1: centroidLow, value2: centroids[triangle]);
            centroidHigh = Vector3.Max(value1: centroidHigh, value2: centroids[triangle]);
        }

        nodes[node].Low = low;
        nodes[node].High = high;

        if (count <= LeafTriangles) {
            nodes[node].First = first;
            nodes[node].Count = count;
            return;
        }

        // A binned surface-area split along the centroids' longest axis; an even split when the bins cannot separate them.
        var extent = (centroidHigh - centroidLow);
        var axis = ((extent.X >= extent.Y) && (extent.X >= extent.Z)) ? 0 : ((extent.Y >= extent.Z) ? 1 : 2);
        var axisLow = Component(value: centroidLow, axis: axis);
        var axisExtent = Component(value: extent, axis: axis);
        var mid = (first + (count / 2));

        if (axisExtent > 0f) {
            Span<int> binCounts = stackalloc int[Bins];
            Span<Vector3> binLows = stackalloc Vector3[Bins];
            Span<Vector3> binHighs = stackalloc Vector3[Bins];

            binLows.Fill(value: new Vector3(value: float.PositiveInfinity));
            binHighs.Fill(value: new Vector3(value: float.NegativeInfinity));
            for (var index = first; (index < (first + count)); index++) {
                var triangle = order[index];
                var bin = BinOf(value: Component(value: centroids[triangle], axis: axis), low: axisLow, extent: axisExtent);

                binCounts[bin]++;
                binLows[bin] = Vector3.Min(value1: binLows[bin], value2: lows[triangle]);
                binHighs[bin] = Vector3.Max(value1: binHighs[bin], value2: highs[triangle]);
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

            if (bestSplit > 0) {
                var left = first;
                var right = ((first + count) - 1);

                while (left <= right) {
                    if (BinOf(value: Component(value: centroids[order[left]], axis: axis), low: axisLow, extent: axisExtent) < bestSplit) {
                        left++;
                    } else {
                        (order[left], order[right]) = (order[right], order[left]);
                        right--;
                    }
                }
                if ((left > first) && (left < (first + count))) { mid = left; }
            }
        }

        var child = nodeCount;

        nodeCount += 2;
        nodes[node].First = child;
        nodes[node].Count = 0;
        BuildNode(node: child, first: first, count: (mid - first), order: order, lows: lows, highs: highs, centroids: centroids, nodes: nodes, nodeCount: ref nodeCount);
        BuildNode(node: (child + 1), first: mid, count: ((first + count) - mid), order: order, lows: lows, highs: highs, centroids: centroids, nodes: nodes, nodeCount: ref nodeCount);
    }
    private static int BinOf(float value, float low, float extent) =>
        Math.Clamp(value: (int)(((value - low) / extent) * Bins), min: 0, max: (Bins - 1));
    private static float Area(Vector3 low, Vector3 high) {
        var d = Vector3.Max(value1: (high - low), value2: Vector3.Zero);

        return ((d.X * d.Y) + (d.Y * d.Z) + (d.Z * d.X));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Component(Vector3 value, int axis) => ((axis == 0) ? value.X : ((axis == 1) ? value.Y : value.Z));

    /// <summary>Finds the nearest triangle a ray meets within a distance.</summary>
    /// <param name="origin">The ray's origin.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="maximum">The farthest distance to search.</param>
    /// <param name="distance">The hit's distance along the ray.</param>
    /// <param name="triangle">The hit triangle, for <see cref="NormalOf"/> and <see cref="MaterialOf"/>.</param>
    /// <returns><see langword="true"/> when the ray meets a triangle.</returns>
    public bool Intersect(Vector3 origin, Vector3 direction, float maximum, out float distance, out int triangle) {
        distance = maximum;
        triangle = -1;
        Traverse(origin: origin, direction: direction, any: false, distance: ref distance, triangle: ref triangle);
        return (triangle >= 0);
    }
    /// <summary>Returns whether a ray reaches a distance without meeting a triangle.</summary>
    /// <param name="origin">The ray's origin.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="maximum">The distance it must reach.</param>
    /// <returns><see langword="true"/> when nothing blocks the ray.</returns>
    public bool Unblocked(Vector3 origin, Vector3 direction, float maximum) {
        var distance = maximum;
        var triangle = -1;

        Traverse(origin: origin, direction: direction, any: true, distance: ref distance, triangle: ref triangle);
        return (triangle < 0);
    }
    /// <summary>Gets a triangle's geometric unit normal, wound as its mesh winds it.</summary>
    /// <param name="triangle">The triangle.</param>
    /// <returns>The normal.</returns>
    public Vector3 NormalOf(int triangle) => m_normals[triangle];
    /// <summary>Gets a triangle's program material.</summary>
    /// <param name="triangle">The triangle.</param>
    /// <returns>The material index.</returns>
    public int MaterialOf(int triangle) => m_materials[triangle];

    private void Traverse(Vector3 origin, Vector3 direction, bool any, ref float distance, ref int triangle) {
        var inverse = new Vector3(x: (1f / direction.X), y: (1f / direction.Y), z: (1f / direction.Z));
        Span<int> stack = stackalloc int[64];
        var top = 0;

        if (!Slab(low: m_nodes[0].Low, high: m_nodes[0].High, origin: origin, inverse: inverse, maximum: distance, entry: out _)) { return; }
        stack[top++] = 0;

        while (top > 0) {
            ref readonly var node = ref m_nodes[stack[--top]];

            if (node.Count > 0) {
                for (var index = node.First; (index < (node.First + node.Count)); index++) {
                    if (HitTriangle(index: index, origin: origin, direction: direction, maximum: distance, distance: out var t)) {
                        distance = t;
                        triangle = index;
                        if (any) { return; }
                    }
                }
                continue;
            }

            var left = node.First;
            var right = (left + 1);
            var hitLeft = Slab(low: m_nodes[left].Low, high: m_nodes[left].High, origin: origin, inverse: inverse, maximum: distance, entry: out var leftEntry);
            var hitRight = Slab(low: m_nodes[right].Low, high: m_nodes[right].High, origin: origin, inverse: inverse, maximum: distance, entry: out var rightEntry);

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
    private static bool Slab(Vector3 low, Vector3 high, Vector3 origin, Vector3 inverse, float maximum, out float entry) {
        var t0 = ((low - origin) * inverse);
        var t1 = ((high - origin) * inverse);
        var near = Vector3.Min(value1: t0, value2: t1);
        var far = Vector3.Max(value1: t0, value2: t1);

        entry = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));

        var exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, maximum));

        return (entry <= exit);
    }
    // Möller-Trumbore, two-sided: a probe inside geometry must see the back faces it would otherwise leak through.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HitTriangle(int index, Vector3 origin, Vector3 direction, float maximum, out float distance) {
        distance = 0f;

        var e1 = m_e1[index];
        var e2 = m_e2[index];
        var p = Vector3.Cross(vector1: direction, vector2: e2);
        var determinant = Vector3.Dot(vector1: e1, vector2: p);

        if (MathF.Abs(determinant) < 1.0e-12f) { return false; }

        var inverse = (1f / determinant);
        var s = (origin - m_v0[index]);
        var u = (Vector3.Dot(vector1: s, vector2: p) * inverse);

        if ((u < 0f) || (u > 1f)) { return false; }

        var q = Vector3.Cross(vector1: s, vector2: e1);
        var v = (Vector3.Dot(vector1: direction, vector2: q) * inverse);

        if ((v < 0f) || ((u + v) > 1f)) { return false; }

        distance = (Vector3.Dot(vector1: e2, vector2: q) * inverse);
        return ((distance > 1.0e-4f) && (distance < maximum));
    }
}
