using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>An algebraic integer <c>A + B·τ</c> in the golden ring <c>ℤ[τ]</c>.</summary>
/// <remarks>
/// Here <c>τ = (1 + √5) / 2</c>. Multiplication by <c>τ</c> is the Fibonacci step
/// <c>(A, B) ↦ (B, A + B)</c>, while the algebraic norm is <c>A² + A·B − B²</c>. The type is an exact
/// coordinate carrier for Fibonacci return lattices; it never converts through floating point.
/// </remarks>
public readonly record struct GoldenInteger(BigInteger A, BigInteger B) {
    /// <summary>Gets the exact real embedding <c>A + B·τ</c>.</summary>
    public QuadraticSurd Embedding =>
        QuadraticSurd.Rational(A) + (QuadraticSurd.Rational(B) * FibonacciResearch.GoldenRatio);

    /// <summary>Gets the exact conjugate embedding <c>A + B·(1−τ)</c>.</summary>
    public QuadraticSurd ConjugateEmbedding =>
        QuadraticSurd.Rational(A) + (QuadraticSurd.Rational(B) * FibonacciResearch.GoldenConjugate);

    /// <summary>Gets the integral algebraic norm <c>A² + A·B − B²</c>.</summary>
    public BigInteger Norm => ((A * A) + (A * B) - (B * B));

    /// <summary>Multiplies by <c>τ</c>.</summary>
    public GoldenInteger Next() => new(A: B, B: A + B);

    /// <summary>Multiplies by <c>τ⁻¹ = τ−1</c>.</summary>
    public GoldenInteger Previous() => new(A: B - A, B: A);

    /// <summary>Multiplies by <c>τ^exponent</c> in logarithmic time.</summary>
    public GoldenInteger Advance(BigInteger exponent) {
        if (exponent.Sign < 0) {
            throw new ArgumentOutOfRangeException(nameof(exponent), "the forward exponent must be non-negative");
        }
        if (exponent.IsZero) { return this; }

        var (current, next) = FibonacciResearch.FibonacciPair(exponent);
        var previous = (next - current);
        return new GoldenInteger(
            A: ((previous * A) + (current * B)),
            B: ((current * A) + (next * B))
        );
    }

    /// <summary>Returns the determinant of the two coefficient columns.</summary>
    public static BigInteger Determinant(GoldenInteger left, GoldenInteger right) =>
        ((left.A * right.B) - (left.B * right.A));
}

/// <summary>The two golden projective orbits modulo <c>2^t</c>, for <c>t ≥ 3</c>.</summary>
public enum GoldenProjectiveOrbit {
    /// <summary>The orbit represented by <c>1</c>, whose norm is congruent to <c>±1</c> modulo eight.</summary>
    UnitNorm,
    /// <summary>The orbit represented by <c>2+τ</c>, whose norm is congruent to <c>±5</c> modulo eight.</summary>
    FiveNorm
}

/// <summary>Exact Fibonacci and golden-ring tools backed by the proved two-adic formulas.</summary>
public static class FibonacciResearch {
    /// <summary>Gets <c>τ = (1 + √5) / 2</c>.</summary>
    public static QuadraticSurd GoldenRatio { get; } = QuadraticSurd.Create(1, 1, 5, 2);

    /// <summary>Gets the conjugate <c>1−τ = (1 − √5) / 2</c>.</summary>
    public static QuadraticSurd GoldenConjugate { get; } = QuadraticSurd.Create(1, -1, 5, 2);

