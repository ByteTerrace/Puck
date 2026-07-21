#:project ../src/Puck.Maths/Puck.Maths.csproj

// Independent exact checks for PolynomialContinuedFractionTail. The production analyzer constructs a symbolic affine
// model and an H/n certificate; this verifier substitutes those results back into the recurrence, checks both interval
// endpoints in exact quadratic arithmetic, and verifies the returned arbitrary-order series as one formal identity.

using System.Numerics;
using Puck.Maths;

var rng = new Random(0x504346);
var surdChecks = 0;

for (var sample = 0; (sample < 20_000); ++sample) {
    var radicand = rng.Next(2, 500);
    var root = IntegerSquareRoot(radicand);
    if ((root * root) == radicand) { continue; }

    var rational = rng.Next(-20_000, 20_001);
    var coefficient = rng.Next(-500, 501);
    var denominator = rng.Next(1, 500);
    var value = QuadraticSurd.Create(rational, coefficient, radicand, denominator);
    var floor = value.Floor();

    if ((SignSurd(rational - (floor * denominator), coefficient, radicand) < 0) ||
        (SignSurd(rational - ((floor + 1) * denominator), coefficient, radicand) >= 0) ||
        (value.Ceiling() != -(-value).Floor())) {
        throw new InvalidOperationException($"quadratic-surd floor failed for ({rational}+{coefficient}sqrt({radicand}))/{denominator}");
    }

    ++surdChecks;
}

Console.WriteLine($"quadratic surd: {surdChecks} mixed-sign exact floor/ceiling inequalities passed");

if ((default(QuadraticSurd) != QuadraticSurd.Zero) || (default(QuadraticSurd).Floor() != BigInteger.Zero)) {
    throw new InvalidOperationException("default QuadraticSurd is not exact zero");
}

var acceptedFamilies = 0;
var rejectedFamilies = 0;
var asymptoticFamilies = 0;

for (var p = 1; (p <= 5); ++p) {
    for (var q = -p; (q <= 2); ++q) {
        for (var r = 1; (r <= 8); ++r) {
            for (var u = -5; (u <= 5); ++u) {
                for (var v = -5; (v <= 5); ++v) {
                    PolynomialContinuedFractionAnalysis analysis;

                    try {
                        analysis = PolynomialContinuedFractionTail.Analyze(
                            linear: p,
                            constant: q,
                            numeratorQuadratic: r,
                            numeratorLinear: u,
                            numeratorConstant: v
                        );
                    } catch (ArgumentOutOfRangeException) {
                        if (BaseAndNumeratorArePositive(p, q, r, u, v)) {
                            throw new InvalidOperationException($"valid family rejected: ({p},{q},{r},{u},{v})");
                        }

                        ++rejectedFamilies;
                        continue;
                    }

                    if (!BaseAndNumeratorArePositive(p, q, r, u, v)) {
                        throw new InvalidOperationException($"invalid family accepted: ({p},{q},{r},{u},{v})");
                    }

                    ++acceptedFamilies;
                    VerifyAffineIdentity(analysis);
                    VerifyCertificateEndpoints(analysis);

                    // Cover every parameter combination analytically, and spread the more expensive formal-series
                    // checks across more than a thousand representatives of the full grid.
                    if (((p * 31) + (q * 17) + (r * 13) + (u * 7) + v) % 19 == 0) {
                        VerifyAsymptoticIdentity(analysis, termCount: 10);
                        ++asymptoticFamilies;
                    }
                }
            }
        }
    }
}

// The difficult contraction regime r >> p^2 ensures uniqueness cannot secretly depend on the crude lower bound p*n.
var wide = PolynomialContinuedFractionTail.Analyze(
    linear: BigInteger.One,
    constant: BigInteger.Zero,
    numeratorQuadratic: (BigInteger.One << 256),
    numeratorLinear: -12345,
    numeratorConstant: (BigInteger.One << 128)
);
VerifyAffineIdentity(wide);
VerifyCertificateEndpoints(wide);
VerifyAsymptoticIdentity(wide, termCount: 12);

var truncationFamilies = new[] {
    PolynomialContinuedFractionTail.Analyze(1, -1, 1, 0, 0),
    PolynomialContinuedFractionTail.Analyze(1, 0, 100, 0, 1),
    PolynomialContinuedFractionTail.Analyze(3, -2, 7, -3, 5),
    PolynomialContinuedFractionTail.Analyze(4, 2, 3, 5, -1),
};
foreach (var family in truncationFamilies) { VerifyExactRationalTruncationsEnterCertificate(family); }

