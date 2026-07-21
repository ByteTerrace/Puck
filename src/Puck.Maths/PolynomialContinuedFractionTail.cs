using System.Numerics;

namespace Puck.Maths;

/// <summary>The five integer coefficients of <c>sₙ = p·n + q + (r·n² + u·n + v)/sₙ₊₁</c>.</summary>
public readonly record struct PolynomialContinuedFractionParameters(
    BigInteger Linear,
    BigInteger Constant,
    BigInteger NumeratorQuadratic,
    BigInteger NumeratorLinear,
    BigInteger NumeratorConstant
);

/// <summary>A certified symmetric tail interval valid from <see cref="Cutoff"/> onward.</summary>
/// <param name="Cutoff">The first positive index covered by the certificate.</param>
/// <param name="RadiusNumerator">The non-negative integer <c>H</c> in the radius <c>H/n</c>.</param>
/// <param name="CenterSlopeLowerNumerator">The numerator of the strict rational lower bound <c>L₁&lt;λ</c>.</param>
/// <param name="PositiveTrapSlopeNumerator">The numerator of <c>L₂&lt;L₁</c>, used to keep every trap positive.</param>
/// <param name="SlopeLowerDenominator">The shared positive denominator of <c>L₁</c> and <c>L₂</c>.</param>
/// <param name="ResidualMagnitudeCeiling">The integer <c>M≥|R|</c> used in the invariant-interval inequality.</param>
public readonly record struct PolynomialTailIntervalCertificate(
    BigInteger Cutoff,
    BigInteger RadiusNumerator,
    BigInteger CenterSlopeLowerNumerator,
    BigInteger PositiveTrapSlopeNumerator,
    BigInteger SlopeLowerDenominator,
    BigInteger ResidualMagnitudeCeiling
);

/// <summary>A certified arbitrary-order asymptotic interval for a positive polynomial continued-fraction tail.</summary>
/// <param name="Order">
/// The positive integer <c>m</c> for which the radius is <c>H/n^m</c>; the center uses
/// <c>lambda*n+c_0+...+c_(m-1)/n^(m-1)</c>.
/// </param>
/// <param name="Cutoff">The first positive index covered by the certificate.</param>
/// <param name="RadiusNumerator">The non-negative integer <c>H</c> in the radius <c>H/n^m</c>.</param>
/// <param name="CenterSlopeLowerNumerator">The numerator of the strict rational lower bound <c>L1</c>.</param>
/// <param name="PositiveTrapSlopeNumerator">The numerator of the positive-trap lower bound <c>L2&lt;L1</c>.</param>
/// <param name="SlopeLowerDenominator">The shared positive denominator of <c>L1</c> and <c>L2</c>.</param>
/// <param name="ResidualMagnitudeCeiling">
/// An integer <c>M</c> such that the recurrence residual at the truncated center has magnitude at most
/// <c>M/n^m</c>.
/// </param>
public readonly record struct PolynomialTailAsymptoticCertificate(
    int Order,
    BigInteger Cutoff,
    BigInteger RadiusNumerator,
    BigInteger CenterSlopeLowerNumerator,
    BigInteger PositiveTrapSlopeNumerator,
    BigInteger SlopeLowerDenominator,
    BigInteger ResidualMagnitudeCeiling
);

/// <summary>
/// A finite quadratic-norm envelope for every integer boundary that a polynomial continued-fraction tail can cross.
/// </summary>
/// <param name="Cutoff">The first tail index covered by the underlying interval certificate.</param>
/// <param name="RadiusNumerator">The integer <c>H</c> in <c>|s_n-(lambda*n+beta)| &lt;= H/n</c>.</param>
/// <param name="Radicand">The common quadratic-field radicand.</param>
/// <param name="CommonDenominator">The positive integer <c>Z</c> clearing the denominators of both <c>lambda</c> and <c>beta</c>.</param>
/// <param name="SlopeRationalNumerator">The integer <c>P</c> in <c>lambda=(P+Q*sqrt(D))/Z</c>.</param>
/// <param name="SlopeSurdNumerator">The integer <c>Q</c> in <c>lambda=(P+Q*sqrt(D))/Z</c>.</param>
/// <param name="OffsetRationalNumerator">The integer <c>R</c> in <c>beta=(R+S*sqrt(D))/Z</c>.</param>
/// <param name="OffsetSurdNumerator">The integer <c>S</c> in <c>beta=(R+S*sqrt(D))/Z</c>.</param>
/// <param name="NormMagnitudeBound">
/// An integer <c>J</c> such that every integer <c>m</c> lying between the tail and its affine center at an index
/// <c>n &gt;= Cutoff</c> has <c>|(Zm-X_n)^2-DY_n^2| &lt;= J</c>, where
/// <c>X_n=P*n+R</c> and <c>Y_n=Q*n+S</c>.
/// </param>
public readonly record struct PolynomialBeattyShadowNormCertificate(
    BigInteger Cutoff,
    BigInteger RadiusNumerator,
    BigInteger Radicand,
    BigInteger CommonDenominator,
    BigInteger SlopeRationalNumerator,
    BigInteger SlopeSurdNumerator,
    BigInteger OffsetRationalNumerator,
    BigInteger OffsetSurdNumerator,
    BigInteger NormMagnitudeBound
) {
    /// <summary>Returns the cleared field norm of <c>boundary-(lambda*n+beta)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tailIndex"/> is not positive.</exception>
    public BigInteger CandidateNorm(BigInteger tailIndex, BigInteger boundary) {
        if (tailIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index must be positive"
            );
        }

        var centerRationalNumerator = ((SlopeRationalNumerator * tailIndex) + OffsetRationalNumerator);
        var centerSurdNumerator = ((SlopeSurdNumerator * tailIndex) + OffsetSurdNumerator);
        var rationalDifference = ((CommonDenominator * boundary) - centerRationalNumerator);

        return ((rationalDifference * rationalDifference) -
            (Radicand * centerSurdNumerator * centerSurdNumerator));
    }
}

