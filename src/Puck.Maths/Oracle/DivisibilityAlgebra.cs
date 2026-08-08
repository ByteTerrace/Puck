using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>
/// The presented algebra of a divisibility window, read as arithmetic: its keys ARE the integers the window holds, its
/// product IS Dirichlet convolution, and the arithmetic functions of elementary number theory are elements of it rather
/// than loops beside it.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// It contributes NO arithmetic. The Möbius function is the sum over all lengths of the negated strict part of zeta;
/// the divisor counts are powers of zeta; the sieve is a product of the elements <c>1 − p</c>; and every readout is a
/// pairing. Each of those is one call into <see cref="PresentedAlgebra{TValue, TOps}"/>, so a bespoke convolution loop
/// appears nowhere — which is the whole claim, since a Dirichlet loop written here would be a second kernel.
/// </para>
/// <para>
/// <b>Keys are not integers in order.</b> A key is the normal form's index in the presentation's canonical
/// ascending-length then lexicographic order, so key zero is the integer one and the rest follow the shape of the
/// factorization, not the size of the value. <see cref="Value"/> and <see cref="TryKey"/> are the map, and neither is a
/// hash: the lookup table is sorted and searched by bisection.
/// </para>
/// <para>
/// <b>The classical identities quantify over ALL integers, and this window holds only the smooth ones.</b> Mertens'
/// sum, Legendre's sieve count and <c>Σ μ(n)·⌊x/n⌋ = 1</c> each range over every integer through their bound, so they
/// are statements about this algebra exactly when the window holds every one of them — that is, exactly when the bound
/// is at most <see cref="ConsecutiveBound"/>. Above it the generating primes stop covering, the missing integers name
/// no key, and a readout silently returns the sum over the smooth ones instead: a smaller number, with no exception
/// and no obstruction. That bound is computed at construction precisely so the precondition can be asserted rather
/// than assumed.
/// </para>
/// <para>
/// Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not: the presentation underneath is
/// immutable and shareable, so give each thread its own algebra.
/// </para>
/// </remarks>
public sealed class DivisibilityAlgebra<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly ulong[] m_descending;
    private readonly long[] m_keyOfOrdered;
    private readonly long[] m_ordered;
    private readonly ISignedMaterial<TValue, TOps>? m_signed;
    private readonly long[] m_value;

    private DivisibilityAlgebra(PresentedAlgebra<TValue, TOps> algebra, ulong[] descending, long window, long[] value) {
        var count = value.Length;
        var material = algebra.Presentation.Material;
        var everyKey = new long[count];
        var ones = new TValue[count];
        var ordered = new int[count];

        for (var key = 0; (key < count); ++key) {
            everyKey[key] = key;
            ones[key] = material.One;
            ordered[key] = key;
        }

        // The integer-to-key lookup, sorted by integer so it is bisected rather than hashed. The sort runs over a copy
        // of the values, since the value-by-key table itself must keep key order.
        Array.Sort(keys: value.ToArray(), items: ordered);

        m_keyOfOrdered = new long[count];
        m_ordered = new long[count];

        for (var index = 0; (index < count); ++index) {
            m_keyOfOrdered[index] = ordered[index];
            m_ordered[index] = value[ordered[index]];
        }

        // How far the window is an initial segment of the integers rather than a set of smooth ones: the ordered
        // values start at one, so the reach ends at the first gap. Every classical identity here is conditioned on it.
        var consecutive = 0L;

        while ((consecutive < count) && (m_ordered[((int)consecutive)] == (consecutive + 1L))) { ++consecutive; }

        Algebra = algebra;
        ConsecutiveBound = consecutive;
        NormalFormCount = count;
        Window = window;
        Zeta = algebra.FromSupport(keys: everyKey, coefficients: ones);
        m_descending = descending;
        m_signed = (material as ISignedMaterial<TValue, TOps>);
        m_value = value;
    }

    /// <summary>Gets the presented algebra whose product is the Dirichlet convolution of this window.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra { get; }
    /// <summary>Gets the largest bound through which this window holds EVERY integer, not only the smooth ones.</summary>
    /// <remarks>It is the length of the initial run <c>1, 2, 3, …</c> the generating primes cover inside
    /// <see cref="Window"/>, and it is the precondition of every classical identity this type states: at
    /// <c>{2, 3, 5}</c> with a window of one hundred it is six, because seven names no key. It equals
    /// <see cref="Window"/> exactly when the generators include every prime through the window.</remarks>
    public long ConsecutiveBound { get; }
    /// <summary>Gets the number of integers the window holds, which is the number of keys.</summary>
    public int NormalFormCount { get; }
    /// <summary>Gets the inclusive integer bound of the window.</summary>
    public long Window { get; }
    /// <summary>Gets the zeta element — the coefficient one at every integer in the window.</summary>
    public PresentedAlgebra<TValue, TOps>.Element Zeta { get; }

    /// <summary>Creates the divisibility algebra of a prime set and an integer bound.</summary>
    /// <param name="primes">The generating primes, in any order.</param>
    /// <param name="window">The inclusive integer bound.</param>
    /// <param name="material">The material.</param>
    /// <returns>The described algebra.</returns>
    /// <exception cref="ArgumentException">A generator is not prime, or two generators repeat.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is below one, more than 128 primes were
    /// given, or the window admits more than 512 integers.</exception>
    public static DivisibilityAlgebra<TValue, TOps> Create(ReadOnlySpan<ulong> primes, long window, TOps material) {
        var presentation = Presentations.DivisibilityWindow<TValue, TOps>(primes: primes, window: window, material: material);
        var descending = primes.ToArray();

        Array.Sort(array: descending);
        Array.Reverse(array: descending);

        var count = presentation.NormalFormCount;
        var value = new long[count];

        for (var key = 0; (key < count); ++key) {
            var product = 1L;

            foreach (var symbol in presentation.NormalFormWord(key: key)) { product *= ((long)descending[symbol]); }

            value[key] = product;
        }

        return new(
            algebra: PresentedAlgebra<TValue, TOps>.Create(presentation: presentation),
            descending: descending,
            window: window,
            value: value
        );
    }

    /// <summary>Returns the element whose coefficient at <c>n</c> is the number of ordered factorizations of <c>n</c>
    /// into a given number of factors — the divisor counts.</summary>
    /// <param name="order">The number of factors; two gives the divisor count itself, one gives zeta, zero the unit.</param>
    /// <returns>The element.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is negative.</exception>
    /// <remarks>It is <see cref="PresentedAlgebra{TValue, TOps}.Power"/> of <see cref="Zeta"/> and nothing else, so the
    /// ascending-bit schedule that pins every other power here pins these too.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element DivisorCounts(int order) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: order);

        return Algebra.Power(value: Zeta, exponent: ((ulong)order));
    }

    /// <summary>Returns the covector whose coefficient at <c>d</c> is <c>⌊bound / d⌋</c>, and zero above the bound.</summary>
    /// <param name="bound">The dividend, at most <see cref="Window"/>.</param>
    /// <returns>The covector.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound"/> is negative, or above
    /// <see cref="Window"/>, where the covector would be silently truncated to the divisors the window happens to
    /// hold rather than refused.</exception>
    /// <remarks>
    /// <para>
    /// Pairing it with an element <c>f</c> gives <c>Σ_d f(d)·⌊bound/d⌋</c>, which counts each integer up to the bound
    /// once per divisor weighted by <c>f</c> — the shape every inclusion-and-exclusion sieve count takes. Paired with
    /// the sieve element it is the Legendre count; paired with the Möbius element it is exactly one.
    /// </para>
    /// <para>
    /// <b>Both of those identities hold only through <see cref="ConsecutiveBound"/>.</b> They quantify over every
    /// integer through the bound, and a bound above the covering reach drops the divisors the generating primes do not
    /// produce — <c>{2, 3, 5}</c> at a window of one hundred answers twenty-six for <c>Σ μ(n)·⌊100/n⌋</c> rather than
    /// one, because seven and everything built on it name no key. The truncation is silent by construction: a missing
    /// integer is not a zero coefficient, it is not a term. Assert <c>bound &lt;= ConsecutiveBound</c> before reading
    /// either identity out of the pairing.
    /// </para>
    /// </remarks>
    public PresentedAlgebra<TValue, TOps>.Element FloorCovector(long bound) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: bound);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: bound, other: Window);

        var material = Algebra.Presentation.Material;
        var coefficients = new List<TValue>();
        var keys = new List<long>();

        for (var key = 0; (key < NormalFormCount); ++key) {
            if (m_value[key] > bound) { continue; }

            coefficients.Add(item: FromCount(count: (bound / m_value[key]), material: material));
            keys.Add(item: key);
        }

        return Algebra.FromSupport(keys: CollectionsMarshal.AsSpan(list: keys), coefficients: CollectionsMarshal.AsSpan(list: coefficients));
    }

    /// <summary>Returns the covector carrying the material's one at every integer of the window through a bound.</summary>
    /// <param name="bound">The inclusive bound, at most <see cref="Window"/>.</param>
    /// <returns>The covector — the window's zeta truncated at the bound.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound"/> is negative, or above
    /// <see cref="Window"/>, where the covector would saturate at the window while still being named for the
    /// bound.</exception>
    /// <remarks>Pairing it with an element sums that element's coefficients through the bound, so pairing it with the
    /// Möbius element is the Mertens partial sum <c>M(bound)</c> — <b>through <see cref="ConsecutiveBound"/> and no
    /// further</b>. Above that reach the sum runs over the window's smooth integers rather than over all of them, and
    /// it is still exactly that restricted sum: a useful quantity, but not <c>M(bound)</c>, and nothing announces the
    /// difference.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Indicator(long bound) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: bound);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: bound, other: Window);

        var material = Algebra.Presentation.Material;
        var coefficients = new List<TValue>();
        var keys = new List<long>();

        for (var key = 0; (key < NormalFormCount); ++key) {
            if (m_value[key] > bound) { continue; }

            coefficients.Add(item: material.One);
            keys.Add(item: key);
        }

        return Algebra.FromSupport(keys: CollectionsMarshal.AsSpan(list: keys), coefficients: CollectionsMarshal.AsSpan(list: coefficients));
    }

    /// <summary>Returns the sieve element of the smallest primes of this window — the product of <c>1 − p</c> over
    /// them.</summary>
    /// <param name="primeCount">How many of the smallest generating primes to sieve by.</param>
    /// <returns>The element, whose coefficient at a squarefree product of those primes is the Möbius sign of that
    /// product and whose coefficient anywhere else is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="primeCount"/> is negative or names more primes than
    /// the window generates.</exception>
    /// <exception cref="InvalidOperationException">The material is not signed, so <c>1 − p</c> has no value; or a prime
    /// of the window is not itself inside it.</exception>
    /// <remarks>Pairing it with <see cref="FloorCovector"/> is the inclusion-and-exclusion count of the integers through
    /// that bound divisible by none of the primes — a sieve stated as one product and one pairing — for a bound at
    /// most <see cref="ConsecutiveBound"/>, which is the reach through which the window holds the integers being
    /// counted.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Sieve(int primeCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: primeCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: primeCount, other: m_descending.Length);

        var signed = RequireSigned();
        var result = Algebra.Identity;

        for (var index = 0; (index < primeCount); ++index) {
            var prime = ((long)m_descending[((m_descending.Length - 1) - index)]);

            if (!TryKey(value: prime, out var key)) {
                throw new InvalidOperationException(message: "A generating prime of this window lies outside the window itself, so it names no key to sieve by.");
            }

            var basis = Algebra.FromSupport(keys: [key], coefficients: [signed.One]);

            result = Algebra.Multiply(
                left: result,
                right: Algebra.Subtract(left: Algebra.Identity, right: basis)
            );
        }

        return result;
    }

    /// <summary>Attempts to compute the Möbius element — the convolution inverse of <see cref="Zeta"/>.</summary>
    /// <param name="mobius">On success, the element whose coefficient at <c>n</c> is <c>μ(n)</c>.</param>
    /// <param name="obstruction">On failure, the certificate attempted and where the attempt stopped.</param>
    /// <returns><see langword="true"/> when a closure certificate was issued; otherwise <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">The material is not signed, so the alternating inverse has no value.</exception>
    /// <remarks>
    /// <para>
    /// It is the sum over all lengths of <c>−(ζ − 1)</c>, which is <c>1/(1 + (ζ − 1)) = 1/ζ</c>. The strict part of zeta
    /// carries no coefficient below degree one, so its <c>k</c>-th power carries none below degree <c>k</c> and the sum
    /// terminates once <c>k</c> passes the window's degree: the window IS the certificate.
    /// </para>
    /// <para>
    /// <b>The issued certificate is <see cref="ClosureCertificate.Nilpotent"/>, not
    /// <see cref="ClosureCertificate.LocallyFinite"/>.</b> Local finiteness is what makes the truncation legitimate, but
    /// what the guarded sum observes is a power that became zero, and it reports what it observed. The distinction is
    /// visible only in a refusal, which this call does not produce for a window that has a finite basis.
    /// </para>
    /// </remarks>
    public bool TryMobius(out PresentedAlgebra<TValue, TOps>.Element mobius, out SumClosureObstruction obstruction) {
        // The guard runs first so the refusal names Möbius inversion rather than the negation it happens to reach.
        RequireSigned();

        var strict = Algebra.Subtract(left: Zeta, right: Algebra.Identity);

        return Algebra.TrySumOverAllLengths(value: Algebra.Negate(value: strict), total: out mobius, obstruction: out obstruction);
    }

    /// <summary>Attempts to find the key of one integer of the window.</summary>
    /// <param name="value">The integer.</param>
    /// <param name="key">On success, the key naming it.</param>
    /// <returns><see langword="true"/> when the integer lies in the window; otherwise <see langword="false"/>.</returns>
    public bool TryKey(long value, out long key) {
        var low = 0;
        var high = m_ordered.Length;

        while (low < high) {
            var middle = ((low + high) >> 1);
            var probe = m_ordered[middle];

            if (probe == value) {
                key = m_keyOfOrdered[middle];

                return true;
            }

            if (probe < value) { low = (middle + 1); } else { high = middle; }
        }

        key = -1L;

        return false;
    }

    /// <summary>Returns the integer one key names.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The integer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key names no normal form of this window.</exception>
    public long Value(long key) {
        if ((key < 0L) || (key >= m_value.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(key), message: "The key names no integer of this window.");
        }

        return m_value[((int)key)];
    }

    // A non-negative count as a material value, by doubling rather than by repeated addition, so a floor quotient of a
    // large bound costs its logarithm.
    private static TValue FromCount(long count, TOps material) {
        var result = material.Zero;
        var scale = material.One;

        while (0L != count) {
            if (0L != (count & 1L)) { result = material.Add(left: result, right: scale); }

            count >>= 1;

            if (0L != count) { scale = material.Add(left: scale, right: scale); }
        }

        return result;
    }
    private ISignedMaterial<TValue, TOps> RequireSigned() =>
        (m_signed ?? throw new InvalidOperationException(message: "Möbius inversion and sieving both subtract, which an unsigned material cannot express."));
}
