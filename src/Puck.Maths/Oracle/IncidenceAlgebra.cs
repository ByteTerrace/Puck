namespace Puck.Maths;

/// <summary>
/// The presented algebra of a finite partially ordered set, read as its incidence algebra: its keys are the intervals,
/// its product is convolution over the ways an interval factors through a middle element, and the Möbius function of
/// the order is an element of it rather than a recursion beside it.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// It contributes no arithmetic. Zeta is the coefficient one at every interval; the Möbius element is the sum over all
/// lengths of the negated strict part of zeta; and every readout is a pairing. Each is one call into
/// <see cref="PresentedAlgebra{TValue, TOps}"/>, so a recursion over intervals appears nowhere — which is the whole
/// claim, since a Möbius recursion written here would be a second kernel.
/// </para>
/// <para>
/// <b>Keys are intervals, not elements.</b> A key is the interval's index in ascending <c>(lower, upper)</c> order, so
/// the singleton intervals are scattered through it rather than grouped. <see cref="Interval"/> and
/// <see cref="TryKey"/> are the map; neither is a hash, since the lookup is a dense table indexed by the two
/// endpoints.
/// </para>
/// <para>
/// <b>The order's height is the closure certificate.</b> The strict part of zeta carries no singleton interval, so its
/// <c>k</c>-th power carries only intervals with a chain of <c>k</c> strict steps, and a finite order runs out of
/// those: the sum over all lengths terminates and <see cref="ClosureCertificate.Nilpotent"/> is issued, computed
/// rather than assumed. That is the same certificate the divisibility window earns from its bound.
/// </para>
/// <para>
/// Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not: the presentation underneath is
/// immutable and shareable, so give each thread its own algebra.
/// </para>
/// </remarks>
public sealed class IncidenceAlgebra<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly int[] m_lower;
    private readonly ISignedMaterial<TValue, TOps>? m_signed;
    private readonly long[] m_symbolOf;
    private readonly int[] m_upper;

    private IncidenceAlgebra(PresentedAlgebra<TValue, TOps> algebra, int elementCount, int[] lower, int[] upper, long[] symbolOf) {
        var count = lower.Length;
        var material = algebra.Presentation.Material;
        var everyKey = new long[count];
        var ones = new TValue[count];

        for (var key = 0; (key < count); ++key) {
            everyKey[key] = key;
            ones[key] = material.One;
        }

        Algebra = algebra;
        ElementCount = elementCount;
        IntervalCount = count;
        Zeta = algebra.FromSupport(
            coefficients: ones,
            keys: everyKey
        );
        m_lower = lower;
        m_signed = (material as ISignedMaterial<TValue, TOps>);
        m_symbolOf = symbolOf;
        m_upper = upper;
    }

    /// <summary>Gets the presented algebra whose product is convolution over the factorizations of an interval.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra { get; }
    /// <summary>Gets the number of elements the order is on.</summary>
    public int ElementCount { get; }
    /// <summary>Gets the number of intervals, which is the number of keys.</summary>
    public int IntervalCount { get; }
    /// <summary>Gets the zeta element — the coefficient one at every interval of the order.</summary>
    public PresentedAlgebra<TValue, TOps>.Element Zeta { get; }

    private ISignedMaterial<TValue, TOps> RequireSigned() =>
        (m_signed ?? throw new InvalidOperationException(message: "Möbius inversion alternates in sign, which an unsigned material cannot express."));

    /// <summary>Creates the incidence algebra of a finite order.</summary>
    /// <param name="elementCount">The number of elements, from one through 256.</param>
    /// <param name="relations">Pairs <c>(lower, upper)</c> generating the strict order; they need not be transitively
    /// closed, so a covering relation is enough.</param>
    /// <param name="material">The material.</param>
    /// <returns>The described algebra.</returns>
    /// <exception cref="ArgumentException">A relation leaves the element range, or the relations close into a cycle.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elementCount"/> is outside one through 256, or the
    /// order has more than 256 intervals.</exception>
    public static IncidenceAlgebra<TValue, TOps> Create(int elementCount, ReadOnlySpan<(int Lower, int Upper)> relations, TOps material) {
        var presentation = Presentations.IntervalPoset<TValue, TOps>(
            elementCount: elementCount,
            material: material,
            relations: relations
        );
        var count = presentation.NormalFormCount;
        var lower = new int[count];
        var symbolOf = new long[(elementCount * elementCount)];
        var upper = new int[count];

        Array.Fill(
            array: symbolOf,
            value: -1L
        );

        // The endpoints are read back off the presentation's own generator boundaries rather than re-derived, so the
        // key scheme is whatever the presentation says it is and this map cannot drift from it.
        for (var key = 0; (key < count); ++key) {
            var generator = presentation.GeneratorOf(symbol: presentation.NormalFormWord(key: key)[0]);

            lower[key] = generator.Inputs[0];
            symbolOf[((generator.Inputs[0] * elementCount) + generator.Outputs[0])] = key;
            upper[key] = generator.Outputs[0];
        }

        return new(
            algebra: PresentedAlgebra<TValue, TOps>.Create(presentation: presentation),
            elementCount: elementCount,
            lower: lower,
            upper: upper,
            symbolOf: symbolOf
        );
    }
    /// <summary>Returns the interval one key names.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The interval's two endpoints.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key names no interval of this order.</exception>
    public (int Lower, int Upper) Interval(long key) {
        if (
            (key < 0L) ||
            (key >= m_lower.Length)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(key),
                message: "The key names no interval of this order."
            );
        }

        return (m_lower[((int)key)], m_upper[((int)key)]);
    }
    /// <summary>Attempts to find the key of one interval.</summary>
    /// <param name="lower">The lower endpoint.</param>
    /// <param name="upper">The upper endpoint.</param>
    /// <param name="key">On success, the key naming the interval.</param>
    /// <returns><see langword="true"/> when the lower endpoint is at or below the upper one; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An endpoint names no element of this order.</exception>
    /// <remarks>Comparability is exactly interval-hood, so this doubles as the order relation itself: the pair names a
    /// key when and only when <c>lower ≤ upper</c>.</remarks>
    public bool TryKey(int lower, int upper, out long key) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: lower);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: lower,
            other: ElementCount
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: upper);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: upper,
            other: ElementCount
        );

        key = m_symbolOf[((lower * ElementCount) + upper)];

        return (key >= 0L);
    }
    /// <summary>Attempts to compute the Möbius element — the convolution inverse of <see cref="Zeta"/>.</summary>
    /// <param name="mobius">On success, the element whose coefficient at <c>[x, y]</c> is <c>μ(x, y)</c>.</param>
    /// <param name="obstruction">On failure, the certificate attempted and where the attempt stopped.</param>
    /// <returns><see langword="true"/> when a closure certificate was issued; otherwise <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">The material is not signed, so the alternating inverse has no value.</exception>
    /// <remarks>It is the sum over all lengths of <c>−(ζ − 1)</c>, which is <c>1 / ζ</c>. The issued certificate is
    /// <see cref="ClosureCertificate.Nilpotent"/> rather than <see cref="ClosureCertificate.LocallyFinite"/>: local
    /// finiteness is what makes the truncation legitimate, and what the guarded sum observes is a power that became
    /// zero, so it reports what it observed.</remarks>
    public bool TryMobius(out PresentedAlgebra<TValue, TOps>.Element mobius, out SumClosureObstruction obstruction) {
        // The guard runs first so the refusal names Möbius inversion rather than the negation it happens to reach.
        _ = RequireSigned();

        var strict = Algebra.Subtract(
            left: Zeta,
            right: Algebra.Identity
        );

        return Algebra.TrySumOverAllLengths(
            value: Algebra.Negate(value: strict),
            total: out mobius,
            obstruction: out obstruction
        );
    }
}