/// <summary>
/// Exact analysis of a positive polynomial continued-fraction tail
/// <c>sₙ = p·n + q + (r·n² + u·n + v)/sₙ₊₁</c>.
/// </summary>
/// <remarks>
/// Construction certifies that <c>p·n+q</c> is non-negative and <c>r·n²+u·n+v</c> is positive for every integer
/// <c>n ≥ 1</c>. Under those hypotheses the recurrence has one and only one everywhere-positive tail. Its
/// affine center is <c>xₙ = Slope·n + Offset</c>, and <see cref="IntervalCertificate"/> proves
/// <c>|sₙ−xₙ| ≤ H/n</c> from its cutoff onward. <see cref="AsymptoticCoefficients"/> returns exact coefficients
/// of the full Poincaré expansion to any requested finite order.
/// </remarks>
public sealed class PolynomialContinuedFractionAnalysis {
    internal PolynomialContinuedFractionAnalysis(
        PolynomialContinuedFractionParameters parameters,
        QuadraticSurd slope,
        QuadraticSurd offset,
        QuadraticSurd affineResidual,
        PolynomialTailIntervalCertificate intervalCertificate) {
        Parameters = parameters;
        Slope = slope;
        Offset = offset;
        AffineResidual = affineResidual;
        IntervalCertificate = intervalCertificate;
    }

    /// <summary>Gets the recurrence coefficients.</summary>
    public PolynomialContinuedFractionParameters Parameters { get; }
    /// <summary>Gets the exact positive root of <c>λ²−pλ−r=0</c>.</summary>
    public QuadraticSurd Slope { get; }
    /// <summary>Gets the exact constant term <c>β</c> in the affine center <c>xₙ=λn+β</c>.</summary>
    public QuadraticSurd Offset { get; }
    /// <summary>Gets the exact constant <c>R</c> for which <c>Tₙ(xₙ₊₁)−xₙ=R/xₙ₊₁</c>.</summary>
    public QuadraticSurd AffineResidual { get; }
    /// <summary>Gets the constructive <c>|sₙ−xₙ| ≤ H/n</c> certificate.</summary>
    public PolynomialTailIntervalCertificate IntervalCertificate { get; }

    /// <summary>
    /// Returns an exact finite norm envelope containing every integer boundary that can separate the tail from its
    /// affine center beyond the certified cutoff.
    /// </summary>
    /// <remarks>
    /// Write <c>lambda=(P+Q*sqrt(D))/Z</c> and <c>beta=(R+S*sqrt(D))/Z</c>. If an integer <c>m</c> lies between
    /// <c>s_n</c> and <c>lambda*n+beta</c>, the interval certificate gives
    /// <c>|m-(lambda*n+beta)| &lt;= H/n</c>. Multiplication by the conjugate difference therefore bounds the cleared
    /// norm <c>(Zm-Pn-R)^2-D(Qn+S)^2</c> independently of <c>n</c>. Thus all possible discrepancies reduce to
    /// finitely many generalized Pell equations, the arithmetic bridge to an Ostrowski decision procedure.
    /// </remarks>
    public PolynomialBeattyShadowNormCertificate BeattyShadowNormCertificate() {
        var slopeDenominator = Slope.Denominator;
        var offsetDenominator = Offset.Denominator;
        var denominatorDivisor = BigInteger.GreatestCommonDivisor(slopeDenominator, offsetDenominator);
        var commonDenominator = ((slopeDenominator / denominatorDivisor) * offsetDenominator);
        var slopeScale = (commonDenominator / slopeDenominator);
        var offsetScale = (commonDenominator / offsetDenominator);
        var slopeRationalNumerator = (Slope.RationalNumerator * slopeScale);
        var slopeSurdNumerator = (Slope.SurdNumerator * slopeScale);
        var offsetRationalNumerator = (Offset.RationalNumerator * offsetScale);
        var offsetSurdNumerator = (Offset.SurdNumerator * offsetScale);
        var radicand = BigInteger.Max(Slope.Radicand, Offset.Radicand);
        var conjugateSlopeMagnitudeCeiling = QuadraticSurd.Create(
            rationalNumerator: BigInteger.Zero,
            surdNumerator: (2 * BigInteger.Abs(slopeSurdNumerator)),
            radicand: radicand,
            denominator: commonDenominator
        ).Ceiling();
        var conjugateOffsetMagnitudeCeiling = QuadraticSurd.Create(
            rationalNumerator: BigInteger.Zero,
            surdNumerator: (2 * BigInteger.Abs(offsetSurdNumerator)),
            radicand: radicand,
            denominator: commonDenominator
        ).Ceiling();
        var radius = IntervalCertificate.RadiusNumerator;
        var normMagnitudeBound = (
            commonDenominator * commonDenominator * radius *
            (radius + conjugateSlopeMagnitudeCeiling + conjugateOffsetMagnitudeCeiling)
        );

        return new PolynomialBeattyShadowNormCertificate(
            Cutoff: IntervalCertificate.Cutoff,
            RadiusNumerator: radius,
            Radicand: radicand,
            CommonDenominator: commonDenominator,
            SlopeRationalNumerator: slopeRationalNumerator,
            SlopeSurdNumerator: slopeSurdNumerator,
            OffsetRationalNumerator: offsetRationalNumerator,
            OffsetSurdNumerator: offsetSurdNumerator,
            NormMagnitudeBound: normMagnitudeBound
        );
    }

