using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>
/// A finite reflection system read entirely off <see cref="SymmetryLattice"/>'s own action: a list of mirror nodes, the
/// point set their reflections close on, the bond orders they satisfy, and the bounded enumeration of the group they
/// generate.
/// </summary>
/// <remarks>
/// <para>
/// It contributes no arithmetic and no product code. Everything here is a lookup or a bounded closure over
/// <see cref="SymmetryLattice.Reflect(int, int)"/>, and its two outputs — <see cref="BondMatrix"/> and
/// <see cref="TryEnumerateGroup"/> — are exactly the argument tuples <c>Presentations.Coxeter</c> and
/// <c>Presentations.PermutationGroup</c> take. A reflection world therefore enters the presented algebra as measured
/// data rather than as a hand-written table, which is what makes the presentation's relations a statement about the
/// lattice rather than a claim beside it.
/// </para>
/// <para>
/// <b>Words act on the right.</b> <see cref="Apply"/> applies a word's letters left to right, so a word is the product
/// in the presentation's own order: <c>node · (u·v)</c> is <c>(node · u) · v</c>. The word that reads every mirror once
/// in descending order is the lattice's own <see cref="SymmetryLattice.Cycle(int)"/>.
/// </para>
/// <para>
/// <b>The group enumeration is a limit and never a promise.</b> The whole lattice symmetry has order 696,729,600, which
/// no enumeration of any budget reaches, so <see cref="TryEnumerateGroup"/> takes a search limit and refuses with
/// <see cref="ClosureOutcome.SearchLimitReached"/> rather than running out of memory or time. The limit bounds the
/// memory as well as the work, because one element is one permutation of the point set. What does not shrink is the
/// attempt: every proper sub-system of the eight simple mirrors that fits a budget is enumerated exactly.
/// </para>
/// </remarks>
public sealed class ReflectionSystem {
    // The rank cap: a bond matrix is the rank squared and every mirror is a generator of whatever presentation consumes
    // it, so this is well under the presentation surface's own generator caps.
    private const int MaximumMirrorCount = 32;

    // The enumeration cap. One element is one permutation of the point set, so a search limit is a memory bound as much
    // as a work bound; this keeps the widest one under sixteen million entries and the flat index inside an Int32.
    private const long MaximumSearchLimit = (1L << 16);

    private readonly int[] m_bonds;
    private readonly int[] m_mirrors;
    private readonly int[] m_pointImage;
    private readonly int[] m_points;

    private ReflectionSystem(int[] mirrors, int[] points, int[] pointImage, int[] bonds) {
        m_bonds = bonds;
        m_mirrors = mirrors;
        m_pointImage = pointImage;
        m_points = points;
    }

    /// <summary>Gets the row-major bond matrix: entry <c>(i, j)</c> is the order of the composite of the two mirrors'
    /// reflections, which is one on the diagonal and at least two off it.</summary>
    /// <remarks>It is the Coxeter matrix of the system, computed from the action rather than declared, and it is the
    /// argument <c>Presentations.Coxeter</c> takes.</remarks>
    public ReadOnlySpan<int> BondMatrix => m_bonds;
    /// <summary>Gets the mirror nodes, in the order they were given; a word's letters index this list.</summary>
    public ReadOnlySpan<int> Mirrors => m_mirrors;
    /// <summary>Gets the nodes the mirrors close on — the sub-root system — ascending by node index.</summary>
    /// <remarks>The group acts faithfully on this set, because a reflection system's own roots span the space it
    /// reflects in, so it is the smallest point set a permutation enumeration can use.</remarks>
    public ReadOnlySpan<int> Points => m_points;
    /// <summary>Gets the lattice's own eight seed mirrors — its first eight node indices — which are a simple system:
    /// their bonds form a tree on eight mirrors and their reflections reach every one of the 240 nodes.</summary>
    /// <remarks>Both facts are measured rather than assumed; nothing here reads a coordinate.</remarks>
    public static ReadOnlySpan<int> SimpleMirrors => [0, 1, 2, 3, 4, 5, 6, 7];

