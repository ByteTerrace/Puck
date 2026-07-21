using System.Collections.ObjectModel;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A finite exact certificate for a rational polynomial continued-fraction tail with a polynomial denominator.
/// </summary>
/// <remarks>
/// If <c>B</c> is the monic polynomial whose ascending coefficients are stored in
/// <see cref="DenominatorCoefficients"/> and <c>m=degree(B)</c>, the certified tail is
/// <c>s_n=B(n-1)*(lambda*n+beta+m*lambda)/B(n)</c>.
/// </remarks>
public sealed class PolynomialRationalTailCertificate {
    private readonly QuadraticSurd[] denominatorCoefficients;
    private readonly ReadOnlyCollection<QuadraticSurd> denominatorCoefficientView;

    public PolynomialRationalTailCertificate(
        QuadraticSurd slope,
        QuadraticSurd offset,
        IEnumerable<QuadraticSurd> denominatorCoefficients) {
        ArgumentNullException.ThrowIfNull(denominatorCoefficients);
        this.denominatorCoefficients = denominatorCoefficients.ToArray();
        denominatorCoefficientView = Array.AsReadOnly(this.denominatorCoefficients);
        Slope = slope;
        Offset = offset;
    }

    /// <summary>Gets the exact rational or real-quadratic asymptotic slope.</summary>
    public QuadraticSurd Slope { get; }
    /// <summary>Gets the exact affine offset in the characteristic field.</summary>
    public QuadraticSurd Offset { get; }
    /// <summary>Gets the denominator coefficients in ascending degree order.</summary>
    public IReadOnlyList<QuadraticSurd> DenominatorCoefficients => denominatorCoefficientView;
    /// <summary>Gets the degree of the monic denominator.</summary>
    public int DenominatorDegree => (denominatorCoefficients.Length - 1);

    /// <summary>Evaluates the certified closed form at a positive integer index.</summary>
    public QuadraticSurd Evaluate(BigInteger tailIndex) {
        if (tailIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index must be positive"
            );
        }
        if (denominatorCoefficients.Length == 0) {
            throw new InvalidOperationException(message: "the denominator polynomial is empty");
        }

        var degree = QuadraticSurd.Rational(DenominatorDegree);
        var numeratorFactor = ((Slope * QuadraticSurd.Rational(tailIndex)) + Offset + (degree * Slope));
        var denominator = EvaluatePolynomial(denominatorCoefficients, QuadraticSurd.Rational(tailIndex));
        var previousDenominator = EvaluatePolynomial(
            denominatorCoefficients,
            QuadraticSurd.Rational(tailIndex - 1)
        );
        return ((previousDenominator * numeratorFactor) / denominator);
    }

    private static QuadraticSurd EvaluatePolynomial(
        IReadOnlyList<QuadraticSurd> coefficients,
        QuadraticSurd value) {
        var result = QuadraticSurd.Zero;
        for (var index = (coefficients.Count - 1); index >= 0; --index) {
            result = ((result * value) + coefficients[index]);
        }
        return result;
    }
}

public sealed partial class PolynomialContinuedFractionAnalysis {
    /// <summary>
    /// The largest denominator degree accepted by the dense exact recognizer. This bounds its quadratic storage and
    /// cubic Gaussian-elimination work; certificates above the bound are rejected rather than risking process-wide
    /// resource exhaustion.
    /// </summary>
    public const int MaximumRationalTailDenominatorDegree = 128;

    /// <summary>
    /// The largest finite positive-index prefix scanned by the exact pole check. A candidate requiring a longer scan is
    /// conservatively left unrecognized so adversarial coefficient magnitudes cannot turn verification into work
    /// proportional to their numeric value.
    /// </summary>
    public const int MaximumRationalTailPoleChecks = 1_000_000;

    private readonly Lazy<PolynomialRationalTailCertificate?> rationalTailCertificate;

    /// <summary>
    /// Attempts to construct a complete finite certificate for a positive rational-function tail. For a reduced
    /// rational solution, pole cancellation forces a monic denominator <c>B</c> and linear factors <c>C,K</c> with
    /// <c>s_n=B(n-1)C(n-1)/B(n)</c> and <c>r*n^2+u*n+v=C(n)K(n)</c>. Asymptotics leave at most two possible degrees
    /// for <c>B</c>; each remaining polynomial identity is solved by exact Gaussian elimination in
    /// <c>Q(lambda)</c>. The current dense solver admits degrees through
    /// <see cref="MaximumRationalTailDenominatorDegree"/> and rejects larger candidates without allocating them.
    /// </summary>
    public bool TryRationalTailCertificate(out PolynomialRationalTailCertificate certificate) {
        var recognized = rationalTailCertificate.Value;
        certificate = recognized!;
        return recognized is not null;
    }

