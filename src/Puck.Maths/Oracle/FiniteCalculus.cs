namespace Puck.Maths;

/// <summary>
/// Finite calculus on degree-bounded jets: the shift presentation read as sequences, where a sequence IS an element,
/// the backward difference is <c>1 − shift</c>, and the antidifference is the shift's guarded sum over all lengths.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// The algebra is <see cref="Presentations.Shift"/>: one generator with <c>x^(bound+1) → 0</c>, so its key IS a degree
/// and an element's coefficient at key <c>n</c> is the sequence's value at <c>n</c>. Multiplying by the generator
/// delays a sequence by one place, so <c>1 − x</c> is the backward difference <c>f(n) − f(n − 1)</c> and its inverse is
/// the prefix sum.
/// </para>
/// <para>
/// <b>The generator is nilpotent, and that nilpotence IS the certificate.</b> The same element is the forward
/// difference <c>S − 1</c> of the jet reading, which annihilates a bounded-degree jet after <c>bound + 1</c>
/// applications; that is why the sum over all lengths <c>1 + x + x² + …</c> terminates, and why
/// <see cref="TryAntidifference"/> is offered rather than assumed. The complementary element
/// <see cref="Difference"/> is a UNIT rather than a nilpotent, so its own sum over all lengths is refused — the two
/// cannot both be starred, and which one is starred is what makes the summation operator the one that exists.
/// </para>
/// <para>
/// Exact at every material; over a rounding carrier the prefix sum still rounds once per place, like every other
/// product here. Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not.
/// </para>
/// </remarks>
public sealed class FiniteCalculus<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private FiniteCalculus(PresentedAlgebra<TValue, TOps> algebra, int degreeBound) {
        var shift = algebra.Generator(symbol: 0);

        Algebra = algebra;
        DegreeBound = degreeBound;
        Difference = algebra.Subtract(
            left: algebra.Identity,
            right: shift
        );
        Shift = shift;
    }

    /// <summary>Gets the presented algebra whose keys are degrees and whose product is sequence convolution.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra { get; }
    /// <summary>Gets the highest surviving degree, which is the last place a sequence carries.</summary>
    public int DegreeBound { get; }
    /// <summary>Gets the backward difference <c>1 − x</c>, whose action on a sequence is <c>f(n) − f(n − 1)</c>.</summary>
    /// <remarks>It is a unit, not a nilpotent: its sum over all lengths is refused, and its inverse is
    /// <see cref="TryAntidifference"/>.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Difference { get; }
    /// <summary>Gets the shift <c>x</c>, whose action on a sequence delays it by one place, and which is the forward
    /// difference <c>S − 1</c> of the jet reading.</summary>
    public PresentedAlgebra<TValue, TOps>.Element Shift { get; }

    /// <summary>Creates the finite calculus of a degree bound.</summary>
    /// <param name="degreeBound">The highest surviving degree; the jet holds <c>degreeBound + 1</c> places.</param>
    /// <param name="material">The material; it must be signed, since the difference subtracts.</param>
    /// <returns>The described calculus.</returns>
    /// <exception cref="ArgumentException">The material is not signed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeBound"/> is negative or above 511.</exception>
    public static FiniteCalculus<TValue, TOps> Create(int degreeBound, TOps material) {
        if (material is not ISignedMaterial<TValue, TOps>) {
            throw new ArgumentException(
                message: "The backward difference subtracts, which an unsigned material cannot express.",
                paramName: nameof(material)
            );
        }

        return new(
            algebra: PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.Shift<TValue, TOps>(
                degreeBound: degreeBound,
                material: material
            )),
            degreeBound: degreeBound
        );
    }
    /// <summary>Returns a sequence as an element, one value per place.</summary>
    /// <param name="values">The sequence's values, place zero first; at most <c>DegreeBound + 1</c> of them.</param>
    /// <returns>The element.</returns>
    /// <exception cref="ArgumentOutOfRangeException">More values were given than the jet holds.</exception>
    public PresentedAlgebra<TValue, TOps>.Element Sequence(ReadOnlySpan<TValue> values) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: values.Length,
            other: (DegreeBound + 1)
        );

        var keys = new long[values.Length];

        for (var index = 0; (index < keys.Length); ++index) { keys[index] = index; }

        return Algebra.FromSupport(
            coefficients: values,
            keys: keys
        );
    }
    /// <summary>Attempts to compute the antidifference — the prefix-sum operator, the sum of the shift over all lengths.</summary>
    /// <param name="antidifference">On success, the element <c>1 + x + x² + …</c>; multiplying a sequence by it replaces
    /// each place with the sum of that place and every place before it.</param>
    /// <param name="obstruction">On failure, the certificate attempted and where the attempt stopped.</param>
    /// <returns><see langword="true"/> when a closure certificate was issued; otherwise <see langword="false"/>.</returns>
    /// <remarks>The issued certificate is <see cref="ClosureCertificate.Nilpotent"/>, computed rather than assumed: the
    /// shift's power at the degree bound is zero, so the sum terminates. It is the exact two-sided inverse of
    /// <see cref="Difference"/>, since <c>(1 − x)·(1 + x + … + x^bound) = 1 − x^(bound+1) = 1</c>.</remarks>
    public bool TryAntidifference(out PresentedAlgebra<TValue, TOps>.Element antidifference, out SumClosureObstruction obstruction) =>
        Algebra.TrySumOverAllLengths(
            value: Shift,
            total: out antidifference,
            obstruction: out obstruction
        );
}