    /// <summary>Returns <c>(F_index, F_(index+1))</c> by exact fast doubling.</summary>
    public static (BigInteger Current, BigInteger Next) FibonacciPair(BigInteger index) {
        if (index.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(index)); }
        return FibonacciPairCore(index);
    }

    /// <summary>Returns <c>(F_index, F_(index+1))</c> reduced to <c>[0, modulus)</c>.</summary>
    public static (BigInteger Current, BigInteger Next) FibonacciPairModulo(
        BigInteger index,
        BigInteger modulus
    ) {
        if (index.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(index)); }
        if (modulus.Sign <= 0) { throw new ArgumentOutOfRangeException(nameof(modulus)); }
        return FibonacciPairModuloCore(index, modulus);
    }

    /// <summary>
    /// Returns the least positive Fibonacci index whose value is divisible by <c>2^exponent</c>.
    /// </summary>
    /// <remarks>The proved closed form is <c>6</c> at exponent two and <c>3·2^(exponent−2)</c> thereafter.</remarks>
    public static BigInteger TwoPowerRankOfApparition(int exponent) {
        if (exponent < 2) {
            throw new ArgumentOutOfRangeException(nameof(exponent), "the closed form requires exponent at least two");
        }
        return (exponent == 2) ? new BigInteger(6) : (3 * (BigInteger.One << (exponent - 2)));
    }

    /// <summary>Returns the cardinality of the projective line over <c>ℤ/2^exponentℤ</c>.</summary>
    public static BigInteger TwoPowerProjectiveLineCardinality(int exponent) {
        ArgumentOutOfRangeException.ThrowIfLessThan(exponent, 1);
        return (3 * (BigInteger.One << (exponent - 1)));
    }

    /// <summary>
    /// Classifies a primitive coefficient pair into one of the two golden projective orbits modulo
    /// <c>2^exponent</c>.
    /// </summary>
    /// <remarks>
    /// A pair is primitive at two when at least one coordinate is odd. Multiplication by an odd projective scalar
    /// preserves its norm modulo eight up to an odd square, and a golden step negates that norm. Thus norm residues
    /// <c>1,7</c> form the unit orbit and residues <c>3,5</c> form the five-norm orbit.
    /// </remarks>
    public static GoldenProjectiveOrbit ClassifyTwoPowerProjectiveOrbit(
        int exponent,
        GoldenInteger value
    ) {
        if (exponent < 3) {
            throw new ArgumentOutOfRangeException(nameof(exponent), "the two-orbit classification requires exponent at least three");
        }
        if (value.A.IsEven && value.B.IsEven) {
            throw new ArgumentException("the projective pair must be primitive at two", nameof(value));
        }

        var residue = PositiveRemainder(value.Norm, 8);
        if ((residue == BigInteger.One) || (residue == 7)) { return GoldenProjectiveOrbit.UnitNorm; }
        if ((residue == 3) || (residue == 5)) { return GoldenProjectiveOrbit.FiveNorm; }
        throw new InvalidOperationException("a primitive golden integer unexpectedly had even norm");
    }

    /// <summary>Advances one golden integer modulo a positive modulus in logarithmic time.</summary>
    public static GoldenInteger AdvanceModulo(
        GoldenInteger value,
        BigInteger exponent,
        BigInteger modulus
    ) {
        if (exponent.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(exponent)); }
        if (modulus.Sign <= 0) { throw new ArgumentOutOfRangeException(nameof(modulus)); }
        if (exponent.IsZero) {
            return new GoldenInteger(
                PositiveRemainder(value.A, modulus),
                PositiveRemainder(value.B, modulus)
            );
        }

        var (current, next) = FibonacciPairModulo(exponent, modulus);
        var previous = PositiveRemainder(next - current, modulus);
        return new GoldenInteger(
            PositiveRemainder((previous * value.A) + (current * value.B), modulus),
            PositiveRemainder((current * value.A) + (next * value.B), modulus)
        );
    }

    internal static QuadraticSurd GoldenPower(int exponent) {
        ArgumentOutOfRangeException.ThrowIfNegative(exponent);
        var result = QuadraticSurd.One;
        var factor = GoldenRatio;
        var remaining = exponent;
        while (remaining > 0) {
            if ((remaining & 1) != 0) { result *= factor; }
            remaining >>= 1;
            if (remaining != 0) { factor *= factor; }
        }
        return result;
    }

    private static (BigInteger Current, BigInteger Next) FibonacciPairCore(BigInteger index) {
        if (index.IsZero) { return (BigInteger.Zero, BigInteger.One); }
        var (halfCurrent, halfNext) = FibonacciPairCore(index >> 1);
        var current = (halfCurrent * ((2 * halfNext) - halfCurrent));
        var next = ((halfCurrent * halfCurrent) + (halfNext * halfNext));
        return index.IsEven ? (current, next) : (next, current + next);
    }

    private static (BigInteger Current, BigInteger Next) FibonacciPairModuloCore(
        BigInteger index,
        BigInteger modulus
    ) {
        if (index.IsZero) { return (BigInteger.Zero, BigInteger.One % modulus); }
        var (halfCurrent, halfNext) = FibonacciPairModuloCore(index >> 1, modulus);
        var current = PositiveRemainder(
            halfCurrent * ((2 * halfNext) - halfCurrent),
            modulus
        );
        var next = PositiveRemainder(
            (halfCurrent * halfCurrent) + (halfNext * halfNext),
            modulus
        );
        return index.IsEven
            ? (current, next)
            : (next, PositiveRemainder(current + next, modulus));
    }

    private static BigInteger PositiveRemainder(BigInteger value, BigInteger modulus) {
        var remainder = (value % modulus);
        return (remainder.Sign < 0) ? (remainder + modulus) : remainder;
    }
}

