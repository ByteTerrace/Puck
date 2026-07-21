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
