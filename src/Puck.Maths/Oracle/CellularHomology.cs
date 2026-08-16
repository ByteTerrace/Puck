using System.Numerics;

namespace Puck.Maths;

/// <summary>A nonzero entry of two adjacent boundary operators' composite.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <param name="Degree">The middle degree <c>d</c> in the failed identity
/// <c>∂<sub>d</sub> ∘ ∂<sub>d+1</sub> = 0</c>.</param>
/// <param name="RowCell">The degree-<c>d−1</c> cell indexing the witness row.</param>
/// <param name="ColumnCell">The degree-<c>d+1</c> cell indexing the witness column.</param>
/// <param name="CompositeCoefficient">The nonzero coefficient at that row and column.</param>
public readonly record struct ChainComplexObstruction<TValue>(int Degree, int RowCell, int ColumnCell, TValue CompositeCoefficient);
/// <summary>The typed refusal raised when incidence data does not define a chain complex.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <remarks>Homology is undefined unless every adjacent boundary composition vanishes. The exception carries the
/// first failed entry in deterministic degree, row-cell, column-cell order so malformed incidence data can be
/// diagnosed without accepting a partial or negative answer.</remarks>
public sealed class ChainComplexException<TValue> : InvalidOperationException {
    internal ChainComplexException(ChainComplexObstruction<TValue> obstruction)
        : base(message: $"The incidence data is not a chain complex: boundary {obstruction.Degree} after boundary {(obstruction.Degree + 1)} maps cell {obstruction.ColumnCell} to cell {obstruction.RowCell} with nonzero coefficient {obstruction.CompositeCoefficient}.") {
        Obstruction = obstruction;
    }

