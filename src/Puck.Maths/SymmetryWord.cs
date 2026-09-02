namespace Puck.Maths;

/// <summary>
/// A word of reflections of <see cref="SymmetryLattice"/>, baked once into the permutation of the 240 nodes it
/// induces: its cycle decomposition, its order, and the constant-time counted power <see cref="Apply(int, long)"/>
/// that carries a node any number of steps along its orbit. The lattice's own thirty-step cycle is the word
/// <see cref="Coxeter"/>; any other word is a different looping generator with its own derived period, so a
/// twelve-position dial or a twenty-four-step day is a word whose order is twelve or twenty-four rather than a
/// period authored beside it.
/// </summary>
/// <remarks>
/// <para>Letters act left to right, the same order <c>ReflectionSystem.Apply</c> reads a word: the image of a node
/// under <c>[a, b]</c> is its reflection through <c>a</c> reflected through <c>b</c>.</para>
/// <para>A word holds at most <see cref="MaximumLength"/> letters. Every element of the lattice's reflection group is
/// a product of at most that many reflections (one per dimension), so the cap loses no element — a longer word is a
/// longer spelling of one that fits.</para>
/// <para>Every accessor after construction is an allocation-free table read; the baked tables are a pure function of
/// the letters, so equal words act identically on every machine.</para>
/// </remarks>
public sealed class SymmetryWord {
    /// <summary>The most letters a word may hold, one per lattice dimension.</summary>
    public const int MaximumLength = SymmetryLattice.Dimension;

    private readonly ushort[] m_cycleLength;
    private readonly ushort[] m_cycleStart;
    private readonly int[] m_mirrors;
    private readonly ushort[] m_orbit;
    private readonly ushort[] m_position;

    private SymmetryWord(int[] mirrors, ushort[] orbit, ushort[] cycleStart, ushort[] cycleLength, ushort[] position, int order) {
        m_cycleLength = cycleLength;
        m_cycleStart = cycleStart;
        m_mirrors = mirrors;
        m_orbit = orbit;
        m_position = position;
        Order = order;
    }

    /// <summary>Gets the lattice's own cycle as a word: the eight seed mirrors read in descending order, whose
    /// permutation is <see cref="SymmetryLattice.Cycle(int)"/> and whose order is <see cref="CyclicRotation.Period"/>.</summary>
    public static SymmetryWord Coxeter { get; } = Create(mirrors: [7, 6, 5, 4, 3, 2, 1, 0]);

    /// <summary>Gets a value indicating whether the word moves no node, so its order is one and it loops nothing.</summary>
    public bool IsIdentity => (Order == 1);
    /// <summary>Gets the number of letters.</summary>
    public int Length => m_mirrors.Length;
    /// <summary>Gets the letters, the mirror nodes in the order they apply.</summary>
    public ReadOnlySpan<int> Mirrors => m_mirrors;
    /// <summary>Gets the order of the permutation: the least positive step count that returns every node to itself,
    /// the least common multiple of the orbit lengths.</summary>
    public int Order { get; }

    /// <summary>Creates the word of a list of mirror nodes.</summary>
    /// <param name="mirrors">The letters, each a node in <c>[0, <see cref="SymmetryLattice.NodeCount"/>)</c>, applied first to last.</param>
    /// <returns>The baked word.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There are no letters, more than <see cref="MaximumLength"/>, or a
    /// letter is outside the node range.</exception>
    public static SymmetryWord Create(ReadOnlySpan<int> mirrors) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: mirrors.Length,
            other: 1,
            paramName: nameof(mirrors)
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: mirrors.Length,
            other: MaximumLength,
            paramName: nameof(mirrors)
        );

        var letters = mirrors.ToArray();

        foreach (var letter in letters) {
            if (((uint)letter) >= ((uint)SymmetryLattice.NodeCount)) {
                throw new ArgumentOutOfRangeException(
                    actualValue: letter,
                    message: $"a letter names a lattice node, so it lies in [0, {SymmetryLattice.NodeCount})",
                    paramName: nameof(mirrors)
                );
            }
        }

        var cycleLength = new ushort[SymmetryLattice.NodeCount];
        var cycleStart = new ushort[SymmetryLattice.NodeCount];
        var orbit = new ushort[SymmetryLattice.NodeCount];
        var position = new ushort[SymmetryLattice.NodeCount];
        Span<bool> placed = stackalloc bool[SymmetryLattice.NodeCount];
        var filled = 0;
        var order = 1;

        for (var seed = 0; (seed < SymmetryLattice.NodeCount); ++seed) {
            if (placed[seed]) { continue; }

            var start = filled;
            var cursor = seed;

            do {
                placed[cursor] = true;
                orbit[filled] = ((ushort)cursor);
                position[cursor] = ((ushort)(filled - start));
                ++filled;
                cursor = Image(
                    letters: letters,
                    node: cursor
                );
            } while (cursor != seed);

            var length = (filled - start);

            for (var index = start; (index < filled); ++index) {
                cycleLength[orbit[index]] = ((ushort)length);
                cycleStart[orbit[index]] = ((ushort)start);
            }

            order = checked(order.LeastCommonMultiple(other: length));
        }

        return new(
            cycleLength: cycleLength,
            cycleStart: cycleStart,
            mirrors: letters,
            orbit: orbit,
            order: order,
            position: position
        );
    }
    private static int Image(ReadOnlySpan<int> letters, int node) {
        foreach (var letter in letters) {
            node = SymmetryLattice.Reflect(
                node: node,
                mirror: letter
            );
        }

        return node;
    }
    private static void ValidateNode(int node) {
        if (((uint)node) >= ((uint)SymmetryLattice.NodeCount)) {
            throw new ArgumentOutOfRangeException(
                actualValue: node,
                message: $"the node index must be in [0, {SymmetryLattice.NodeCount})",
                paramName: nameof(node)
            );
        }
    }

    /// <summary>Applies the word once.</summary>
    /// <param name="node">The node to move, in <c>[0, <see cref="SymmetryLattice.NodeCount"/>)</c>.</param>
    /// <returns>The node the word carries <paramref name="node"/> to.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node"/> is outside the node range.</exception>
    public int Apply(int node) =>
        Apply(
            node: node,
            steps: 1L
        );
    /// <summary>Applies the word a whole number of times — the counted power, read in constant time from the node's
    /// orbit, so a negative count walks the orbit backwards and every multiple of the orbit's length returns the node
    /// itself.</summary>
    /// <param name="node">The node to move, in <c>[0, <see cref="SymmetryLattice.NodeCount"/>)</c>.</param>
    /// <param name="steps">The number of applications; any value, positive or negative.</param>
    /// <returns>The node <paramref name="steps"/> applications carry <paramref name="node"/> to.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node"/> is outside the node range.</exception>
    public int Apply(int node, long steps) {
        ValidateNode(node: node);

        var length = ((long)m_cycleLength[node]);
        var offset = (((long)m_position[node]) + steps).FloorModulo(modulus: length);

        return m_orbit[(m_cycleStart[node] + ((int)offset))];
    }
    /// <summary>Returns the length of a node's orbit: the least positive step count that returns that one node to
    /// itself, a divisor of <see cref="Order"/>.</summary>
    /// <param name="node">The node, in <c>[0, <see cref="SymmetryLattice.NodeCount"/>)</c>.</param>
    /// <returns>The orbit length, at least one.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node"/> is outside the node range.</exception>
    public int OrbitLength(int node) {
        ValidateNode(node: node);

        return m_cycleLength[node];
    }
}