// The original metallic family must be exactly the specialization p=k,q=-1,r=1,u=v=0, including its proved offset.
for (var k = 1; (k <= 64); ++k) {
    var metallic = MetallicPolynomialContinuedFraction.Analyze(k);
    var discriminant = new BigInteger((k * k) + 4);
    var expectedOffset = QuadraticSurd.Create(
        rationalNumerator: -discriminant,
        surdNumerator: -(k + 2),
        radicand: discriminant,
        denominator: (2 * discriminant)
    );

    if (metallic.Offset != expectedOffset) {
        throw new InvalidOperationException($"metallic offset mismatch at k={k}: {metallic.Offset} != {expectedOffset}");
    }
}

VerifyAsymptoticIdentity(MetallicPolynomialContinuedFraction.Analyze(1), termCount: 32);

Console.WriteLine(
    $"general family: {acceptedFamilies} accepted and {rejectedFamilies} rejected parameter tuples; " +
    $"all affine identities and exact H/n endpoint inclusions passed"
);
Console.WriteLine($"asymptotics: {asymptoticFamilies + 1} families satisfy the exact formal recurrence through 10/12 terms; golden case through 32 terms");
Console.WriteLine($"existence witnesses: consecutive exact rational truncations enter the certified interval in {truncationFamilies.Length} distinct regimes");
Console.WriteLine("wide regime: 256-bit numerator-leading coefficient certificate and 12-term expansion passed");
Console.WriteLine("ALL GENERAL POLYNOMIAL CONTINUED-FRACTION CHECKS PASSED");

static void VerifyAffineIdentity(PolynomialContinuedFractionAnalysis analysis) {
    var p = analysis.Parameters;
    var characteristic = ((analysis.Slope * analysis.Slope) -
        (QuadraticSurd.Rational(p.Linear) * analysis.Slope) - QuadraticSurd.Rational(p.NumeratorQuadratic));

    if (characteristic.Sign != 0) {
        throw new InvalidOperationException($"slope failed its characteristic equation: {characteristic}");
    }

    foreach (var n in new BigInteger[] { 1, 2, 17, 1009 }) {
        var next = analysis.AffineCenter(n + 1);
        var residual = (analysis.Map(n, next) - analysis.AffineCenter(n));
        var expected = (analysis.AffineResidual / next);

        if (residual != expected) {
            throw new InvalidOperationException($"affine residual identity failed at n={n}: {residual} != {expected}");
        }
    }
}

static void VerifyCertificateEndpoints(PolynomialContinuedFractionAnalysis analysis) {
    if (!analysis.VerifyIntervalCertificate()) {
        throw new InvalidOperationException($"certificate witness inequalities failed, parameters={analysis.Parameters}");
    }

    var cutoff = analysis.IntervalCertificate.Cutoff;

    foreach (var n in new[] { cutoff, cutoff + 1, (2 * cutoff) + 3, (17 * cutoff) + 11 }) {
        var current = analysis.CertifiedInterval(n);
        var next = analysis.CertifiedInterval(n + 1);

        if ((next.Lower.Sign <= 0) || (analysis.Map(n, next.Upper) < current.Lower) ||
            (analysis.Map(n, next.Lower) > current.Upper)) {
            throw new InvalidOperationException(
                $"certified interval is not invariant at n={n}, parameters={analysis.Parameters}"
            );
        }
    }
}

