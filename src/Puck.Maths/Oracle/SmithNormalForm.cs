using System.Numerics;

namespace Puck.Maths;

/// <summary>The refusal of a bounded elementary-divisor reduction.</summary>
/// <param name="Stage">The diagonal position the reduction had reached — the number of elementary divisors already
/// fixed when the attempt was cut off.</param>
/// <param name="MagnitudeBits">The bit length the entry that outgrew the ceiling reached, which is above the declared
/// one.</param>
/// <param name="StepsTaken">The number of unimodular operations applied before the attempt was cut off.</param>
/// <remarks>There is exactly one refusal, and it is the magnitude ceiling: elementary-divisor reduction over the
/// integers terminates, so nothing here is an open-ended search. What is genuinely unbounded a priori is how large an
/// intermediate entry becomes, since no polynomial in the input's bit length bounds it for a general pivot rule. The
/// ceiling makes the attempt finite in memory; the guarantee shrinks — some matrices are refused — and no unproved
/// answer is ever returned in exchange.</remarks>
public readonly record struct SmithObstruction(int Stage, int MagnitudeBits, long StepsTaken);

/// <summary>
/// The elementary-divisor reduction of an integer matrix: unimodular <c>U</c> and <c>V</c> and a diagonal <c>D</c>
/// whose entries divide one another in order, with <c>U·A·V = D</c>. It is the DECLARED SECOND KERNEL of the presented
/// algebra: the one operation of that wing which is not a convolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a second kernel.</b> Every other operation in the presented-algebra wing is one product at a
/// presentation — the matrix product, the convolution, the star and the pairing are all
/// <see cref="PresentedAlgebra{TValue, TOps}.Multiply"/> over compiled cells. Elementary-divisor reduction is not: it
/// searches for a pivot, it divides with remainder, and its answer depends on the whole matrix rather than on any
/// bilinear rule. No presentation computes it, so it is declared and carried openly rather than smuggled in as a rule
/// kind. The claim is about THIS wing and not about the library, which carries Gaussian elimination elsewhere
/// (<c>OddCyclicIncidence.ComponentRank</c> and the duality layer's internal field echelon among them); what this type
/// owns is integer elimination, and a second copy of that is what must not appear.
/// </para>
/// <para>
/// <b>It is self-proving.</b> The triple is its own certificate — <c>U·A·V = D</c> is an identity a caller can check
/// with nothing but integer arithmetic — and <see cref="TryReduce"/> checks it, along with the two-sided inverses of
/// both transforms and the divisibility chain, BEFORE returning. A reduction that fails its own certificate is refused
/// with an exception rather than returned unproved, because such a failure is a defect in this type and not a fact
/// about the matrix. <see cref="Verify"/> re-runs the same check on demand.
/// </para>
/// <para>
/// <b>It is division-free in the sense that matters.</b> Every step is a unimodular row or column operation — a swap, a
/// negation, or the addition of an integer multiple of one line to another — so no entry is ever divided and every
/// intermediate is an exact integer. The textbook step that scales a row by the inverse of its pivot, which needs exact
/// divisibility and leaves the integers when it does not hold, appears nowhere. A Euclidean quotient is computed, but
/// only as the multiplier of an addition, never as a value written into the matrix.
/// </para>
/// <para>
/// <b>The pivot rule, which is part of the contract.</b> At each diagonal stage the reduction takes the nonzero entry
/// of SMALLEST absolute value in the remaining submatrix, breaking ties by the smallest row and then the smallest
/// column, and swaps it onto the diagonal. It then clears the pivot's column and row by division with remainder,
/// swapping any nonzero remainder onto the diagonal as the new pivot, until both are zero; then, if the pivot does not
/// divide some entry of the remaining submatrix, it adds that entry's row to the pivot row and repeats, which drives
/// the pivot down to the greatest common divisor and is what produces the divisibility chain. The rule is total and
/// value-determined, so the same matrix always yields the same triple, on every run and every machine.
/// </para>
/// <para>
/// <b>The bound is a memory bound, and it is honest.</b> <see cref="PeakMagnitudeBits"/> reports the largest bit length
/// any entry of the working matrix or of the four transforms reached. Coefficient growth here is real and large: ten by
/// ten with every entry below ten in magnitude already drives intermediates into the thousands of bits, and the law
/// suite pins that rather than describing it. The smallest-pivot rule CONTAINS that growth where it bites — measured
/// against a first-nonzero rule on such a family, it stays below on seven members of eight — but it neither eliminates
/// growth nor wins uniformly, and on small matrices it can cost a few bits over a naive rule. Growth is a property of
/// the matrix and not only of the rule, which is why the ceiling is a parameter and why exceeding it is a refusal
/// carrying <see cref="SmithObstruction"/> rather than a slower and larger grind.
/// </para>
/// <para>
/// Instances are immutable and therefore shareable across threads.
/// </para>
/// </remarks>
public sealed class SmithNormalForm {
    // The widest ceiling a caller may ask for. Beyond this a single entry is a megabyte-scale integer and a transform
    // is that squared, so the promise the ceiling exists to make — that the attempt is finite in MEMORY — stops being
    // one. It is a constant rather than two copies of a literal because the homology readouts forward the same bound.
    internal const int MaximumMagnitudeBits = 65536;

