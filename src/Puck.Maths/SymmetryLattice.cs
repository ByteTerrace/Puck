namespace Puck.Maths;

/// <summary>
/// A fixed, maximally symmetric arrangement of 240 nodes in eight dimensions, with the reflections that permute them,
/// the order-30 cycle that <see cref="CyclicRotation"/> drives, and a projection into eight concentric rings of thirty.
/// Nodes are addressed by index in <c>[0, <see cref="NodeCount"/>)</c>, and the symmetry is exposed coordinate-free as
/// index maps: composing <see cref="Reflect(int, int)"/> reaches the whole symmetry group (order 696,729,600), while
/// <see cref="Cycle(int)"/> walks a node around its ring.
/// </summary>
/// <remarks>
/// This is the root system of the exceptional Lie algebra E₈: the nodes are its 240 roots, <see cref="Reflect(int, int)"/>
/// is a Weyl reflection, and <see cref="Cycle(int)"/> is the Coxeter element — the same rotation
/// <see cref="CyclicRotation"/> exposes as four planes. The cycle partitions the nodes into <see cref="RingCount"/> orbits
/// of <see cref="RingSize"/> — the eight rings of thirty that <see cref="Project(int)"/> lays out — and the eight ring
/// radii fall into four exact golden-ratio pairs, the 4D 600-cell (H₄) that lives inside E₈. Every accessor is a single
/// baked lookup with no allocation; the nodes, their coordinates, and the closure that builds them are computed once at
/// load (by reflection closure of eight seed nodes, in exact integer arithmetic) and then discarded — only the index
/// maps survive, so results are identical on every machine.
/// </remarks>
public static class SymmetryLattice {
    /// <summary>The dimension of the space the nodes live in.</summary>
    public const int Dimension = 8;
    /// <summary>The number of nodes.</summary>
    public const int NodeCount = 240;
    /// <summary>The number of cycle orbits (the concentric rings of the projection).</summary>
    public const int RingCount = 8;
    /// <summary>The size of every cycle orbit — equal to <see cref="CyclicRotation.Period"/>, the order of the cycle.</summary>
    public const int RingSize = 30;

    // The eight seed nodes (the E₈ simple roots), each scaled by two so its half-integer coordinates are integers in [-2, 2].
    private static readonly sbyte[][] SeedNodes = [
        [1, -1, -1, -1, -1, -1, -1, 1],
        [2, 2, 0, 0, 0, 0, 0, 0],
        [-2, 2, 0, 0, 0, 0, 0, 0],
        [0, -2, 2, 0, 0, 0, 0, 0],
        [0, 0, -2, 2, 0, 0, 0, 0],
        [0, 0, 0, -2, 2, 0, 0, 0],
        [0, 0, 0, 0, -2, 2, 0, 0],
        [0, 0, 0, 0, 0, -2, 2, 0],
    ];
    // The two orthonormal Coxeter-plane basis vectors (FixedQ4816 raw, Q16): the e^{2πi/30}-eigenspace of the cycle,
    // isolated once and baked. Projecting a node onto this plane sorts it into one of the eight rings.
    private static readonly long[] PlaneBasisX = [-6338L, -30797L, -16271L, -872L, 14726L, 29842L, 43815L, -3694L];
    private static readonly long[] PlaneBasisY = [-388L, 5935L, 11658L, 14236L, 13557L, 9649L, 2684L, 60307L];

    // The only state that survives construction: baked index maps, read by the accessors with no allocation. ReflectMap
    // is row-major [node · NodeCount + mirror]; node indices fit a ushort, halving its footprint.
    private static readonly ushort[] ReflectMap;
    private static readonly int[] CycleMap;
    private static readonly int[] RingMap;
    private static readonly FixedVector2[] Projection;