    private PolynomialRationalTailCertificate? RecognizeRationalTailCertificate() {
        foreach (var degree in RationalDenominatorDegreeCandidates(MaximumRationalTailDenominatorDegree)) {
            var denominator = SolveMonicDenominator(degree);
            if (denominator is null) { continue; }

            var candidate = new PolynomialRationalTailCertificate(Slope, Offset, denominator);
            if (VerifyRationalTailCertificate(candidate)) {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Independently verifies the factorization identity, recurrence coefficients, absence of positive-integer poles,
    /// and eventual positivity for a bounded-degree rational-tail certificate.
    /// </summary>
    public bool VerifyRationalTailCertificate(PolynomialRationalTailCertificate certificate) {
        ArgumentNullException.ThrowIfNull(certificate);
        var coefficients = certificate.DenominatorCoefficients;
        if ((certificate.Slope != Slope) || (certificate.Offset != Offset) ||
            (coefficients.Count == 0) ||
            (coefficients.Count > (MaximumRationalTailDenominatorDegree + 1)) ||
            !coefficients.All(BelongsToCharacteristicField) ||
            (coefficients[^1] != QuadraticSurd.One)) {
            return false;
        }

        var degree = certificate.DenominatorDegree;
        var p = QuadraticSurd.Rational(Parameters.Linear);
        var q = QuadraticSurd.Rational(Parameters.Constant);
        var cConstant = (certificate.Offset + (QuadraticSurd.Rational(degree + 1) * certificate.Slope));
        var kSlope = (certificate.Slope - p);
        var kConstant = (certificate.Offset - q - (QuadraticSurd.Rational(degree) * kSlope));

        // Every positive certified tail has cConstant>0.  If it were non-positive, the base constraint and
        // q=c0-k0-lambda-m(lambda+mu) would force both C and K negative through index m+1.  Positivity of
        // B(n-1)C(n-1)/B(n) would then make the degree-m polynomial B alternate sign more than m times.
        if (cConstant.Sign <= 0 ||
            (certificate.Slope * kSlope) != QuadraticSurd.Rational(Parameters.NumeratorQuadratic) ||
            ((certificate.Slope * kConstant) + (cConstant * kSlope)) !=
                QuadraticSurd.Rational(Parameters.NumeratorLinear) ||
            (cConstant * kConstant) != QuadraticSurd.Rational(Parameters.NumeratorConstant)
        ) {
            return false;
        }

        var identity = PolynomialIdentityContributions(
            maximumDegree: degree,
            cConstant: cConstant,
            kSlope: kSlope,
            kConstant: kConstant
        );
        for (var power = 0; power < identity[0].Length; ++power) {
            var coefficient = QuadraticSurd.Zero;
            for (var denominatorPower = 0; denominatorPower <= degree; ++denominatorPower) {
                coefficient += (coefficients[denominatorPower] * identity[denominatorPower][power]);
            }
            if (coefficient != QuadraticSurd.Zero) { return false; }
        }

        return MonicPolynomialHasNoPositiveIntegerZero(coefficients);
    }

    private IReadOnlyList<int> RationalDenominatorDegreeCandidates(int maximumDegree) {
        var p = QuadraticSurd.Rational(Parameters.Linear);
        var q = QuadraticSurd.Rational(Parameters.Constant);
        var v = QuadraticSurd.Rational(Parameters.NumeratorConstant);
        var kSlope = (Slope - p);
        var firstC = (Offset + Slope);
        var firstK = (Offset - q);

        // (firstC + m*lambda)*(firstK - m*(lambda-p)) = v.
        var quadratic = -(Slope * kSlope);
        var linear = ((Slope * firstK) - (kSlope * firstC));
        var constant = ((firstC * firstK) - v);
        if (!quadratic.IsRational) {
            throw new InvalidOperationException("the denominator-degree quadratic coefficient must be rational");
        }

        // The m^2 coefficient is -lambda*(lambda-p)=-r and is rational.  If a surd component remains, it is
        // therefore linear in m and determines the sole possible integer degree.  Otherwise solve the rational
        // quadratic as before.
        var linearSurd = linear.IsRational ? BigInteger.Zero : linear.SurdNumerator;
        var constantSurd = constant.IsRational ? BigInteger.Zero : constant.SurdNumerator;
        if (!linear.IsRational && !constant.IsRational && (linear.Radicand != constant.Radicand)) {
            return [];
        }
        if (!linearSurd.IsZero) {
            var numerator = (-constantSurd * linear.Denominator);
            var denominator = (linearSurd * constant.Denominator);
            var candidate = BigInteger.DivRem(numerator, denominator, out var remainder);
            if (!remainder.IsZero || candidate < BigInteger.Zero || candidate > maximumDegree) { return []; }
            var degree = (int)candidate;
            var value = ((quadratic * QuadraticSurd.Rational(degree * (BigInteger)degree)) +
                (linear * QuadraticSurd.Rational(degree)) + constant);
            return value == QuadraticSurd.Zero ? [degree] : [];
        }
        if (!constantSurd.IsZero) { return []; }

        var scale = LeastCommonMultiple(
            quadratic.Denominator,
            LeastCommonMultiple(linear.Denominator, constant.Denominator)
        );
        var a = (quadratic.RationalNumerator * (scale / quadratic.Denominator));
        var b = (linear.RationalNumerator * (scale / linear.Denominator));
        var c = (constant.RationalNumerator * (scale / constant.Denominator));
        var discriminant = ((b * b) - (4 * a * c));
        if (discriminant < BigInteger.Zero) { return []; }

        var root = BigIntegerMath.SquareRoot(discriminant);
        if ((root * root) != discriminant) { return []; }

        var result = new List<int>(capacity: 2);
        AddRoot(-b + root);
        AddRoot(-b - root);
        result.Sort();
        return result;

        void AddRoot(BigInteger numerator) {
            var denominator = (2 * a);
            var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
            if (!remainder.IsZero || (quotient < BigInteger.Zero) || (quotient > maximumDegree)) { return; }
            var degree = (int)quotient;
            if (!result.Contains(degree)) { result.Add(degree); }
        }
    }

    private bool BelongsToCharacteristicField(QuadraticSurd value) {
        if (value.IsRational) { return true; }
        if (!Slope.IsRational) { return value.Radicand == Slope.Radicand; }
        return !Offset.IsRational && (value.Radicand == Offset.Radicand);
    }

    private QuadraticSurd[]? SolveMonicDenominator(int degree) {
        var p = QuadraticSurd.Rational(Parameters.Linear);
        var q = QuadraticSurd.Rational(Parameters.Constant);
        var cConstant = (Offset + (QuadraticSurd.Rational(degree + 1) * Slope));
        var kSlope = (Slope - p);
        var kConstant = (Offset - q - (QuadraticSurd.Rational(degree) * kSlope));
        var contributions = PolynomialIdentityContributions(degree, cConstant, kSlope, kConstant);
        if (degree == 0) {
            return contributions[0].All(value => value == QuadraticSurd.Zero)
                ? [QuadraticSurd.One]
                : null;
        }

        var rowCount = (degree + 2);
        var matrix = new QuadraticSurd[rowCount][];
        for (var row = 0; row < rowCount; ++row) {
            matrix[row] = new QuadraticSurd[degree + 1];
            for (var column = 0; column < degree; ++column) {
                matrix[row][column] = contributions[column][row];
            }
            matrix[row][degree] = -contributions[degree][row];
        }

        var pivotRow = 0;
        var pivots = new int[degree];
        Array.Fill(pivots, -1);
        for (var column = 0; (column < degree) && (pivotRow < rowCount); ++column) {
            var selected = pivotRow;
            while ((selected < rowCount) && (matrix[selected][column] == QuadraticSurd.Zero)) { ++selected; }
            if (selected == rowCount) { continue; }
            (matrix[pivotRow], matrix[selected]) = (matrix[selected], matrix[pivotRow]);

            var divisor = matrix[pivotRow][column];
            for (var index = column; index <= degree; ++index) { matrix[pivotRow][index] /= divisor; }
            for (var row = 0; row < rowCount; ++row) {
                if ((row == pivotRow) || (matrix[row][column] == QuadraticSurd.Zero)) { continue; }
                var multiplier = matrix[row][column];
                for (var index = column; index <= degree; ++index) {
                    matrix[row][index] -= (multiplier * matrix[pivotRow][index]);
                }
            }
            pivots[column] = pivotRow++;
        }

        for (var row = 0; row < rowCount; ++row) {
            if (matrix[row].Take(degree).All(value => value == QuadraticSurd.Zero) &&
                (matrix[row][degree] != QuadraticSurd.Zero)) {
                return null;
            }
        }
        if (pivots.Any(row => row < 0)) { return null; }

        var result = new QuadraticSurd[degree + 1];
        for (var column = 0; column < degree; ++column) { result[column] = matrix[pivots[column]][degree]; }
        result[degree] = QuadraticSurd.One;
        return result;
    }

    private QuadraticSurd[][] PolynomialIdentityContributions(
        int maximumDegree,
        QuadraticSurd cConstant,
        QuadraticSurd kSlope,
        QuadraticSurd kConstant) {
        var p = QuadraticSurd.Rational(Parameters.Linear);
        var q = QuadraticSurd.Rational(Parameters.Constant);
        var result = new QuadraticSurd[maximumDegree + 1][];

        for (var basisDegree = 0; basisDegree <= maximumDegree; ++basisDegree) {
            var contribution = new QuadraticSurd[maximumDegree + 2];
            var previous = ShiftedMonomial(basisDegree, shift: -1);
            var next = ShiftedMonomial(basisDegree, shift: 1);
            AddLinearProduct(contribution, previous, Slope, cConstant - Slope, QuadraticSurd.One);
            contribution[basisDegree] -= q;
            contribution[basisDegree + 1] -= p;
            AddLinearProduct(contribution, next, kSlope, kConstant, -QuadraticSurd.One);
            result[basisDegree] = contribution;
        }

        return result;
    }

    private static QuadraticSurd[] ShiftedMonomial(int degree, int shift) {
        var result = new QuadraticSurd[degree + 1];
        for (var power = 0; power <= degree; ++power) {
            var sign = ((shift < 0) && (((degree - power) & 1) != 0)) ? -1 : 1;
            result[power] = QuadraticSurd.Rational(sign * BinomialCoefficient(degree, power));
        }
        return result;
    }

    private static void AddLinearProduct(
        QuadraticSurd[] target,
        IReadOnlyList<QuadraticSurd> polynomial,
        QuadraticSurd linear,
        QuadraticSurd constant,
        QuadraticSurd scale) {
        for (var power = 0; power < polynomial.Count; ++power) {
            target[power] += (scale * constant * polynomial[power]);
            target[power + 1] += (scale * linear * polynomial[power]);
        }
    }

    private static bool MonicPolynomialHasNoPositiveIntegerZero(IReadOnlyList<QuadraticSurd> polynomial) {
        var cutoff = BigInteger.Zero;
        while (true) {
            var translated = TranslatePolynomial(polynomial, cutoff);
            if ((translated[0].Sign > 0) && translated.All(coefficient => coefficient.Sign >= 0)) { break; }
            cutoff = cutoff.IsZero ? BigInteger.One : (2 * cutoff);
            if (cutoff > MaximumRationalTailPoleChecks) { return false; }
        }

        for (var index = BigInteger.One; index < cutoff; ++index) {
            if (EvaluatePolynomial(polynomial, index) == QuadraticSurd.Zero) { return false; }
        }
        return true;
    }

    private static QuadraticSurd[] TranslatePolynomial(
        IReadOnlyList<QuadraticSurd> polynomial,
        BigInteger shift) {
        var result = new QuadraticSurd[polynomial.Count];
        for (var oldPower = 0; oldPower < polynomial.Count; ++oldPower) {
            var shiftPower = BigInteger.One;
            for (var newPower = oldPower; newPower >= 0; --newPower) {
                result[newPower] += polynomial[oldPower] * QuadraticSurd.Rational(
                    BinomialCoefficient(oldPower, newPower) * shiftPower
                );
                shiftPower *= shift;
            }
        }
        return result;
    }

    private static QuadraticSurd EvaluatePolynomial(
        IReadOnlyList<QuadraticSurd> polynomial,
        BigInteger value) {
        var result = QuadraticSurd.Zero;
        var argument = QuadraticSurd.Rational(value);
        for (var power = (polynomial.Count - 1); power >= 0; --power) {
            result = ((result * argument) + polynomial[power]);
        }
        return result;
    }
}