    /// <summary>Builds the reflection system of a list of mirror nodes.</summary>
    /// <param name="mirrors">The mirror nodes, each a distinct reflection of the lattice.</param>
    /// <returns>The described system.</returns>
    /// <exception cref="ArgumentException">Two mirrors name the same reflection, which they do when they are equal or
    /// antipodal, so the pair would carry a bond of one and present no relation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There are no mirrors or more than thirty-two, or a mirror is
    /// outside the lattice's node range.</exception>
    public static ReflectionSystem Create(ReadOnlySpan<int> mirrors) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: mirrors.Length, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: mirrors.Length, other: MaximumMirrorCount);

        var count = mirrors.Length;
        var chosen = mirrors.ToArray();

        for (var index = 0; (index < count); ++index) {
            if ((chosen[index] < 0) || (chosen[index] >= SymmetryLattice.NodeCount)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(mirrors), actualValue: chosen[index], message: $"A mirror names a lattice node, so it lies in [0, {SymmetryLattice.NodeCount}).");
            }
        }

        var bonds = new int[(count * count)];

        for (var first = 0; (first < count); ++first) {
            for (var second = 0; (second < count); ++second) {
                var bond = CompositeOrder(first: chosen[first], second: chosen[second]);

                if ((first != second) && (1 == bond)) {
                    throw new ArgumentException(message: "Two mirrors name the same reflection, so they present no relation and cannot both be generators.", paramName: nameof(mirrors));
                }

                bonds[((first * count) + second)] = bond;
            }
        }

        Span<bool> seen = stackalloc bool[SymmetryLattice.NodeCount];
        Span<int> frontier = stackalloc int[SymmetryLattice.NodeCount];

        _ = CloseUnderMirrors(mirrors: chosen, seeds: chosen, seen: seen, frontier: frontier);

        var points = new List<int>();

        for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
            if (seen[node]) { points.Add(item: node); }
        }

        var pointImage = new int[(points.Count * count)];

        for (var point = 0; (point < points.Count); ++point) {
            for (var index = 0; (index < count); ++index) {
                pointImage[((point * count) + index)] = points.BinarySearch(item: SymmetryLattice.Reflect(node: points[point], mirror: chosen[index]));
            }
        }

        return new(mirrors: chosen, points: [.. points], pointImage: pointImage, bonds: bonds);
    }

    // The one lattice closure: a seed set closed under the mirrors' reflections, marking `seen` and returning how many
    // nodes were reached. Create closes the mirror set to find the points a system acts on; TryEnumerateOrbit closes a
    // single node. Same walk, one home, and the lattice's own node count bounds it either way.
    private static int CloseUnderMirrors(ReadOnlySpan<int> mirrors, ReadOnlySpan<int> seeds, Span<bool> seen, Span<int> frontier) {
        var reached = 0;

        seen.Clear();

        foreach (var seed in seeds) {
            if (seen[seed]) { continue; }

            seen[seed] = true;
            frontier[reached++] = seed;
        }

        for (var cursor = 0; (cursor < reached); ++cursor) {
            for (var index = 0; (index < mirrors.Length); ++index) {
                var image = SymmetryLattice.Reflect(node: frontier[cursor], mirror: mirrors[index]);

                if (seen[image]) { continue; }

                seen[image] = true;
                frontier[reached++] = image;
            }
        }

        return reached;
    }

    /// <summary>Applies a word of mirrors to a lattice node.</summary>
    /// <param name="word">The word, its letters indexing <see cref="Mirrors"/>, applied left to right.</param>
    /// <param name="node">The node to move, in <c>[0, <see cref="SymmetryLattice.NodeCount"/>)</c>.</param>
    /// <returns>The node the word carries <paramref name="node"/> to.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A letter names no mirror of this system, or the node is outside the
    /// lattice's node range.</exception>
    /// <remarks>Any node is admitted, not only a <see cref="Points"/> member: a sub-system's reflections still move the
    /// whole lattice, and the points are only the set it acts faithfully on.</remarks>
    public int Apply(ReadOnlySpan<int> word, int node) {
        var image = node;

        for (var index = 0; (index < word.Length); ++index) {
            var letter = word[index];

            if ((letter < 0) || (letter >= m_mirrors.Length)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(word), actualValue: letter, message: $"A letter names a mirror of this system, so it lies in [0, {m_mirrors.Length}).");
            }

            image = SymmetryLattice.Reflect(node: image, mirror: m_mirrors[letter]);
        }

        return image;
    }

    /// <summary>Enumerates the group the mirrors generate, as permutations of <see cref="Points"/>, bounded.</summary>
    /// <param name="searchLimit">The largest number of elements to admit; the enumeration stops there and refuses.</param>
    /// <param name="permutations">On success, the elements as a row-major table of <see cref="Points"/> images, one row
    /// per element, ascending in lexicographic order of their rows — so the identity is the first row.</param>
    /// <param name="obstruction">On failure, <see cref="ClosureOutcome.SearchLimitReached"/> and the number of elements
    /// reached before the limit.</param>
    /// <returns><see langword="true"/> when the whole group fit the limit; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="searchLimit"/> is negative or above 65,536.</exception>
    /// <remarks>
    /// The enumeration is exact and order-independent: it closes the identity under the mirrors, decides membership by
    /// binary search over a lexicographically sorted index, and emits the elements in that order, so the table is the
    /// same on every machine. The limit bounds the memory as well as the work — one element is one permutation of
    /// <see cref="Points"/> — which is why it carries a cap of its own.
    /// </remarks>
    public bool TryEnumerateGroup(long searchLimit, out ReadOnlyMemory<int> permutations, out GroupObstruction obstruction) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: searchLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: searchLimit, other: MaximumSearchLimit);

        var mirrorCount = m_mirrors.Length;
        var pointCount = m_points.Length;
        var order = new List<int> { 0 };
        var table = new List<int>();

        for (var point = 0; (point < pointCount); ++point) { table.Add(item: point); }

        var count = 1L;
        var image = new int[pointCount];

        permutations = default;
        obstruction = default;

        for (var cursor = 0L; (cursor < count); ++cursor) {
            for (var mirror = 0; (mirror < mirrorCount); ++mirror) {
                var start = ((int)(cursor * pointCount));

                for (var point = 0; (point < pointCount); ++point) {
                    image[point] = m_pointImage[((table[(start + point)] * mirrorCount) + mirror)];
                }

                if (TryFindRow(table: table, order: order, row: image, pointCount: pointCount, slot: out var slot)) { continue; }

                if (count >= searchLimit) {
                    obstruction = new(Outcome: ClosureOutcome.SearchLimitReached, BlockedSymbol: mirror, BlockedKey: -1L, PointsReached: count);

                    return false;
                }

                order.Insert(index: slot, item: ((int)count));
                table.AddRange(collection: image);
                ++count;
            }
        }

        var emitted = new int[((int)count * pointCount)];

        for (var index = 0; (index < order.Count); ++index) {
            table.CopyTo(index: (order[index] * pointCount), array: emitted, arrayIndex: (index * pointCount), count: pointCount);
        }

        permutations = emitted;

        return true;
    }

    /// <summary>Enumerates one node's orbit under the mirrors, into a caller buffer that bounds what is written.</summary>
    /// <param name="seed">The node to close, in <c>[0, <see cref="SymmetryLattice.NodeCount"/>)</c>.</param>
    /// <param name="orbit">Receives the orbit, ascending by node index.</param>
    /// <param name="count">On success, the number of nodes written; on failure, the size the orbit actually reached.</param>
    /// <param name="obstruction">On failure, <see cref="ClosureOutcome.SearchLimitReached"/> and the size reached.</param>
    /// <returns><see langword="true"/> when the orbit fit the buffer; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The seed is outside the lattice's node range.</exception>
    /// <remarks>The buffer is not a work budget and is not treated as one: the orbit of a lattice node is closed inside
    /// the lattice, which is 240 nodes, so the walk is bounded before the buffer is consulted. What a short buffer
    /// shrinks is the ANSWER, and the refusal still reports the size the caller would need.</remarks>
    public bool TryEnumerateOrbit(int seed, Span<int> orbit, out int count, out GroupObstruction obstruction) {
        if ((seed < 0) || (seed >= SymmetryLattice.NodeCount)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(seed), actualValue: seed, message: $"A seed names a lattice node, so it lies in [0, {SymmetryLattice.NodeCount}).");
        }

        Span<bool> seen = stackalloc bool[SymmetryLattice.NodeCount];
        Span<int> frontier = stackalloc int[SymmetryLattice.NodeCount];

        var reached = CloseUnderMirrors(mirrors: m_mirrors, seeds: [seed], seen: seen, frontier: frontier);

        count = reached;
        obstruction = default;

        if (reached > orbit.Length) {
            obstruction = new(Outcome: ClosureOutcome.SearchLimitReached, BlockedSymbol: -1, BlockedKey: seed, PointsReached: reached);

            return false;
        }

        var written = 0;

        for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
            if (seen[node]) { orbit[written++] = node; }
        }

        return true;
    }

    // The order of the composite of two mirrors' reflections, as the least common multiple of its cycle lengths over
    // every node. It is a whole-lattice statement rather than a sub-system one, so a bond never depends on which points
    // the caller happens to have asked about.
    private static int CompositeOrder(int first, int second) {
        var order = 1;

        for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
            var cursor = node;
            var length = 0;

            do {
                cursor = SymmetryLattice.Reflect(node: SymmetryLattice.Reflect(node: cursor, mirror: first), mirror: second);
                ++length;
            } while (cursor != node);

            order = ((order / order.GreatestCommonDivisor(other: length)) * length);
        }

        return order;
    }

    // Membership in the lexicographically ordered index, and the slot a fresh row belongs at. Binary search over a
    // sorted index, never a hash of a rendered row, so the enumeration order is the same everywhere.
    private static bool TryFindRow(List<int> table, List<int> order, ReadOnlySpan<int> row, int pointCount, out int slot) {
        var rows = CollectionsMarshal.AsSpan(list: table);
        var low = 0;
        var high = order.Count;

        while (low < high) {
            var middle = ((low + high) >> 1);
            var candidate = rows.Slice(start: (order[middle] * pointCount), length: pointCount);
            var comparison = candidate.SequenceCompareTo(other: row);

            if (0 == comparison) {
                slot = middle;

                return true;
            }

            if (comparison < 0) { low = (middle + 1); } else { high = middle; }
        }

        slot = low;

        return false;
    }
}