    /// <summary>Rechecks the finite exact inequalities that imply the interval is positive and invariant for every covered index.</summary>
    public bool VerifyIntervalCertificate() {
        var certificate = IntervalCertificate;
        if ((certificate.Cutoff < 1) || (certificate.RadiusNumerator < 0) ||
            (certificate.PositiveTrapSlopeNumerator <= 0) ||
            (certificate.CenterSlopeLowerNumerator <= certificate.PositiveTrapSlopeNumerator) ||
            (certificate.SlopeLowerDenominator <= 0) ||
            (certificate.ResidualMagnitudeCeiling < 0)) {
            return false;
        }

        var scale = certificate.SlopeLowerDenominator;
        var lowerOneNumerator = certificate.CenterSlopeLowerNumerator;
        var lowerTwoNumerator = certificate.PositiveTrapSlopeNumerator;
        var lowerOne = QuadraticSurd.Rational(lowerOneNumerator, scale);
        var lowerTwo = QuadraticSurd.Rational(lowerTwoNumerator, scale);
        if ((lowerTwo >= lowerOne) || (lowerOne >= Slope) ||
            ((lowerOne * lowerTwo) <= QuadraticSurd.Rational(Parameters.NumeratorQuadratic)) ||
            (QuadraticSurd.Rational(certificate.ResidualMagnitudeCeiling) < AffineResidual.Abs())) {
            return false;
        }

        var successor = (certificate.Cutoff + 1);
        var successorSquared = (successor * successor);
        if ((Slope + (Offset / QuadraticSurd.Rational(successor))) < lowerOne) { return false; }

        var positiveLinear = BigInteger.Max(BigInteger.Zero, Parameters.NumeratorLinear);
        var positiveConstant = BigInteger.Max(BigInteger.Zero, Parameters.NumeratorConstant);
        var wNumerator = ((Parameters.NumeratorQuadratic * successorSquared) +
            (positiveLinear * successor) + positiveConstant);
        var contractionGap = ((successorSquared * lowerOneNumerator * lowerTwoNumerator) -
            (wNumerator * scale * scale));
        if (contractionGap <= 0) { return false; }

        // H >= M/[L1(1-W/(L1L2))], with every rational denominator cleared.
        var requiredRadiusNumerator = (certificate.ResidualMagnitudeCeiling * successorSquared *
            lowerTwoNumerator * scale);
        if ((certificate.RadiusNumerator * contractionGap) < requiredRadiusNumerator) { return false; }

        // H/(N+1)^2 <= L1-L2.
        return ((certificate.RadiusNumerator * scale) <=
            (successorSquared * (lowerOneNumerator - lowerTwoNumerator)));
    }

    /// <summary>Returns the exact affine center <c>xₙ=λn+β</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tailIndex"/> is not positive.</exception>
    public QuadraticSurd AffineCenter(BigInteger tailIndex) {
        ValidateTailIndex(tailIndex: tailIndex);

        return ((Slope * QuadraticSurd.Rational(value: tailIndex)) + Offset);
    }