/// <summary>The exact symmetric Fibonacci return minimum at one positive coloring period.</summary>
/// <param name="Period">The common coloring period <c>H</c>.</param>
/// <param name="ScaleIndex">The integer <c>N</c> with <c>τ^(N+1) ≤ H &lt; τ^(N+2)</c>.</param>
/// <param name="Lambda">The coefficient of one in the minimum weight.</param>
/// <param name="Kappa">The coefficient of <c>τ</c> in the minimum weight.</param>
public readonly record struct FibonacciSymmetricMinimum(
    BigInteger Period,
    int ScaleIndex,
    BigInteger Lambda,
    BigInteger Kappa
) {
    /// <summary>Gets the exact minimum weight <c>Lambda + Kappa·τ = τ^(N+1)</c>.</summary>
    public QuadraticSurd Weight =>
        QuadraticSurd.Rational(Lambda) +
        (QuadraticSurd.Rational(Kappa) * FibonacciResearch.GoldenRatio);

    /// <summary>Gets the exact approximation error <c>|Lambda·τ − Kappa|</c>.</summary>
    public QuadraticSurd Error =>
        ((QuadraticSurd.Rational(Lambda) * FibonacciResearch.GoldenRatio) -
            QuadraticSurd.Rational(Kappa)).Abs();

    /// <summary>Gets the exact strict admissibility cutoff <c>τ²/H</c>.</summary>
    public QuadraticSurd Cutoff =>
        (FibonacciResearch.GoldenRatio * FibonacciResearch.GoldenRatio) /
        QuadraticSurd.Rational(Period);

    /// <summary>Gets the exact asymptotic critical exponent of the symmetric Fibonacci construction.</summary>
    public QuadraticSurd CriticalExponent =>
        QuadraticSurd.One +
        ((FibonacciResearch.GoldenRatio * FibonacciResearch.GoldenRatio) /
            (QuadraticSurd.Rational(Period) * Weight));

    /// <summary>Gets <c>H²(E*−1) = H·τ²/Weight</c>.</summary>
    public QuadraticSurd DeterminantNormalizedExcess =>
        (QuadraticSurd.Rational(Period) * FibonacciResearch.GoldenRatio *
            FibonacciResearch.GoldenRatio) / Weight;

    /// <summary>
    /// Gets whether the determinant-normalized candidate is strictly below the proved non-Fibonacci gap
    /// <c>8/3</c>.
    /// </summary>
    public bool IsBelowThreeCellGap =>
        DeterminantNormalizedExcess < QuadraticSurd.Rational(8, 3);

    /// <summary>Rechecks every exact identity and strict inequality in this certificate.</summary>
    public bool Verify() {
        if ((Period.Sign <= 0) || (ScaleIndex < -1) || (Lambda.Sign < 0) || (Kappa.Sign < 0) ||
            (Lambda.IsZero && Kappa.IsZero)) {
            return false;
        }

        var lowerPower = FibonacciResearch.GoldenPower(ScaleIndex + 1);
        var upperPower = FibonacciResearch.GoldenPower(ScaleIndex + 2);
        var periodValue = QuadraticSurd.Rational(Period);
        if ((lowerPower > periodValue) || !(periodValue < upperPower) || (Weight != lowerPower) || !(Error < Cutoff)) {
            return false;
        }

        var expected = FibonacciSymmetricMinimum.Find(Period);
        return (ScaleIndex == expected.ScaleIndex) &&
            (Lambda == expected.Lambda) &&
            (Kappa == expected.Kappa);
    }

    /// <summary>Constructs the proved exact minimum for a positive common period <c>H</c>.</summary>
    public static FibonacciSymmetricMinimum Find(BigInteger period) {
        if (period.Sign <= 0) { throw new ArgumentOutOfRangeException(nameof(period)); }

        var periodValue = QuadraticSurd.Rational(period);
        var power = QuadraticSurd.One;
        var exponent = 0;
        while ((power * FibonacciResearch.GoldenRatio) <= periodValue) {
            power *= FibonacciResearch.GoldenRatio;
            exponent = checked(exponent + 1);
        }

        var scaleIndex = (exponent - 1);
        return scaleIndex switch {
            -1 => new FibonacciSymmetricMinimum(period, scaleIndex, Lambda: 1, Kappa: 0),
            0 => new FibonacciSymmetricMinimum(period, scaleIndex, Lambda: 0, Kappa: 1),
            _ => FromFibonacciPair(period, scaleIndex)
        };
    }

    private static FibonacciSymmetricMinimum FromFibonacciPair(BigInteger period, int scaleIndex) {
        var (lambda, kappa) = FibonacciResearch.FibonacciPair(scaleIndex);
        return new FibonacciSymmetricMinimum(period, scaleIndex, lambda, kappa);
    }
}