    static SymmetryLattice() {
        var nodes = new sbyte[NodeCount][];
        var index = new Dictionary<int, int>(capacity: NodeCount);
        var frontier = new Queue<int>();

        void Admit(ReadOnlySpan<sbyte> node) {
            var key = Pack(node: node);

            if (index.ContainsKey(key)) { return; }

            var stored = node.ToArray();

            nodes[index.Count] = stored;
            frontier.Enqueue(index.Count);
            index[key] = (index.Count);
        }

        // Reflection closure of the seed nodes, breadth-first from a fixed order, gives a deterministic node ordering.
        Span<sbyte> scratch = stackalloc sbyte[Dimension];

        foreach (var seed in SeedNodes) { Admit(node: seed); }
        while (frontier.Count > 0) {
            var node = nodes[frontier.Dequeue()];

            foreach (var mirror in SeedNodes) {
                ReflectRaw(node: node, mirror: mirror, result: scratch);
                Admit(node: scratch);
            }
        }

        // The cycle c = s₁s₂…s₈ (the Coxeter element), as a permutation of the nodes, and the orbits it cuts them into.
        CycleMap = new int[NodeCount];
        Span<sbyte> image = stackalloc sbyte[Dimension];
        for (var i = 0; (i < NodeCount); ++i) {
            nodes[i].CopyTo(image);

            for (var s = (SeedNodes.Length - 1); (s >= 0); --s) {
                ReflectRaw(node: image, mirror: SeedNodes[s], result: scratch);
                scratch.CopyTo(image);
            }

            CycleMap[i] = index[Pack(node: image)];
        }

        // Every Weyl reflection as an index map: ReflectMap[i · NodeCount + j] = index of node i reflected through node j.
        ReflectMap = new ushort[NodeCount * NodeCount];
        for (var i = 0; (i < NodeCount); ++i) {
            for (var j = 0; (j < NodeCount); ++j) {
                ReflectRaw(node: nodes[i], mirror: nodes[j], result: scratch);
                ReflectMap[((i * NodeCount) + j)] = ((ushort)index[Pack(node: scratch)]);
            }
        }

        RingMap = new int[NodeCount];
        Array.Fill(array: RingMap, value: -1);
        var nextRing = 0;
        for (var i = 0; (i < NodeCount); ++i) {
            if (RingMap[i] >= 0) { continue; }

            for (var cursor = i; (RingMap[cursor] < 0); cursor = CycleMap[cursor]) { RingMap[cursor] = nextRing; }

            ++nextRing;
        }

        // The Coxeter-plane projection: one FixedVector2 per node. The node is stored doubled, so halving folds into the
        // final shift — the summed products carry a factor of two that the closing divide-by-two removes.
        Projection = new FixedVector2[NodeCount];
        for (var i = 0; (i < NodeCount); ++i) {
            var node = nodes[i];
            var sumX = 0L;
            var sumY = 0L;

            for (var k = 0; (k < Dimension); ++k) {
                sumX += (PlaneBasisX[k] * node[k]);
                sumY += (PlaneBasisY[k] * node[k]);
            }

            Projection[i] = new FixedVector2(
                X: FixedQ4816.FromRawBits(value: (sumX / 2L)),
                Y: FixedQ4816.FromRawBits(value: (sumY / 2L))
            );
        }
    }

    /// <summary>Packs a doubled-coordinate node into a base-five key for the construction-time index map.</summary>
    /// <param name="node">The node's eight doubled coordinates, each in <c>[-2, 2]</c>.</param>
    /// <returns>A distinct non-negative key.</returns>
    private static int Pack(ReadOnlySpan<sbyte> node) {
        var key = 0;

        for (var i = 0; (i < Dimension); ++i) { key = ((key * 5) + (node[i] + 2)); }

        return key;
    }
    /// <summary>Reflects a doubled-coordinate vector through the hyperplane of a doubled-coordinate node, into a caller buffer.</summary>
    /// <param name="node">The doubled-coordinate vector to reflect.</param>
    /// <param name="mirror">The doubled-coordinate node whose hyperplane reflects; norm-squared eight in these units.</param>
    /// <param name="result">The destination for the reflection, still in doubled coordinates; length <see cref="Dimension"/>.</param>
    private static void ReflectRaw(ReadOnlySpan<sbyte> node, ReadOnlySpan<sbyte> mirror, Span<sbyte> result) {
        var projection = 0;

        for (var i = 0; (i < Dimension); ++i) { projection += (node[i] * mirror[i]); }

        // projection/4 is the integer coefficient ⟨node, mirror⟩ (E₈ is an even integral lattice), so this is exact.
        var coefficient = (projection / 4);

        for (var i = 0; (i < Dimension); ++i) { result[i] = ((sbyte)(node[i] - (coefficient * mirror[i]))); }
    }

    /// <summary>Reflects one node through another's hyperplane: a Weyl reflection, the generators of the symmetry group.</summary>
    /// <param name="node">The node to reflect, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <param name="mirror">The node whose hyperplane reflects, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The index of the reflected node; composing reflections realizes the whole symmetry group W(E₈).</returns>
    public static int Reflect(int node, int mirror) =>
        ReflectMap[((node * NodeCount) + mirror)];
    /// <summary>Advances a node one step around its ring — the order-30 cycle whose planes are <see cref="CyclicRotation"/>.</summary>
    /// <param name="node">The node to advance, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The index of the next node in the same ring; returning to the start after <see cref="RingSize"/> steps.</returns>
    public static int Cycle(int node) =>
        CycleMap[node];
    /// <summary>Returns which ring a node belongs to: the orbit it occupies under repeated <see cref="Cycle(int)"/>.</summary>
    /// <param name="node">The node, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The ring in <c>[0, <see cref="RingCount"/>)</c>.</returns>
    public static int Ring(int node) =>
        RingMap[node];
    /// <summary>Projects a node onto the plane where the 240 nodes resolve into eight concentric rings of thirty.</summary>
    /// <param name="node">The node, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The projected point; its ring is <see cref="Ring(int)"/> and one <see cref="Cycle(int)"/> step turns it 12°.</returns>
    public static FixedVector2 Project(int node) =>
        Projection[node];
}