    private readonly int m_columnCount;
    private readonly BigInteger[] m_divisors;
    private readonly BigInteger[] m_left;
    private readonly BigInteger[] m_leftInverse;
    private readonly BigInteger[] m_original;
    private readonly int m_peakMagnitudeBits;
    private readonly BigInteger[] m_right;
    private readonly BigInteger[] m_rightInverse;
    private readonly int m_rowCount;

    private SmithNormalForm(Reduction reduction) {
        m_columnCount = reduction.ColumnCount;
        m_divisors = reduction.Divisors();
        m_left = reduction.Left;
        m_leftInverse = reduction.LeftInverse;
        m_original = reduction.Original;
        m_peakMagnitudeBits = reduction.PeakMagnitudeBits;
        m_right = reduction.Right;
        m_rightInverse = reduction.RightInverse;
        m_rowCount = reduction.RowCount;
    }

    /// <summary>Gets the largest number of rows or columns a matrix may carry, which is 128.</summary>
    /// <remarks>It is the presented algebra's own basis cap read through this type's inputs: a boundary operator here
    /// is indexed by cells of one degree of a complex whose face order fits <c>IncidenceAlgebra</c>, and the four
    /// transforms a reduction accumulates are the order squared in arbitrary-precision integers, so the cap bounds
    /// memory quadratically in exactly the quantity the magnitude ceiling bounds linearly.</remarks>
    public static int MaximumOrder => 128;

    /// <summary>Gets the number of columns of the reduced matrix.</summary>
    public int ColumnCount => m_columnCount;
    /// <summary>Gets the elementary divisors — the nonzero diagonal of <c>D</c>, every one positive and each dividing
    /// the next.</summary>
    public ReadOnlySpan<BigInteger> Divisors => m_divisors;
    /// <summary>Gets the largest bit length any entry of the working matrix or of the four transforms reached during
    /// the reduction.</summary>
    /// <remarks>It is the number the ceiling bounds, reported so a caller can see how much headroom a reduction
    /// actually used rather than guess at one.</remarks>
    public int PeakMagnitudeBits => m_peakMagnitudeBits;
    /// <summary>Gets the rank of the matrix over the rationals, which is the number of elementary divisors.</summary>
    public int Rank => m_divisors.Length;
    /// <summary>Gets the number of rows of the reduced matrix.</summary>
    public int RowCount => m_rowCount;