/// <summary>The exact counts of the two Fibonacci base letters in a finite factor.</summary>
public readonly record struct FibonacciFactorCounts(BigInteger FalseCount, BigInteger TrueCount) {
    /// <summary>Gets the factor length.</summary>
    public BigInteger Length => (FalseCount + TrueCount);
}

/// <summary>One letter of the ruler-colored Fibonacci word.</summary>
/// <param name="BaseLetter"><see langword="false"/> or <see langword="true"/> from the base Fibonacci word.</param>
/// <param name="Color">The ruler color in <c>[0, RulerDepth]</c>.</param>
public readonly record struct FibonacciRulerLetter(bool BaseLetter, int Color) {
    /// <summary>Maps the tagged letter into a dense alphabet with all false colors first.</summary>
    public int SymbolIndex(int rulerDepth) {
        ArgumentOutOfRangeException.ThrowIfNegative(rulerDepth);
        if ((Color < 0) || (Color > rulerDepth)) {
            throw new InvalidOperationException("the ruler color is outside the requested alphabet");
        }
        return BaseLetter ? checked((rulerDepth + 1) + Color) : Color;
    }
}

/// <summary>
/// Exact random access to the balanced ruler-coloring of the characteristic Fibonacci mechanical word.
/// </summary>
/// <remarks>
/// The base slope is <c>1−τ⁻¹ = τ⁻²</c>. Prefix counts are one exact quadratic-surd floor, and
/// the occurrence color is <c>min(v₂(rank+1), RulerDepth)</c>. No prefix is materialized.
/// </remarks>
public sealed class FibonacciRulerWordIndex {
    private static readonly QuadraticSurd BaseSlope =
        QuadraticSurd.One - (QuadraticSurd.One / FibonacciResearch.GoldenRatio);