    /// <summary>Gets the first nonzero entry of an adjacent boundary composition.</summary>
    public ChainComplexObstruction<TValue> Obstruction { get; }
}
/// <summary>
/// The Betti numbers of a finite oriented complex over a field material: the ranks of its graded boundary operators,
/// taken with the same reduced row echelon basis the duality layer already uses, and nothing else.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material; it must be a field, since a rank needs inverses.</typeparam>
/// <remarks>
/// <para>
/// <b>It adds no linear algebra.</b> A Betti number is <c>c_d − rank ∂_d − rank ∂_{d+1}</c>, and every rank in it is a
/// count of independent columns — which the internal echelon that decides machine equivalence already produces. So the
/// only new code here is the bookkeeping that turns three counts into one number, exactly as the declared obstruction
/// for elementary divisors promised: Betti numbers over field materials need no second kernel.
/// </para>
/// <para>
/// <b>What a field cannot see is torsion.</b> Over a field the homology of a complex is a vector space, so its
/// dimensions are the whole answer and a torsion coefficient has nowhere to live. The same complex read over the
/// integers by <see cref="IntegerHomology"/> answers strictly more, and the two disagree exactly where the torsion
/// meets the field's characteristic — which is a measurement rather than a caveat.
/// </para>
/// <para>
/// Not thread-safe during construction, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not; the finished
/// instance is immutable.
/// </para>
/// </remarks>
public sealed class FieldHomology<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly int[] m_betti;
    private readonly int[] m_ranks;

    private FieldHomology(int dimension, int[] betti, int[] ranks) {
        Dimension = dimension;
        EulerCharacteristic = GradedHomology.AlternatingSum(counts: betti);
        m_betti = betti;
        m_ranks = ranks;
    }

    /// <summary>Gets the highest cell dimension the complex carries.</summary>
    public int Dimension { get; }
    /// <summary>Gets the alternating sum of the Betti numbers.</summary>
    /// <remarks>It is the Euler-Poincaré identity's left-hand side. That it equals the alternating cell count, and the
    /// Möbius mass <see cref="ExteriorCalculus{TValue, TOps}.TryEulerCharacteristic"/> reads off the same complex, is
    /// the statement — three routes to one number, sharing no step.</remarks>
    public int EulerCharacteristic { get; }

    /// <summary>Returns the Betti number of one degree — the dimension of that homology space.</summary>
    /// <param name="degree">The degree.</param>
    /// <returns>The Betti number.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree is negative or above <see cref="Dimension"/>.</exception>
    public int BettiNumber(int degree) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: degree,
            other: Dimension
        );

        return m_betti[degree];
    }
    /// <summary>Returns the rank of one graded boundary operator, the one whose columns are the cells of that degree.</summary>
    /// <param name="degree">The degree.</param>
    /// <returns>The rank.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree is negative or above <see cref="Dimension"/> plus one.</exception>
    /// <remarks>Degree zero and one past the top are both the zero map, and both are answered rather than refused.</remarks>
    public int BoundaryRank(int degree) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: degree,
            other: (Dimension + 1)
        );

        return m_ranks[degree];
    }
    /// <summary>Computes the Betti numbers of a complex over a field material.</summary>
    /// <param name="calculus">The complex.</param>
    /// <returns>The described homology.</returns>
    /// <exception cref="ArgumentException">The material is not a field, so a rank is not defined over it.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="calculus"/> is <see langword="null"/>.</exception>
    /// <exception cref="ChainComplexException{TValue}">Two adjacent boundary operators have a nonzero composite. The
    /// exception carries the failed degree and matrix entry; no homology is constructed.</exception>
    public static FieldHomology<TValue, TOps> Create(ExteriorCalculus<TValue, TOps> calculus) {
        ArgumentNullException.ThrowIfNull(argument: calculus);

        if (calculus.Poset.Algebra.Presentation.Material is not IFieldMaterial<TValue, TOps> field) {
            throw new ArgumentException(
                message: "A rank counts independent columns, which a material without inverses cannot decide.",
                paramName: nameof(calculus)
            );
        }

        GradedHomology.RequireChainComplex(calculus: calculus);

        var dimension = calculus.Dimension;
        var cellCounts = new int[(dimension + 1)];
        var ranks = new int[(dimension + 2)];

        for (var degree = 0; (degree <= dimension); ++degree) { cellCounts[degree] = calculus.CellsOfDegree(degree: degree).Length; }

        for (var degree = 1; (degree <= dimension); ++degree) {
            var columns = cellCounts[degree];
            var rows = cellCounts[(degree - 1)];

            if (
                (0 == columns) ||
                (0 == rows)
            ) { continue; }

            var entries = new TValue[(rows * columns)];

            calculus.BoundaryMatrix(
                degree: degree,
                entries: entries
            );

            var echelon = new FieldEchelon<TValue, TOps>(
                field: field,
                width: rows
            );
            var vector = new TValue[rows];

            for (var column = 0; (column < columns); ++column) {
                for (var row = 0; (row < rows); ++row) { vector[row] = entries[((row * columns) + column)]; }

                _ = echelon.TryAdmit(vector: vector);
            }

            ranks[degree] = echelon.Count;
        }

        return new(
            dimension: dimension,
            betti: GradedHomology.BettiNumbers(
                cellCounts: cellCounts,
                ranks: ranks
            ),
            ranks: ranks
        );
    }
}
/// <summary>
/// The integral homology of a finite oriented complex: Betti numbers and torsion coefficients, read off the elementary
/// divisors of its graded boundary operators through the declared second kernel.
/// </summary>
/// <remarks>
/// <para>
/// <b>The elementary divisors are the torsion coefficients.</b> Reducing <c>∂_{d+1}</c> to <c>U·∂·V = D</c> changes
/// basis in the two chain groups by unimodular transforms, so it changes neither the kernel nor the image as subgroups;
/// in the reduced basis the image of <c>∂_{d+1}</c> inside the kernel of <c>∂_d</c> is generated by <c>d_i</c> times a
/// basis vector, and the quotient is <c>ℤ^b ⊕ ⊕_i ℤ/d_i</c>. So the divisors above one, in order, are exactly the
/// torsion of that degree, and no homology-specific arithmetic appears here at all.
/// </para>
/// <para>
/// <b>It is bounded, and it refuses.</b> Every reduction runs under the same magnitude ceiling
/// <see cref="SmithNormalForm.TryReduce"/> takes, and the first one that outgrows it stops the whole computation with
/// that reduction's own <see cref="SmithObstruction"/>. A partial answer is never returned.
/// </para>
/// <para>
/// <b>Every answer carries its certificate.</b> <see cref="TryBoundary"/> hands back the reduction the invariants were
/// read from, so a caller can re-multiply <c>U·∂·V</c> and check the numbers rather than take them.
/// </para>
/// </remarks>
public sealed class IntegerHomology {
    private readonly int[] m_betti;
    private readonly SmithNormalForm?[] m_boundaries;
    private readonly int[] m_ranks;
    private readonly BigInteger[][] m_torsion;

    private IntegerHomology(int dimension, int[] betti, SmithNormalForm?[] boundaries, int[] ranks, BigInteger[][] torsion) {
        Dimension = dimension;
        EulerCharacteristic = GradedHomology.AlternatingSum(counts: betti);
        m_betti = betti;
        m_boundaries = boundaries;
        m_ranks = ranks;
        m_torsion = torsion;
    }