    /// <summary>Returns the certified interval <c>[xₙ−H/n, xₙ+H/n]</c> containing the unique positive tail.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tailIndex"/> precedes the certificate cutoff.</exception>
    public (QuadraticSurd Lower, QuadraticSurd Upper) CertifiedInterval(BigInteger tailIndex) {
        if (tailIndex < IntervalCertificate.Cutoff) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index precedes the certified asymptotic interval"
            );
        }

        var center = AffineCenter(tailIndex: tailIndex);
        var radius = QuadraticSurd.Rational(
            numerator: IntervalCertificate.RadiusNumerator,
            denominator: tailIndex
        );

        return (center - radius, center + radius);
    }

    /// <summary>Evaluates the recurrence map <c>Tₙ(y)=p·n+q+(r·n²+u·n+v)/y</c> exactly.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index or denominator is not positive.</exception>
    public QuadraticSurd Map(BigInteger tailIndex, QuadraticSurd nextTail) {
        ValidateTailIndex(tailIndex: tailIndex);
        if (nextTail.Sign <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(nextTail),
                message: "the next tail must be positive"
            );
        }

        var baseTerm = ((Parameters.Linear * tailIndex) + Parameters.Constant);
        var numerator = ((Parameters.NumeratorQuadratic * tailIndex * tailIndex) +
            (Parameters.NumeratorLinear * tailIndex) + Parameters.NumeratorConstant);

        return (QuadraticSurd.Rational(value: baseTerm) +
            (QuadraticSurd.Rational(value: numerator) / nextTail));
    }

    /// <summary>
    /// Returns <paramref name="termCount"/> exact coefficients <c>c₀,…,cₘ₋₁</c> such that the unique positive
    /// tail satisfies <c>sₙ = λn + c₀ + c₁/n + ⋯ + cₘ₋₁/nᵐ⁻¹ + O(n⁻ᵐ)</c>.
    /// </summary>
    /// <remarks>The first coefficient is exactly <see cref="Offset"/>. No fixed maximum order is imposed.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="termCount"/> is negative.</exception>
    public IReadOnlyList<QuadraticSurd> AsymptoticCoefficients(int termCount) {
        if (termCount < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(termCount),
                message: "the asymptotic term count must be non-negative"
            );
        }
        if (termCount == 0) { return []; }

        var coefficients = new QuadraticSurd[termCount];
        var lambdaSquared = (Slope * Slope);
        var solveDivisor = (QuadraticSurd.One +
            (QuadraticSurd.Rational(value: Parameters.NumeratorQuadratic) / lambdaSquared));

        for (var order = 0; (order < termCount); ++order) {
            // Factor S(n+1)=(lambda/t)G(t), t=1/n. With the current unknown coefficient set to zero,
            // invert G as a formal series and read the t^order coefficient of the recurrence's right side.
            var maximumDegree = (order + 1);
            var g = new QuadraticSurd[maximumDegree + 1];
            var inverse = new QuadraticSurd[maximumDegree + 1];

            Array.Fill(array: g, value: QuadraticSurd.Zero);
            Array.Fill(array: inverse, value: QuadraticSurd.Zero);
            g[0] = QuadraticSurd.One;
            g[1] = QuadraticSurd.One; // the +lambda in lambda(n+1)

            for (var coefficientIndex = 0; (coefficientIndex < order); ++coefficientIndex) {
                var scaledCoefficient = (coefficients[coefficientIndex] / Slope);
                var firstDegree = (coefficientIndex + 1);

                for (var extraDegree = 0; ((firstDegree + extraDegree) <= maximumDegree); ++extraDegree) {
                    var binomial = NegativeBinomialCoefficient(power: coefficientIndex, degree: extraDegree);
                    g[firstDegree + extraDegree] +=
                        (scaledCoefficient * QuadraticSurd.Rational(value: binomial));
                }
            }

            inverse[0] = QuadraticSurd.One;

            for (var degree = 1; (degree <= maximumDegree); ++degree) {
                var convolution = QuadraticSurd.Zero;

                for (var leftDegree = 1; (leftDegree <= degree); ++leftDegree) {
                    convolution += (g[leftDegree] * inverse[degree - leftDegree]);
                }

                inverse[degree] = -convolution;
            }

            var recurrenceCoefficient =
                (QuadraticSurd.Rational(value: Parameters.NumeratorQuadratic) * inverse[order + 1]) +
                (QuadraticSurd.Rational(value: Parameters.NumeratorLinear) * inverse[order]);

            if (order >= 1) {
                recurrenceCoefficient +=
                    (QuadraticSurd.Rational(value: Parameters.NumeratorConstant) * inverse[order - 1]);
            }

            recurrenceCoefficient /= Slope;
            if (order == 0) {
                recurrenceCoefficient += QuadraticSurd.Rational(value: Parameters.Constant);
            }

            coefficients[order] = (recurrenceCoefficient / solveDivisor);
        }

        if (coefficients[0] != Offset) {
            throw new InvalidOperationException(message: "the formal-series offset disagrees with the affine identity");
        }

        return coefficients;
    }

    /// <summary>
    /// Constructs a finite exact proof of the order-<paramref name="termCount"/> expansion, with an explicit
    /// <c>H/n^termCount</c> remainder valid at every index beyond the returned cutoff.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="termCount"/> is not positive.</exception>
    public PolynomialTailAsymptoticCertificate AsymptoticIntervalCertificate(int termCount) {
        if (termCount <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(termCount),
                message: "the certified asymptotic term count must be positive"
            );
        }

        var coefficients = AsymptoticCoefficients(termCount: termCount);
        var residualPolynomial = TruncatedCenterResidualPolynomial(coefficients: coefficients);
        if (PolynomialDegree(polynomial: residualPolynomial) >= termCount) {
            throw new InvalidOperationException(
                message: "the formal coefficients did not cancel the required recurrence-residual orders"
            );
        }

        ChooseSlopeBounds(
            numeratorQuadratic: Parameters.NumeratorQuadratic,
            out var scale,
            out var lowerOneNumerator,
            out var lowerTwoNumerator
        );

        var lowerOne = QuadraticSurd.Rational(lowerOneNumerator, scale);
        var residualCoefficientMagnitude = PolynomialCoefficientMagnitude(polynomial: residualPolynomial);
        var residualMagnitudeCeiling = (residualCoefficientMagnitude / lowerOne).Ceiling();
        var centerCorrectionMagnitude = coefficients
            .Aggregate(QuadraticSurd.Zero, (sum, coefficient) => sum + coefficient.Abs());
        var positiveLinear = BigInteger.Max(BigInteger.Zero, Parameters.NumeratorLinear);
        var positiveConstant = BigInteger.Max(BigInteger.Zero, Parameters.NumeratorConstant);
        var cutoff = BigInteger.One;

        while (true) {
            var successor = (cutoff + 1);
            var successorSquared = (successor * successor);

            // z_t/t >= lambda-sum(|c_j|)/t for t>=1.
            var centerSlopeLower = (Slope -
                (centerCorrectionMagnitude / QuadraticSurd.Rational(successor)));
            if (centerSlopeLower > lowerOne) {
                var wNumerator = ((Parameters.NumeratorQuadratic * successorSquared) +
                    (positiveLinear * successor) + positiveConstant);
                var contractionGap = ((successorSquared * lowerOneNumerator * lowerTwoNumerator) -
                    (wNumerator * scale * scale));

                if (contractionGap > 0) {
                    var radius = BigIntegerMath.CeilingDivide(
                        numerator: (residualMagnitudeCeiling * successorSquared *
                            lowerOneNumerator * lowerTwoNumerator),
                        denominator: contractionGap
                    );
                    var successorPower = BigInteger.Pow(successor, checked(termCount + 1));

                    if ((radius * scale) <=
                        ((lowerOneNumerator - lowerTwoNumerator) * successorPower)) {
                        var certificate = new PolynomialTailAsymptoticCertificate(
                            Order: termCount,
                            Cutoff: cutoff,
                            RadiusNumerator: radius,
                            CenterSlopeLowerNumerator: lowerOneNumerator,
                            PositiveTrapSlopeNumerator: lowerTwoNumerator,
                            SlopeLowerDenominator: scale,
                            ResidualMagnitudeCeiling: residualMagnitudeCeiling
                        );

                        if (!VerifyAsymptoticIntervalCertificate(certificate: certificate)) {
                            throw new InvalidOperationException(
                                message: "the constructed arbitrary-order asymptotic certificate did not verify"
                            );
                        }

                        return certificate;
                    }
                }
            }

            cutoff *= 2;
        }
    }

    /// <summary>Rechecks all finite exact inequalities in an arbitrary-order asymptotic certificate.</summary>
    public bool VerifyAsymptoticIntervalCertificate(PolynomialTailAsymptoticCertificate certificate) {
        if ((certificate.Order <= 0) || (certificate.Cutoff < 1) ||
            (certificate.RadiusNumerator < 0) ||
            (certificate.PositiveTrapSlopeNumerator <= 0) ||
            (certificate.CenterSlopeLowerNumerator <= certificate.PositiveTrapSlopeNumerator) ||
            (certificate.SlopeLowerDenominator <= 0) ||
            (certificate.ResidualMagnitudeCeiling < 0)) {
            return false;
        }

        var coefficients = AsymptoticCoefficients(termCount: certificate.Order);
        var residualPolynomial = TruncatedCenterResidualPolynomial(coefficients: coefficients);
        if (PolynomialDegree(polynomial: residualPolynomial) >= certificate.Order) { return false; }

        var scale = certificate.SlopeLowerDenominator;
        var lowerOneNumerator = certificate.CenterSlopeLowerNumerator;
        var lowerTwoNumerator = certificate.PositiveTrapSlopeNumerator;
        var lowerOne = QuadraticSurd.Rational(lowerOneNumerator, scale);
        var lowerTwo = QuadraticSurd.Rational(lowerTwoNumerator, scale);
        if ((lowerTwo >= lowerOne) || (lowerOne >= Slope) ||
            ((lowerOne * lowerTwo) <= QuadraticSurd.Rational(Parameters.NumeratorQuadratic))) {
            return false;
        }

        var residualCoefficientMagnitude = PolynomialCoefficientMagnitude(polynomial: residualPolynomial);
        if (QuadraticSurd.Rational(certificate.ResidualMagnitudeCeiling) <
            (residualCoefficientMagnitude / lowerOne)) {
            return false;
        }

        var centerCorrectionMagnitude = coefficients
            .Aggregate(QuadraticSurd.Zero, (sum, coefficient) => sum + coefficient.Abs());
        var successor = (certificate.Cutoff + 1);
        if ((Slope - (centerCorrectionMagnitude / QuadraticSurd.Rational(successor))) <= lowerOne) {
            return false;
        }

        var successorSquared = (successor * successor);
        var positiveLinear = BigInteger.Max(BigInteger.Zero, Parameters.NumeratorLinear);
        var positiveConstant = BigInteger.Max(BigInteger.Zero, Parameters.NumeratorConstant);
        var wNumerator = ((Parameters.NumeratorQuadratic * successorSquared) +
            (positiveLinear * successor) + positiveConstant);
        var contractionGap = ((successorSquared * lowerOneNumerator * lowerTwoNumerator) -
            (wNumerator * scale * scale));
        if (contractionGap <= 0) { return false; }

        var requiredRadiusNumerator = (certificate.ResidualMagnitudeCeiling * successorSquared *
            lowerOneNumerator * lowerTwoNumerator);
        if ((certificate.RadiusNumerator * contractionGap) < requiredRadiusNumerator) { return false; }

        var successorPower = BigInteger.Pow(successor, checked(certificate.Order + 1));
        return ((certificate.RadiusNumerator * scale) <=
            ((lowerOneNumerator - lowerTwoNumerator) * successorPower));
    }

    /// <summary>Returns the exact center of the requested finite asymptotic expansion.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is not positive or the term count is negative.</exception>
    public QuadraticSurd AsymptoticCenter(BigInteger tailIndex, int termCount) {
        ValidateTailIndex(tailIndex: tailIndex);
        var coefficients = AsymptoticCoefficients(termCount: termCount);
        var center = (Slope * QuadraticSurd.Rational(tailIndex));
        var denominatorPower = BigInteger.One;

        for (var index = 0; (index < coefficients.Count); ++index) {
            if (index > 0) { denominatorPower *= tailIndex; }
            center += (coefficients[index] / QuadraticSurd.Rational(denominatorPower));
        }

        return center;
    }

    /// <summary>Returns the certified arbitrary-order interval associated with <paramref name="certificate"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tailIndex"/> precedes the certificate cutoff.</exception>
    /// <exception cref="ArgumentException">The certificate does not verify for this analysis.</exception>
    public (QuadraticSurd Lower, QuadraticSurd Upper) CertifiedAsymptoticInterval(
        BigInteger tailIndex,
        PolynomialTailAsymptoticCertificate certificate) {
        if (!VerifyAsymptoticIntervalCertificate(certificate: certificate)) {
            throw new ArgumentException(
                message: "the asymptotic certificate does not verify for this analysis",
                paramName: nameof(certificate)
            );
        }
        if (tailIndex < certificate.Cutoff) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index precedes the certified asymptotic interval"
            );
        }

        var center = AsymptoticCenter(tailIndex: tailIndex, termCount: certificate.Order);
        var radius = QuadraticSurd.Rational(
            numerator: certificate.RadiusNumerator,
            denominator: BigInteger.Pow(tailIndex, certificate.Order)
        );

        return (center - radius, center + radius);
    }

    /// <summary>
    /// Attempts to evaluate the tail through a certified non-affine rational closed form. The recognized family has
    /// <c>q=v=0</c>, <c>r=2p+4</c>, <c>u=4p+12</c>, and
    /// <c>s_n=(p+2)n+2-2/(n+1)</c>.
    /// </summary>
    public bool TryCertifiedRationalTail(BigInteger tailIndex, out QuadraticSurd tail) {
        ValidateTailIndex(tailIndex);
        var p = Parameters.Linear;
        if (Parameters.Constant.IsZero &&
            (Parameters.NumeratorQuadratic == ((2 * p) + 4)) &&
            (Parameters.NumeratorLinear == ((4 * p) + 12)) &&
            Parameters.NumeratorConstant.IsZero) {
            tail = QuadraticSurd.Rational(
                ((((p + 2) * tailIndex) + 2) * (tailIndex + 1)) - 2,
                tailIndex + 1
            );
            return true;
        }

        tail = QuadraticSurd.Zero;
        return false;
    }

    /// <summary>
    /// Attempts to decide the exact floor of one finite tail by propagating certified far-tail intervals backward.
    /// Failure after the requested rounds means only that the remaining interval straddles an integer; it never guesses
    /// whether the tail equals that boundary.
    /// </summary>
    public bool TryCertifiedFloor(
        BigInteger tailIndex,
        int refinementRounds,
        out BigInteger floor) {
        ValidateTailIndex(tailIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(refinementRounds);

        if (TryCertifiedRationalTail(tailIndex, out var rationalTail)) {
            floor = rationalTail.Floor();
            return true;
        }

        if (AffineResidual == QuadraticSurd.Zero) {
            floor = AffineCenter(tailIndex).Floor();
            return true;
        }

        var farIndex = BigInteger.Max(tailIndex, IntervalCertificate.Cutoff);
        for (var round = 0; round <= refinementRounds; ++round) {
            var interval = CertifiedInterval(farIndex);
            var lower = interval.Lower;
            var upper = interval.Upper;

            for (var index = (farIndex - 1); index >= tailIndex; --index) {
                var nextLower = Map(index, upper);
                var nextUpper = Map(index, lower);
                lower = nextLower;
                upper = nextUpper;
            }

            var lowerFloor = lower.Floor();
            if (lowerFloor == upper.Floor()) {
                floor = lowerFloor;
                return true;
            }

            var distance = BigInteger.Max(BigInteger.One, (farIndex - tailIndex + 1));
            farIndex = (tailIndex + (2 * distance));
            farIndex = BigInteger.Max(farIndex, IntervalCertificate.Cutoff);
        }

        floor = BigInteger.Zero;
        return false;
    }

    private QuadraticSurd[] TruncatedCenterResidualPolynomial(IReadOnlyList<QuadraticSurd> coefficients) {
        var order = coefficients.Count;
        var denominatorPower = (order - 1);
        var centerNumerator = new QuadraticSurd[order + 1];
        Array.Fill(centerNumerator, QuadraticSurd.Zero);
        centerNumerator[order] = Slope;

        for (var index = 0; (index < order); ++index) {
            centerNumerator[order - 1 - index] = coefficients[index];
        }

        var successorCenterNumerator = ShiftPolynomial(centerNumerator, BigInteger.One);
        var baseNumerator = new QuadraticSurd[order + 1];
        Array.Fill(baseNumerator, QuadraticSurd.Zero);
        baseNumerator[denominatorPower] = QuadraticSurd.Rational(Parameters.Constant);
        baseNumerator[denominatorPower + 1] = QuadraticSurd.Rational(Parameters.Linear);
        baseNumerator = AddPolynomials(baseNumerator, ScalePolynomial(centerNumerator, -QuadraticSurd.One));

        var firstProduct = MultiplyPolynomials(baseNumerator, successorCenterNumerator);
        var numeratorPolynomial = new[] {
            QuadraticSurd.Rational(Parameters.NumeratorConstant),
            QuadraticSurd.Rational(Parameters.NumeratorLinear),
            QuadraticSurd.Rational(Parameters.NumeratorQuadratic)
        };
        var shiftedNumerator = ShiftPolynomialByDegree(numeratorPolynomial, denominatorPower);
        var successorPowerPolynomial = new QuadraticSurd[denominatorPower + 1];

        for (var degree = 0; (degree <= denominatorPower); ++degree) {
            successorPowerPolynomial[degree] = QuadraticSurd.Rational(
                BinomialCoefficient(denominatorPower, degree)
            );
        }

        return TrimPolynomial(AddPolynomials(
            firstProduct,
            MultiplyPolynomials(shiftedNumerator, successorPowerPolynomial)
        ));
    }

    private void ChooseSlopeBounds(
        BigInteger numeratorQuadratic,
        out BigInteger scale,
        out BigInteger lowerOneNumerator,
        out BigInteger lowerTwoNumerator) {
        var precisionBits = 8;

        while (true) {
            scale = (BigInteger.One << precisionBits);
            var scaledFloor = (Slope * QuadraticSurd.Rational(scale)).Floor();
            lowerOneNumerator = (scaledFloor - 1);
            lowerTwoNumerator = (scaledFloor - 2);

            if ((lowerTwoNumerator > 0) &&
                ((lowerOneNumerator * lowerTwoNumerator) >
                    (numeratorQuadratic * scale * scale))) {
                return;
            }

            precisionBits = checked(precisionBits * 2);
        }
    }

    private static QuadraticSurd PolynomialCoefficientMagnitude(QuadraticSurd[] polynomial) =>
        polynomial.Aggregate(QuadraticSurd.Zero, (sum, coefficient) => sum + coefficient.Abs());

    private static int PolynomialDegree(QuadraticSurd[] polynomial) {
        for (var degree = (polynomial.Length - 1); (degree >= 0); --degree) {
            if (polynomial[degree] != QuadraticSurd.Zero) { return degree; }
        }

        return -1;
    }

    private static QuadraticSurd[] AddPolynomials(QuadraticSurd[] left, QuadraticSurd[] right) {
        var result = new QuadraticSurd[Math.Max(left.Length, right.Length)];
        Array.Fill(result, QuadraticSurd.Zero);

        for (var index = 0; (index < left.Length); ++index) { result[index] += left[index]; }
        for (var index = 0; (index < right.Length); ++index) { result[index] += right[index]; }

        return TrimPolynomial(result);
    }

    private static QuadraticSurd[] ScalePolynomial(QuadraticSurd[] polynomial, QuadraticSurd scale) =>
        polynomial.Select(coefficient => coefficient * scale).ToArray();

    private static QuadraticSurd[] MultiplyPolynomials(QuadraticSurd[] left, QuadraticSurd[] right) {
        var result = new QuadraticSurd[left.Length + right.Length - 1];
        Array.Fill(result, QuadraticSurd.Zero);

        for (var leftDegree = 0; (leftDegree < left.Length); ++leftDegree) {
            for (var rightDegree = 0; (rightDegree < right.Length); ++rightDegree) {
                result[leftDegree + rightDegree] += (left[leftDegree] * right[rightDegree]);
            }
        }

        return TrimPolynomial(result);
    }

    private static QuadraticSurd[] ShiftPolynomial(QuadraticSurd[] polynomial, BigInteger shift) {
        var result = new QuadraticSurd[polynomial.Length];
        Array.Fill(result, QuadraticSurd.Zero);

        for (var sourceDegree = 0; (sourceDegree < polynomial.Length); ++sourceDegree) {
            var shiftPower = BigInteger.One;

            for (var targetDegree = sourceDegree; (targetDegree >= 0); --targetDegree) {
                result[targetDegree] += (polynomial[sourceDegree] * QuadraticSurd.Rational(
                    BinomialCoefficient(sourceDegree, targetDegree) * shiftPower
                ));
                shiftPower *= shift;
            }
        }

        return TrimPolynomial(result);
    }

    private static QuadraticSurd[] ShiftPolynomialByDegree(QuadraticSurd[] polynomial, int degree) {
        var result = new QuadraticSurd[polynomial.Length + degree];
        Array.Fill(result, QuadraticSurd.Zero);
        Array.Copy(polynomial, 0, result, degree, polynomial.Length);
        return result;
    }

    private static QuadraticSurd[] TrimPolynomial(QuadraticSurd[] polynomial) {
        var degree = PolynomialDegree(polynomial);
        return degree < 0 ? [QuadraticSurd.Zero] : polynomial[..(degree + 1)];
    }

    private static BigInteger BinomialCoefficient(int upper, int lower) {
        if ((lower < 0) || (lower > upper)) { return BigInteger.Zero; }
        lower = Math.Min(lower, upper - lower);
        var result = BigInteger.One;

        for (var index = 1; (index <= lower); ++index) {
            result = ((result * (upper - lower + index)) / index);
        }

        return result;
    }

    private static BigInteger NegativeBinomialCoefficient(int power, int degree) {
        if (degree == 0) { return BigInteger.One; }
        if (power == 0) { return BigInteger.Zero; }

        var magnitude = BigInteger.One;

        for (var index = 1; (index <= degree); ++index) {
            magnitude = ((magnitude * (power + index - 1)) / index);
        }

        return ((degree & 1) == 0) ? magnitude : -magnitude;
    }

    private static void ValidateTailIndex(BigInteger tailIndex) {
        if (tailIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index must be positive"
            );
        }
    }
}

