namespace Puck.Maths;

/// <summary>The refusal of an exact solve: the basis column that found no unit pivot, and the rank reached before it.</summary>
/// <param name="BlockedKey">The normal-form key of the column that could not be pivoted — the non-unit witness.</param>
/// <param name="RankReached">The number of columns pivoted before the block, which is the rank of the divisor's action.</param>
public readonly record struct DivisionObstruction(long BlockedKey, long RankReached);
public sealed partial class PresentedAlgebra<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    /// <summary>Attempts to divide exactly: finds the element that the divisor multiplies into the target.</summary>
    /// <param name="divisor">The left factor.</param>
    /// <param name="target">The product to reach.</param>
    /// <param name="quotient">On success, the unique element with <c>divisor · quotient == target</c>.</param>
    /// <param name="obstruction">On failure, the column that found no unit pivot and the rank reached.</param>
    /// <returns><see langword="true"/> when the divisor's action is invertible; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another algebra.</exception>
    /// <exception cref="InvalidOperationException">The material is not a certified field, or the presentation has no
    /// finite basis to coordinatize.</exception>
    /// <remarks>
    /// <para>
    /// The algebra carries no <c>Divide</c> and no <c>Inverse</c>, deliberately, and this is why: division is not an
    /// operation of the object, it is a linear solve against the divisor's own action on the basis, and it succeeds
    /// exactly when that action has full rank. A divisor that is a zero divisor, or whose action is singular for any
    /// other reason, is refused with the offending column rather than approximated.
    /// </para>
    /// <para>
    /// <b>Exact-only.</b> Reduced row echelon over a rounding carrier accumulates a rounding per elimination step, so
    /// the returned element would not satisfy the equation it was solved from; the field gate is the type-level
    /// statement of that, since no rounding material of this library is a field.
    /// </para>
    /// <para>
    /// It is a left division: the solved equation is <c>divisor · quotient</c>, in that order. At a quiver presentation
    /// that is the matrix equation <c>A·X = B</c>, so the two sides are not interchangeable unless the algebra
    /// commutes.
    /// </para>
    /// </remarks>
    public bool TrySolve(in Element divisor, in Element target, out Element quotient, out DivisionObstruction obstruction) {
        RequireOwned(
            value: divisor,
            paramName: nameof(divisor)
        );
        RequireOwned(
            value: target,
            paramName: nameof(target)
        );

        var field = RequireFieldMaterial();
        var width = RequireFiniteWidth();
        var matrix = new TValue[width][];

        for (var row = 0; (row < width); ++row) {
            matrix[row] = new TValue[(width + 1)];

            Array.Fill(
                array: matrix[row],
                value: field.Zero
            );
        }

        for (var column = 0; (column < width); ++column) {
            var image = Multiply(
                left: divisor,
                right: BasisElement(key: column)
            );

            for (var index = 0; (index < image.SupportCount); ++index) {
                matrix[((int)image.Keys[index])][column] = image.Coefficients[index];
            }
        }

        for (var index = 0; (index < target.SupportCount); ++index) {
            matrix[((int)target.Keys[index])][width] = target.Coefficients[index];
        }

        obstruction = default;
        quotient = Zero;

        for (var column = 0; (column < width); ++column) {
            var pivot = -1;

            for (var row = column; (row < width); ++row) {
                if (!field.IsZero(value: matrix[row][column])) {
                    pivot = row;

                    break;
                }
            }

            if (
                (pivot < 0) ||
                !field.TryInvert(
                value: matrix[pivot][column],
                inverse: out var inverse
            )
            ) {
                obstruction = new(
                    BlockedKey: column,
                    RankReached: column
                );

                return false;
            }

            (matrix[column], matrix[pivot]) = (matrix[pivot], matrix[column]);

            for (var entry = column; (entry <= width); ++entry) {
                matrix[column][entry] = field.Multiply(
                    left: matrix[column][entry],
                    right: inverse
                );
            }

            for (var row = 0; (row < width); ++row) {
                if (row == column) { continue; }

                var factor = matrix[row][column];

                if (field.IsZero(value: factor)) { continue; }

                for (var entry = column; (entry <= width); ++entry) {
                    matrix[row][entry] = field.Subtract(
                        left: matrix[row][entry],
                        right: field.Multiply(
                            left: factor,
                            right: matrix[column][entry]
                        )
                    );
                }
            }
        }

        var coefficients = new TValue[width];
        var keys = new long[width];

        for (var index = 0; (index < width); ++index) {
            coefficients[index] = matrix[index][width];
            keys[index] = index;
        }

        quotient = FromSupport(
            coefficients: coefficients,
            keys: keys
        );

        return true;
    }
    /// <summary>Attempts to sum an element over all lengths by resolvent rather than by iteration — the exact
    /// <c>(1 − value)⁻¹</c>.</summary>
    /// <param name="value">The element to sum the powers of.</param>
    /// <param name="resolvent">On success, the total <c>1 + value + value² + …</c> as a closed form.</param>
    /// <param name="obstruction">On failure, <see cref="ClosureCertificate.FieldResolvent"/> with the column that
    /// blocked and the rank reached.</param>
    /// <returns><see langword="true"/> when the resolvent certificate was issued; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">The element belongs to another algebra.</exception>
    /// <exception cref="InvalidOperationException">The material is not a certified field, or the presentation has no
    /// finite basis.</exception>
    /// <remarks>
    /// <para>
    /// This is the <see cref="ClosureCertificate.FieldResolvent"/> path, and it is the certificate the iterative
    /// <see cref="TrySumOverAllLengths"/> cannot issue: a substochastic chain's powers neither reach zero nor
    /// stabilize, so iteration refuses forever while the resolvent answers in one solve. What it returns is not a
    /// truncation — it is the unique element whose product with <c>1 − value</c> is the unit, which
    /// <c>Multiply(Add(Identity, −value), resolvent)</c> re-checks exactly.
    /// </para>
    /// <para>
    /// The identity that ties it back to the sum is exact and worth testing:
    /// <c>resolvent − (1 + … + value^k) == value^(k+1)·resolvent</c>, so the resolvent is the sum whenever the sum
    /// converges, and is the analytic continuation of it whenever the sum does not.
    /// </para>
    /// </remarks>
    public bool TryResolvent(in Element value, out Element resolvent, out SumClosureObstruction obstruction) {
        RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        var divisor = Subtract(
            left: Identity,
            right: value
        );

        if (TrySolve(
            divisor: divisor,
            target: Identity,
            quotient: out resolvent,
            obstruction: out var division
        )) {
            obstruction = default;

            return true;
        }

        obstruction = new(
            Attempted: ClosureCertificate.FieldResolvent,
            SupportKey: division.BlockedKey,
            StepsTaken: division.RankReached
        );
        resolvent = Zero;

        return false;
    }

    private IFieldMaterial<TValue, TOps> RequireFieldMaterial() {
        if (m_material is not IFieldMaterial<TValue, TOps> field) {
            throw new InvalidOperationException(message: "An exact solve inverts coefficients, which a material that is not a certified field cannot do.");
        }

        return field;
    }
    private int RequireFiniteWidth() {
        if (!m_isDense) {
            throw new InvalidOperationException(message: "An exact solve needs a finite basis to coordinatize, which this presentation does not have.");
        }

        return m_keyCount;
    }
}
