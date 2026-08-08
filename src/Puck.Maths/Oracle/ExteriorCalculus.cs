using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>
/// Discrete exterior calculus on a finite oriented complex: the cells' face order presented as an
/// <see cref="IncidenceAlgebra{TValue, TOps}"/>, the oriented incidence numbers carried as ONE element of it, and the
/// boundary and coboundary as the two sides that element multiplies on.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material; it must be signed, since an incidence number is a sign.</typeparam>
/// <remarks>
/// <para>
/// <b>It contributes no arithmetic and carries no matrix.</b> The face order is bounded below by an empty face and
/// above by a top, so the intervals from the bottom index the cells one way and the intervals to the top index them
/// the other. A cochain is the first family and a chain is the second; the incidence element <see cref="Incidence"/>
/// sits on the covering intervals between them. Right-multiplying a cochain by it is the coboundary and
/// left-multiplying a chain by it is the boundary — one element, two sides, and
/// <see cref="PresentedAlgebra{TValue, TOps}.Multiply"/> is the whole implementation.
/// </para>
/// <para>
/// <b>The pairing is the product landing on the top interval.</b> A cochain runs from the bottom and a chain runs to
/// the top, so their product collapses onto the single interval that spans the whole order and its coefficient is
/// <c>Σ_σ ω(σ)·c(σ)</c>. <see cref="Pair"/> is therefore
/// <see cref="PresentedAlgebra{TValue, TOps}.Behavior"/> at that readout — an initial vector, an acting element, a
/// counit — and the fold is the product's own, so a pairing carries exactly one rounding.
/// </para>
/// <para>
/// <b>Stokes' identity IS the associativity of that product.</b> <c>⟨dω, c⟩</c> brackets <c>(ω·δ)·c</c> and
/// <c>⟨ω, ∂c⟩</c> brackets <c>ω·(δ·c)</c>, so the adjunction is not a theorem about two operators, it is one product
/// read two ways. It is exact at every exact material, and over <see cref="FixedQ4816"/> it is bit-identical for a
/// reason rather than by luck: every incidence number is an exact integer of the carrier, so the intermediate
/// coboundary and boundary coefficients round to exact signed sums and the one fused fold on each side accumulates the
/// identical exact quantity. <b>The bound is the carrier, not the rounding.</b> Those intermediate sums are ordinary
/// wrapping additions, so operands large enough to wrap them carry the two sides apart — measured, and pinned by the
/// law both ways.
/// </para>
/// <para>
/// <b><c>∂∘∂ = 0</c> is the statement that the incidence element squares to zero</b>, and that is a fact about the
/// declared numbers rather than about this type: it is computed and reported, never enforced at construction. What
/// construction does enforce is what makes the numbers readable — a covering pair steps the dimension by exactly one,
/// a sign is a sign, no pair is declared twice, and a dimension is a degree the cell count can reach.
/// </para>
/// <para>
/// Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not.
/// </para>
/// </remarks>
public sealed class ExteriorCalculus<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly int[][] m_byDegree;
    private readonly long[] m_chainKey;
    private readonly long[] m_cochainKey;
    private readonly PresentedAlgebra<TValue, TOps>.Element m_counit;
    private readonly long m_counitKey;
    private readonly int[] m_dimensions;

    private ExteriorCalculus(
        IncidenceAlgebra<TValue, TOps> poset,
        int cellCount,
        int[] dimensions,
        long[] chainKey,
        long[] cochainKey,
        long counitKey,
        in PresentedAlgebra<TValue, TOps>.Element incidence
    ) {
        var top = 0;

        foreach (var dimension in dimensions) { top = Math.Max(val1: top, val2: dimension); }

        var byDegree = new int[(top + 1)][];
        var counts = new int[(top + 1)];

        foreach (var dimension in dimensions) { ++counts[dimension]; }

        for (var degree = 0; (degree <= top); ++degree) { byDegree[degree] = new int[counts[degree]]; }

        Array.Clear(array: counts);

        // Ascending by cell index inside each degree, so the boundary matrix's rows and columns carry a total order
        // that does not depend on how the incidences were declared.
        for (var cell = 0; (cell < cellCount); ++cell) { byDegree[dimensions[cell]][counts[dimensions[cell]]++] = cell; }

        CellCount = cellCount;
        Dimension = top;
        Incidence = incidence;
        Poset = poset;
        m_byDegree = byDegree;
        m_chainKey = chainKey;
        m_cochainKey = cochainKey;
        m_counit = poset.Algebra.FromSupport(keys: [counitKey], coefficients: [poset.Algebra.Presentation.Material.One]);
        m_counitKey = counitKey;
        m_dimensions = dimensions;
    }

    /// <summary>Gets the largest number of cells a complex may carry, which is 84.</summary>
    /// <remarks>It is derived rather than chosen, and it is the tightest number that can ever be reached: a bounded
    /// face order on <c>n</c> cells already carries <c>3n + 3</c> intervals before a single incidence is declared —
    /// one per element of the order, one from the empty face to the top, one from the empty face to each cell and one
    /// from each cell to the top — and the incidence algebra's basis holds 256, so 84 cells is the last count that can
    /// be presented at all and 85 is refused for every incidence list. A complex with incidences reaches the interval
    /// cap sooner: the seven-vertex torus is 42 cells and 255 of the 256.</remarks>
    public static int MaximumCellCount => 84;

    /// <summary>Gets the number of cells.</summary>
    public int CellCount { get; }
    /// <summary>Gets the highest cell dimension the complex carries.</summary>
    public int Dimension { get; }
    /// <summary>Gets the incidence element — the declared orientation signs, one per covering pair of cells.</summary>
    /// <remarks>It is the boundary operator and the coboundary operator at once, since which one it is depends only on
    /// the side it multiplies from. Its square is zero exactly when the declared numbers form a chain complex.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Incidence { get; }
    /// <summary>Gets the incidence algebra of the complex's face order, bounded below by an empty face and above by a
    /// top.</summary>
    public IncidenceAlgebra<TValue, TOps> Poset { get; }

    /// <summary>Creates the exterior calculus of an oriented complex.</summary>
    /// <param name="dimensions">One dimension per cell.</param>
    /// <param name="incidences">The oriented incidence numbers: a face, the coface it bounds, and the sign it enters
    /// that coface's boundary with.</param>
    /// <param name="material">The material; it must be signed.</param>
    /// <returns>The described calculus.</returns>
    /// <exception cref="ArgumentException">The material is not signed, an incidence names a cell outside the range, a
    /// coface's dimension is not one above its face's, a sign is neither one nor minus one, or a face-and-coface pair
    /// is declared twice.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There are no cells or more than
    /// <see cref="MaximumCellCount"/> of them, a dimension is negative or past the last degree the cell count reaches,
    /// or the face order has more intervals than a finite basis of this library holds.</exception>
    /// <remarks>A dimension is a label, and the labels are not free: the graded reading runs densely from degree zero
    /// to the top, so a complex of <c>n</c> cells reaches dimension <c>n - 1</c> at the most and a larger label names
    /// degrees no cell can fill. That is what keeps the whole construction inside <see cref="MaximumCellCount"/>: with
    /// the labels bounded by the cells, the widest grading any complex presents is 84 degrees, so no single number can
    /// size it.</remarks>
    public static ExteriorCalculus<TValue, TOps> Create(ReadOnlySpan<int> dimensions, ReadOnlySpan<(int Face, int Coface, int Sign)> incidences, TOps material) {
        var cellCount = dimensions.Length;

        ArgumentOutOfRangeException.ThrowIfLessThan(value: cellCount, other: 1, paramName: nameof(dimensions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: cellCount, other: MaximumCellCount, paramName: nameof(dimensions));

        if (material is not ISignedMaterial<TValue, TOps> signed) {
            throw new ArgumentException(message: "An incidence number is a sign, which an unsigned material cannot express.", paramName: nameof(material));
        }

        // The degree table runs densely from zero to the top, so its size is set by the largest LABEL rather than by
        // the number of cells — and a complex of n cells can occupy at most n degrees. A label past n - 1 therefore
        // names degrees no cell can ever fill, and admitting one would let a single integer size an allocation the
        // cell cap was written to bound.
        foreach (var dimension in dimensions) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: dimension, paramName: nameof(dimensions));

            if (dimension >= cellCount) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(dimensions),
                    actualValue: dimension,
                    message: $"A complex of {cellCount} cell(s) reaches dimension {(cellCount - 1)} at the most, and this cell is declared at dimension {dimension}."
                );
            }
        }

        // The empty face and the top, adjoined so the order is bounded: the intervals from the one index cochains and
        // the intervals to the other index chains, which is what makes both families live in the same algebra.
        var bottom = cellCount;
        var top = (cellCount + 1);
        var declared = new bool[(cellCount * cellCount)];
        var relations = new List<(int Lower, int Upper)> { (bottom, top) };

        for (var cell = 0; (cell < cellCount); ++cell) {
            relations.Add(item: (bottom, cell));
            relations.Add(item: (cell, top));
        }

        foreach (var (face, coface, sign) in incidences) {
            if ((face < 0) || (face >= cellCount) || (coface < 0) || (coface >= cellCount)) {
                throw new ArgumentException(message: "An incidence names a cell outside this complex.", paramName: nameof(incidences));
            }

            if (dimensions[coface] != (dimensions[face] + 1)) {
                throw new ArgumentException(message: "An incidence relates a face to a coface of one higher dimension, and this pair does not step by one.", paramName: nameof(incidences));
            }

            if ((1 != sign) && (-1 != sign)) {
                throw new ArgumentException(message: "An orientation sign is one or minus one.", paramName: nameof(incidences));
            }

            if (declared[((face * cellCount) + coface)]) {
                throw new ArgumentException(message: "An incidence pair is declared twice, and the second number would silently add to the first.", paramName: nameof(incidences));
            }

            declared[((face * cellCount) + coface)] = true;

            relations.Add(item: (face, coface));
        }

        var poset = IncidenceAlgebra<TValue, TOps>.Create(elementCount: (cellCount + 2), relations: CollectionsMarshal.AsSpan(list: relations), material: material);
        var negativeOne = signed.Negate(value: material.One);
        var chainKey = new long[cellCount];
        var cochainKey = new long[cellCount];

        // Every lookup below names a relation this method just declared, so each one is an interval of the order it
        // built and the comparability answer carries no information.
        for (var cell = 0; (cell < cellCount); ++cell) {
            _ = poset.TryKey(lower: cell, upper: top, key: out chainKey[cell]);
            _ = poset.TryKey(lower: bottom, upper: cell, key: out cochainKey[cell]);
        }

        _ = poset.TryKey(lower: bottom, upper: top, key: out var counitKey);

        var charges = new TValue[incidences.Length];
        var keys = new long[incidences.Length];

        for (var index = 0; (index < incidences.Length); ++index) {
            var (face, coface, sign) = incidences[index];

            _ = poset.TryKey(lower: face, upper: coface, key: out keys[index]);

            charges[index] = ((sign > 0) ? material.One : negativeOne);
        }

        return new(
            poset: poset,
            cellCount: cellCount,
            dimensions: dimensions.ToArray(),
            chainKey: chainKey,
            cochainKey: cochainKey,
            counitKey: counitKey,
            incidence: poset.Algebra.FromSupport(keys: keys, coefficients: charges)
        );
    }

    /// <summary>Returns the boundary of a chain.</summary>
    /// <param name="chain">The chain.</param>
    /// <returns>The chain whose coefficient at a cell is the signed sum of the coefficients of the cells it bounds.</returns>
    /// <exception cref="ArgumentException">The chain belongs to another algebra.</exception>
    /// <remarks>It is <see cref="Incidence"/> multiplied on the LEFT and nothing else. A chain sits on the intervals
    /// running to the top, and left-multiplying such an interval by a covering one replaces its lower endpoint with
    /// the face — which is what lowers the dimension.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Boundary(in PresentedAlgebra<TValue, TOps>.Element chain) {
        Poset.Algebra.RequireOwned(value: chain, paramName: nameof(chain));

        return Poset.Algebra.Multiply(left: Incidence, right: chain);
    }

    /// <summary>Writes one graded piece of the boundary operator as a matrix.</summary>
    /// <param name="degree">The degree: the columns are the cells of this dimension and the rows are the cells of one
    /// less.</param>
    /// <param name="entries">The destination, row-major, exactly as many entries as rows times columns.</param>
    /// <exception cref="ArgumentException"><paramref name="entries"/> is not exactly the size of the matrix.</exception>
    /// <remarks>Each column is <see cref="Boundary"/> of that cell's basis chain, read at the rows' chain keys — so the
    /// matrix is the product's own answer rather than a second reading of the declared incidence numbers. A degree with
    /// no cells on one of its two sides gives an empty matrix, which is the correct boundary operator there.</remarks>
    public void BoundaryMatrix(int degree, Span<TValue> entries) {
        var columns = CellsOfDegree(degree: degree);
        var rows = CellsOfDegree(degree: (degree - 1));

        if (entries.Length != (rows.Length * columns.Length)) {
            throw new ArgumentException(message: "The destination is not exactly the size of this graded boundary matrix.", paramName: nameof(entries));
        }

        for (var column = 0; (column < columns.Length); ++column) {
            var image = Boundary(chain: Poset.Algebra.FromSupport(keys: [m_chainKey[columns[column]]], coefficients: [Poset.Algebra.Presentation.Material.One]));

            for (var row = 0; (row < rows.Length); ++row) { entries[((row * columns.Length) + column)] = image[m_chainKey[rows[row]]]; }
        }
    }

    /// <summary>Returns the declared dimension of one cell.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The dimension.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The cell names no cell of this complex.</exception>
    public int CellDimension(int cell) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: cell, other: CellCount);

        return m_dimensions[cell];
    }

    /// <summary>Returns the cells of one dimension, ascending by cell index.</summary>
    /// <param name="degree">The dimension.</param>
    /// <returns>The cells of that dimension, or an empty span for a dimension the complex does not carry.</returns>
    /// <remarks>A degree outside the complex is answered rather than refused, because the chain groups above the top
    /// dimension and below zero are genuinely the zero group and a caller walking the whole complex should not have to
    /// special-case its two ends.</remarks>
    public ReadOnlySpan<int> CellsOfDegree(int degree) =>
        (((degree < 0) || (degree > Dimension)) ? [] : m_byDegree[degree]);

    /// <summary>Returns a chain as an element.</summary>
    /// <param name="values">One coefficient per cell, cell zero first; a shorter span leaves the remaining cells at the
    /// material's zero.</param>
    /// <returns>The element, supported on the intervals running to the top.</returns>
    /// <exception cref="ArgumentOutOfRangeException">More values were given than the complex has cells.</exception>
    public PresentedAlgebra<TValue, TOps>.Element Chain(ReadOnlySpan<TValue> values) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: values.Length, other: CellCount);

        return Poset.Algebra.FromSupport(keys: m_chainKey.AsSpan(start: 0, length: values.Length), coefficients: values);
    }

    /// <summary>Returns the key a chain carries one cell's coefficient at.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The key of the interval from that cell to the top.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The cell names no cell of this complex.</exception>
    public long ChainKey(int cell) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: cell, other: CellCount);

        return m_chainKey[cell];
    }

    /// <summary>Returns the coboundary of a cochain.</summary>
    /// <param name="cochain">The cochain.</param>
    /// <returns>The cochain whose coefficient at a cell is the signed sum of the coefficients of that cell's own
    /// faces.</returns>
    /// <exception cref="ArgumentException">The cochain belongs to another algebra.</exception>
    /// <remarks>It is <see cref="Incidence"/> multiplied on the RIGHT, which is the whole of its definition through the
    /// pairing adjunction: with the boundary on the left of the same element, <c>⟨dω, c⟩</c> and <c>⟨ω, ∂c⟩</c> are two
    /// bracketings of one product.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Coboundary(in PresentedAlgebra<TValue, TOps>.Element cochain) {
        Poset.Algebra.RequireOwned(value: cochain, paramName: nameof(cochain));

        return Poset.Algebra.Multiply(left: cochain, right: Incidence);
    }

    /// <summary>Returns a cochain as an element.</summary>
    /// <param name="values">One coefficient per cell, cell zero first; a shorter span leaves the remaining cells at the
    /// material's zero.</param>
    /// <returns>The element, supported on the intervals running from the empty face.</returns>
    /// <exception cref="ArgumentOutOfRangeException">More values were given than the complex has cells.</exception>
    public PresentedAlgebra<TValue, TOps>.Element Cochain(ReadOnlySpan<TValue> values) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: values.Length, other: CellCount);

        return Poset.Algebra.FromSupport(keys: m_cochainKey.AsSpan(start: 0, length: values.Length), coefficients: values);
    }

    /// <summary>Returns the key a cochain carries one cell's coefficient at.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The key of the interval from the empty face to that cell.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The cell names no cell of this complex.</exception>
    public long CochainKey(int cell) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: cell, other: CellCount);

        return m_cochainKey[cell];
    }

    /// <summary>Pairs a cochain with a chain.</summary>
    /// <param name="cochain">The cochain.</param>
    /// <param name="chain">The chain.</param>
    /// <returns><c>Σ_σ ω(σ)·c(σ)</c>, folded with exactly one rounding.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another algebra.</exception>
    /// <remarks>The evaluation half of the duality, and it is the product itself: a cochain runs from the empty face
    /// and a chain runs to the top, so their product carries a single term, on the interval that spans the order. The
    /// readout at that interval is the counit.</remarks>
    public TValue Pair(in PresentedAlgebra<TValue, TOps>.Element cochain, in PresentedAlgebra<TValue, TOps>.Element chain) {
        Poset.Algebra.RequireOwned(value: cochain, paramName: nameof(cochain));
        Poset.Algebra.RequireOwned(value: chain, paramName: nameof(chain));

        return Poset.Algebra.Behavior(initial: cochain, value: chain, readout: m_counit);
    }

    /// <summary>Attempts to compute the Euler characteristic as Möbius mass.</summary>
    /// <param name="characteristic">On success, the Euler characteristic of the complex.</param>
    /// <param name="obstruction">On failure, the certificate attempted and where the attempt stopped.</param>
    /// <returns><see langword="true"/> when the Möbius element was computed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">The material is not signed.</exception>
    /// <remarks>It is <c>1 + μ(∅, ⊤)</c>: the Möbius value of the one interval spanning the bounded face order counts
    /// the chains of cells with alternating sign, which is the reduced Euler characteristic, and the empty face is the
    /// one it leaves out. Nothing counts cells by dimension here — the alternating count over the cells is the
    /// independent statement, not the definition.</remarks>
    public bool TryEulerCharacteristic(out TValue characteristic, out SumClosureObstruction obstruction) {
        var material = Poset.Algebra.Presentation.Material;

        if (!Poset.TryMobius(mobius: out var mobius, obstruction: out obstruction)) {
            characteristic = material.Zero;

            return false;
        }

        characteristic = material.Add(left: material.One, right: mobius[m_counitKey]);

        return true;
    }
}