/// <summary>Constructs exact analyses of positive polynomial continued-fraction tails.</summary>
public static class PolynomialContinuedFractionTail {
    /// <summary>
    /// Analyzes <c>sₙ = p·n + q + (r·n² + u·n + v)/sₙ₊₁</c>, proving existence, uniqueness, its exact
    /// affine asymptote, and a constructive <c>H/n</c> interval.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The leading coefficients are not positive, the base polynomial is negative, or the numerator is non-positive at some integer
    /// index <c>n ≥ 1</c>.
    /// </exception>
    public static PolynomialContinuedFractionAnalysis Analyze(
        BigInteger linear,
        BigInteger constant,
        BigInteger numeratorQuadratic,
        BigInteger numeratorLinear,
        BigInteger numeratorConstant) {
        if (linear <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(paramName: nameof(linear), message: "the linear coefficient must be positive");
        }
        if (numeratorQuadratic <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(numeratorQuadratic),
                message: "the quadratic numerator coefficient must be positive"
            );
        }
        if ((linear + constant) < BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(constant),
                message: "the base polynomial must be non-negative at every positive integer index"
            );
        }
        if (!NumeratorIsPositive(
            quadratic: numeratorQuadratic,
            linear: numeratorLinear,
            constant: numeratorConstant)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(numeratorConstant),
                message: "the numerator polynomial must be positive at every positive integer index"
            );
        }

        var parameters = new PolynomialContinuedFractionParameters(
            Linear: linear,
            Constant: constant,
            NumeratorQuadratic: numeratorQuadratic,
            NumeratorLinear: numeratorLinear,
            NumeratorConstant: numeratorConstant
        );
        var discriminant = ((linear * linear) + (4 * numeratorQuadratic));
        var slope = QuadraticSurd.Create(
            rationalNumerator: linear,
            surdNumerator: BigInteger.One,
            radicand: discriminant,
            denominator: 2
        );
        var slopeSquared = (slope * slope);
        var offset = (
            (QuadraticSurd.Rational(value: constant) * slopeSquared) +
            (QuadraticSurd.Rational(value: (numeratorLinear - numeratorQuadratic)) * slope)
        ) / (slopeSquared + QuadraticSurd.Rational(value: numeratorQuadratic));
        var residual = (
            (QuadraticSurd.Rational(value: constant) - offset) * (slope + offset)
        ) + QuadraticSurd.Rational(value: numeratorConstant);
        var certificate = BuildIntervalCertificate(
            parameters: parameters,
            slope: slope,
            offset: offset,
            residual: residual
        );

        var analysis = new PolynomialContinuedFractionAnalysis(
            parameters: parameters,
            slope: slope,
            offset: offset,
            affineResidual: residual,
            intervalCertificate: certificate
        );

        if (!analysis.VerifyIntervalCertificate()) {
            throw new InvalidOperationException(message: "the constructed asymptotic interval certificate did not verify");
        }

        return analysis;
    }

    private static bool NumeratorIsPositive(BigInteger quadratic, BigInteger linear, BigInteger constant) {
        var vertexFloor = BigIntegerMath.FloorDivide(numerator: -linear, denominator: (2 * quadratic));
        var firstCandidate = BigInteger.Max(BigInteger.One, vertexFloor);
        var secondCandidate = BigInteger.Max(BigInteger.One, (vertexFloor + 1));

        return (EvaluateQuadratic(quadratic, linear, constant, firstCandidate) > 0) &&
            (EvaluateQuadratic(quadratic, linear, constant, secondCandidate) > 0) &&
            (EvaluateQuadratic(quadratic, linear, constant, BigInteger.One) > 0);
    }

    private static BigInteger EvaluateQuadratic(
        BigInteger quadratic,
        BigInteger linear,
        BigInteger constant,
        BigInteger value) =>
        ((quadratic * value * value) + (linear * value) + constant);

    private static PolynomialTailIntervalCertificate BuildIntervalCertificate(
        PolynomialContinuedFractionParameters parameters,
        QuadraticSurd slope,
        QuadraticSurd offset,
        QuadraticSurd residual) {
        // Choose exact dyadic L2<L1<lambda with L1*L2>r. Such a pair always exists because lambda^2>r.
        var precisionBits = 8;
        BigInteger scale;
        BigInteger lowerOneNumerator;
        BigInteger lowerTwoNumerator;

        while (true) {
            scale = (BigInteger.One << precisionBits);
            var scaledFloor = (slope * QuadraticSurd.Rational(value: scale)).Floor();
            lowerOneNumerator = (scaledFloor - 1);
            lowerTwoNumerator = (scaledFloor - 2);

            if ((lowerTwoNumerator > 0) &&
                ((lowerOneNumerator * lowerTwoNumerator) >
                    (parameters.NumeratorQuadratic * scale * scale))) {
                break;
            }

            precisionBits = checked(precisionBits * 2);
        }

        var residualMagnitudeCeiling = residual.Abs().Ceiling();
        var positiveLinear = BigInteger.Max(BigInteger.Zero, parameters.NumeratorLinear);
        var positiveConstant = BigInteger.Max(BigInteger.Zero, parameters.NumeratorConstant);
        var cutoff = BigInteger.One;

        while (true) {
            var successor = (cutoff + 1);
            var successorSquared = (successor * successor);
            var lowerOne = QuadraticSurd.Rational(numerator: lowerOneNumerator, denominator: scale);
            var centerRatioLower = (slope +
                (offset / QuadraticSurd.Rational(value: successor)) - lowerOne);

            if (centerRatioLower.Sign > 0) {
                // B_n/(n+1)^2 <= r + max(u,0)/(N+1) + max(v,0)/(N+1)^2 = W.
                var wNumerator = ((parameters.NumeratorQuadratic * successorSquared) +
                    (positiveLinear * successor) + positiveConstant);
                var contractionGap = (
                    (successorSquared * lowerOneNumerator * lowerTwoNumerator) -
                    (wNumerator * scale * scale)
                );

                if (contractionGap > 0) {
                    // H >= |R|/[L1(1-q)], q=W/(L1*L2). The expression below is that rational bound
                    // after clearing all dyadic and (N+1)^2 denominators.
                    var radiusNumerator = (residualMagnitudeCeiling * successorSquared *
                        lowerTwoNumerator * scale);
                    var radius = BigIntegerMath.CeilingDivide(
                        numerator: radiusNumerator,
                        denominator: contractionGap
                    );

                    // This makes x_{n+1}-H/(n+1) >= L2(n+1), keeping the whole next trap positive.
                    if ((radius * scale) <= successorSquared) {
                        return new PolynomialTailIntervalCertificate(
                            Cutoff: cutoff,
                            RadiusNumerator: radius,
                            CenterSlopeLowerNumerator: lowerOneNumerator,
                            PositiveTrapSlopeNumerator: lowerTwoNumerator,
                            SlopeLowerDenominator: scale,
                            ResidualMagnitudeCeiling: residualMagnitudeCeiling
                        );
                    }
                }
            }

            cutoff *= 2;
        }
    }
}
