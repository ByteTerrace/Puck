using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims over <c>SimplestRational</c>, <c>BeattyQuantization</c> with its certificate, and
/// <c>ContinuedFraction.Convergents</c>.
/// </summary>
/// <remarks>
/// Every reference computation below is written out in this file rather than calling <c>Oracles.cs</c> or any
/// <c>Puck.Maths</c> kernel, per the shared-nothing discipline. The reference side never touches
/// <c>QuadraticSurd</c>: a floor of <c>n·(a + b·√d)/c</c> is an integer square root plus a floor division, a
/// comparison against a surd is a cleared-denominator sign evaluation (<see cref="SurdSign"/>), and a comparison of
/// two distances <c>|A + B·√d|</c> is a squared cross-comparison (<see cref="MagnitudeCompare"/>) — no square root
/// is ever approximated and no floating point appears anywhere.
/// </remarks>
internal static class QuantizationClaims {
    private static readonly ((long A, long B, long D, long C) Slope, int FractionBits)[] CertifyBattery = [
        ((0L, 1L, 2L, 1L), 0),
        ((0L, 1L, 2L, 1L), 2),
        ((0L, 1L, 2L, 1L), 4),
        ((0L, 1L, 2L, 1L), 6),
        ((0L, 1L, 2L, 1L), 8),
        ((0L, 1L, 3L, 1L), 3),
        ((0L, 1L, 3L, 1L), 5),
        ((1L, 1L, 5L, 2L), 4),
        ((1L, 1L, 5L, 2L), 7),
        ((0L, 1L, 5L, 1L), 6),
        ((3L, 1L, 29L, 2L), 5),
    ];
    private static readonly ((long A, long B, long D, long C) Value, int Count)[] ConvergentBattery = [
        ((1L, 1L, 5L, 2L), 12),
        ((0L, 1L, 2L, 1L), 10),
        ((0L, 1L, 3L, 1L), 10),
        ((0L, 1L, 7L, 1L), 9),
        ((3L, 1L, 29L, 2L), 5),
    ];
    private static readonly ((long A, long B, long D, long C) Low, (long A, long B, long D, long C) High)[] IntervalBattery = [
        ((0L, 1L, 2L, 1L), (0L, 1L, 3L, 1L)),
        ((1L, 1L, 5L, 2L), (5L, 0L, 0L, 3L)),
        ((41L, 0L, 0L, 29L), (0L, 1L, 2L, 1L)),
        ((0L, 1L, 5L, 1L), (9L, 0L, 0L, 4L)),
        ((1L, 0L, 0L, 3L), (1L, 0L, 0L, 2L)),
        ((0L, 1L, 7L, 1L), (8L, 0L, 0L, 3L)),
    ];
    private static readonly (long A, long B, long D, long C)[] QuantizeBattery = [
        (0L, 1L, 2L, 1L),
        (0L, 1L, 3L, 1L),
        (1L, 1L, 5L, 2L),
        (3L, 1L, 29L, 2L),
        (0L, 1L, 13L, 3L),
        (5L, 2L, 7L, 3L),
        (5L, 0L, 0L, 2L),
        (7L, 0L, 0L, 2L),
        (3L, 0L, 0L, 4L),
        (1L, 0L, 0L, 3L),
    ];
    private static readonly int[] QuantizeFractionBits = [0, 1, 4, 16];
    private static readonly (long ExactNumerator, long ExactDenominator, long ApproximateNumerator, long ApproximateDenominator)[] RationalPairBattery = [
        (7L, 5L, 3L, 2L),
        (3L, 2L, 7L, 5L),
        (355L, 113L, 22L, 7L),
        (1L, 3L, 2L, 5L),
    ];