    /// <summary>Attempts the elementary-divisor reduction of an integer matrix.</summary>
    /// <param name="entries">The matrix, row-major, <paramref name="rowCount"/> times <paramref name="columnCount"/>
    /// entries.</param>
    /// <param name="rowCount">The number of rows, from one through <see cref="MaximumOrder"/>.</param>
    /// <param name="columnCount">The number of columns, from one through <see cref="MaximumOrder"/>.</param>
    /// <param name="magnitudeBits">The ceiling, in bits: the reduction refuses as soon as an entry of the working
    /// matrix or of a transform needs more than this many bits to hold its absolute value.</param>
    /// <param name="form">On success, the certified reduction.</param>
    /// <param name="obstruction">On failure, the stage the reduction had reached and the magnitude that stopped it.</param>
    /// <returns><see langword="true"/> when the reduction finished inside the ceiling and its certificate verified;
    /// otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="entries"/> does not hold exactly
    /// <paramref name="rowCount"/> times <paramref name="columnCount"/> entries.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside one through <see cref="MaximumOrder"/>, or
    /// <paramref name="magnitudeBits"/> is outside one through 65,536.</exception>
    /// <exception cref="InvalidOperationException">The reduction finished but its own certificate did not verify. That
    /// is a defect in this type rather than a fact about the matrix, so it is raised rather than reported.</exception>
    public static bool TryReduce(ReadOnlySpan<BigInteger> entries, int rowCount, int columnCount, int magnitudeBits, out SmithNormalForm form, out SmithObstruction obstruction) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: rowCount, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: rowCount, other: MaximumOrder);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: columnCount, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: columnCount, other: MaximumOrder);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: magnitudeBits, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: magnitudeBits, other: MaximumMagnitudeBits);

        if (entries.Length != (rowCount * columnCount)) {
            throw new ArgumentException(message: "A matrix carries exactly one entry per row and column.", paramName: nameof(entries));
        }

        var reduction = new Reduction(entries: entries, rowCount: rowCount, columnCount: columnCount, magnitudeBits: magnitudeBits);

        form = null!;

        if (!reduction.TryRun(obstruction: out obstruction)) { return false; }

        form = new(reduction: reduction);

        if (!form.Verify()) {
            throw new InvalidOperationException(message: "An elementary-divisor reduction failed its own certificate, so no triple is returned.");
        }

        return true;
    }

    /// <summary>Reads one entry of the left transform <c>U</c>, which is square of order <see cref="RowCount"/>.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate leaves the transform.</exception>
    public BigInteger Left(int row, int column) =>
        Read(matrix: m_left, order: m_rowCount, row: row, column: column);

    /// <summary>Reads one entry of the inverse of the left transform.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate leaves the transform.</exception>
    /// <remarks>It is accumulated beside <see cref="Left"/> rather than solved for afterwards, which is what makes
    /// unimodularity a checked identity — <c>U·U⁻¹ = I</c> over the integers — instead of a determinant to trust.</remarks>
    public BigInteger LeftInverse(int row, int column) =>
        Read(matrix: m_leftInverse, order: m_rowCount, row: row, column: column);

    /// <summary>Reads one entry of the right transform <c>V</c>, which is square of order <see cref="ColumnCount"/>.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate leaves the transform.</exception>
    public BigInteger Right(int row, int column) =>
        Read(matrix: m_right, order: m_columnCount, row: row, column: column);

    /// <summary>Reads one entry of the inverse of the right transform.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate leaves the transform.</exception>
    public BigInteger RightInverse(int row, int column) =>
        Read(matrix: m_rightInverse, order: m_columnCount, row: row, column: column);

    /// <summary>Re-checks the whole certificate: the divisibility chain, both transforms' two-sided inverses, and the
    /// identity <c>U·A·V = D</c>.</summary>
    /// <returns><see langword="true"/> when every statement held; otherwise <see langword="false"/>.</returns>
    /// <remarks>It is the same check <see cref="TryReduce"/> runs before returning, kept public because the certificate
    /// is the point: nothing here asks to be trusted, and re-multiplying is cheaper than reading the reduction.</remarks>
    public bool Verify() {
        for (var index = 0; (index < m_divisors.Length); ++index) {
            if (m_divisors[index] <= BigInteger.Zero) { return false; }

            if ((0 != index) && !BigInteger.Zero.Equals(other: BigInteger.Remainder(dividend: m_divisors[index], divisor: m_divisors[(index - 1)]))) { return false; }
        }

        if (!IsIdentityProduct(left: m_left, right: m_leftInverse, order: m_rowCount)) { return false; }

        if (!IsIdentityProduct(left: m_leftInverse, right: m_left, order: m_rowCount)) { return false; }

        if (!IsIdentityProduct(left: m_right, right: m_rightInverse, order: m_columnCount)) { return false; }

        if (!IsIdentityProduct(left: m_rightInverse, right: m_right, order: m_columnCount)) { return false; }

        // U·A, then that by V, against the diagonal the divisors name. Nothing is cached: the identity is recomputed
        // from the three matrices every time it is asked for.
        var staged = new BigInteger[(m_rowCount * m_columnCount)];

        for (var row = 0; (row < m_rowCount); ++row) {
            for (var column = 0; (column < m_columnCount); ++column) {
                var total = BigInteger.Zero;

                for (var index = 0; (index < m_rowCount); ++index) {
                    total += (m_left[((row * m_rowCount) + index)] * m_original[((index * m_columnCount) + column)]);
                }

                staged[((row * m_columnCount) + column)] = total;
            }
        }

        for (var row = 0; (row < m_rowCount); ++row) {
            for (var column = 0; (column < m_columnCount); ++column) {
                var total = BigInteger.Zero;

                for (var index = 0; (index < m_columnCount); ++index) {
                    total += (staged[((row * m_columnCount) + index)] * m_right[((index * m_columnCount) + column)]);
                }

                var expected = (((row == column) && (row < m_divisors.Length)) ? m_divisors[row] : BigInteger.Zero);

                if (total != expected) { return false; }
            }
        }

        return true;
    }

    private static bool IsIdentityProduct(BigInteger[] left, BigInteger[] right, int order) {
        for (var row = 0; (row < order); ++row) {
            for (var column = 0; (column < order); ++column) {
                var total = BigInteger.Zero;

                for (var index = 0; (index < order); ++index) {
                    total += (left[((row * order) + index)] * right[((index * order) + column)]);
                }

                if (total != ((row == column) ? BigInteger.One : BigInteger.Zero)) { return false; }
            }
        }

        return true;
    }
    private static BigInteger Read(BigInteger[] matrix, int order, int row, int column) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: row, other: order);
        ArgumentOutOfRangeException.ThrowIfNegative(value: column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: column, other: order);

        return matrix[((row * order) + column)];
    }

    // The reduction itself. It owns the five arrays and every write to them goes through one magnitude observation, so
    // the ceiling covers the transforms as well as the working matrix and there is one place a peak is recorded.
    private sealed class Reduction {
        private readonly int m_magnitudeBits;
        private readonly BigInteger[] m_matrix;
        private int m_blockedBits;
        private bool m_refused;
        private int m_stage;

        internal Reduction(ReadOnlySpan<BigInteger> entries, int rowCount, int columnCount, int magnitudeBits) {
            ColumnCount = columnCount;
            Left = Identity(order: rowCount);
            LeftInverse = Identity(order: rowCount);
            Original = entries.ToArray();
            Right = Identity(order: columnCount);
            RightInverse = Identity(order: columnCount);
            RowCount = rowCount;
            m_magnitudeBits = magnitudeBits;
            m_matrix = entries.ToArray();
        }

        internal int ColumnCount { get; }
        internal BigInteger[] Left { get; }
        internal BigInteger[] LeftInverse { get; }
        internal BigInteger[] Original { get; }
        internal int PeakMagnitudeBits { get; private set; }
        internal BigInteger[] Right { get; }
        internal BigInteger[] RightInverse { get; }
        internal int RowCount { get; }
        internal long StepsTaken { get; private set; }

        internal BigInteger[] Divisors() {
            var order = Math.Min(val1: RowCount, val2: ColumnCount);
            var count = 0;

            while ((count < order) && !m_matrix[((count * ColumnCount) + count)].IsZero) { ++count; }

            var divisors = new BigInteger[count];

            for (var index = 0; (index < count); ++index) { divisors[index] = m_matrix[((index * ColumnCount) + index)]; }

            return divisors;
        }
        internal bool TryRun(out SmithObstruction obstruction) {
            var order = Math.Min(val1: RowCount, val2: ColumnCount);

            obstruction = default;

            // The declared entries are held to the same ceiling as every intermediate, so a matrix that could not be
            // reduced inside the budget is refused before any work is done rather than after.
            for (var index = 0; (index < m_matrix.Length); ++index) { Observe(value: m_matrix[index]); }

            // The stage counter is advanced only by a stage that FINISHED, so a refusal reports the diagonal position
            // it was working on rather than the next one. A `for` whose increment runs before the guard reported one
            // too many.
            while ((m_stage < order) && !m_refused) {
                if (!TryChoosePivot(pivotRow: out var pivotRow, pivotColumn: out var pivotColumn)) { break; }

                SwapRows(first: m_stage, second: pivotRow);
                SwapColumns(first: m_stage, second: pivotColumn);
                FixPivot();

                if (m_refused) { break; }

                ++m_stage;
            }

            if (m_refused) {
                obstruction = new(Stage: m_stage, MagnitudeBits: m_blockedBits, StepsTaken: StepsTaken);

                return false;
            }

            return true;
        }

        private static BigInteger[] Identity(int order) {
            var matrix = new BigInteger[(order * order)];

            for (var index = 0; (index < order); ++index) { matrix[((index * order) + index)] = BigInteger.One; }

            return matrix;
        }

        // Column j += factor * column k, in the working matrix and in the right transform, with the inverse taking the
        // matching row operation from the other side.
        private void AddColumn(int target, int source, BigInteger factor) {
            if (factor.IsZero) { return; }

            for (var row = 0; (row < RowCount); ++row) {
                Write(matrix: m_matrix, index: ((row * ColumnCount) + target), value: (m_matrix[((row * ColumnCount) + target)] + (factor * m_matrix[((row * ColumnCount) + source)])));
            }

            for (var row = 0; (row < ColumnCount); ++row) {
                Write(matrix: Right, index: ((row * ColumnCount) + target), value: (Right[((row * ColumnCount) + target)] + (factor * Right[((row * ColumnCount) + source)])));
            }

            for (var column = 0; (column < ColumnCount); ++column) {
                Write(matrix: RightInverse, index: ((source * ColumnCount) + column), value: (RightInverse[((source * ColumnCount) + column)] - (factor * RightInverse[((target * ColumnCount) + column)])));
            }

            ++StepsTaken;
        }

        // Row i += factor * row k, in the working matrix and in the left transform, with the inverse taking the
        // matching column operation from the other side.
        private void AddRow(int target, int source, BigInteger factor) {
            if (factor.IsZero) { return; }

            for (var column = 0; (column < ColumnCount); ++column) {
                Write(matrix: m_matrix, index: ((target * ColumnCount) + column), value: (m_matrix[((target * ColumnCount) + column)] + (factor * m_matrix[((source * ColumnCount) + column)])));
            }

            for (var column = 0; (column < RowCount); ++column) {
                Write(matrix: Left, index: ((target * RowCount) + column), value: (Left[((target * RowCount) + column)] + (factor * Left[((source * RowCount) + column)])));
            }

            for (var row = 0; (row < RowCount); ++row) {
                Write(matrix: LeftInverse, index: ((row * RowCount) + source), value: (LeftInverse[((row * RowCount) + source)] - (factor * LeftInverse[((row * RowCount) + target)])));
            }

            ++StepsTaken;
        }

        // The whole stage: drive the pivot down to the greatest common divisor of its row, its column and — through the
        // divisibility repair — the entire remaining submatrix, then leave it positive.
        private void FixPivot() {
            while (!m_refused) {
                if (!ClearColumn()) { continue; }

                if (!ClearRow()) { continue; }

                // A clearing pass that aborted on the ceiling still reports itself clean, since it wrote no nonzero
                // remainder to swap up. Without this the repair below would apply one more unimodular step after the
                // refusal and inflate both the step count and the peak.
                if (m_refused) { return; }

                if (RepairDivisibility()) { continue; }

                if (m_matrix[((m_stage * ColumnCount) + m_stage)].Sign < 0) { NegateRow(row: m_stage); }

                return;
            }
        }

        // Clears the pivot's column below the diagonal. A nonzero remainder is smaller than the pivot, so swapping it
        // up strictly decreases the pivot's absolute value — which is why the surrounding loop terminates.
        private bool ClearColumn() {
            var clean = true;

            for (var row = (m_stage + 1); ((row < RowCount) && !m_refused); ++row) {
                var entry = m_matrix[((row * ColumnCount) + m_stage)];

                if (entry.IsZero) { continue; }

                AddRow(target: row, source: m_stage, factor: -BigInteger.Divide(dividend: entry, divisor: m_matrix[((m_stage * ColumnCount) + m_stage)]));

                if (!m_matrix[((row * ColumnCount) + m_stage)].IsZero) {
                    SwapRows(first: m_stage, second: row);

                    clean = false;
                }
            }

            return clean;
        }

        // The mirror image, clearing the pivot's row to the right of the diagonal.
        private bool ClearRow() {
            var clean = true;

            for (var column = (m_stage + 1); ((column < ColumnCount) && !m_refused); ++column) {
                var entry = m_matrix[((m_stage * ColumnCount) + column)];

                if (entry.IsZero) { continue; }

                AddColumn(target: column, source: m_stage, factor: -BigInteger.Divide(dividend: entry, divisor: m_matrix[((m_stage * ColumnCount) + m_stage)]));

                if (!m_matrix[((m_stage * ColumnCount) + column)].IsZero) {
                    SwapColumns(first: m_stage, second: column);

                    clean = false;
                }
            }

            return clean;
        }
        private void NegateRow(int row) {
            for (var column = 0; (column < ColumnCount); ++column) {
                Write(matrix: m_matrix, index: ((row * ColumnCount) + column), value: -m_matrix[((row * ColumnCount) + column)]);
            }

            for (var column = 0; (column < RowCount); ++column) {
                Write(matrix: Left, index: ((row * RowCount) + column), value: -Left[((row * RowCount) + column)]);
            }

            for (var index = 0; (index < RowCount); ++index) {
                Write(matrix: LeftInverse, index: ((index * RowCount) + row), value: -LeftInverse[((index * RowCount) + row)]);
            }

            ++StepsTaken;
        }

        // A clearing pass writes a whole line before the caller can see the refusal, so later writes of the same pass
        // are still observed. The FIRST breach is the one that stopped the reduction, so it is the one recorded; a
        // plain assignment let a smaller later breach overwrite it and understate the magnitude.
        private void Observe(BigInteger value) {
            var bits = ((int)BigInteger.Abs(value: value).GetBitLength());

            if (bits > PeakMagnitudeBits) { PeakMagnitudeBits = bits; }

            if ((bits > m_magnitudeBits) && !m_refused) {
                m_blockedBits = bits;
                m_refused = true;
            }
        }

        // The step that makes the diagonal a chain: an entry the pivot does not divide is folded into the pivot row, so
        // the next clearing pass drives the pivot down to a proper divisor of itself.
        private bool RepairDivisibility() {
            var pivot = m_matrix[((m_stage * ColumnCount) + m_stage)];

            for (var row = (m_stage + 1); (row < RowCount); ++row) {
                for (var column = (m_stage + 1); (column < ColumnCount); ++column) {
                    if (BigInteger.Remainder(dividend: m_matrix[((row * ColumnCount) + column)], divisor: pivot).IsZero) { continue; }

                    AddRow(target: m_stage, source: row, factor: BigInteger.One);

                    return true;
                }
            }

            return false;
        }
        private void SwapColumns(int first, int second) {
            if (first == second) { return; }

            for (var row = 0; (row < RowCount); ++row) {
                (m_matrix[((row * ColumnCount) + first)], m_matrix[((row * ColumnCount) + second)]) = (m_matrix[((row * ColumnCount) + second)], m_matrix[((row * ColumnCount) + first)]);
            }

            for (var row = 0; (row < ColumnCount); ++row) {
                (Right[((row * ColumnCount) + first)], Right[((row * ColumnCount) + second)]) = (Right[((row * ColumnCount) + second)], Right[((row * ColumnCount) + first)]);
            }

            for (var column = 0; (column < ColumnCount); ++column) {
                (RightInverse[((first * ColumnCount) + column)], RightInverse[((second * ColumnCount) + column)]) = (RightInverse[((second * ColumnCount) + column)], RightInverse[((first * ColumnCount) + column)]);
            }

            ++StepsTaken;
        }
        private void SwapRows(int first, int second) {
            if (first == second) { return; }

            for (var column = 0; (column < ColumnCount); ++column) {
                (m_matrix[((first * ColumnCount) + column)], m_matrix[((second * ColumnCount) + column)]) = (m_matrix[((second * ColumnCount) + column)], m_matrix[((first * ColumnCount) + column)]);
            }

            for (var column = 0; (column < RowCount); ++column) {
                (Left[((first * RowCount) + column)], Left[((second * RowCount) + column)]) = (Left[((second * RowCount) + column)], Left[((first * RowCount) + column)]);
            }

            for (var row = 0; (row < RowCount); ++row) {
                (LeftInverse[((row * RowCount) + first)], LeftInverse[((row * RowCount) + second)]) = (LeftInverse[((row * RowCount) + second)], LeftInverse[((row * RowCount) + first)]);
            }

            ++StepsTaken;
        }

        // The pivot rule: the nonzero entry of smallest absolute value in the remaining submatrix, ties broken by the
        // smallest row and then the smallest column. Row-major iteration IS that tie-break.
        private bool TryChoosePivot(out int pivotRow, out int pivotColumn) {
            var best = BigInteger.Zero;

            pivotColumn = -1;
            pivotRow = -1;

            for (var row = m_stage; (row < RowCount); ++row) {
                for (var column = m_stage; (column < ColumnCount); ++column) {
                    var entry = m_matrix[((row * ColumnCount) + column)];

                    if (entry.IsZero) { continue; }

                    var magnitude = BigInteger.Abs(value: entry);

                    if ((pivotRow >= 0) && (magnitude >= best)) { continue; }

                    best = magnitude;
                    pivotColumn = column;
                    pivotRow = row;
                }
            }

            return (pivotRow >= 0);
        }
        private void Write(BigInteger[] matrix, int index, BigInteger value) {
            matrix[index] = value;

            Observe(value: value);
        }
    }
}