    /// <summary>Creates the construction on <c>2·(rulerDepth+1)</c> letters.</summary>
    public FibonacciRulerWordIndex(int rulerDepth) {
        ArgumentOutOfRangeException.ThrowIfNegative(rulerDepth);
        _ = checked(2 * (rulerDepth + 1));
        RulerDepth = rulerDepth;
    }

    /// <summary>Gets <c>r</c>, the largest ruler color.</summary>
    public int RulerDepth { get; }
    /// <summary>Gets the total alphabet size <c>2(r+1)</c>.</summary>
    public int AlphabetSize => checked(2 * (RulerDepth + 1));
    /// <summary>Gets the individual constant-gap coloring period <c>2^r</c>.</summary>
    public BigInteger ColoringPeriod => (BigInteger.One << RulerDepth);
    /// <summary>
    /// Gets the factor length <c>3·2^r</c> from which every base-letter subsequence contains a complete ruler period.
    /// </summary>
    public BigInteger GuaranteedRichFactorLength => (3 * ColoringPeriod);

    /// <summary>Counts true base letters in <c>[0, exclusiveEnd)</c>.</summary>
    public BigInteger TruePrefixCount(BigInteger exclusiveEnd) {
        if (exclusiveEnd.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(exclusiveEnd)); }
        return (QuadraticSurd.Rational(exclusiveEnd + BigInteger.One) * BaseSlope).Floor();
    }

    /// <summary>Counts false base letters in <c>[0, exclusiveEnd)</c>.</summary>
    public BigInteger FalsePrefixCount(BigInteger exclusiveEnd) =>
        (exclusiveEnd - TruePrefixCount(exclusiveEnd));

    /// <summary>Returns the base Fibonacci letter at a non-negative index.</summary>
    public bool BaseLetterAt(BigInteger index) {
        if (index.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(index)); }
        return TruePrefixCount(index + BigInteger.One) != TruePrefixCount(index);
    }

    /// <summary>Returns the tagged ruler-colored letter at a non-negative index.</summary>
    public FibonacciRulerLetter LetterAt(BigInteger index) {
        if (index.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(index)); }
        var trueBefore = TruePrefixCount(index);
        var baseLetter = TruePrefixCount(index + BigInteger.One) != trueBefore;
        var occurrenceRank = baseLetter ? trueBefore : (index - trueBefore);
        return new FibonacciRulerLetter(baseLetter, ColorOfOccurrence(occurrenceRank, RulerDepth));
    }

    /// <summary>Returns exact base-letter counts in <c>[start, start+length)</c>.</summary>
    public FibonacciFactorCounts FactorCounts(BigInteger start, BigInteger length) {
        if (start.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (length.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(length)); }
        var end = (start + length);
        var trueCount = (TruePrefixCount(end) - TruePrefixCount(start));
        return new FibonacciFactorCounts(length - trueCount, trueCount);
    }

    /// <summary>
    /// Gets whether a concrete base factor contains at least one complete ruler period of each base letter.
    /// </summary>
    public bool IsRulerRich(BigInteger start, BigInteger length) {
        var counts = FactorCounts(start, length);
        return (counts.FalseCount >= ColoringPeriod) && (counts.TrueCount >= ColoringPeriod);
    }

    /// <summary>Returns <c>min(v₂(occurrenceRank+1), rulerDepth)</c>.</summary>
    public static int ColorOfOccurrence(BigInteger occurrenceRank, int rulerDepth) {
        if (occurrenceRank.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(occurrenceRank)); }
        ArgumentOutOfRangeException.ThrowIfNegative(rulerDepth);
        var shifted = (occurrenceRank + BigInteger.One);
        var color = 0;
        while ((color < rulerDepth) && shifted.IsEven) {
            shifted >>= 1;
            ++color;
        }
        return color;
    }
}
