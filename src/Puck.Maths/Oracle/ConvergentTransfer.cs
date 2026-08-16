namespace Puck.Maths;

/// <summary>
/// The transfer functor of a continued fraction: a word of partial quotients evaluated through the codiscrete quiver on
/// two objects, which is the two-by-two matrix algebra, so the convergent recurrence is a module run and not a
/// hand-rolled matrix product.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// A partial quotient <c>a</c> becomes the element <c>[[a, 1], [1, 0]]</c>, and a word becomes the ordered product of
/// its digits under <see cref="PresentedAlgebra{TValue, TOps}.Multiply"/>. Nothing else happens: the quiver's
/// composition rule — an arrow times an arrow whose source matches its target — is matrix multiplication exactly, and
/// the mismatch is the charge-zero annihilation, so this type carries no arithmetic of its own.
/// </para>
/// <para>
/// <b>Four copies of this product are open-coded in the tree</b> — in the quadratic inflation lens, the quasicrystal
/// index, the Ostrowski numeration system, and the Sturmian return spectrum. Three of them fold left to right, so
/// <see cref="Evaluate"/> reproduces them entry for entry; the Ostrowski one folds right to left, so it reproduces the
/// transpose, which is the same value because every digit element is symmetric.
/// </para>
/// <para>
/// <b>Which entry is which.</b> The left-to-right fold of <c>[a₀, …, a_n]</c> is
/// <c>[[p_n, p_{n−1}], [q_n, q_{n−1}]]</c>: the first row carries the convergent numerators and the second row the
/// denominators, so <see cref="Entry"/> at <c>(0, 0)</c> and <c>(0, 1)</c> answers the numerator recurrence and
/// <c>(1, 0)</c>/<c>(1, 1)</c> the denominator one. On <c>[1, 2, 2, 2]</c> that reads <c>[[17, 7], [12, 5]]</c>
/// against the convergents <c>1/1, 3/2, 7/5, 17/12</c>. A word given without its integer part — the Ostrowski
/// convention, where the digits are <c>a₁</c> onwards — shifts the reading by one, so its first row carries the
/// denominators <c>q</c> of the full expansion; that is the same matrix read against a different expansion, not a
/// second convention in this type.
/// </para>
/// <para>
/// Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not.
/// </para>
/// </remarks>
public sealed class ConvergentTransfer<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private ConvergentTransfer(PresentedAlgebra<TValue, TOps> algebra) {
        Algebra = algebra;
    }

    /// <summary>Gets the presented algebra of the codiscrete quiver on two objects — the two-by-two matrices.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra { get; }

    // The quiver key of a two-by-two cell, and the one place the coordinate range is decided.
    private static long CellKey(int row, int column) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: row);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: row,
            other: 1
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: column);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: column,
            other: 1
        );

        return ((row * 2L) + column);
    }

    /// <summary>Creates the transfer functor over a material.</summary>
    /// <param name="material">The material.</param>
    /// <returns>The described functor.</returns>
    public static ConvergentTransfer<TValue, TOps> Create(TOps material) {
        var one = material.One;

        return new(algebra: PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.Quiver<TValue, TOps>(
            arrows: [(0, 0, one), (0, 1, one), (1, 0, one), (1, 1, one)],
            material: material,
            objectCount: 2
        )));
    }
    /// <summary>Returns the digit element <c>[[a, 1], [1, 0]]</c> of one partial quotient.</summary>
    /// <param name="partialQuotient">The partial quotient.</param>
    /// <returns>The element.</returns>
    public PresentedAlgebra<TValue, TOps>.Element Digit(TValue partialQuotient) {
        var one = Algebra.Presentation.Material.One;

        return Algebra.FromSupport(
            coefficients: [partialQuotient, one, one],
            keys: [0L, 1L, 2L]
        );
    }
    /// <summary>Returns one entry of a transfer element.</summary>
    /// <param name="value">The element.</param>
    /// <param name="row">The row, zero or one.</param>
    /// <param name="column">The column, zero or one.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentException">The element belongs to another transfer algebra.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside zero through one.</exception>
    public TValue Entry(in PresentedAlgebra<TValue, TOps>.Element value, int row, int column) {
        Algebra.RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        var key = CellKey(
            column: column,
            row: row
        );

        return ((0 == value.SupportCount)
            ? Algebra.Presentation.Material.Zero
            : value[key]
        );
    }
    /// <summary>Evaluates a word of partial quotients into its transfer element.</summary>
    /// <param name="partialQuotients">The partial quotients, in the order they are composed.</param>
    /// <returns>The ordered product of the digit elements, left to right, starting from the unit.</returns>
    public PresentedAlgebra<TValue, TOps>.Element Evaluate(ReadOnlySpan<TValue> partialQuotients) {
        var result = Algebra.Identity;

        foreach (var quotient in partialQuotients) {
            result = Algebra.Multiply(
                left: result,
                right: Digit(partialQuotient: quotient)
            );
        }

        return result;
    }
    /// <summary>Runs a word of partial quotients as a module over this algebra and reads one entry out.</summary>
    /// <param name="partialQuotients">The partial quotients, in the order they are applied.</param>
    /// <param name="row">The readout row, zero or one.</param>
    /// <param name="column">The readout column, zero or one.</param>
    /// <returns>The entry the run reaches.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside zero through one.</exception>
    /// <remarks>The state is the unit, each step is one digit element, and the readout is the basis covector of the
    /// requested cell, so the value equals <see cref="Entry"/> of <see cref="Evaluate"/> at every word. That equality is
    /// the point: the machine adds nothing, it only names what the product already was.</remarks>
    public TValue Run(ReadOnlySpan<TValue> partialQuotients, int row, int column) {
        var readoutKey = CellKey(
            column: column,
            row: row
        );
        var steps = new PresentedAlgebra<TValue, TOps>.Element[partialQuotients.Length];
        var word = new int[partialQuotients.Length];

        for (var index = 0; (index < steps.Length); ++index) {
            steps[index] = Digit(partialQuotient: partialQuotients[index]);
            word[index] = index;
        }

        return PresentedMachine<TValue, TOps>.Create(
            algebra: Algebra,
            initial: Algebra.Identity,
            steps: steps,
            readout: Algebra.FromSupport(
                keys: [readoutKey],
                coefficients: [Algebra.Presentation.Material.One]
            )
        ).Run(word: word);
    }
}