    private static BigInteger FloorDivide(BigInteger numerator, BigInteger denominator) {
        var quotient = BigInteger.DivRem(
            dividend: numerator,
            divisor: denominator,
            remainder: out var remainder
        );

        return (((remainder.Sign != 0) && ((remainder.Sign < 0) != (denominator.Sign < 0)))
            ? (quotient - BigInteger.One)
            : quotient
        );
    }
    /// <summary>The exact integer square root by bit-length seed and Newton descent, settled by exact squaring; independent of every <c>Puck.Maths</c> root.</summary>
    private static BigInteger IntegerSquareRoot(BigInteger value) {
        if (value.Sign <= 0) { return BigInteger.Zero; }

        var root = (BigInteger.One << ((int)((value.GetBitLength() + 1L) / 2L)));

        while (true) {
            var next = ((root + (value / root)) >> 1);

            if (next >= root) { break; }

            root = next;
        }

        while ((root * root) > value) { root -= BigInteger.One; }
        while (((root + BigInteger.One) * (root + BigInteger.One)) <= value) { root += BigInteger.One; }

        return root;
    }
    /// <summary>Compares <c>|a1 + b1·√d|</c> against <c>|a2 + b2·√d|</c> by comparing squares.</summary>
    private static int MagnitudeCompare(BigInteger a1, BigInteger b1, BigInteger a2, BigInteger b2, BigInteger d) {
        var rational = (((a1 * a1) + ((b1 * b1) * d)) - ((a2 * a2) + ((b2 * b2) * d)));
        var surd = (2 * ((a1 * b1) - (a2 * b2)));

        return SurdSign(
            a: rational,
            b: surd,
            d: d
        );
    }
    /// <summary>The reference nearest-grid decision: floor, then a cleared half-step comparison, ties to the even numerator.</summary>
    private static (BigInteger Numerator, int RoundingSign) OracleQuantize(long a, long b, long d, long c, int fractionBits) {
        var scale = (BigInteger.One << fractionBits);
        var floor = ScaledFloor(
            a: a,
            b: b,
            c: c,
            d: d,
            n: scale
        );
        var rationalPart = ((scale * a) - (c * floor));
        var surdPart = (scale * b);
        var halfComparison = SurdSign(
            a: ((2 * rationalPart) - c),
            b: (2 * surdPart),
            d: d
        );

        if (halfComparison < 0) {
            var isExact = (SurdSign(
                a: rationalPart,
                b: surdPart,
                d: d
            ) == 0);

            return (floor, (isExact
                ? 0
                : -1));
        }
        if (halfComparison > 0) { return ((floor + BigInteger.One), 1); }

        return (floor.IsEven
            ? (floor, -1)
            : ((floor + BigInteger.One), 1)
        );
    }
    /// <summary>The exact <c>⌊n·(a + b·√d)/c⌋</c> for <c>b ≥ 0</c>, <c>c &gt; 0</c>, <c>n ≥ 0</c>.</summary>
    private static BigInteger ScaledFloor(long a, long b, long d, long c, BigInteger n) {
        var surdPart = IntegerSquareRoot(value: (((n * n) * (b * b)) * d));

        return FloorDivide(
            denominator: c,
            numerator: ((n * a) + surdPart)
        );
    }
    private static QuadraticSurd Surd(long a, long b, long d, long c) => ((b == 0L)
        ? QuadraticSurd.Rational(
            denominator: c,
            numerator: a
        )
        : QuadraticSurd.Create(
            denominator: c,
            radicand: d,
            rationalNumerator: a,
            surdNumerator: b
        )
    );
    /// <summary>The sign of <c>a + b·√d</c> for <c>d ≥ 0</c>, by squared cross-comparison — no root is taken.</summary>
    private static int SurdSign(BigInteger a, BigInteger b, BigInteger d) {
        if (b.IsZero) { return a.Sign; }
        if (
            (a.Sign >= 0) &&
            (b.Sign > 0)
        ) { return 1; }
        if (
            (a.Sign <= 0) &&
            (b.Sign < 0)
        ) { return -1; }

        var rationalSquare = (a * a);
        var surdSquare = ((b * b) * d);
        var comparison = rationalSquare.CompareTo(other: surdSquare);

        if (comparison == 0) { return 0; }

        // a and b have opposite signs; the larger square decides which term dominates.
        return ((comparison > 0)
            ? a.Sign
            : b.Sign
        );
    }

