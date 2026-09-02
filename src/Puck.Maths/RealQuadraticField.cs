using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The descriptor of a real quadratic field <c>ℚ(√d)</c>: the canonical radicand the field is named by, validated
/// and canonicalized once, so the <see cref="RealQuadratic"/> values that carry it combine by the plain field
/// formulas and compare as tuples. The zero radicand names the rationals themselves, the field every other field
/// contains.
/// </summary>
/// <remarks>
/// <para>
/// Canonicalization strips every square factor it can find cheaply — a perfect square collapses to the rationals,
/// and the square of every prime below <see cref="SmallPrimeBound"/> is divided out — so <c>√8</c> and <c>2·√2</c>
/// name the same field with the same radicand. A square factor whose prime factors all exceed the bound is left in
/// place rather than factored out: the equality, hashing and common-field logic of <see cref="RealQuadratic"/>
/// still identify such a radicand with its square-free twin by the exact GCD test, so correctness never depends on
/// the bound; only the fast tuple path does.
/// </para>
/// <para>
/// Mirrors the descriptor-plus-element shape of <see cref="QuadraticExtensionField64"/> and
/// <see cref="QuadraticAlgebra{T}"/>: the relation lives once, on the descriptor.
/// </para>
/// </remarks>
public readonly record struct RealQuadraticField {
    /// <summary>The bound below which every prime's square is divided out of a radicand at construction.</summary>
    public const uint SmallPrimeBound = 1024U;

    private RealQuadraticField(BigInteger radicand) {
        Radicand = radicand;
    }

    /// <summary>Gets the canonical radicand: above one and not a perfect square for a genuine quadratic field, or
    /// zero for <see cref="Rationals"/>.</summary>
    public BigInteger Radicand { get; }
    /// <summary>Gets whether this descriptor names the rationals rather than a quadratic extension of them.</summary>
    public bool IsRationals => Radicand.IsZero;
    /// <summary>Gets the descriptor of the rationals — the field with no adjoined root, compatible with every other.</summary>
    public static RealQuadraticField Rationals => default;
    /// <summary>Gets <c>√d</c> as a value of this field.</summary>
    /// <exception cref="InvalidOperationException">The descriptor names the rationals, which adjoin no root.</exception>
    public RealQuadratic Sqrt {
        get {
            if (IsRationals) { throw new InvalidOperationException(message: "The rationals adjoin no square root."); }

            return RealQuadratic.FromCanonical(
                denominator: BigInteger.One,
                field: this,
                rationalNumerator: BigInteger.Zero,
                surdNumerator: BigInteger.One
            );
        }
    }

    /// <summary>Names the real quadratic field <c>ℚ(√radicand)</c>.</summary>
    /// <param name="radicand">The radicand; must be positive and not a perfect square.</param>
    /// <returns>The descriptor, with the radicand canonicalized (a radicand of <c>8</c> names <c>ℚ(√2)</c>).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="radicand"/> is a perfect square, so it adjoins nothing; use <see cref="Rationals"/> or <see cref="Rational"/> instead.</exception>
    public static RealQuadraticField Create(BigInteger radicand) =>
        Create(
            radicand: radicand,
            scale: out _
        );
    /// <summary>Names the real quadratic field <c>ℚ(√radicand)</c> and reports the square factor canonicalization
    /// removed, so that <c>√radicand = scale · √Radicand</c>.</summary>
    /// <param name="radicand">The radicand; must be positive and not a perfect square.</param>
    /// <param name="scale">Receives the positive integer with <c>radicand = scale² · Radicand</c>.</param>
    /// <returns>The descriptor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radicand"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="radicand"/> is a perfect square.</exception>
    public static RealQuadraticField Create(BigInteger radicand, out BigInteger scale) {
        if (radicand.Sign <= 0) {
            throw new ArgumentOutOfRangeException(paramName: nameof(radicand), message: "The radicand of a real quadratic field must be positive.");
        }

        var (canonical, factor) = Canonicalize(radicand: radicand);

        if (canonical.IsOne) {
            throw new ArgumentException(message: $"{radicand} is a perfect square and adjoins no root; the field is the rationals.", paramName: nameof(radicand));
        }

        scale = factor;

        return new(radicand: canonical);
    }
    /// <summary>Builds a value of this field from its coordinates.</summary>
    /// <param name="rationalNumerator">The rational coordinate's numerator.</param>
    /// <param name="surdNumerator">The coefficient of <c>√d</c>'s numerator.</param>
    /// <param name="denominator">The shared denominator; must be nonzero.</param>
    /// <returns>The reduced value <c>(rationalNumerator + surdNumerator·√d) / denominator</c>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    public RealQuadratic Element(BigInteger rationalNumerator, BigInteger surdNumerator, BigInteger denominator) =>
        RealQuadratic.Create(
            denominator: denominator,
            radicand: Radicand,
            rationalNumerator: rationalNumerator,
            surdNumerator: surdNumerator
        );

    // (canonical, scale) with radicand = scale² · canonical: a perfect square collapses to canonical one; otherwise
    // every small prime's square is divided out — the factor four by its trailing zero count, the odd primes from
    // the sieve's base list, which starts at three. The result of stripping squares from a non-square is never a
    // square, so no second perfect-square test is needed.
    internal static (BigInteger Radicand, BigInteger Scale) Canonicalize(BigInteger radicand) {
        if (radicand.IsZero) { return (BigInteger.One, BigInteger.Zero); }

        var root = BigIntegerFunctions.SquareRoot(value: radicand);

        if ((root * root) == radicand) { return (BigInteger.One, root); }

        var pairsOfTwo = (int)(((long)BigInteger.TrailingZeroCount(value: radicand)) >> 1);
        var scale = (BigInteger.One << pairsOfTwo);

        radicand >>= (pairsOfTwo << 1);

        var primes = PrimeKernels.BasePrimes;

        for (var index = 0; (index < primes.Length); ++index) {
            var prime = primes[index];

            if (prime >= SmallPrimeBound) { break; }

            var square = new BigInteger(value: (((ulong)prime) * prime));

            if (radicand < square) { break; }

            while (BigInteger.Remainder(dividend: radicand, divisor: square).IsZero) {
                radicand /= square;
                scale *= prime;
            }
        }

        return (radicand, scale);
    }
    // Whether two canonical radicands name one field: equal, or — when a large square factor survived
    // canonicalization on one side — their ratio's numerator and denominator are both perfect squares. On success the
    // shared field is the smaller radicand and each side's scale carries its coefficient over.
    internal static bool TrySame(BigInteger left, BigInteger right, out BigInteger common, out BigInteger leftScale, out BigInteger rightScale) {
        if (left == right) {
            common = left;
            leftScale = BigInteger.One;
            rightScale = BigInteger.One;

            return true;
        }

        var divisor = BigInteger.GreatestCommonDivisor(
            left: left,
            right: right
        );
        var leftQuotient = (left / divisor);
        var rightQuotient = (right / divisor);
        var leftRoot = BigIntegerFunctions.SquareRoot(value: leftQuotient);
        var rightRoot = BigIntegerFunctions.SquareRoot(value: rightQuotient);

        if (
            ((leftRoot * leftRoot) == leftQuotient) &&
            ((rightRoot * rightRoot) == rightQuotient)
        ) {
            common = divisor;
            leftScale = leftRoot;
            rightScale = rightRoot;

            return true;
        }

        common = default;
        leftScale = default;
        rightScale = default;

        return false;
    }
}
