using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A certificate that the dyadic grid value <c>QuantizedNumerator / 2^FractionBits</c> is the nearest representable
/// approximation of <c>Slope</c>, and that the quantized Beatty sequence <c>⌊quantized·n⌋</c> reproduces the exact
/// <c>⌊Slope·n⌋</c> for every <c>1 ≤ n &lt; FirstDivergence</c>, with the disagreement at <c>FirstDivergence</c>
/// exhibited by <c>DivergenceWitness</c>.
/// </summary>
/// <param name="Slope">The exact irrational slope that was quantized.</param>
/// <param name="FractionBits">The number of fraction bits in the dyadic grid.</param>
/// <param name="QuantizedNumerator">The numerator of the nearest grid value at denominator <c>2^FractionBits</c>.</param>
/// <param name="RoundingSign">Plus one when the grid value lies above the slope, minus one when below.</param>
/// <param name="FirstDivergence">The least index <c>n ≥ 1</c> at which the quantized and exact floors disagree.</param>
/// <param name="DivergenceWitness">The integer caught between <c>Slope·n</c> and <c>quantized·n</c> at that index.</param>
public readonly record struct BeattyQuantizationCertificate(
    QuadraticSurd Slope,
    int FractionBits,
    BigInteger QuantizedNumerator,
    int RoundingSign,
    BigInteger FirstDivergence,
    BigInteger DivergenceWitness) {
    /// <summary>Checks the certificate's verifiable claims in exact arithmetic.</summary>
    /// <remarks>
    /// This confirms that the grid value is strictly within half a grid step of the slope on the declared side, and
    /// that the witness integer sits between the exact and quantized lines at <see cref="FirstDivergence"/> — so the
    /// floors there genuinely disagree. Minimality of <see cref="FirstDivergence"/> is the construction's theorem
    /// (see <see cref="BeattyQuantization.FirstFloorDisagreement"/>) and is not re-derived here.
    /// </remarks>
    /// <returns><see langword="true"/> when every verifiable claim holds.</returns>
    public bool Verify() {
        if (
            (RoundingSign != 1) &&
            (RoundingSign != -1)
        ) { return false; }
        if (
            (FractionBits < 0) ||
            (FirstDivergence < BigInteger.One)
        ) { return false; }

        var gridDenominator = (BigInteger.One << FractionBits);
        var grid = QuadraticSurd.Rational(
            numerator: QuantizedNumerator,
            denominator: gridDenominator
        );
        var difference = (grid - Slope);

        if (difference.Sign != RoundingSign) { return false; }

        var halfStep = QuadraticSurd.Rational(
            numerator: BigInteger.One,
            denominator: (gridDenominator << 1)
        );

        if (difference.Abs().CompareTo(other: halfStep) >= 0) { return false; }

        var index = QuadraticSurd.Rational(value: FirstDivergence);
        var witness = QuadraticSurd.Rational(value: DivergenceWitness);
        var exactAtIndex = (Slope * index);
        var gridAtIndex = (grid * index);

        return ((RoundingSign > 0)
            ? ((exactAtIndex < witness) && (witness <= gridAtIndex))
            : ((gridAtIndex < witness) && (witness <= exactAtIndex))
        );
    }
}
/// <summary>
/// Exact answers to the question of when a rounded slope betrays the true one: the nearest dyadic grid value of an
/// exact slope, and the first index at which the Beatty sequence of an approximation disagrees with the exact
/// <c>⌊slope·n⌋</c>.
/// </summary>
/// <remarks>
/// A fixed-point constant stepping an accumulator is a rational Beatty sequence standing in for an irrational one;
/// these methods replace "enough bits, surely" with an exact certificate: the two sequences agree up to a computed
/// first-divergence index, and the divergence there is exhibited by a witness integer. Everything is decided in
/// exact <see cref="QuadraticSurd"/> and <see cref="BigInteger"/> arithmetic; nothing is sampled.
/// </remarks>
public static class BeattyQuantization {
    /// <summary>Certifies the nearest dyadic quantization of an irrational slope: the grid value, its side, and the exact first index at which the quantized Beatty floors diverge from the true ones.</summary>
    /// <param name="slope">The exact slope; it must be positive and irrational.</param>
    /// <param name="fractionBits">The number of fraction bits in the dyadic grid; it must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slope"/> is rational or not positive, or <paramref name="fractionBits"/> is negative.</exception>
    public static BeattyQuantizationCertificate CertifySlope(QuadraticSurd slope, int fractionBits) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: fractionBits);

        if (slope.IsRational) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slope),
                message: "the slope must be irrational; a rational slope admits an exact representation whose floors never diverge"
            );
        }
        if (slope.Sign <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slope),
                message: "the slope must be positive"
            );
        }

        var (numerator, roundingSign) = QuantizeNearest(
            fractionBits: fractionBits,
            value: slope
        );
        var (index, witness) = FirstFloorDisagreement(
            exact: slope,
            approximateNumerator: numerator,
            approximateDenominator: (BigInteger.One << fractionBits)
        );

        return new BeattyQuantizationCertificate(
            DivergenceWitness: witness,
            FirstDivergence: index,
            FractionBits: fractionBits,
            QuantizedNumerator: numerator,
            RoundingSign: roundingSign,
            Slope: slope
        );
    }
    /// <summary>Returns the least index <c>n ≥ 1</c> at which <c>⌊approximate·n⌋</c> differs from <c>⌊exact·n⌋</c>, with the witness integer caught between the two lines there.</summary>
    /// <remarks>
    /// A disagreement at <c>n</c> is an integer <c>m</c> with <c>m/n</c> inside the interval between the two slopes
    /// (closed on a rational upper endpoint, open elsewhere), so the least such <c>n</c> is the smallest denominator
    /// carried by that interval — <see cref="SimplestRational.InOpenInterval"/> supplies it, and the closed endpoint
    /// competes with its own reduced denominator. The witness returned is the least integer strictly above the lower
    /// line at the divergence index.
    /// </remarks>
    /// <param name="exact">The exact slope; it must be positive.</param>
    /// <param name="approximateNumerator">The numerator of the approximation; it must be non-negative.</param>
    /// <param name="approximateDenominator">The denominator of the approximation; it must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exact"/> is not positive, <paramref name="approximateNumerator"/> is negative, or <paramref name="approximateDenominator"/> is not positive.</exception>
    /// <exception cref="ArgumentException">The approximation equals <paramref name="exact"/>, so the floors never disagree.</exception>
    public static (BigInteger Index, BigInteger Witness) FirstFloorDisagreement(
        QuadraticSurd exact,
        BigInteger approximateNumerator,
        BigInteger approximateDenominator) {
        if (exact.Sign <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(exact),
                message: "the exact slope must be positive"
            );
        }
        if (approximateNumerator.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(approximateNumerator),
                message: "the approximate numerator must be non-negative"
            );
        }
        if (approximateDenominator.Sign <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(approximateDenominator),
                message: "the approximate denominator must be positive"
            );
        }

        var approximate = QuadraticSurd.Rational(
            denominator: approximateDenominator,
            numerator: approximateNumerator
        );
        var comparison = approximate.CompareTo(other: exact);

        if (comparison == 0) {
            throw new ArgumentException(
                message: "the approximation equals the exact slope; the floors never disagree",
                paramName: nameof(approximateNumerator)
            );
        }

        var low = ((comparison > 0)
            ? exact
            : approximate
        );
        var high = ((comparison > 0)
            ? approximate
            : exact
        );

        var (_, simplestDenominator) = SimplestRational.InOpenInterval(
            high: high,
            low: low
        );
        var index = simplestDenominator;

        // The upper endpoint is reached with equality (m ≤ high·n), so when it is rational its own reduced
        // denominator is a crossing index too, and may beat the open-interval minimum.
        if (
            high.IsRational &&
            (high.Denominator < index)
        ) {
            index = high.Denominator;
        }

        var witness = ((low * QuadraticSurd.Rational(value: index)).Floor() + BigInteger.One);

        return (index, witness);
    }
    /// <summary>Returns the numerator of the grid value nearest to <paramref name="value"/> at denominator <c>2^fractionBits</c>, and which side of the value it landed on.</summary>
    /// <remarks>An exact tie — possible only for rational values — rounds to the even numerator. The sign is plus one when the grid value lies above <paramref name="value"/>, minus one when below, and zero when the value sits exactly on the grid.</remarks>
    /// <param name="value">The exact value to quantize.</param>
    /// <param name="fractionBits">The number of fraction bits in the dyadic grid; it must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fractionBits"/> is negative.</exception>
    public static (BigInteger Numerator, int RoundingSign) QuantizeNearest(QuadraticSurd value, int fractionBits) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: fractionBits);

        var scaled = (value * QuadraticSurd.Rational(value: (BigInteger.One << fractionBits)));
        var floor = scaled.Floor();
        var remainder = (scaled - QuadraticSurd.Rational(value: floor));
        var half = QuadraticSurd.Rational(
            numerator: BigInteger.One,
            denominator: 2
        );
        var comparison = remainder.CompareTo(other: half);

        if (comparison < 0) {
            return (floor, ((remainder.Sign == 0)
                ? 0
                : -1));
        }
        if (comparison > 0) { return ((floor + BigInteger.One), 1); }

        // An exact half-step tie; round to the even numerator.
        return (floor.IsEven
            ? (floor, -1)
            : ((floor + BigInteger.One), 1)
        );
    }
}