    /// <summary>Gets the highest cell dimension the complex carries.</summary>
    public int Dimension { get; }
    /// <summary>Gets the alternating sum of the Betti numbers.</summary>
    /// <remarks>Torsion contributes nothing to it, which is why it can equal the alternating cell count of a complex
    /// whose homology is not free.</remarks>
    public int EulerCharacteristic { get; }

    private static BigInteger[] TorsionCoefficients(ReadOnlySpan<BigInteger> divisors) {
        var count = 0;

        foreach (var divisor in divisors) {
            if (divisor > BigInteger.One) { ++count; }
        }

        var coefficients = new BigInteger[count];
        var slot = 0;

        foreach (var divisor in divisors) {
            if (divisor > BigInteger.One) { coefficients[slot++] = divisor; }
        }

        return coefficients;
    }

    /// <summary>Returns the Betti number of one degree — the free rank of that homology group.</summary>
    /// <param name="degree">The degree.</param>
    /// <returns>The Betti number.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree is negative or above <see cref="Dimension"/>.</exception>
    public int BettiNumber(int degree) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: degree,
            other: Dimension
        );

        return m_betti[degree];
    }
    /// <summary>Returns the rank of one graded boundary operator over the rationals.</summary>
    /// <param name="degree">The degree.</param>
    /// <returns>The rank.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree is negative or above <see cref="Dimension"/> plus one.</exception>
    public int BoundaryRank(int degree) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: degree,
            other: (Dimension + 1)
        );

        return m_ranks[degree];
    }
    /// <summary>Returns the torsion coefficients of one degree, each dividing the next.</summary>
    /// <param name="degree">The degree.</param>
    /// <returns>The elementary divisors above one of the boundary operator one degree higher, which are the orders of
    /// the cyclic torsion summands.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree is negative or above <see cref="Dimension"/>.</exception>
    public ReadOnlySpan<BigInteger> Torsion(int degree) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: degree,
            other: Dimension
        );

        return m_torsion[degree];
    }
    /// <summary>Attempts to read the certified reduction one degree's invariants came from.</summary>
    /// <param name="degree">The degree.</param>
    /// <param name="form">On success, the reduction of the boundary operator whose columns are that degree's cells.</param>
    /// <returns><see langword="true"/> when that graded operator has both a row and a column; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree is negative or above <see cref="Dimension"/> plus one.</exception>
    /// <remarks>An operator with no rows or no columns is the zero map between groups one of which is trivial: it has
    /// rank zero and no divisors, and there is no matrix to reduce, so it carries no certificate rather than an empty
    /// one.</remarks>
    public bool TryBoundary(int degree, out SmithNormalForm form) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: degree,
            other: (Dimension + 1)
        );

        var stored = m_boundaries[degree];

        form = stored!;

        return (stored is not null);
    }
    /// <summary>Attempts the integral homology of a complex.</summary>
    /// <param name="calculus">The complex, over the integers.</param>
    /// <param name="magnitudeBits">The magnitude ceiling every elementary-divisor reduction runs under.</param>
    /// <param name="homology">On success, the invariants and the reductions they were read from.</param>
    /// <param name="obstruction">On failure, the refusal of the first reduction that outgrew the ceiling.</param>
    /// <returns><see langword="true"/> when every graded boundary operator reduced inside the ceiling; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="calculus"/> is <see langword="null"/>.</exception>
    /// <exception cref="ChainComplexException{BigInteger}">Two adjacent boundary operators have a nonzero composite.
    /// The exception carries the failed degree and matrix entry; no reduction or homology is returned.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="magnitudeBits"/> is outside one through the
    /// widest ceiling <see cref="SmithNormalForm.TryReduce"/> admits, or a graded boundary operator is larger than
    /// <see cref="SmithNormalForm.MaximumOrder"/> on a side.</exception>
    public static bool TryCompute(ExteriorCalculus<BigInteger, IntegerMaterial> calculus, int magnitudeBits, out IntegerHomology homology, out SmithObstruction obstruction) {
        ArgumentNullException.ThrowIfNull(argument: calculus);

        // The ceiling is validated here rather than left to the first reduction, because a complex with no boundary
        // operator at all — a zero-dimensional one — reduces nothing and would have accepted any number in silence.
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: magnitudeBits,
            other: 1
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: magnitudeBits,
            other: SmithNormalForm.MaximumMagnitudeBits
        );

        GradedHomology.RequireChainComplex(calculus: calculus);

        var dimension = calculus.Dimension;
        var boundaries = new SmithNormalForm?[(dimension + 2)];
        var cellCounts = new int[(dimension + 1)];
        var ranks = new int[(dimension + 2)];
        var torsion = new BigInteger[(dimension + 1)][];

        for (var degree = 0; (degree <= dimension); ++degree) {
            cellCounts[degree] = calculus.CellsOfDegree(degree: degree).Length;
            torsion[degree] = [];
        }

        homology = null!;
        obstruction = default;

        for (var degree = 1; (degree <= dimension); ++degree) {
            var columns = cellCounts[degree];
            var rows = cellCounts[(degree - 1)];

            if (
                (0 == columns) ||
                (0 == rows)
            ) { continue; }

            var entries = new BigInteger[(rows * columns)];

            calculus.BoundaryMatrix(
                degree: degree,
                entries: entries
            );

            if (!SmithNormalForm.TryReduce(
                columnCount: columns,
                entries: entries,
                form: out var form,
                magnitudeBits: magnitudeBits,
                obstruction: out obstruction,
                rowCount: rows
            )) {
                return false;
            }

            boundaries[degree] = form;
            ranks[degree] = form.Rank;
            torsion[(degree - 1)] = TorsionCoefficients(divisors: form.Divisors);
        }

        homology = new(
            dimension: dimension,
            betti: GradedHomology.BettiNumbers(
                cellCounts: cellCounts,
                ranks: ranks
            ),
            boundaries: boundaries,
            ranks: ranks,
            torsion: torsion
        );

        return true;
    }
}