    /// <summary>Convergents are coprime, alternate sides, strictly shrink the residual, and their distinct denominators are exactly the closest-approach record indices; mismatched spans refuse.</summary>
    public static string? ConvergentsAreClosestApproachRecords() {
        foreach (var ((a, b, d, c), count) in ConvergentBattery) {
            var numerators = new BigInteger[count];
            var denominators = new BigInteger[count];

            ContinuedFraction.Convergents(
                p: a,
                q: b,
                d: d,
                r: c,
                numerators: numerators,
                denominators: denominators
            );

            for (var k = 0; (k < count); ++k) {
                if (!BigInteger.GreatestCommonDivisor(
                    left: numerators[k],
                    right: denominators[k]
                ).IsOne) {
                    return $"convergent {numerators[k]}/{denominators[k]} of (({a}+{b}*sqrt({d}))/{c}) is not in lowest terms";
                }

                // p_k - q_k*x cleared by c is (c*p_k - q_k*a) - q_k*b*sqrt(d).
                var side = SurdSign(
                    a: ((c * numerators[k]) - (denominators[k] * a)),
                    b: (-(denominators[k] * b)),
                    d: d
                );

                if (side != (((k % 2) == 0)
                    ? -1
                    : 1)) {
                    return $"convergent {k} of (({a}+{b}*sqrt({d}))/{c}) sits on the wrong side";
                }
                if (k > 0) {
                    var shrinks = (MagnitudeCompare(
                        a1: ((denominators[k] * a) - (c * numerators[k])),
                        b1: (denominators[k] * b),
                        a2: ((denominators[(k - 1)] * a) - (c * numerators[(k - 1)])),
                        b2: (denominators[(k - 1)] * b),
                        d: d
                    ) < 0);

                    if (!shrinks) {
                        return $"residual |q*x - p| fails to shrink at convergent {k} of (({a}+{b}*sqrt({d}))/{c})";
                    }
                }
            }

            // Sweep every index up to the last denominator; the strict closest-approach records must be exactly the
            // distinct convergent denominators, in order.
            var distinct = new List<BigInteger>();

            foreach (var denominator in denominators) {
                if (
                    (distinct.Count == 0) ||
                    (distinct[^1] != denominator)
                ) { distinct.Add(item: denominator); }
            }

            var records = new List<BigInteger>();
            var recordRational = BigInteger.Zero;
            var recordSurd = BigInteger.Zero;

            for (var n = BigInteger.One; (n <= denominators[(count - 1)]); ++n) {
                var floor = ScaledFloor(
                    a: a,
                    b: b,
                    c: c,
                    d: d,
                    n: n
                );
                var upperComparison = SurdSign(
                    a: (((2 * n) * a) - (c * ((2 * floor) + BigInteger.One))),
                    b: ((2 * n) * b),
                    d: d
                );

                if (upperComparison == 0) {
                    return $"n*x landed exactly between integers at n={n} for irrational (({a}+{b}*sqrt({d}))/{c})";
                }

                var nearest = ((upperComparison > 0)
                    ? (floor + BigInteger.One)
                    : floor
                );
                var distanceRational = ((n * a) - (c * nearest));
                var distanceSurd = (n * b);
                var isRecord = ((records.Count == 0) || (MagnitudeCompare(
                    a1: distanceRational,
                    a2: recordRational,
                    b1: distanceSurd,
                    b2: recordSurd,
                    d: d
                ) < 0));

                if (isRecord) {
                    records.Add(item: n);
                    (recordRational, recordSurd) = (distanceRational, distanceSurd);
                }
            }

            if (!records.SequenceEqual(second: distinct)) {
                return $"closest-approach records [{string.Join(
                    separator: ",",
                    values: records
                )}] differ from distinct convergent denominators [{string.Join(
                    separator: ",",
                    values: distinct
                )}] for (({a}+{b}*sqrt({d}))/{c})";
            }
        }

        try {
            ContinuedFraction.Convergents(
                p: 0L,
                q: 1L,
                d: 2L,
                r: 1L,
                numerators: new BigInteger[3],
                denominators: new BigInteger[2]
            );

            return "Convergents accepted mismatched span lengths";
        } catch (ArgumentException exception) when ((exception.ParamName == "denominators")) {
        }

        return null;
    }
    /// <summary>Certified first divergences match a brute-force floor comparison, certificates verify, witnesses sit one above the lower line, and the refusals hold.</summary>
    public static string? FirstDivergenceMatchesBruteForce() {
        const long BruteForceCap = 200_000L;

        foreach (var ((a, b, d, c), fractionBits) in CertifyBattery) {
            var certificate = BeattyQuantization.CertifySlope(
                slope: Surd(
                    a: a,
                    b: b,
                    c: c,
                    d: d
                ),
                fractionBits: fractionBits
            );

            if (!certificate.Verify()) {
                return $"CertifySlope(({a}+{b}*sqrt({d}))/{c}, {fractionBits}) produced a certificate that fails its own Verify";
            }

            var oracle = OracleQuantize(
                a: a,
                b: b,
                c: c,
                d: d,
                fractionBits: fractionBits
            );

            if (
                (certificate.QuantizedNumerator != oracle.Numerator) ||
                (certificate.RoundingSign != oracle.RoundingSign)
            ) {
                return $"CertifySlope(({a}+{b}*sqrt({d}))/{c}, {fractionBits}) quantized to ({certificate.QuantizedNumerator}, {certificate.RoundingSign}) but the reference gives ({oracle.Numerator}, {oracle.RoundingSign})";
            }

            var scale = (BigInteger.One << fractionBits);
            var found = BigInteger.Zero;

            for (var n = BigInteger.One; (n <= BruteForceCap); ++n) {
                var exactFloor = ScaledFloor(
                    a: a,
                    b: b,
                    c: c,
                    d: d,
                    n: n
                );
                var quantizedFloor = FloorDivide(
                    numerator: (certificate.QuantizedNumerator * n),
                    denominator: scale
                );

                if (exactFloor == quantizedFloor) { continue; }

                found = n;

                if (((quantizedFloor > exactFloor)
                    ? 1
                    : -1) != certificate.RoundingSign) {
                    return $"at (({a}+{b}*sqrt({d}))/{c}, {fractionBits}) the divergence direction at n={n} contradicts RoundingSign={certificate.RoundingSign}";
                }
                if (certificate.DivergenceWitness != (BigInteger.Min(
                    left: exactFloor,
                    right: quantizedFloor
                ) + BigInteger.One)) {
                    return $"at (({a}+{b}*sqrt({d}))/{c}, {fractionBits}) the witness {certificate.DivergenceWitness} is not one above the lower floor at n={n}";
                }

                break;
            }

            if (found.IsZero) {
                return $"brute force found no divergence within {BruteForceCap} for (({a}+{b}*sqrt({d}))/{c}, {fractionBits}); the battery envelope is broken";
            }
            if (found != certificate.FirstDivergence) {
                return $"CertifySlope(({a}+{b}*sqrt({d}))/{c}, {fractionBits}) claims first divergence {certificate.FirstDivergence} but brute force finds {found}";
            }
        }

        foreach (var (exactNumerator, exactDenominator, approximateNumerator, approximateDenominator) in RationalPairBattery) {
            var (index, witness) = BeattyQuantization.FirstFloorDisagreement(
                exact: QuadraticSurd.Rational(
                    denominator: exactDenominator,
                    numerator: exactNumerator
                ),
                approximateNumerator: approximateNumerator,
                approximateDenominator: approximateDenominator
            );
            var found = BigInteger.Zero;

            for (var n = BigInteger.One; (n <= BruteForceCap); ++n) {
                var exactFloor = FloorDivide(
                    denominator: exactDenominator,
                    numerator: (exactNumerator * n)
                );
                var approximateFloor = FloorDivide(
                    denominator: approximateDenominator,
                    numerator: (approximateNumerator * n)
                );

                if (exactFloor == approximateFloor) { continue; }

                found = n;

                if (witness != (BigInteger.Min(
                    left: exactFloor,
                    right: approximateFloor
                ) + BigInteger.One)) {
                    return $"FirstFloorDisagreement({exactNumerator}/{exactDenominator}, {approximateNumerator}/{approximateDenominator}) witness {witness} is not one above the lower floor at n={n}";
                }

                break;
            }

            if (found != index) {
                return $"FirstFloorDisagreement({exactNumerator}/{exactDenominator}, {approximateNumerator}/{approximateDenominator}) claims {index} but brute force finds {found}";
            }
        }

        try {
            _ = BeattyQuantization.CertifySlope(
                slope: QuadraticSurd.Rational(
                    denominator: 2,
                    numerator: 3
                ),
                fractionBits: 4
            );

            return "CertifySlope accepted a rational slope";
        } catch (ArgumentOutOfRangeException exception) when ((exception.ParamName == "slope")) {
        }
        try {
            _ = BeattyQuantization.FirstFloorDisagreement(
                exact: QuadraticSurd.Rational(
                    denominator: 7,
                    numerator: 3
                ),
                approximateNumerator: 3,
                approximateDenominator: 7
            );

            return "FirstFloorDisagreement accepted an approximation equal to the exact slope";
        } catch (ArgumentException exception) when ((exception.ParamName == "approximateNumerator")) {
        }
        try {
            _ = BeattyQuantization.FirstFloorDisagreement(
                exact: QuadraticSurd.Rational(
                    denominator: 7,
                    numerator: 3
                ),
                approximateNumerator: 1,
                approximateDenominator: 0
            );

            return "FirstFloorDisagreement accepted a zero denominator";
        } catch (ArgumentOutOfRangeException exception) when ((exception.ParamName == "approximateDenominator")) {
        }

        return null;
    }
    /// <summary>Nearest-grid quantization agrees with the cleared-comparison reference at every battery value and width, ties round to even, and a negative width refuses.</summary>
    public static string? QuantizeNearestMatchesIntegerOracle() {
        foreach (var (a, b, d, c) in QuantizeBattery) {
            foreach (var fractionBits in QuantizeFractionBits) {
                var subject = BeattyQuantization.QuantizeNearest(
                    value: Surd(
                        a: a,
                        b: b,
                        c: c,
                        d: d
                    ),
                    fractionBits: fractionBits
                );
                var oracle = OracleQuantize(
                    a: a,
                    b: b,
                    c: c,
                    d: d,
                    fractionBits: fractionBits
                );

                if (
                    (subject.Numerator != oracle.Numerator) ||
                    (subject.RoundingSign != oracle.RoundingSign)
                ) {
                    return $"QuantizeNearest(({a}+{b}*sqrt({d}))/{c}, {fractionBits}) returned ({subject.Numerator}, {subject.RoundingSign}) but the cleared-comparison reference gives ({oracle.Numerator}, {oracle.RoundingSign})";
                }
            }
        }

        try {
            _ = BeattyQuantization.QuantizeNearest(
                value: Surd(
                    a: 0L,
                    b: 1L,
                    c: 1L,
                    d: 2L
                ),
                fractionBits: -1
            );

            return "QuantizeNearest accepted a negative fractionBits";
        } catch (ArgumentOutOfRangeException exception) when ((exception.ParamName == "fractionBits")) {
        }

        return null;
    }
    /// <summary>The simplest fraction lies strictly inside its interval in lowest terms, no smaller denominator carries a fraction inside, and an empty interval refuses.</summary>
    public static string? SimplestRationalIsMinimalInInterval() {
        const long DenominatorCap = 5_000L;

        foreach (var (low, high) in IntervalBattery) {
            var (numerator, denominator) = SimplestRational.InOpenInterval(
                low: Surd(
                    a: low.A,
                    b: low.B,
                    c: low.C,
                    d: low.D
                ),
                high: Surd(
                    a: high.A,
                    b: high.B,
                    c: high.C,
                    d: high.D
                )
            );

            if (denominator < BigInteger.One) {
                return $"interval (({low.A}+{low.B}*sqrt({low.D}))/{low.C}, ({high.A}+{high.B}*sqrt({high.D}))/{high.C}) produced a non-positive denominator {denominator}";
            }
            if (denominator > DenominatorCap) {
                return $"interval battery envelope broken: denominator {denominator} exceeds the scan cap";
            }
            if (!BigInteger.GreatestCommonDivisor(
                numerator,
                denominator
            ).IsOne) {
                return $"simplest fraction {numerator}/{denominator} is not in lowest terms";
            }

            // m/n > (a + b*sqrt(d))/c cleared by n*c > 0 is the sign of (c*m - n*a) - n*b*sqrt(d).
            var aboveLow = SurdSign(
                a: ((low.C * numerator) - (denominator * low.A)),
                b: (-(denominator * low.B)),
                d: low.D
            );
            var belowHigh = SurdSign(
                a: ((high.C * numerator) - (denominator * high.A)),
                b: (-(denominator * high.B)),
                d: high.D
            );

            if (
                (aboveLow <= 0) ||
                (belowHigh >= 0)
            ) {
                return $"simplest fraction {numerator}/{denominator} is not strictly inside (({low.A}+{low.B}*sqrt({low.D}))/{low.C}, ({high.A}+{high.B}*sqrt({high.D}))/{high.C})";
            }

            for (var n = BigInteger.One; (n < denominator); ++n) {
                var candidate = (ScaledFloor(
                    a: low.A,
                    b: low.B,
                    c: low.C,
                    d: low.D,
                    n: n
                ) + BigInteger.One);
                var inside = (SurdSign(
                    a: ((high.C * candidate) - (n * high.A)),
                    b: (-(n * high.B)),
                    d: high.D
                ) < 0);

                if (inside) {
                    return $"minimality broken: {candidate}/{n} lies inside the interval but the subject returned denominator {denominator}";
                }
            }
        }

        try {
            _ = SimplestRational.InOpenInterval(
                low: Surd(
                    a: 0L,
                    b: 1L,
                    c: 1L,
                    d: 2L
                ),
                high: Surd(
                    a: 0L,
                    b: 1L,
                    c: 1L,
                    d: 2L
                )
            );

            return "InOpenInterval accepted an empty interval";
        } catch (ArgumentOutOfRangeException exception) when ((exception.ParamName == "high")) {
        }

        return null;
    }
}