static void VerifyAsymptoticIdentity(PolynomialContinuedFractionAnalysis analysis, int termCount) {
    var coefficients = analysis.AsymptoticCoefficients(termCount);
    if ((coefficients.Count != termCount) || (coefficients[0] != analysis.Offset)) {
        throw new InvalidOperationException("asymptotic coefficient count/offset mismatch");
    }

    var maximumDegree = termCount;
    var g = Enumerable.Repeat(QuadraticSurd.Zero, maximumDegree + 1).ToArray();
    var inverse = Enumerable.Repeat(QuadraticSurd.Zero, maximumDegree + 1).ToArray();
    g[0] = QuadraticSurd.One;
    g[1] = QuadraticSurd.One;

    for (var coefficientIndex = 0; (coefficientIndex < termCount); ++coefficientIndex) {
        var scaled = (coefficients[coefficientIndex] / analysis.Slope);
        var firstDegree = (coefficientIndex + 1);

        for (var extra = 0; ((firstDegree + extra) <= maximumDegree); ++extra) {
            g[firstDegree + extra] +=
                (scaled * QuadraticSurd.Rational(NegativeBinomial(coefficientIndex, extra)));
        }
    }

    inverse[0] = QuadraticSurd.One;
    for (var degree = 1; (degree <= maximumDegree); ++degree) {
        for (var left = 1; (left <= degree); ++left) {
            inverse[degree] -= (g[left] * inverse[degree - left]);
        }
    }

    var parameters = analysis.Parameters;
    if ((QuadraticSurd.Rational(parameters.Linear) +
        (QuadraticSurd.Rational(parameters.NumeratorQuadratic) / analysis.Slope)) != analysis.Slope) {
        throw new InvalidOperationException("formal recurrence leading coefficient failed");
    }

    for (var order = 0; (order < termCount); ++order) {
        var right =
            (QuadraticSurd.Rational(parameters.NumeratorQuadratic) * inverse[order + 1]) +
            (QuadraticSurd.Rational(parameters.NumeratorLinear) * inverse[order]);

        if (order >= 1) {
            right += (QuadraticSurd.Rational(parameters.NumeratorConstant) * inverse[order - 1]);
        }

        right /= analysis.Slope;
        if (order == 0) { right += QuadraticSurd.Rational(parameters.Constant); }

        if (right != coefficients[order]) {
            throw new InvalidOperationException(
                $"formal recurrence coefficient {order} failed: {right} != {coefficients[order]}"
            );
        }
    }
}

static void VerifyExactRationalTruncationsEnterCertificate(PolynomialContinuedFractionAnalysis analysis) {
    var target = checked((int)analysis.IntervalCertificate.Cutoff);
    var interval = analysis.CertifiedInterval(target);
    var extraDepth = 32;

    while (true) {
        var shallow = TruncatedTail(analysis, target, checked(target + extraDepth));
        var deep = TruncatedTail(analysis, target, checked(target + extraDepth + 1));

        if ((shallow >= interval.Lower) && (shallow <= interval.Upper) &&
            (deep >= interval.Lower) && (deep <= interval.Upper)) {
            return;
        }

        extraDepth = checked(extraDepth * 2);
        if (extraDepth > 4096) {
            throw new InvalidOperationException($"exact rational truncations did not enter certificate: {analysis.Parameters}");
        }
    }
}

static QuadraticSurd TruncatedTail(PolynomialContinuedFractionAnalysis analysis, int target, int depth) {
    var p = analysis.Parameters;
    var depthInteger = new BigInteger(depth);
    var numerator = ((p.Linear * depthInteger) + p.Constant);
    var denominator = BigInteger.One;

    for (var n = (depth - 1); (n >= target); --n) {
        var index = new BigInteger(n);
        var baseTerm = ((p.Linear * index) + p.Constant);
        var fractionNumerator = ((p.NumeratorQuadratic * index * index) +
            (p.NumeratorLinear * index) + p.NumeratorConstant);
        var previousNumerator = numerator;

        numerator = ((baseTerm * numerator) + (fractionNumerator * denominator));
        denominator = previousNumerator;

        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
    }

    return QuadraticSurd.Rational(numerator, denominator);
}

static BigInteger NegativeBinomial(int power, int degree) {
    if (degree == 0) { return BigInteger.One; }
    if (power == 0) { return BigInteger.Zero; }

    var magnitude = BigInteger.One;
    for (var index = 1; (index <= degree); ++index) {
        magnitude = ((magnitude * (power + index - 1)) / index);
    }

    return ((degree & 1) == 0) ? magnitude : -magnitude;
}

static bool BaseAndNumeratorArePositive(int p, int q, int r, int u, int v) {
    if ((p <= 0) || (r <= 0) || ((p + q) < 0)) { return false; }

    for (var n = 1; (n <= 100); ++n) {
        if (((r * n * n) + (u * n) + v) <= 0) { return false; }
    }

    return true;
}

static int IntegerSquareRoot(int value) => (int)Math.Floor(Math.Sqrt(value));

static int SignSurd(BigInteger rational, BigInteger coefficient, BigInteger radicand) {
    if (coefficient.IsZero) { return rational.Sign; }
    if ((rational.Sign >= 0) && (coefficient.Sign >= 0)) { return 1; }
    if ((rational.Sign <= 0) && (coefficient.Sign <= 0)) { return -1; }

    var comparison = (rational * rational).CompareTo((coefficient * coefficient) * radicand);
    return (rational.Sign > 0) ? comparison : -comparison;
}