// The bookkeeping both coefficient rings share, single-homed so the two readouts cannot drift: a Betti number is the
// cell count of a degree less the ranks of the two boundary operators that touch it, and the Euler characteristic is
// the alternating sum of those.
internal static class GradedHomology {
    internal static int AlternatingSum(ReadOnlySpan<int> counts) {
        var total = 0;

        for (var degree = 0; (degree < counts.Length); ++degree) {
            total += ((0 == (degree & 1))
                ? counts[degree]
                : -counts[degree]
            );
        }

        return total;
    }
    internal static int[] BettiNumbers(ReadOnlySpan<int> cellCounts, ReadOnlySpan<int> ranks) {
        var betti = new int[cellCounts.Length];

        for (var degree = 0; (degree < cellCounts.Length); ++degree) {
            betti[degree] = ((cellCounts[degree] - ranks[degree]) - ranks[(degree + 1)]);

            if (betti[degree] < 0) {
                throw new InvalidOperationException(message: $"A certified chain complex produced the negative Betti number {betti[degree]} in degree {degree}; the rank computation violated its postcondition.");
            }
        }

        return betti;
    }
    internal static void RequireChainComplex<TValue, TOps>(ExteriorCalculus<TValue, TOps> calculus)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var material = calculus.Poset.Algebra.Presentation.Material;

        for (var degree = 1; (degree < calculus.Dimension); ++degree) {
            var rowCells = calculus.CellsOfDegree(degree: (degree - 1));
            var middleCells = calculus.CellsOfDegree(degree: degree);
            var columnCells = calculus.CellsOfDegree(degree: (degree + 1));

            if (
                (0 == rowCells.Length) ||
                (0 == middleCells.Length) ||
                (0 == columnCells.Length)
            ) { continue; }

            var lower = new TValue[(rowCells.Length * middleCells.Length)];
            var upper = new TValue[(middleCells.Length * columnCells.Length)];

            calculus.BoundaryMatrix(
                degree: degree,
                entries: lower
            );
            calculus.BoundaryMatrix(
                degree: (degree + 1),
                entries: upper
            );

            for (var row = 0; (row < rowCells.Length); ++row) {
                for (var column = 0; (column < columnCells.Length); ++column) {
                    var coefficient = material.Zero;

                    for (var middle = 0; (middle < middleCells.Length); ++middle) {
                        coefficient = material.Add(
                            left: coefficient,
                            right: material.Multiply(
                                left: lower[((row * middleCells.Length) + middle)],
                                right: upper[((middle * columnCells.Length) + column)]
                            )
                        );
                    }

                    if (material.IsZero(value: coefficient)) { continue; }

                    throw new ChainComplexException<TValue>(obstruction: new(
                        Degree: degree,
                        RowCell: rowCells[row],
                        ColumnCell: columnCells[column],
                        CompositeCoefficient: coefficient
                    ));
                }
            }
        }
    }
}
