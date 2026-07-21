namespace Puck.Maths.Research;

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
/// radii fall into four golden-ratio pairs, the 4D 600-cell (H₄) that lives inside E₈. Every accessor is an allocation-free,
/// bounds-checked baked lookup; the nodes, their coordinates, and the closure that builds them are computed once at
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
    /// <summary>The number of antipodal ray pairs.</summary>
    public const int RayCount = (NodeCount / 2);
    /// <summary>The order induced by <see cref="Cycle(int)"/> after antipodal nodes are identified as rays.</summary>
    public const int RayCycleOrder = (RingSize / 2);

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
    // Q16 approximations to two orthonormal Coxeter-plane basis vectors: the e^{2πi/30}-eigenspace of the cycle,
    // isolated once and baked. Projecting a node onto this plane sorts it into one of the eight rings.
    private static readonly long[] PlaneBasisX = [-6338L, -30797L, -16271L, -872L, 14726L, 29842L, 43815L, -3694L];
    private static readonly long[] PlaneBasisY = [-388L, 5935L, 11658L, 14236L, 13557L, 9649L, 2684L, 60307L];

    // The only state that survives construction: baked index maps, read by the accessors with no allocation. ReflectMap
    // is row-major [node · NodeCount + mirror]; node indices fit a ushort, halving its footprint.
    private static readonly ushort[] ReflectMap;
    private static readonly int[] CycleMap;
    private static readonly int[] RingMap;
    private static readonly FixedVector2[] Projection;
    private static readonly BinaryPolynomial[] RayCycleFactorStorage = BinaryPolynomial.FactorOddCycle(cycleOrder: RayCycleOrder);

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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node"/> or <paramref name="mirror"/> is outside the node range.</exception>
    public static int Reflect(int node, int mirror) {
        ValidateNode(node: node, paramName: nameof(node));
        ValidateNode(node: mirror, paramName: nameof(mirror));

        return ReflectMap[((node * NodeCount) + mirror)];
    }
    /// <summary>Returns the antipodal node, which represents the same unoriented ray.</summary>
    /// <param name="node">The node in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The node opposite <paramref name="node"/>.</returns>
    public static int Antipode(int node) {
        ValidateNode(node: node, paramName: nameof(node));

        return ReflectMap[((node * NodeCount) + node)];
    }
    /// <summary>Returns the smaller node index in an antipodal ray pair, providing a stable ray key.</summary>
    /// <param name="node">Either node representing the ray.</param>
    /// <returns>The canonical node index of the unoriented ray.</returns>
    public static int CanonicalRay(int node) {
        var antipode = Antipode(node: node);

        return Math.Min(node, antipode);
    }
    /// <summary>Tests exact E₈ orthogonality through the reflection action.</summary>
    /// <param name="first">The first node.</param>
    /// <param name="second">The second node.</param>
    /// <returns><see langword="true"/> exactly when the roots, and therefore their rays, are orthogonal.</returns>
    public static bool AreOrthogonal(int first, int second) {
        ValidateNode(node: first, paramName: nameof(first));
        ValidateNode(node: second, paramName: nameof(second));

        return (ReflectMap[((first * NodeCount) + second)] == first);
    }
    /// <summary>
    /// Gets the verified factors of <c>t^15 + 1</c> governing the order-15 action induced on antipodal E₈ rays.
    /// </summary>
    public static ReadOnlyMemory<BinaryPolynomial> RayCycleFactors => RayCycleFactorStorage;
    /// <summary>Advances a node one step around its ring — the order-30 cycle whose planes are <see cref="CyclicRotation"/>.</summary>
    /// <param name="node">The node to advance, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The index of the next node in the same ring; returning to the start after <see cref="RingSize"/> steps.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node"/> is outside the node range.</exception>
    public static int Cycle(int node) {
        ValidateNode(node: node, paramName: nameof(node));

        return CycleMap[node];
    }
    /// <summary>Returns which ring a node belongs to: the orbit it occupies under repeated <see cref="Cycle(int)"/>.</summary>
    /// <param name="node">The node, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The ring in <c>[0, <see cref="RingCount"/>)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node"/> is outside the node range.</exception>
    public static int Ring(int node) {
        ValidateNode(node: node, paramName: nameof(node));

        return RingMap[node];
    }
    /// <summary>Projects a node onto the plane where the 240 nodes resolve into eight concentric rings of thirty.</summary>
    /// <param name="node">The node, in <c>[0, <see cref="NodeCount"/>)</c>.</param>
    /// <returns>The projected point; its ring is <see cref="Ring(int)"/> and one <see cref="Cycle(int)"/> step turns it 12°.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node"/> is outside the node range.</exception>
    public static FixedVector2 Project(int node) {
        ValidateNode(node: node, paramName: nameof(node));

        return Projection[node];
    }

    /// <summary>Rejects invalid public node indices before they can wrap a flattened lookup-table offset.</summary>
    private static void ValidateNode(int node, string paramName) {
        if ((uint)node >= ((uint)NodeCount)) {
            throw new ArgumentOutOfRangeException(
                paramName: paramName,
                actualValue: node,
                message: $"the node index must be in [0, {NodeCount})"
            );
        }
    }
}
