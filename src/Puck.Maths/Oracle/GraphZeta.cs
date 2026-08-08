namespace Puck.Maths;

/// <summary>The index of the trace recursion whose divisor the material could not invert, carried as the index itself
/// rather than as a fault.</summary>
/// <param name="BlockedIndex">The smallest index of the trace recursion whose divisor <c>k · one</c> the material cannot
/// invert, or <c>-1</c> where none did. Division lives at <see cref="IFieldMaterial{TValue, TSelf}"/>, so a material
/// that certifies no inverses blocks at the recursion's first divisor, index one; a field whose characteristic is at or
/// below the order blocks at that characteristic.</param>
/// <param name="Order">The order the call was made at, so the refusal reads on its own.</param>
/// <remarks>A successful call returns <see cref="BlockedIndex"/> at <c>-1</c>, which reads unambiguously as "nothing
/// blocked": the recursion's indexes run from one.</remarks>
public readonly record struct ZetaObstruction(int BlockedIndex, int Order);

/// <summary>
/// The characteristic polynomial and the dynamical zeta of one element, read out of the algebra's own trace and powers.
/// At a quiver the element is a weighted adjacency matrix, the trace of its <c>m</c>-th power is the closed-walk count
/// at length <c>m</c>, and the two series this type carries are <c>det(I − tA)</c> and its reciprocal.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// <b>It adds no arithmetic.</b> The power sums are <see cref="PresentedAlgebra{TValue, TOps}.Trace"/> of
/// <see cref="PresentedAlgebra{TValue, TOps}.Power"/>, the coefficients come out of a bounded loop over them, and the
/// reciprocal is the shipped guarded sum at <see cref="Presentations.Shift"/>. There is no new division, no new star and
/// no second kernel: the one division is in the material, which is what the field licence below is for.
/// </para>
/// <para>
/// <b>The trace recursion, and why it needs a field.</b> Writing <c>det(I − tA) = Σ c_k t^k</c> and differentiating its
/// logarithm gives <c>k · c_k = −Σ_{i=1..k} p_i · c_{k−i}</c> with <c>c_0 = 1</c>, where <c>p_i</c> is the trace of the
/// <c>i</c>-th power. Every index up to the order is divided by, so the material must invert <c>k · one</c> for each of
/// them: at an <see cref="IFieldMaterial{TValue, TSelf}"/> of characteristic zero or above the order every such divisor
/// is a unit, and anywhere else the power sums simply do not determine the coefficient that index carries. A material
/// that is not a certified field is refused at index one, a field of characteristic <c>p</c> at or below the order at
/// index <c>p</c>, and the index is reported rather than described. This is a LIMIT and it is exact-only: over
/// <see cref="FixedQ4816"/> — which is not a field material — nothing is offered at all.
/// </para>
/// <para>
/// <b>The order is not a free argument.</b> The recursion's own <c>p_0</c> is the trace of the unit, so the order must
/// be the count the algebra's unit carries: <c>Trace(Identity)</c> is required to equal <c>order · one</c>, which at a
/// quiver on <c>n</c> objects means the order is <c>n</c> and nothing else. An order the unit does not count names a
/// polynomial this readout is not about, so it is refused at construction rather than answered wrongly. The order is
/// bounded above by the algebra's normal-form count for the same reason: the trace pairs the unit against itself and
/// reads at most one term per normal form, so no larger multiple of the one can be a count of diagonal cells.
/// </para>
/// <para>
/// <b>The zeta is the reciprocal, and its certificate is computed.</b> Inside
/// <see cref="Presentations.Shift"/><c>(degreeBound)</c> the characteristic polynomial is <c>1 + q</c> where <c>q</c>
/// carries no constant term, so <c>q</c> is nilpotent — its <c>(degreeBound + 1)</c>-st power is zero in the truncated
/// ring — and <c>1 / (1 + q)</c> is the star of <c>−q</c>, which
/// <see cref="PresentedAlgebra{TValue, TOps}.TrySumOverAllLengths"/> already computes under a
/// <see cref="ClosureCertificate.Nilpotent"/> certificate it issues rather than assumes. That vanishing power is
/// measured here too, so <see cref="Certificate"/> reports which certificate the star actually ran under instead of
/// naming the one it attempted. Where the degree bound is below the order the polynomial is truncated to the ring, and
/// the reciprocal is still exact there, because an inverse modulo <c>t^(d+1)</c> depends on nothing above that degree.
/// </para>
/// <para>
/// Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not: every product this type forms runs in
/// an algebra's own scratch.
/// </para>
/// </remarks>
public sealed class GraphZeta<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly TValue[] m_coefficients;
    private readonly TValue[] m_powerSums;

    private GraphZeta(
        PresentedAlgebra<TValue, TOps> algebra,
        in PresentedAlgebra<TValue, TOps>.Element value,
        int order,
        int degreeBound,
        IFieldMaterial<TValue, TOps> field,
        TValue[] inverses
    ) {
        var lane = algebra.Presentation.Lane;
        var material = algebra.Presentation.Material;

        Algebra = algebra;
        DegreeBound = degreeBound;
        Order = order;
        m_coefficients = new TValue[(order + 1)];
        m_powerSums = new TValue[(order + 1)];

        for (var length = 0; (length <= order); ++length) {
            m_powerSums[length] = algebra.Trace(value: algebra.Power(value: value, exponent: ((ulong)length)));
        }

        // The recursion, one degree at a time. The sum is a charged linear fold — the shape the material rounds exactly
        // once — so the coefficient carries one rounding over a rounding carrier and none at all over the exact ones
        // this licence admits.
        var charges = new TValue[order];
        var terms = new TValue[order];

        m_coefficients[0] = material.One;

        for (var degree = 1; (degree <= order); ++degree) {
            for (var index = 1; (index <= degree); ++index) {
                charges[(index - 1)] = m_powerSums[index];
                terms[(index - 1)] = m_coefficients[(degree - index)];
            }

            var total = material.FusedChargedLinear(
                charges: charges.AsSpan(start: 0, length: degree),
                values: terms.AsSpan(start: 0, length: degree),
                lane: lane
            );

            m_coefficients[degree] = material.Multiply(left: field.Negate(value: total), right: inverses[degree]);
        }

        var series = PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.Shift<TValue, TOps>(degreeBound: degreeBound, material: material));
        var width = Math.Min(val1: order, val2: degreeBound);
        var keys = new long[(width + 1)];

        for (var degree = 0; (degree <= width); ++degree) { keys[degree] = degree; }

        Series = series;
        CharacteristicPolynomial = series.FromSupport(keys: keys, coefficients: m_coefficients.AsSpan(start: 0, length: (width + 1)));

        // The reciprocal. Writing the polynomial as 1 + q, its inverse is the star of −q, and one subtraction from the
        // unit forms −q directly. Its nilpotence — which is what the star runs under, and which the guarded sum reports
        // only when it FAILS — is measured here instead of inferred.
        var augmentation = series.Subtract(left: series.Identity, right: CharacteristicPolynomial);
        var vanishes = (0 == series.Power(value: augmentation, exponent: ((ulong)(degreeBound + 1))).SupportCount);
        var summed = series.TrySumOverAllLengths(value: augmentation, total: out var reciprocal, obstruction: out _);

        Certificate = ((vanishes && summed)
            ? ClosureCertificate.Nilpotent
            : ClosureCertificate.None);
        DynamicalZeta = reciprocal;
    }

    /// <summary>Gets the algebra the element was read in.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra { get; }
    /// <summary>Gets the certificate the reciprocal's guarded sum ran under, measured rather than declared.</summary>
    /// <remarks>It is <see cref="ClosureCertificate.Nilpotent"/> at every characteristic polynomial, because the
    /// augmentation part of a series whose constant term is the material's one carries no constant term of its own.</remarks>
    public ClosureCertificate Certificate { get; }
    /// <summary>Gets the characteristic polynomial <c>det(I − tA)</c> as an element of <see cref="Series"/>, truncated
    /// to the degree bound.</summary>
    public PresentedAlgebra<TValue, TOps>.Element CharacteristicPolynomial { get; }
    /// <summary>Gets the highest surviving degree of <see cref="Series"/>.</summary>
    public int DegreeBound { get; }
    /// <summary>Gets the dynamical zeta <c>1 / det(I − tA)</c> as an element of <see cref="Series"/>.</summary>
    /// <remarks>It is the exact inverse of <see cref="CharacteristicPolynomial"/> in the truncated ring, so the two
    /// multiply to the unit there; read as a generating function its coefficient at <c>t^m</c> is the one the closed-walk
    /// counts exponentiate to.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element DynamicalZeta { get; }
    /// <summary>Gets the degree of the characteristic polynomial, which the algebra's unit trace pins.</summary>
    public int Order { get; }
    /// <summary>Gets the truncated jet algebra the two series live in.</summary>
    public PresentedAlgebra<TValue, TOps> Series { get; }

    /// <summary>Reads the characteristic polynomial and the dynamical zeta of one element.</summary>
    /// <param name="algebra">The algebra the element belongs to; it has a finite basis.</param>
    /// <param name="value">The element — at a quiver, the weighted adjacency matrix.</param>
    /// <param name="order">The degree of the characteristic polynomial; the algebra's unit trace must be that many
    /// ones.</param>
    /// <param name="degreeBound">The highest surviving degree of the series ring the two polynomials live in.</param>
    /// <param name="zeta">On success, the readout; otherwise <see langword="null"/>.</param>
    /// <param name="obstruction">On failure, the index of the trace recursion whose divisor the material cannot
    /// invert.</param>
    /// <returns><see langword="true"/> when the material can carry the whole recursion; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="algebra"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The algebra has no finite basis, the element belongs to another algebra, or
    /// the algebra's unit trace is not <paramref name="order"/> ones.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is below one or above the algebra's
    /// normal-form count, or <paramref name="degreeBound"/> is negative or above the bound
    /// <see cref="Presentations.Shift"/> admits.</exception>
    /// <remarks>The whole cost is paid here, once: <paramref name="order"/> powers and traces in the algebra, an
    /// order-squared fold over the material, and one truncated jet algebra whose star is bounded by its own degree.</remarks>
    public static bool TryCreate(
        PresentedAlgebra<TValue, TOps> algebra,
        in PresentedAlgebra<TValue, TOps>.Element value,
        int order,
        int degreeBound,
        out GraphZeta<TValue, TOps>? zeta,
        out ZetaObstruction obstruction
    ) {
        ArgumentNullException.ThrowIfNull(argument: algebra);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: order, other: 1);
        ArgumentOutOfRangeException.ThrowIfNegative(value: degreeBound);

        var presentation = algebra.Presentation;

        if (!presentation.HasCompiledNormalFormBasis) {
            throw new ArgumentException(
                message: "The characteristic polynomial is read off a finite trace form, which a presentation with no finite basis does not have.",
                paramName: nameof(algebra)
            );
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: order, other: presentation.NormalFormCount);

        algebra.RequireOwned(value: value, paramName: nameof(value));

        var inverses = new TValue[(order + 1)];
        var material = presentation.Material;
        var divisor = material.Zero;

        zeta = null;
        obstruction = new(BlockedIndex: -1, Order: order);

        // Division lives at a certified field, so a material that declares none cannot reach even the recursion's first
        // divisor, and index one is where it stops.
        if (material is not IFieldMaterial<TValue, TOps> field) {
            obstruction = new(BlockedIndex: 1, Order: order);

            return false;
        }

        for (var index = 1; (index <= order); ++index) {
            divisor = material.Add(left: divisor, right: material.One);

            if (!field.TryInvert(value: divisor, inverse: out inverses[index])) {
                obstruction = new(BlockedIndex: index, Order: order);

                return false;
            }
        }

        if (!EqualityComparer<TValue>.Default.Equals(x: algebra.Trace(value: algebra.Identity), y: divisor)) {
            throw new ArgumentException(
                message: "The trace of this algebra's unit is not the declared order's worth of ones, so the trace recursion would recover the coefficients of a polynomial this element does not have.",
                paramName: nameof(order)
            );
        }

        zeta = new GraphZeta<TValue, TOps>(algebra: algebra, value: value, order: order, degreeBound: degreeBound, field: field, inverses: inverses);

        return true;
    }

    /// <summary>Returns one coefficient of the characteristic polynomial.</summary>
    /// <param name="degree">The degree, from zero through <see cref="Order"/>.</param>
    /// <returns>The coefficient of <c>t^degree</c> in <c>det(I − tA)</c>; degree zero is the material's one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The degree is negative or above <see cref="Order"/>.</exception>
    /// <remarks>It is the full coefficient even where <see cref="DegreeBound"/> truncates
    /// <see cref="CharacteristicPolynomial"/>, since the recursion reaches every degree the order carries.</remarks>
    public TValue Coefficient(int degree) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: degree, other: Order);

        return m_coefficients[degree];
    }

    /// <summary>Returns one power sum: the trace of a power of the element.</summary>
    /// <param name="length">The power, from zero through <see cref="Order"/>.</param>
    /// <returns><c>Trace(Aˡ)</c>, which at a quiver is the number of closed walks of that length; length zero is the
    /// order's worth of ones.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative or above <see cref="Order"/>.</exception>
    public TValue PowerSum(int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: length, other: Order);

        return m_powerSums[length];
    }
}
