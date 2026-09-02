using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Fast exact and structural claims over the root Core and Sampling surface. The declarations in
/// <see cref="LawRegistry"/> invoke these methods as Default-tier laws, so every assertion participates in both the
/// ordinary test gate and the mechanically generated public-member coverage ledger.
/// </summary>
internal static class CoreSurfaceClaims {
    public static string? BinaryIntegerSurface() {
        for (uint x = 0; (x < 32); ++x) {
            for (uint y = 0; (y < 32); ++y) {
                var paired = x.BitwisePair<uint, ulong>(other: y);
                var unpaired = paired.BitwiseUnpair<ulong, uint>();

                Assert.Equal(actual: unpaired, expected: (x, y));
            }
        }

        Assert.Equal(expected: 0b101000U, actual: 0b101100U.ClearLowestSetBit());
        Assert.Equal(expected: 9, actual: (-999).DigitalRoot());
        Assert.Equal(expected: [5, 4, 3, 2, 1], actual: 12345.EnumerateDigits().ToArray());
        Assert.Equal(expected: 243, actual: 3.Exponentiate(exponent: 5));
        Assert.Equal(expected: 0b100U, actual: 0b1100U.ExtractLowestSetBit());
        Assert.Equal(expected: 0b1000U, actual: 0b1011U.FillFromLowestClearBit());
        Assert.Equal(expected: 0b1111U, actual: 0b1000U.FillFromLowestSetBit());
        Assert.Equal(expected: 2, actual: (-7).FloorModulo(modulus: 3));
        Assert.Equal(expected: 12U, actual: 48U.GreatestCommonDivisor(other: 36U));
        Assert.Equal(expected: 144U, actual: 48U.LeastCommonMultiple(other: 36U));
        Assert.Equal(expected: 4U, actual: 0b1000U.LeastSignificantBit());
        Assert.Equal(expected: 5, actual: 12345.LeastSignificantDigit());
        Assert.Equal(expected: 5, actual: 12345.LogarithmBase10());
        Assert.Equal(expected: 4U, actual: 0b1000U.MostSignificantBit());
        Assert.Equal(expected: 1, actual: 12345.MostSignificantDigit());

        foreach (var value in new uint[] { 1U, 2U, 3U, 5U, 7U, 11U, 0x0010_0001U }) {
            var next = value.PermuteBitsLexicographically();

            Assert.True(condition: (next > value));
            Assert.Equal(expected: uint.PopCount(value: value), actual: uint.PopCount(value: next));
        }
        for (var raw = 0; (raw <= byte.MaxValue); ++raw) {
            var value = ((byte)raw);
            var expected = NextBytePermutation(value: value);

            Assert.Equal(expected: expected, actual: value.PermuteBitsLexicographically());
            Assert.Equal(
                expected: expected,
                actual: unchecked((byte)unchecked((sbyte)value).PermuteBitsLexicographically())
            );
        }
        Assert.Equal(expected: 0U, actual: 0U.PermuteBitsLexicographically());
        Assert.Equal(expected: uint.MaxValue, actual: uint.MaxValue.PermuteBitsLexicographically());
        Assert.Equal(expected: 1U, actual: 0x8000_0000U.PermuteBitsLexicographically());
        Assert.Equal(expected: -1, actual: (-1).PermuteBitsLexicographically());
        Assert.Equal(expected: 1, actual: int.MinValue.PermuteBitsLexicographically());
        Assert.Equal(expected: unchecked((int)0xBFFF_FFFFU), actual: int.MaxValue.PermuteBitsLexicographically());
        Assert.Equal(expected: BigInteger.Zero, actual: BigInteger.Zero.PermuteBitsLexicographically());
        Assert.Equal(expected: new BigInteger(value: 2), actual: BigInteger.One.PermuteBitsLexicographically());
        Assert.Equal(expected: new BigInteger(value: 191), actual: new BigInteger(value: 127).PermuteBitsLexicographically());
        Assert.Equal(expected: new BigInteger(value: 256), actual: new BigInteger(value: 128).PermuteBitsLexicographically());
        Assert.Equal(expected: BigInteger.MinusOne, actual: BigInteger.MinusOne.PermuteBitsLexicographically());
        Assert.Equal(expected: new BigInteger(value: -3), actual: new BigInteger(value: -2).PermuteBitsLexicographically());
        Assert.Equal(expected: new BigInteger(value: -192), actual: new BigInteger(value: -128).PermuteBitsLexicographically());
        Assert.Equal(expected: new BigInteger(value: -257), actual: new BigInteger(value: -129).PermuteBitsLexicographically());

        var ascending = new BigInteger(value: 11);

        for (var step = 0; (step < 16); ++step) {
            var next = ascending.PermuteBitsLexicographically();

            Assert.True(condition: (next > ascending));
            Assert.Equal(expected: 3, actual: MinorityBitCount(value: next));
            ascending = next;
        }
        var descending = new BigInteger(value: -11);

        for (var step = 0; (step < 16); ++step) {
            var next = descending.PermuteBitsLexicographically();

            Assert.True(condition: (next < descending));
            Assert.Equal(expected: 2, actual: MinorityBitCount(value: next));
            descending = next;
        }

        var maximumInt128 = ((BigInteger.One << 127) - BigInteger.One);

        Assert.Equal(
            expected: (((BigInteger.One << 127) + (BigInteger.One << 126)) - BigInteger.One),
            actual: maximumInt128.PermuteBitsLexicographically()
        );
        Assert.Equal(
            expected: -((BigInteger.One << 127) + (BigInteger.One << 126)),
            actual: (-(BigInteger.One << 127)).PermuteBitsLexicographically()
        );
        Assert.Equal(
            expected: (BigInteger.One << 129),
            actual: (BigInteger.One << 128).PermuteBitsLexicographically()
        );

        Assert.Equal(expected: 1U, actual: 0b1011U.PopulationParity());
        for (uint value = 0; (value < 1024); ++value) {
            Assert.Equal(expected: value, actual: value.ReflectedBinaryEncode().ReflectedBinaryDecode());
        }
        Assert.Equal(expected: 0x8000_0000U, actual: 1U.ReverseBits());
        Assert.Equal(expected: -321, actual: (-1230).ReverseDigits());
        Assert.Equal(expected: 34512, actual: 12345.RotateDigitsLeft(count: 2));
        Assert.Equal(expected: 45123, actual: 12345.RotateDigitsRight(count: 2));

        return null;
    }

    private static int MinorityBitCount(BigInteger value) =>
        int.CreateChecked(value: BigInteger.PopCount(value: ((value.Sign < 0) ? ~value : value)));
    private static byte NextBytePermutation(byte value) {
        var populationCount = uint.PopCount(value: value);

        for (var offset = 1; (offset <= (byte.MaxValue + 1)); ++offset) {
            var candidate = unchecked((byte)(value + offset));

            if (uint.PopCount(value: candidate) == populationCount) {
                return candidate;
            }
        }

        throw new InvalidOperationException(message: "the byte carrier must contain a cyclic same-popcount result");
    }

    public static string? DiscreteMeasureSurface() {
        var source = DiscreteMeasure.Rational(
            denominator: 3,
            numerator: 4,
            offsetDenominator: 3,
            offsetNumerator: 1
        );

        Assert.Equal(expected: DiscreteMeasure.Zero, actual: default);
        Assert.Equal(expected: RealQuadratic.Rational(denominator: 3, numerator: 4), actual: source.Rate);
        Assert.Equal(expected: RealQuadratic.Rational(denominator: 3, numerator: 1), actual: source.Offset);
        Assert.True(condition: source.IsPeriodic);
        Assert.Equal(expected: new BigInteger(value: 3), actual: source.Period);
        Assert.Equal(expected: BigInteger.One, actual: source.MinimumAmount);
        Assert.Equal(expected: new BigInteger(value: 2), actual: source.MaximumAmount);
        Assert.Equal(expected: BigInteger.Zero, actual: source.Cumulative(index: 0));
        Assert.Equal(expected: new BigInteger(value: 2), actual: source.AmountAt(index: 1));
        Assert.Equal(expected: new BigInteger(value: 4), actual: source.AmountOver(length: 3, start: 0));
        Assert.Equal(expected: new BigInteger(value: 4), actual: source.AmountBetween(end: 3, start: 0));
        Assert.Equal(expected: (new BigInteger(value: 1), new BigInteger(value: 4)), actual: source.Map(length: 3, start: 1));
        Assert.Equal(expected: (new BigInteger(value: 1), new BigInteger(value: 4)), actual: source.MapBetween(end: 4, start: 1));
        Assert.Equal(expected: new BigInteger(value: 2), actual: source.LowerBound(amount: 3));
        Assert.Equal(expected: BigInteger.One, actual: source.IndexContaining(outputIndex: 2));
        Assert.Equal(expected: new BigInteger(value: 1), actual: source.NextNonemptyIndex(start: 1));

        var translated = source.Translate(distance: 7);

        for (var index = -8; (index <= 8); ++index) {
            Assert.Equal(
                expected: source.AmountAt(index: (index + 7)),
                actual: translated.AmountAt(index: index)
            );
        }

        Assert.True(condition: source.TryCompileInt64(compiled: out var compiled));
        Assert.True(condition: source.TryCompileInt64(compiled: out var detailed, failure: out var failure));
        Assert.Equal(actual: failure, expected: DiscreteMeasureCompilationFailure.None);
        Assert.Equal(actual: detailed, expected: compiled);
        Assert.Equal(expected: compiled, actual: source.CompileInt64());
        Assert.True(condition: compiled.IsValid);
        Assert.True(condition: compiled.IsPeriodic);
        Assert.False(condition: compiled.IsQuadratic);
        Assert.Equal(expected: 1L, actual: compiled.IntegralRate);
        Assert.Equal(expected: 1L, actual: compiled.FractionalRateNumerator);
        Assert.Equal(expected: 3L, actual: compiled.FractionalRateDenominator);
        Assert.Equal(expected: 1L, actual: compiled.OffsetNumerator);
        Assert.Equal(expected: 3L, actual: compiled.OffsetDenominator);
        Assert.Equal(expected: 3L, actual: compiled.Period);
        Assert.Equal(expected: 1L, actual: compiled.Cumulative(index: 1));
        Assert.Equal(expected: 2L, actual: compiled.AmountAt(index: 1));
        Assert.Equal(expected: 4L, actual: compiled.AmountOver(length: 3, start: 0));
        Assert.Equal(expected: 4L, actual: compiled.AmountBetween(end: 3, start: 0));
        Assert.Equal(expected: (1L, 4L), actual: compiled.Map(length: 3, start: 1));
        Assert.Equal(expected: 2L, actual: compiled.LowerBound(amount: 3));
        Assert.Equal(expected: 1L, actual: compiled.IndexContaining(outputIndex: 2));

        Assert.True(condition: compiled.TryCumulative(cumulative: out var cumulative, index: 1));
        Assert.Equal(actual: cumulative, expected: 1L);
        Assert.True(condition: compiled.TryAmountAt(amount: out var at, index: 1));
        Assert.Equal(actual: at, expected: 2L);
        Assert.True(condition: compiled.TryAmountOver(amount: out var over, length: 3, start: 0));
        Assert.Equal(actual: over, expected: 4L);
        Assert.True(condition: compiled.TryAmountBetween(amount: out var between, end: 3, start: 0));
        Assert.Equal(actual: between, expected: 4L);
        Assert.True(condition: compiled.TryMap(length: 3, mappedLength: out var mappedLength, mappedStart: out var mappedStart, start: 1));
        Assert.Equal(actual: (mappedStart, mappedLength), expected: (1L, 4L));
        Assert.True(condition: compiled.TryLowerBound(amount: 3, index: out var lowerBound));
        Assert.Equal(actual: lowerBound, expected: 2L);
        Assert.True(condition: compiled.TryIndexContaining(inputIndex: out var containing, outputIndex: 2));
        Assert.Equal(actual: containing, expected: 1L);

        // Every coefficient that will not narrow to signed 64-bit storage, and every radicand outside the two-limb
        // kernel, is a coefficient failure — the outcome the enum defines for exactly that condition.
        var oversizedRational = DiscreteMeasure.Rational(
            numerator: (((BigInteger)long.MaxValue) + 1),
            denominator: BigInteger.One
        );

        Assert.False(condition: oversizedRational.TryCompileInt64(compiled: out _, failure: out var rationalFailure));
        Assert.Equal(actual: rationalFailure, expected: DiscreteMeasureCompilationFailure.CoefficientOutOfRange);
        _ = Assert.Throws<OverflowException>(testCode: () => oversizedRational.CompileInt64());

        var oversizedRadicand = DiscreteMeasure.Create(
            rate: RealQuadratic.One,
            offset: RealQuadratic.Create(0, 1, ((BigInteger.One << 64) + 1), 1)
        );

        Assert.False(condition: oversizedRadicand.TryCompileInt64(compiled: out _, failure: out var radicandFailure));
        Assert.Equal(actual: radicandFailure, expected: DiscreteMeasureCompilationFailure.CoefficientOutOfRange);
        _ = Assert.Throws<OverflowException>(testCode: () => oversizedRadicand.CompileInt64());

        // (-2^63 + 2^63·√2)/(2^63 - 1) lies in [0, 1) with both rational parts inside signed 64-bit storage, so its
        // surd numerator is the only coefficient that refuses. The refusal must not depend on which operand carries
        // the surd: a rational rate and an irrational one both report the coefficient outcome, never an irrational one.
        var wideOffsetSurd = RealQuadratic.Create(
            rationalNumerator: long.MinValue,
            surdNumerator: (BigInteger.One << 63),
            radicand: 2,
            denominator: long.MaxValue
        );

        foreach (var rate in new[] { RealQuadratic.One, RealQuadratic.Create(denominator: 1, radicand: 2, rationalNumerator: 0, surdNumerator: 1) }) {
            var wideSurdMeasure = DiscreteMeasure.Create(offset: wideOffsetSurd, rate: rate);

            Assert.False(condition: wideSurdMeasure.TryCompileInt64(compiled: out _, failure: out var surdFailure));
            Assert.Equal(actual: surdFailure, expected: DiscreteMeasureCompilationFailure.CoefficientOutOfRange);
            _ = Assert.Throws<OverflowException>(testCode: () => wideSurdMeasure.CompileInt64());
        }

        // The irrational outcome is reserved for coefficients that do narrow and then leave the bounded floor
        // kernel's magnitude envelope: long.MaxValue·√5 per unit interval squares past the Int128 root budget.
        var oversizedIrrationalRate = DiscreteMeasure.Create(
            rate: RealQuadratic.Create(denominator: 1, radicand: 5, rationalNumerator: 0, surdNumerator: long.MaxValue),
            offset: RealQuadratic.Zero
        );

        Assert.False(condition: oversizedIrrationalRate.TryCompileInt64(compiled: out _, failure: out var rateFailure));
        Assert.Equal(actual: rateFailure, expected: DiscreteMeasureCompilationFailure.IrrationalRate);
        _ = Assert.Throws<OverflowException>(testCode: () => oversizedIrrationalRate.CompileInt64());

        return null;
    }
    public static string? CompiledRadicalTransport() {
        var spellings = new[] {
            (
                Rate: RealQuadratic.Create(denominator: 1, radicand: 8, rationalNumerator: 0, surdNumerator: 1),
                Offset: RealQuadratic.Create(denominator: 1, radicand: 2, rationalNumerator: 0, surdNumerator: 1)
            ),
            (
                Rate: RealQuadratic.Create(denominator: 1, radicand: 12, rationalNumerator: 0, surdNumerator: 1),
                Offset: RealQuadratic.Create(denominator: 2, radicand: 3, rationalNumerator: 7, surdNumerator: -1)
            ),
            (
                Rate: RealQuadratic.Create(denominator: 1, radicand: 3, rationalNumerator: 0, surdNumerator: 2),
                Offset: RealQuadratic.Create(denominator: 1, radicand: 12, rationalNumerator: 0, surdNumerator: -1)
            ),
            (
                Rate: RealQuadratic.Create(denominator: 1, radicand: 3, rationalNumerator: 0, surdNumerator: 1),
                Offset: RealQuadratic.Create(denominator: 4, radicand: 12, rationalNumerator: 29, surdNumerator: -1)
            ),
        };

        foreach (var (rate, offset) in spellings) {
            var exact = DiscreteMeasure.Create(offset: offset, rate: rate);

            Assert.True(condition: exact.TryCompileInt64(compiled: out var compiled, failure: out var failure));
            Assert.Equal(actual: failure, expected: DiscreteMeasureCompilationFailure.None);
            Assert.True(condition: compiled.IsQuadratic);

            foreach (var index in new long[] { -1_000_000L, -37L, -2L, -1L, 0L, 1L, 2L, 37L, 1_000_000L }) {
                Assert.Equal(expected: ((long)exact.Cumulative(index: index)), actual: compiled.Cumulative(index: index));
                Assert.Equal(expected: ((long)exact.AmountAt(index: index)), actual: compiled.AmountAt(index: index));
            }

            foreach (var endpoint in new[] { long.MinValue, long.MaxValue }) {
                var expected = exact.Cumulative(index: endpoint);
                var fits = ((expected >= long.MinValue) && (expected <= long.MaxValue));

                Assert.Equal(expected: fits, actual: compiled.TryCumulative(cumulative: out var bounded, index: endpoint));
                if (fits) { Assert.Equal(actual: bounded, expected: ((long)expected)); }
            }
        }

        return null;
    }
    public static string? NumberTheorySurface() {
        var callbackPrimes = new List<ulong>();

        NumberTheoryFunctions.SegmentedPrimeSieve(high: 512, low: 0, onPrime: callbackPrimes.Add);
        var expected = Enumerable.Range(count: 513, start: 0)
            .Where(predicate: IsPrimeByTrialDivision)
            .Select(selector: value => ((ulong)value))
            .ToArray();

        Assert.Equal(actual: callbackPrimes, expected: expected);
        Assert.Equal(expected: expected, actual: NumberTheoryFunctions.EnumeratePrimes(high: 512, low: 0));

        var ceilingPrime = new List<ulong>();

        NumberTheoryFunctions.SegmentedPrimeSieve(
            high: uint.MaxValue,
            low: 4_294_967_291UL,
            onPrime: ceilingPrime.Add
        );
        Assert.Equal(actual: ceilingPrime, expected: [4_294_967_291UL]);
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            testCode: () => NumberTheoryFunctions.SegmentedPrimeSieve(
                high: ulong.MaxValue,
                low: ulong.MaxValue,
                onPrime: static _ => { }
            )
        );

        Assert.Equal(expected: -1, actual: NumberTheoryFunctions.JacobiSymbol(denominator: 9907, numerator: 1001));

        BigInteger[] nonUnit = [0, 2];

        _ = Assert.Throws<ArgumentException>(
            testCode: () => NumberTheoryFunctions.HenselLiftRoot(
                baseModulus: 4,
                coefficients: nonUnit,
                root: 0,
                targetPower: 2
            )
        );

        // x^2 - 7 over the composite base nine: 4^2 = 16 is congruent to 7 modulo 9 and the derivative 2*4 = 8 is a
        // unit there, so the lift is unique. Every correction step is nonzero, and the checks below share no code
        // with the lifting loop: residue, range, and a direct Horner evaluation modulo 9^4.
        BigInteger[] unit = [-7, 0, 1];
        var baseModulus = new BigInteger(value: 9);
        var targetModulus = BigInteger.Pow(exponent: 4, value: baseModulus);
        var lifted = NumberTheoryFunctions.HenselLiftRoot(
            baseModulus: baseModulus,
            coefficients: unit,
            root: 4,
            targetPower: 4
        );

        Assert.Equal(expected: new BigInteger(value: 4), actual: (lifted % baseModulus));
        Assert.InRange(actual: lifted, low: BigInteger.Zero, high: (targetModulus - BigInteger.One));
        Assert.Equal(expected: BigInteger.Zero, actual: (EvaluatePolynomial(coefficients: unit, point: lifted) % targetModulus));

        return null;
    }
    public static string? RealQuadraticSurface() {
        var zero = RealQuadratic.Zero;
        var one = RealQuadratic.One;
        var sqrt2 = RealQuadratic.Create(denominator: 1, radicand: 2, rationalNumerator: 0, surdNumerator: 1);
        var sqrt8 = RealQuadratic.Create(denominator: 1, radicand: 8, rationalNumerator: 0, surdNumerator: 1);
        var twoSqrt2 = RealQuadratic.Create(denominator: 1, radicand: 2, rationalNumerator: 0, surdNumerator: 2);
        var rational = RealQuadratic.Rational(denominator: 4, numerator: 6);

        Assert.True(condition: zero.IsRational);
        Assert.Equal(expected: BigInteger.Zero, actual: zero.RationalNumerator);
        Assert.Equal(expected: BigInteger.Zero, actual: zero.SurdNumerator);
        Assert.Equal(expected: BigInteger.Zero, actual: zero.Radicand);
        Assert.Equal(expected: BigInteger.One, actual: zero.Denominator);
        Assert.Equal(expected: RealQuadratic.Rational(value: 1), actual: one);
        Assert.Equal(expected: RealQuadratic.Rational(denominator: 2, numerator: 3), actual: rational);
        Assert.Equal(actual: sqrt8, expected: twoSqrt2);
        Assert.Equal(expected: 1, actual: sqrt2.Sign);
        Assert.Equal(expected: BigInteger.One, actual: sqrt2.Floor());
        Assert.Equal(expected: new BigInteger(value: 2), actual: sqrt2.Ceiling());
        Assert.Equal(expected: sqrt2, actual: (-sqrt2).Abs());
        Assert.True(condition: (sqrt2.CompareTo(other: rational) < 0));
        Assert.True(condition: (sqrt2 < rational));
        Assert.True(condition: (sqrt2 <= sqrt8));
        Assert.True(condition: (sqrt8 > sqrt2));
        Assert.True(condition: (sqrt8 >= twoSqrt2));
        Assert.Equal(expected: RealQuadratic.Rational(value: 2), actual: (sqrt2 * sqrt2));
        Assert.Equal(actual: ((sqrt2 + one) - one), expected: sqrt2);
        Assert.Equal(actual: (sqrt2 / sqrt2), expected: one);
        Assert.Equal(actual: -sqrt2, expected: -sqrt2);

        var scale = BigInteger.Pow(exponent: 400, value: 10);
        var nearOne = RealQuadratic.Rational(denominator: scale, numerator: (scale + 1)).ToDouble();

        Assert.True(condition: double.IsFinite(d: nearOne));
        Assert.Equal(actual: nearOne, expected: 1.0);
        Assert.Equal(expected: -1.0, actual: RealQuadratic.Rational(denominator: scale, numerator: -(scale + 1)).ToDouble());
        Assert.Equal(expected: double.PositiveInfinity, actual: RealQuadratic.Rational(denominator: 1, numerator: scale).ToDouble());
        Assert.Equal(expected: 0.0, actual: RealQuadratic.Rational(denominator: scale, numerator: 1).ToDouble());

        var pellLike = RealQuadratic.Create(
            denominator: 1,
            radicand: ((scale * scale) + 1),
            rationalNumerator: -((2 * scale) * scale),
            surdNumerator: (2 * scale)
        ).ToDouble();

        Assert.True(condition: double.IsFinite(d: pellLike));
        Assert.InRange(actual: pellLike, high: 1.000000000000001, low: 0.999999999999999);

        return null;
    }
    public static string? RealQuadraticFieldSurface() {
        // Canonicalization: every square factor below the small-prime bound leaves the radicand, a perfect square is
        // refused as a field, and the scale reports what left.
        var eight = RealQuadraticField.Create(radicand: 8, scale: out var eightScale);
        var twelve = RealQuadraticField.Create(radicand: 12, scale: out var twelveScale);

        Assert.Equal(expected: new BigInteger(value: 2), actual: eight.Radicand);
        Assert.Equal(expected: new BigInteger(value: 2), actual: eightScale);
        Assert.Equal(expected: new BigInteger(value: 3), actual: twelve.Radicand);
        Assert.Equal(expected: new BigInteger(value: 2), actual: twelveScale);
        Assert.Equal(expected: RealQuadraticField.Create(radicand: 2), actual: eight);
        Assert.Throws<ArgumentException>(testCode: () => RealQuadraticField.Create(radicand: 4));
        Assert.Throws<ArgumentException>(testCode: () => RealQuadraticField.Create(radicand: 1));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => RealQuadraticField.Create(radicand: 0));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => RealQuadraticField.Create(radicand: -2));
        Assert.True(condition: RealQuadraticField.Rationals.IsRationals);
        Assert.False(condition: eight.IsRationals);
        Assert.Throws<InvalidOperationException>(testCode: () => RealQuadraticField.Rationals.Sqrt);

        // Values of one field compare as tuples: √8 and 2·√2 are the same coordinates in the same field.
        var sqrt8 = RealQuadratic.Create(denominator: 1, radicand: 8, rationalNumerator: 0, surdNumerator: 1);
        var twoSqrt2 = RealQuadratic.Create(denominator: 1, radicand: 2, rationalNumerator: 0, surdNumerator: 2);

        Assert.Equal(expected: twoSqrt2.Field, actual: sqrt8.Field);
        Assert.Equal(expected: new BigInteger(value: 2), actual: sqrt8.SurdNumerator);
        Assert.Equal(expected: twoSqrt2, actual: sqrt8);
        Assert.Equal(expected: twoSqrt2.GetHashCode(), actual: sqrt8.GetHashCode());
        Assert.Equal(expected: eight.Element(rationalNumerator: 0, surdNumerator: 2, denominator: 1), actual: sqrt8);
        Assert.Equal(expected: twoSqrt2, actual: (eight.Sqrt + eight.Sqrt));
        Assert.Equal(expected: RealQuadratic.Rational(value: 2), actual: (eight.Sqrt * eight.Sqrt));

        // A square factor above the bound survives canonicalization, and equality, hashing, ordering and arithmetic
        // still identify the two representations exactly.
        var bigSquare = new BigInteger(value: (1031L * 1031L));
        var hidden = RealQuadratic.Create(denominator: 1, radicand: (2 * bigSquare), rationalNumerator: 0, surdNumerator: 1);
        var plain = RealQuadratic.Create(denominator: 1, radicand: 2, rationalNumerator: 0, surdNumerator: 1031);

        Assert.NotEqual(expected: plain.Field, actual: hidden.Field);
        Assert.Equal(expected: plain, actual: hidden);
        Assert.Equal(expected: plain.GetHashCode(), actual: hidden.GetHashCode());
        Assert.Equal(expected: 0, actual: hidden.CompareTo(other: plain));
        Assert.Equal(expected: RealQuadratic.Create(denominator: 1, radicand: 2, rationalNumerator: 0, surdNumerator: 2062), actual: (hidden + plain));
        Assert.Equal(expected: RealQuadratic.Rational(value: (2 * bigSquare)), actual: (hidden * plain));

        // Conjugate, norm and trace are the field's own identities, and the rational coordinates read back.
        var golden = RealQuadratic.Create(denominator: 2, radicand: 5, rationalNumerator: 1, surdNumerator: 1);
        var conjugate = golden.Conjugate();

        Assert.Equal(expected: RealQuadratic.Create(denominator: 2, radicand: 5, rationalNumerator: 1, surdNumerator: -1), actual: conjugate);
        Assert.Equal(expected: RealQuadratic.FromRational(value: golden.Norm()), actual: (golden * conjugate));
        Assert.Equal(expected: RealQuadratic.FromRational(value: golden.Trace()), actual: (golden + conjugate));
        Assert.Equal(expected: new Rational(Numerator: -1, Denominator: 1), actual: golden.Norm());
        Assert.Equal(expected: Rational.One, actual: golden.Trace());
        Assert.Equal(expected: new Rational(Numerator: 1, Denominator: 2), actual: golden.RationalPart);
        Assert.Equal(expected: new Rational(Numerator: 1, Denominator: 2), actual: golden.SurdPart);
        Assert.Equal(expected: RealQuadraticField.Create(radicand: 5), actual: golden.Field);
        Assert.Equal(expected: RealQuadraticField.Rationals, actual: RealQuadratic.One.Field);
        Assert.Equal(expected: RealQuadratic.Rational(denominator: 3, numerator: 2), actual: RealQuadratic.FromRational(value: new Rational(Numerator: 4, Denominator: 6)));

        // A perfect-square radicand folds into the rational part, and different fields refuse to combine.
        Assert.Equal(expected: RealQuadratic.Rational(value: 7), actual: RealQuadratic.Create(denominator: 1, radicand: 9, rationalNumerator: 1, surdNumerator: 2));
        Assert.Throws<ArgumentException>(testCode: () => (golden + sqrt8));
        Assert.Throws<ArgumentException>(testCode: () => (golden * sqrt8));
        Assert.True(condition: (golden > sqrt8.Conjugate()));
        Assert.True(condition: (golden < sqrt8));

        // The exact conversion: the returned double is at least as close to the value as either binary64 neighbour,
        // decided by the exact ordering against the neighbours' midpoints; the platform's correctly rounded square
        // root of a small integer agrees where it applies.
        Assert.True(condition: IsNearestDouble(value: golden, candidate: golden.ToDouble()));
        Assert.True(condition: IsNearestDouble(value: -golden, candidate: (-golden).ToDouble()));
        Assert.True(condition: IsNearestDouble(value: sqrt8, candidate: sqrt8.ToDouble()));
        Assert.True(condition: IsNearestDouble(value: golden.Conjugate(), candidate: golden.Conjugate().ToDouble()));
        Assert.True(condition: IsNearestDouble(value: hidden, candidate: hidden.ToDouble()));
        Assert.Equal(expected: Math.Sqrt(d: 8.0), actual: sqrt8.ToDouble());
        Assert.Equal(expected: Math.Sqrt(d: 2.0), actual: RealQuadraticField.Create(radicand: 2).Sqrt.ToDouble());

        return null;
    }
    public static string? BigIntegerToDoubleSurface() {
        Assert.Equal(expected: 0.0, actual: BigIntegerFunctions.ToDouble(value: BigInteger.Zero));
        Assert.Equal(expected: 5.0, actual: BigIntegerFunctions.ToDouble(value: 5));
        Assert.Equal(expected: -40.0, actual: BigIntegerFunctions.ToDouble(binaryExponent: 3, value: -5));
        Assert.Equal(expected: 0.375, actual: BigIntegerFunctions.ToDouble(binaryExponent: -3, value: 3));
        Assert.Equal(expected: double.Epsilon, actual: BigIntegerFunctions.ToDouble(binaryExponent: -1074, value: 1));
        Assert.Equal(expected: double.MaxValue, actual: BigIntegerFunctions.ToDouble(binaryExponent: 971, value: ((BigInteger.One << 53) - 1)));
        Assert.Equal(expected: double.PositiveInfinity, actual: BigIntegerFunctions.ToDouble(value: (BigInteger.One << 1024)));
        Assert.Equal(expected: double.NegativeInfinity, actual: BigIntegerFunctions.ToDouble(binaryExponent: 970, value: -((BigInteger.One << 54) - 1)));

        // Every width around the mantissa boundary, every nearby offset, and exponents spanning the subnormal floor,
        // the overflow ceiling and the ordinary range, checked against the exact neighbour oracle.
        ReadOnlySpan<int> widths = [1, 52, 53, 54, 55, 60, 63, 64, 65, 100, 127, 128, 1000, 1023, 1024, 1025];
        ReadOnlySpan<int> exponents = [0, -1, -52, -53, -60, -1000, -1074, -1075, -1076, -1100, -1126, -1127, -1128, -2000, 900, 970, 971, 972];

        foreach (var width in widths) {
            var power = (BigInteger.One << (width - 1));

            for (var offset = -3; (offset <= 3); ++offset) {
                var value = (power + offset);

                if (value.Sign <= 0) { continue; }

                foreach (var exponent in exponents) {
                    foreach (var signed in new[] { value, -value }) {
                        var converted = BigIntegerFunctions.ToDouble(binaryExponent: exponent, value: signed);
                        var (numerator, denominator) = ((exponent >= 0) ? ((signed << exponent), BigInteger.One) : (signed, (BigInteger.One << -exponent)));

                        Assert.True(
                            condition: Oracles.IsNearestDouble(numerator: numerator, denominator: denominator, candidate: converted),
                            userMessage: $"ToDouble({signed}, {exponent}) = {converted} is not the nearest double"
                        );
                    }
                }
            }
        }

        // The truncated form: a discarded remainder is the sticky bit, so an exact tie without one rounds to even and
        // the same truncation with one rounds up; the remainder is only admitted under a magnitude wide enough to keep
        // it below the rounding position.
        var tie = ((BigInteger.One << 60) + 128);

        Assert.Equal(expected: Math.ScaleB(n: 60, x: 1.0), actual: BigIntegerFunctions.ToDouble(binaryExponent: 0, hasRemainder: false, truncatedMagnitude: tie));
        Assert.Equal(expected: (Math.ScaleB(n: 60, x: 1.0) + 256.0), actual: BigIntegerFunctions.ToDouble(binaryExponent: 0, hasRemainder: true, truncatedMagnitude: tie));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => BigIntegerFunctions.ToDouble(binaryExponent: 0, hasRemainder: false, truncatedMagnitude: -tie));
        Assert.Throws<ArgumentException>(testCode: () => BigIntegerFunctions.ToDouble(binaryExponent: 0, hasRemainder: true, truncatedMagnitude: ((BigInteger.One << 53) - 1)));
        Assert.Equal(expected: 0.0, actual: BigIntegerFunctions.ToDouble(binaryExponent: 0, hasRemainder: false, truncatedMagnitude: BigInteger.Zero));

        foreach (var (numerator, denominator) in new (BigInteger, BigInteger)[] { (1, 3), (2, 3), ((BigInteger.One << 100) + 7, 1000003), (5, (BigInteger.One << 1076)) }) {
            var scale = (100 + Math.Max(val1: 0, val2: (int)((long)denominator.GetBitLength() - (long)numerator.GetBitLength())));
            var quotient = BigInteger.DivRem(dividend: (numerator << scale), divisor: denominator, remainder: out var remainder);
            var converted = BigIntegerFunctions.ToDouble(binaryExponent: -scale, hasRemainder: !remainder.IsZero, truncatedMagnitude: quotient);

            Assert.True(
                condition: Oracles.IsNearestDouble(numerator: numerator, denominator: denominator, candidate: converted),
                userMessage: $"truncated ToDouble({numerator}/{denominator}) = {converted} is not the nearest double"
            );
        }

        return null;
    }
    // Whether candidate is the double nearest an exact real-quadratic value: the value is ordered against the exact
    // midpoints between the candidate and its binary64 neighbours, with a tie admitted only toward an even mantissa.
    private static bool IsNearestDouble(RealQuadratic value, double candidate) {
        if (double.IsNaN(d: candidate) || double.IsInfinity(d: candidate) || (candidate == 0.0)) { return false; }

        var lowerMidpoint = Midpoint(left: Math.BitDecrement(x: candidate), right: candidate);
        var upperMidpoint = Midpoint(left: candidate, right: Math.BitIncrement(x: candidate));
        var mantissaIsOdd = ((BitConverter.DoubleToInt64Bits(value: candidate) & 1L) != 0L);
        var aboveLower = value.CompareTo(other: lowerMidpoint);
        var belowUpper = value.CompareTo(other: upperMidpoint);

        return (mantissaIsOdd
            ? ((aboveLower > 0) && (belowUpper < 0))
            : ((aboveLower >= 0) && (belowUpper <= 0)));
    }
    // (left + right) / 2 as an exact rational value, for finite doubles.
    private static RealQuadratic Midpoint(double left, double right) {
        var (leftMantissa, leftExponent) = Decompose(value: left);
        var (rightMantissa, rightExponent) = Decompose(value: right);
        var exponent = Math.Min(val1: leftExponent, val2: rightExponent);
        var sum = ((leftMantissa << (leftExponent - exponent)) + (rightMantissa << (rightExponent - exponent)));

        return ((exponent >= 1)
            ? RealQuadratic.Rational(value: (sum << (exponent - 1)))
            : RealQuadratic.Rational(denominator: (BigInteger.One << (1 - exponent)), numerator: sum));
    }
    private static (BigInteger Mantissa, int Exponent) Decompose(double value) {
        var bits = BitConverter.DoubleToInt64Bits(value: value);
        var biasedExponent = (int)((bits >> 52) & 0x7FFL);
        var fraction = (bits & 0xFFFFFFFFFFFFFL);
        var mantissa = ((biasedExponent == 0) ? fraction : (fraction | (1L << 52)));
        var exponent = ((biasedExponent == 0) ? -1074 : (biasedExponent - 1075));

        return (((bits < 0L) ? -new BigInteger(value: mantissa) : new BigInteger(value: mantissa)), exponent);
    }
    public static string? PrimeExtensionsSurface() {
        for (uint value = 0; (value <= 2048); ++value) {
            Assert.Equal(expected: IsPrimeByTrialDivision(value: ((int)value)), actual: value.IsPrime());
        }

        uint[] firstPrimes = [2U, 3U, 5U, 7U, 11U, 13U, 17U, 19U, 23U, 29U, 31U];

        for (uint index = 0; (index < firstPrimes.Length); ++index) {
            Assert.Equal(expected: firstPrimes[index], actual: index.NthPrime());
            Assert.Equal(expected: (index + 1U), actual: firstPrimes[index].PrimeCountingFunction());
        }

        Span<uint> factors = stackalloc uint[32];
        var count = 360U.Factorize(destination: factors);

        Assert.Equal(expected: [2U, 2U, 2U, 3U, 3U, 5U], actual: factors[..count].ToArray());
        uint[] undersized = [101U, 102U, 103U, 104U, 105U];
        var beforeRefusal = undersized.ToArray();
        var refusal = Assert.Throws<ArgumentException>(
            testCode: () => 360U.Factorize(destination: undersized)
        );

        Assert.Equal(expected: "destination", actual: refusal.ParamName);
        Assert.Equal(actual: undersized, expected: beforeRefusal);
        // A prime reports ITSELF; only a value below two reports nothing.
        Assert.Equal(expected: 1, actual: 359U.Factorize(destination: factors));
        Assert.Equal(expected: 359U, actual: factors[0]);
        Assert.Equal(expected: 0, actual: 1U.Factorize(destination: factors));

        return null;
    }
    public static string? UnsignedIntegerSurface() {
        for (uint x = 0; (x < 32); ++x) {
            for (uint y = 0; (y < 32); ++y) {
                var paired = x.ElegantPair<uint, ulong>(other: y);

                Assert.Equal(expected: (x, y), actual: paired.ElegantUnpair<ulong, uint>());
            }
        }

        Assert.Equal(
            expected: [2U, 2U, 2U, 3U, 3U, 5U],
            actual: 360U.EnumeratePrimeFactors().ToArray()
        );
        // A prime reports ITSELF; only a value below two has no factorization to report.
        Assert.Equal(expected: [359U], actual: 359U.EnumeratePrimeFactors().ToArray());
        Assert.Empty(collection: 1U.EnumeratePrimeFactors());

        var inverse = 3U.ModularInverse();

        Assert.Equal(actual: unchecked((3U * inverse)), expected: 1U);
        Assert.Equal(expected: 0U, actual: 0U.NextPowerOfTwo());
        Assert.Equal(expected: 32U, actual: 17U.NextPowerOfTwo());
        Assert.Equal(expected: 16U, actual: 15U.NextSquare());
        Assert.Equal(expected: 25U, actual: 16U.NextSquare());

        for (uint value = 0; (value <= 10_000); ++value) {
            var root = value.SquareRoot();

            Assert.True(condition: ((((ulong)root) * root) <= value));
            Assert.True(condition: ((((ulong)(root + 1U)) * (root + 1U)) > value));
        }

        return null;
    }
    public static string? Fnv1aSurface() {
        var bytes = Fnv1aHash.Create();

        foreach (var value in "hello"u8) { bytes.Add(value: value); }
        Assert.Equal(expected: 0xA430D84680AABD0BUL, actual: bytes.Value);

        var packed32 = Fnv1aHash.Create();

        packed32.Add(value: 0x04030201U);
        var individual32 = Fnv1aHash.Create();

        foreach (var value in new byte[] { 1, 2, 3, 4 }) { individual32.Add(value: value); }
        Assert.Equal(expected: individual32.Value, actual: packed32.Value);

        var packed64 = Fnv1aHash.Create();

        packed64.Add(value: ulong.MaxValue);
        var signed64 = Fnv1aHash.Create();

        signed64.Add(value: -1L);
        Assert.Equal(expected: packed64.Value, actual: signed64.Value);

        // Compute, against the canonical published FNV-1a 64-bit vectors. The empty span must return the offset basis
        // rather than zero, which is the one value a fold that forgot to prime itself would produce.
        Assert.Equal(expected: 0xCBF29CE484222325UL, actual: Fnv1aHash.Compute(values: ReadOnlySpan<byte>.Empty));
        Assert.Equal(expected: 0xAF63DC4C8601EC8CUL, actual: Fnv1aHash.Compute(values: "a"u8));
        Assert.Equal(expected: 0xAF63DF4C8601F1A5UL, actual: Fnv1aHash.Compute(values: "b"u8));
        Assert.Equal(expected: 0xAF63DE4C8601EFF2UL, actual: Fnv1aHash.Compute(values: "c"u8));
        Assert.Equal(expected: 0x85944171F73967E8UL, actual: Fnv1aHash.Compute(values: "foobar"u8));
        Assert.Equal(expected: 0xA430D84680AABD0BUL, actual: Fnv1aHash.Compute(values: "hello"u8));

        // The order sensitivity a published vector set of single characters cannot show on its own: two folds of the
        // same multiset in different orders must differ, so a Compute that summed rather than folded is visible.
        Assert.NotEqual(expected: Fnv1aHash.Compute(values: "ab"u8), actual: Fnv1aHash.Compute(values: "ba"u8));

        return null;
    }
    public static string? MonotonicPartitionerSurface() {
        Assert.Equal(actual: MonotonicPartitioner.MaxBucketCount, expected: 1024);
        Assert.Equal(actual: MonotonicPartitioner.MaxValueCount, expected: 65536);

        for (var value = 0; (value < 4096); ++value) {
            var prior = 0;

            for (var bucketCount = 1; (bucketCount <= 64); ++bucketCount) {
                var safe = MonotonicPartitioner.GetBucketId(bucketCount: bucketCount, value: ((ushort)value));
                var dangerous = MonotonicPartitioner.GetBucketIdDangerous(bucketCount: bucketCount, value: ((ushort)value));

                Assert.Equal(actual: dangerous, expected: safe);
                Assert.InRange(actual: safe, high: (bucketCount - 1), low: 0);
                if ((bucketCount > 1) && (safe != prior)) {
                    Assert.Equal(actual: safe, expected: (bucketCount - 1));
                }
                prior = safe;
            }
        }

        var metrics = MonotonicPartitioner.GetMetrics(bucketCount: 17, value: ((ushort)12345));

        Assert.Equal(expected: 17, actual: metrics.BucketCount);
        Assert.Equal(expected: ((ushort)12345), actual: metrics.Value);
        Assert.InRange(actual: metrics.Rank, low: 0, high: ushort.MaxValue);
        Assert.InRange(actual: metrics.JumpCount, low: 0, high: (MonotonicPartitioner.MaxBucketCount - 1));
        Assert.True(condition: (metrics.MigrationDistance >= 0));
        Assert.Equal(
            expected: ((metrics.MigrationDistance == 0) ? 0.0f : (1.0f / metrics.MigrationDistance)),
            actual: metrics.Velocity
        );

        var constructed = new MonotonicPartitionerMetrics(
            BucketCount: 8,
            JumpCount: 3,
            MigrationDistance: 2,
            Rank: 7,
            Value: 11
        );

        Assert.Equal(expected: 8, actual: constructed.BucketCount);
        Assert.Equal(expected: 3, actual: constructed.JumpCount);
        Assert.Equal(expected: 2, actual: constructed.MigrationDistance);
        Assert.Equal(expected: 7, actual: constructed.Rank);
        Assert.Equal(expected: ((ushort)11), actual: constructed.Value);
        Assert.Equal(expected: 0.5f, actual: constructed.Velocity);

        var guid = new Guid(g: "00112233-4455-6677-8899-aabbccddeeff");

        Assert.Equal(
            expected: MonotonicPartitioner.GetBucketIdDangerous(bucketCount: 257, value: guid),
            actual: MonotonicPartitioner.GetBucketId(bucketCount: 257, value: guid)
        );
        var guidMetrics = MonotonicPartitioner.GetMetrics(bucketCount: 257, value: guid);

        Assert.Equal(expected: 257, actual: guidMetrics.BucketCount);

        return null;
    }
    public static string? BigIntegerSquareRootSurface() {
        // The shared range: every ulong is a BigInteger, so the arbitrary-width Newton descent and the hardware-seeded
        // fixed-width kernel must agree everywhere both are defined. They share no line of code and no idea.
        for (ulong value = 0; (value <= 4096); ++value) {
            Assert.Equal(
                expected: new BigInteger(value: value.SquareRoot()),
                actual: BigIntegerFunctions.SquareRoot(value: new BigInteger(value: value))
            );
        }
        foreach (var value in new ulong[] {
            0UL, 1UL, 2UL, 3UL, 4UL, 255UL, 256UL, 257UL,
            4294967294UL, 4294967295UL, 4294967296UL, 4294967297UL,
            9223372036854775807UL, 9223372036854775808UL,
            18446744065119617024UL, 18446744065119617025UL, 18446744065119617026UL,
            (ulong.MaxValue - 1UL), ulong.MaxValue,
        }) {
            Assert.Equal(
                expected: new BigInteger(value: value.SquareRoot()),
                actual: BigIntegerFunctions.SquareRoot(value: new BigInteger(value: value))
            );
        }

        // Hand-derived above the shared range, where no fixed-width kernel can follow.
        // 2^200 = (2^100)^2 exactly; (2^100 - 1)^2 = 2^200 - 2^101 + 1 < 2^200, so one below roots to 2^100 - 1; and
        // (2^100 + 1)^2 = 2^200 + 2^101 + 1 > 2^200 + 1, so one above still roots to 2^100.
        var twoPow100 = (BigInteger.One << 100);
        var twoPow200 = (BigInteger.One << 200);

        Assert.Equal(expected: twoPow100, actual: BigIntegerFunctions.SquareRoot(value: twoPow200));
        Assert.Equal(expected: (twoPow100 - BigInteger.One), actual: BigIntegerFunctions.SquareRoot(value: (twoPow200 - BigInteger.One)));
        Assert.Equal(expected: twoPow100, actual: BigIntegerFunctions.SquareRoot(value: (twoPow200 + BigInteger.One)));

        // 10^100 = (10^50)^2 exactly, and the same two neighbours by the same derivation.
        var tenPow50 = BigInteger.Pow(value: new BigInteger(value: 10), exponent: 50);
        var tenPow100 = BigInteger.Pow(value: new BigInteger(value: 10), exponent: 100);

        Assert.Equal(expected: tenPow50, actual: BigIntegerFunctions.SquareRoot(value: tenPow100));
        Assert.Equal(expected: (tenPow50 - BigInteger.One), actual: BigIntegerFunctions.SquareRoot(value: (tenPow100 - BigInteger.One)));
        Assert.Equal(expected: tenPow50, actual: BigIntegerFunctions.SquareRoot(value: (tenPow100 + BigInteger.One)));

        // The defining inequality, at operands no fixed-width carrier reaches and at odd powers of two whose root is
        // irrational, where there is no closed literal to compare against at all.
        foreach (var value in new[] {
            twoPow200, (twoPow200 + BigInteger.One), (twoPow200 - BigInteger.One),
            (BigInteger.One << 201), (BigInteger.One << 333), (BigInteger.One << 1024),
            tenPow100, (tenPow100 - BigInteger.One), BigInteger.Pow(value: new BigInteger(value: 10), exponent: 101),
            (BigInteger.One << 64), ((BigInteger.One << 64) - BigInteger.One), ((BigInteger.One << 64) + BigInteger.One),
        }) {
            var root = BigIntegerFunctions.SquareRoot(value: value);

            Assert.True(condition: (root.Sign >= 0));
            Assert.True(condition: ((root * root) <= value));
            Assert.True(condition: (((root + BigInteger.One) * (root + BigInteger.One)) > value));
        }

        // Zero roots to zero; a negative operand is refused rather than answered.
        Assert.Equal(expected: BigInteger.Zero, actual: BigIntegerFunctions.SquareRoot(value: BigInteger.Zero));
        Assert.Equal(
            expected: "value",
            actual: Assert.Throws<ArgumentOutOfRangeException>(
                testCode: () => BigIntegerFunctions.SquareRoot(value: BigInteger.MinusOne)
            ).ParamName
        );
        Assert.Equal(
            expected: "value",
            actual: Assert.Throws<ArgumentOutOfRangeException>(
                testCode: () => BigIntegerFunctions.SquareRoot(value: -twoPow200)
            ).ParamName
        );

        return null;
    }
    public static string? BigIntegerModularInverseSurface() {
        // Hand-derived rows, each verified by multiplying back: 3*5 = 15 = 2*7 + 1; 3*4 = 12 = 11 + 1;
        // 10*12 = 120 = 7*17 + 1; 2*3 = 6 = 5 + 1. The negative row reduces first: -1 = 6 (mod 7) and 6*6 = 36 = 5*7 + 1.
        (BigInteger Value, BigInteger Modulus, BigInteger Inverse)[] rows = [
            (3, 7, 5),
            (3, 11, 4),
            (10, 17, 12),
            (2, 5, 3),
            (-1, 7, 6),
            (1, 7, 1),
            (1, 1, 0),
            (5, 1, 0),
        ];

        foreach (var (value, modulus, inverse) in rows) {
            Assert.Equal(expected: inverse, actual: BigIntegerFunctions.ModularInverse(modulus: modulus, value: value));
        }

        // 2 * (2^63 + 1) = 2^64 + 2 = (2^64 + 1) + 1, so the inverse of two modulo the odd 2^64 + 1 is 2^63 + 1.
        Assert.Equal(
            expected: ((BigInteger.One << 63) + BigInteger.One),
            actual: BigIntegerFunctions.ModularInverse(value: new BigInteger(value: 2), modulus: ((BigInteger.One << 64) + BigInteger.One))
        );

        // The shared range: modulo 2^64 an odd value is invertible, and that is the ONLY modulus the fixed-width
        // Newton-Hensel kernel knows. Extended Euclid and a ladder of squarings share nothing but the answer.
        var twoPow64 = (BigInteger.One << 64);

        for (var value = 1UL; (value < 4096UL); value += 2UL) {
            Assert.Equal(
                expected: new BigInteger(value: value.ModularInverse()),
                actual: BigIntegerFunctions.ModularInverse(value: new BigInteger(value: value), modulus: twoPow64)
            );
        }
        foreach (var value in new ulong[] {
            1UL, 3UL, 5UL, 7UL, 9UL, 4294967295UL, 4294967297UL,
            9223372036854775807UL, 9223372036854775809UL,
            12345678901234567UL, (ulong.MaxValue - 2UL), ulong.MaxValue,
        }) {
            Assert.Equal(
                expected: new BigInteger(value: value.ModularInverse()),
                actual: BigIntegerFunctions.ModularInverse(value: new BigInteger(value: value), modulus: twoPow64)
            );
        }

        // The defining identity and the declared range, at moduli of every shape — prime, composite, a power of two,
        // and one past every fixed-width carrier. The product is reduced with the built-in remainder, not with any
        // Puck.Maths kernel, so this leg leans on nothing the subject also uses.
        BigInteger[] moduli = [
            new(value: 2), new(value: 7), new(value: 8), new(value: 97), new(value: 1000),
            new(value: 2305843009213693951L), twoPow64, (twoPow64 + BigInteger.One),
            BigInteger.Pow(value: new BigInteger(value: 10), exponent: 40),
        ];

        foreach (var modulus in moduli) {
            for (var offset = 0; (offset < 64); ++offset) {
                var value = (new BigInteger(value: ((offset * 2654435761L) + 7L)) - (BigInteger.One << 40));

                if (!BigInteger.GreatestCommonDivisor(left: value, right: modulus).IsOne) { continue; }

                var inverse = BigIntegerFunctions.ModularInverse(modulus: modulus, value: value);

                Assert.InRange(actual: inverse, low: BigInteger.Zero, high: (modulus - BigInteger.One));
                Assert.Equal(
                    expected: (BigInteger.One % modulus),
                    actual: ((((value % modulus) + modulus) * inverse) % modulus)
                );
            }
        }

        // A value sharing a factor with the modulus has no inverse, and the Bezout coefficient in hand would multiply
        // to that factor rather than to one — so the call REFUSES, naming the value, instead of answering.
        //
        // The last two rows are why the ladder above is odd-only, stated here rather than left to be inferred: modulo a
        // power of two the units are exactly the odd residues, so an EVEN value is refused at 2^64 however large it is.
        // An even rung in that ladder is therefore not a weak operand but an illegal one, and it belongs here.
        foreach (var (value, modulus) in new (BigInteger, BigInteger)[] {
            (6, 9), (0, 5), (4, 8), (14, 21), (twoPow64, twoPow64),
            (new BigInteger(value: (ulong.MaxValue - 1UL)), twoPow64), (2, twoPow64),
        }) {
            var refusal = Assert.Throws<ArgumentException>(
                testCode: () => BigIntegerFunctions.ModularInverse(modulus: modulus, value: value)
            );

            Assert.Equal(expected: "value", actual: refusal.ParamName);
        }

        // A non-positive modulus names no ring to invert in.
        foreach (var modulus in new BigInteger[] { 0, -1, -7 }) {
            Assert.Equal(
                expected: "modulus",
                actual: Assert.Throws<ArgumentOutOfRangeException>(
                    testCode: () => BigIntegerFunctions.ModularInverse(value: new BigInteger(value: 3), modulus: modulus)
                ).ParamName
            );
        }

        return null;
    }
    public static string? BigIntegerModularSquareRootSurface() {
        // Hand-derived rows. Mod 7 the squares are 1, 4, 2, 2, 4, 1 at 1..6, so the residues are {0, 1, 2, 4} and the
        // non-residues are {3, 5, 6}; 7 = 3 (mod 4), so a residue answers with the single power v^((7+1)/4) = v^2, and
        // 2^2 = 4 is therefore the returned root of 2. -5 reduces to 2 first and answers identically.
        AssertRoot(expected: 4, oddPrime: 7, value: 2);
        AssertRoot(expected: 4, oddPrime: 7, value: -5);
        AssertRoot(expected: 0, oddPrime: 7, value: 0);
        AssertRoot(expected: 0, oddPrime: 7, value: 14);
        foreach (var nonResidue in new BigInteger[] { 3, 5, 6, -1, -2, -4 }) {
            Assert.False(condition: BigIntegerFunctions.TrySquareRootModuloOddPrime(oddPrime: 7, root: out var refused, value: nonResidue));
            Assert.Equal(expected: BigInteger.Zero, actual: refused);
        }

        // Mod 13 the squares at 1..6 are 1, 4, 9, 3, 12, 10, so the residues are {0, 1, 3, 4, 9, 10, 12}. 13 = 1 (mod 4),
        // so these run the two-part descent rather than the single power, and either of the two roots is legal.
        foreach (var (value, roots) in new (BigInteger Value, BigInteger[] Roots)[] {
            (1, [1, 12]), (3, [4, 9]), (4, [2, 11]), (9, [3, 10]), (10, [6, 7]), (12, [5, 8]),
        }) {
            Assert.True(condition: BigIntegerFunctions.TrySquareRootModuloOddPrime(oddPrime: 13, root: out var root, value: value));
            Assert.Contains(collection: roots, expected: root);
        }
        foreach (var nonResidue in new BigInteger[] { 2, 5, 6, 7, 8, 11 }) {
            Assert.False(condition: BigIntegerFunctions.TrySquareRootModuloOddPrime(oddPrime: 13, root: out var refused, value: nonResidue));
            Assert.Equal(expected: BigInteger.Zero, actual: refused);
        }

        // The shared range: every prime the fixed-width field admits sits below 2^62, and the two carriers must decide
        // the character identically and return roots that are each other or each other's negation. The ladder spans both
        // branches and both extremes of the two-adic valuation of p - 1: 65537 and 257 are Fermat primes whose whole
        // p - 1 is a power of two, and 998244353 = 119 * 2^23 + 1 carries the deepest descent below 2^30.
        foreach (var modulus in new ulong[] {
            3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 97UL, 257UL, 65537UL,
            1000000009UL, 998244353UL, 2305843009213693951UL,
        }) {
            Assert.True(condition: PrimeField64.IsPrime(value: modulus));

            var field = PrimeField64.Create(modulus: modulus);
            var bigModulus = new BigInteger(value: modulus);

            for (var step = 0; (step < 48); ++step) {
                var value = (new BigInteger(value: (step * 6364136223846793005L) ^ 0x2545F4914F6CDD1DL) - (BigInteger.One << 61));
                var reduced = (((value % bigModulus) + bigModulus) % bigModulus);
                var expected = field.TrySqrt(root: out var fieldRoot, value: ((ulong)reduced));
                var actual = BigIntegerFunctions.TrySquareRootModuloOddPrime(oddPrime: bigModulus, root: out var root, value: value);

                Assert.Equal(actual: actual, expected: expected);

                // The quadratic character, decided a third way: binary reciprocity, which neither side runs. A prime
                // modulus makes the Jacobi symbol the Legendre symbol, and zero is the p-divides-value case both accept.
                Assert.Equal(
                    expected: (NumberTheoryFunctions.JacobiSymbol(denominator: bigModulus, numerator: value) >= 0),
                    actual: actual
                );

                if (actual) {
                    Assert.InRange(actual: root, low: BigInteger.Zero, high: (bigModulus - BigInteger.One));
                    Assert.Equal(actual: ((root * root) % bigModulus), expected: reduced);

                    var big = new BigInteger(value: fieldRoot);

                    Assert.True(condition: ((root == big) || (root == (bigModulus - big))));
                } else {
                    Assert.Equal(expected: BigInteger.Zero, actual: root);
                }
            }
        }

        // Above every fixed-width carrier: 2^89 - 1 is the published Mersenne prime M89, and every Mersenne prime is
        // 3 (mod 4), so this reaches the single-power branch at an operand no prime field can hold. The root is checked
        // by squaring it back and the character by reciprocity; nothing here is compared against a second root-finder.
        var mersenne89 = ((BigInteger.One << 89) - BigInteger.One);

        for (var step = 0; (step < 24); ++step) {
            var value = new BigInteger(value: ((step * 2862933555777941757L) + 3037000493L));
            var reduced = (((value % mersenne89) + mersenne89) % mersenne89);
            var accepted = BigIntegerFunctions.TrySquareRootModuloOddPrime(oddPrime: mersenne89, root: out var root, value: value);

            Assert.Equal(
                expected: (NumberTheoryFunctions.JacobiSymbol(denominator: mersenne89, numerator: value) >= 0),
                actual: accepted
            );

            if (accepted) {
                Assert.InRange(actual: root, low: BigInteger.Zero, high: (mersenne89 - BigInteger.One));
                Assert.Equal(actual: ((root * root) % mersenne89), expected: reduced);
            } else {
                Assert.Equal(expected: BigInteger.Zero, actual: root);
            }
        }
        // A square by construction roots back to itself or its negation, so the accept direction is exercised at a
        // full-width operand rather than only at whatever the stream happened to land on.
        var planted = ((BigInteger.One << 44) + new BigInteger(value: 1234567));

        Assert.True(condition: BigIntegerFunctions.TrySquareRootModuloOddPrime(oddPrime: mersenne89, root: out var plantedRoot, value: ((planted * planted) % mersenne89)));
        Assert.True(condition: ((plantedRoot == planted) || (plantedRoot == (mersenne89 - planted))));

        // What is decidable at a glance is enforced; primality is not, and the doc says so.
        // Below three, and even at or above it: both are decidable without factoring, so both are refused outright.
        BigInteger[] refusedModuli = [0, 1, 2, -3, -7, 4, 8, 100, (BigInteger.One << 80)];

        foreach (var modulus in refusedModuli) {
            var rejected = modulus;

            Assert.Equal(
                expected: "oddPrime",
                actual: Assert.Throws<ArgumentOutOfRangeException>(
                    testCode: () => { _ = BigIntegerFunctions.TrySquareRootModuloOddPrime(value: BigInteger.One, oddPrime: rejected, root: out _); }
                ).ParamName
            );
        }

        return null;
    }

    private static void AssertRoot(BigInteger value, BigInteger oddPrime, BigInteger expected) {
        Assert.True(condition: BigIntegerFunctions.TrySquareRootModuloOddPrime(oddPrime: oddPrime, root: out var root, value: value));
        Assert.Equal(actual: root, expected: expected);
    }
    private static BigInteger EvaluatePolynomial(ReadOnlySpan<BigInteger> coefficients, BigInteger point) {
        var result = BigInteger.Zero;

        for (var index = (coefficients.Length - 1); (index >= 0); --index) {
            result = ((result * point) + coefficients[index]);
        }
        return result;
    }
    private static bool IsPrimeByTrialDivision(int value) {
        if (value < 2) { return false; }
        for (var divisor = 2; ((((long)divisor) * divisor) <= value); ++divisor) {
            if ((value % divisor) == 0) { return false; }
        }
        return true;
    }

    public static string? BitMixConstantsInvertSurface() {
        // The multiplier pairs are inverses modulo 2^32 — an algebraic fact about the CONSTANTS, independent of Mix and
        // Unmix ever being called, and the thing a mistyped digit in either constant breaks first.
        var firstMultiplier = InvertibleBitMix.FirstMultiplier;
        var firstInverse = InvertibleBitMix.FirstMultiplierInverse;
        var secondMultiplier = InvertibleBitMix.SecondMultiplier;
        var secondInverse = InvertibleBitMix.SecondMultiplierInverse;

        Assert.Equal(actual: unchecked((firstMultiplier * firstInverse)), expected: 1U);
        Assert.Equal(actual: unchecked((secondMultiplier * secondInverse)), expected: 1U);

        // Both multipliers must be ODD, which is what makes them units modulo 2^32 at all; an even one has no inverse
        // and the pair above could not exist.
        Assert.True(condition: (1U == (firstMultiplier & 1U)), userMessage: "the first multiplier is even and cannot be a unit modulo 2^32");
        Assert.True(condition: (1U == (secondMultiplier & 1U)), userMessage: "the second multiplier is even and cannot be a unit modulo 2^32");

        // The xor-shift inversions Unmix performs are exact only when the shift's own iterate vanishes off the end of a
        // 32-bit word: one doubled term suffices at FirstShift, and MiddleShift needs the tripled one too, which is
        // exactly the asymmetry between Unmix's two xor-shift steps.
        var firstShift = InvertibleBitMix.FirstShift;
        var middleShift = InvertibleBitMix.MiddleShift;

        Assert.True(condition: ((2 * firstShift) >= 32), userMessage: $"a doubled shift of {firstShift} does not vanish, so Unmix's single-term inversion is wrong");
        Assert.True(condition: ((3 * middleShift) >= 32), userMessage: $"a tripled shift of {middleShift} does not vanish, so Unmix's two-term inversion is wrong");
        Assert.True(condition: ((2 * middleShift) < 32), userMessage: $"a doubled shift of {middleShift} already vanishes, so Unmix's second term is dead and the shift is not what the inversion assumes");

        // The round trip over the edge words and a deterministic spread. This is a SAMPLE — bijectivity itself is the
        // exhaustive tier's statement.
        foreach (var value in new uint[] {
            0U, 1U, 2U, 3U, uint.MaxValue, (uint.MaxValue - 1U), 0x80000000U, 0x7FFFFFFFU,
            0xFFFF0000U, 0x0000FFFFU, 0xAAAAAAAAU, 0x55555555U, 0x9E3779B9U,
        }) {
            Assert.Equal(expected: value, actual: InvertibleBitMix.Unmix(value: InvertibleBitMix.Mix(value: value)));
            Assert.Equal(expected: value, actual: InvertibleBitMix.Mix(value: InvertibleBitMix.Unmix(value: value)));
        }

        for (var step = 0UL; (step < 4096UL); ++step) {
            var value = ((uint)((step * 2654435761UL) + 17UL));

            Assert.Equal(expected: value, actual: InvertibleBitMix.Unmix(value: InvertibleBitMix.Mix(value: value)));
        }

        return null;
    }
    public static string? BitMixIsAPermutationSurface() {
        // Unmix o Mix is the identity on EVERY 32-bit word. On a finite set a left inverse forces injectivity and
        // injectivity forces bijectivity, so this one sweep is the whole permutation claim — not a sample of it.
        //
        // Partitioned across cores, reporting the SMALLEST counterexample rather than the first one observed, so the
        // message does not depend on which partition lost the race.
        var counterexample = -1L;
        var gate = new object();

        _ = Parallel.For(
            fromInclusive: 0L,
            toExclusive: 256L,
            body: partition => {
                var low = ((uint)(partition << 24));
                var high = (low + 0xFFFFFFU);
                var local = -1L;

                for (var value = low; ; ++value) {
                    if (InvertibleBitMix.Unmix(value: InvertibleBitMix.Mix(value: value)) != value) {
                        local = value;

                        break;
                    }

                    if (value == high) { break; }
                }

                if (0 <= local) {
                    lock (gate) {
                        counterexample = ((0 <= counterexample) ? Math.Min(val1: counterexample, val2: local) : local);
                    }
                }
            }
        );

        return ((0 <= counterexample)
            ? $"Unmix(Mix(x)) != x at x = {counterexample}, so the mix is not a permutation"
            : null);
    }

    // The nearest integer to numerator/positiveDenominator, ties rounded to even — for a NON-NEGATIVE numerator and
    // a POSITIVE denominator (both true at every call site below).
    private static BigInteger RoundToNearestTiesToEven(BigInteger numerator, BigInteger positiveDenominator) {
        var quotient = BigInteger.DivRem(dividend: numerator, divisor: positiveDenominator, remainder: out var remainder);
        var doubledRemainder = (remainder * 2);

        if ((doubledRemainder > positiveDenominator) || ((doubledRemainder == positiveDenominator) && !quotient.IsEven)) {
            quotient += BigInteger.One;
        }

        return quotient;
    }
    // The turn-fraction 2·π·step/30, correctly quantized to the NEAREST Q48.16 raw (ties to even) — using the SAME
    // Machin-derived circle constant scalar.sincos-vs-series cross-checks against the published expansion, never the
    // kernel's own TurnRawQ16 constant or its per-step integer-division formula, so this owes nothing to how
    // BuildRotors happens to quantize its angle. 2π·step/30 is irrational for every step here (step=0 aside), so a
    // FLOOR/CEILING pair around it never collapses to one integer no matter the precision — the single ONE-DIVISION
    // round below is what a "correctly quantized" target actually means. Oracles.Pi(384) — the same scale the
    // production SinCos reduction itself uses for a full-range angle — has a width of a mere handful of units at
    // that scale, utterly negligible against the 2^368 division here, so using its LOWER bound alone (rather than
    // its midpoint) introduces no error the single rounding below could ever observe.
    private static long IdealTurnFractionRaw(int step) {
        const int WorkingBitCount = 384;
        var circle = Oracles.Pi(bitCount: WorkingBitCount);
        var numerator = ((circle.Low * 2) * step);
        var denominator = (((BigInteger)CyclicRotation.Period) << (WorkingBitCount - FixedQ4816.FractionBitCount));

        return ((long)RoundToNearestTiesToEven(numerator: numerator, positiveDenominator: denominator));
    }

    // The tolerance a "correctly quantized 30th root of unity" comparison allows, in guard units: the kernel's own
    // committed SinCos envelope for |raw| within one turn (3/4 raw ULP, the same regime scalar.sincos-vs-series pins)
    // plus a PROVEN strictly-less-than-two-raw-unit gap between BuildRotors' own per-step integer-division formula and
    // the independently rounded ideal raw above. Proof of that second term: let R = round(2π·2¹⁶) (the kernel's
    // TurnRawQ16, so |R − 2π·2¹⁶| ≤ ½) and X = 2π·2¹⁶ exactly. |R·step/30 − X·step/30| ≤ (29/30)·½ < ½ for every step
    // in [0, 30). floor(R·step/30) is within (−1, 0] of its own argument, and round(X·step/30) is within [−½, ½] of
    // its own — summing the three bounds places BuildRotors' internal raw strictly between −2 and 1 raw units away
    // from the ideal one, and both are integers, so the gap is at most one raw unit. Sine and cosine are 1-Lipschitz
    // in raw units (their exact derivative never exceeds unity), so that gap carries through the trig call exactly,
    // with no widening. 3/4 + 1 rounds up to two whole raw ULP, not fitted to any observed output.
    private static readonly BigInteger RotorToleranceGuardUnits = ((BigInteger.One << Oracles.GuardBitCount) * 2);

    // Pins the baked rotor table's VALUES: entry k lies within the stated envelope of the correctly quantized 30th
    // root of unity, cos(2πk/30) and sin(2πk/30) — the statement this law's OWED marker named as missing. No
    // floating point anywhere: the ideal raw and its enclosure are both BigInteger-exact.
    private static string? CyclicRotationValueAt(int step) {
        var rotor = CyclicRotation.At(plane: 0, tick: step);
        var idealRaw = IdealTurnFractionRaw(step: step);
        var enclosure = Oracles.EncloseSinCos(guardBitCount: Oracles.GuardBitCount, raw: idealRaw);
        var cosScaled = (new BigInteger(value: rotor.Real.Value) << Oracles.GuardBitCount);
        var sinScaled = (new BigInteger(value: rotor.Imaginary.Value) << Oracles.GuardBitCount);

        if ((cosScaled < (enclosure.Cos.Low - RotorToleranceGuardUnits)) || (cosScaled > (enclosure.Cos.High + RotorToleranceGuardUnits))) {
            return $"the rotor at step {step} has Real={rotor.Real.Value}, outside the correctly-quantized 30th root's cosine envelope [{enclosure.Cos.Low}, {enclosure.Cos.High}] by more than {RotorToleranceGuardUnits} guard units";
        }
        if ((sinScaled < (enclosure.Sin.Low - RotorToleranceGuardUnits)) || (sinScaled > (enclosure.Sin.High + RotorToleranceGuardUnits))) {
            return $"the rotor at step {step} has Imaginary={rotor.Imaginary.Value}, outside the correctly-quantized 30th root's sine envelope [{enclosure.Sin.Low}, {enclosure.Sin.High}] by more than {RotorToleranceGuardUnits} guard units";
        }

        return null;
    }

    public static string? CyclicRotationStructureSurface() {
        var period = CyclicRotation.Period;
        var planeCount = CyclicRotation.PlaneCount;

        for (var step = 0; (step < period); ++step) {
            if (CyclicRotationValueAt(step: step) is { } detail) { return detail; }
        }

        for (var plane = 0; (plane < planeCount); ++plane) {
            // Step is exact modular arithmetic, checked against a BigInteger reduction that shares no line with it and
            // handles the negative ticks the subject's FloorModulo is there to absorb.
            for (var tick = -90L; (tick <= 90L); ++tick) {
                var step = CyclicRotation.Step(plane: plane, tick: tick);
                var phase = (((int)((((BigInteger)tick) % period) + period)) % period);

                Assert.InRange(actual: step, high: (period - 1), low: 0);
                Assert.Equal(expected: step, actual: ((CyclicRotation.Step(plane: plane, tick: 1L) * phase) % period));

                // The loop closes bit-exactly: a whole period later is the SAME rotor, not merely a near one.
                Assert.Equal(expected: CyclicRotation.At(plane: plane, tick: tick), actual: CyclicRotation.At(plane: plane, tick: (tick + period)));
                Assert.Equal(expected: step, actual: CyclicRotation.Step(plane: plane, tick: (tick + period)));

                // Rotor is the table read At performs once the step is known, and it reduces a negative or
                // past-period step count the same way.
                Assert.Equal(expected: CyclicRotation.At(plane: plane, tick: tick), actual: CyclicRotation.Rotor(step: step));
                Assert.Equal(expected: CyclicRotation.Rotor(step: (step - period)), actual: CyclicRotation.Rotor(step: (step + (3L * period))));
                Assert.Equal(expected: CyclicRotation.At(plane: 0, tick: tick), actual: CyclicRotation.Rotor(step: tick));
            }

            // Every multiple of the period is the identity, and the identity leaves a vector bit-identical.
            var identity = CyclicRotation.At(plane: plane, tick: 0L);

            foreach (var multiple in new long[] { -3L, -1L, 0L, 1L, 7L }) {
                Assert.Equal(expected: identity, actual: CyclicRotation.At(plane: plane, tick: (multiple * period)));
            }

            // The plane's speed is coprime to the period, so its orbit visits every one of the thirty steps — which is
            // what makes the four planes' speeds {1, 7, 11, 13} a choice rather than an accident.
            var visited = new HashSet<int>();

            for (var tick = 0L; (tick < period); ++tick) { _ = visited.Add(item: CyclicRotation.Step(plane: plane, tick: tick)); }

            Assert.Equal(expected: period, actual: visited.Count);
        }

        // Rotate is At composed with the rotation, and nothing else.
        foreach (var raw in new long[] { 0L, 1L, -1L, 65536L, -65536L, 123456L }) {
            var vector = new FixedVector2(X: FixedQ4816.FromRawBits(value: raw), Y: FixedQ4816.FromRawBits(value: (-raw)));

            for (var plane = 0; (plane < planeCount); ++plane) {
                for (var tick = 0L; (tick < period); ++tick) {
                    Assert.Equal(
                        expected: CyclicRotation.At(plane: plane, tick: tick).Rotate(vector: vector),
                        actual: CyclicRotation.Rotate(plane: plane, tick: tick, vector: vector)
                    );
                }
            }
        }

        return null;
    }

    /// <summary>The Mersenne exponents whose <c>2^p − 1</c> is prime, over the band this law samples.</summary>
    /// <remarks>Provenance: the Mersenne prime exponents, known complete through this range since Lucas (1876) settled
    /// M127 and Powers (1914) M107. Nothing here is computed by the subject.</remarks>
    private static readonly int[] MersennePrimeExponents = [61, 89, 107, 127];
    /// <summary>Exponents in the same band whose <c>2^p − 1</c> is composite.</summary>
    /// <remarks>Provenance: the complement of the exponent list above over the same band. M67 is Cole's (1903).</remarks>
    private static readonly int[] MersenneCompositeExponents = [67, 83, 101];

    public static string? BigIntegerIsPrimeSurface() {
        // The shared range, against an oracle that computes in BigInteger from a trial-division screen and its own
        // strong-probable-prime rounds: no line and no table is common to the two.
        for (var value = 0UL; (value < 4096UL); ++value) {
            Assert.Equal(
                expected: Oracles.ExactPrimality(value: value),
                actual: BigIntegerFunctions.IsPrime(value: new BigInteger(value: value))
            );
        }

        foreach (var value in new ulong[] {
            4093UL, 4099UL, 65521UL, 65537UL, 2147483647UL, 2147483649UL, 4294967291UL, 4294967297UL,
            2305843009213693951UL, 9223372036854775783UL, 18446744073709551557UL, ulong.MaxValue,
        }) {
            Assert.Equal(
                expected: Oracles.ExactPrimality(value: value),
                actual: BigIntegerFunctions.IsPrime(value: new BigInteger(value: value))
            );
        }

        // Past ulong the twelve-base decision is the whole answer. Every one of these operands is above 3.19 * 10^23 or
        // reaches it, so the ACCEPTANCES here are probable rather than proven — but each is a published prime, and a
        // published prime that the test rejected would still be a defect the assertion catches.
        foreach (var exponent in MersennePrimeExponents) {
            var mersenne = ((BigInteger.One << exponent) - BigInteger.One);

            Assert.True(
                condition: BigIntegerFunctions.IsPrime(value: mersenne),
                userMessage: $"the Mersenne prime 2^{exponent} - 1 was rejected"
            );
        }
        foreach (var exponent in MersenneCompositeExponents) {
            var mersenne = ((BigInteger.One << exponent) - BigInteger.One);

            Assert.False(
                condition: BigIntegerFunctions.IsPrime(value: mersenne),
                userMessage: $"the composite 2^{exponent} - 1 was accepted"
            );
        }

        // A product of two primes each past the fixed-width carrier: composite by CONSTRUCTION, so no table is trusted.
        var wideLeft = ((BigInteger.One << 89) - BigInteger.One);
        var wideRight = ((BigInteger.One << 107) - BigInteger.One);

        Assert.False(condition: BigIntegerFunctions.IsPrime(value: (wideLeft * wideRight)));
        Assert.False(condition: BigIntegerFunctions.IsPrime(value: (wideLeft * wideLeft)));

        // Below two there is no primality to decide, and the answer is false rather than a throw — including for the
        // negative operands the BigInteger carrier admits and the fixed-width deciders cannot even spell.
        foreach (var value in new BigInteger[] { 1, 0, -1, -2, -3, -(BigInteger.One << 200) }) {
            Assert.False(condition: BigIntegerFunctions.IsPrime(value: value), userMessage: $"{value} was accepted as prime");
        }

        return null;
    }
    public static string? BigIntegerPrimeFactorsSurface() {
        // The shared range, against the SECOND shipped factorization: the word-sized kernel's Brent walk over a
        // Montgomery ring, which reaches none of the BigInteger splitter's lines.
        for (var value = 2UL; (value < 2048UL); ++value) {
            Assert.Equal(
                expected: value.EnumeratePrimeFactors().ToArray(),
                actual: BigIntegerFunctions.EnumeratePrimeFactors(value: new BigInteger(value: value)).Select(selector: factor => ((ulong)factor)).ToArray()
            );
        }

        // Reassembly, ordering and the primality of each reported factor, over operands of every shape the carrier
        // admits. The product is formed here and compared against the operand, so nothing is taken on the subject's word.
        BigInteger[] operands = [
            2, 3, 4, 360, 1024, 65535, 65536, 2147483647,
            new(value: 2305843009213693951L),
            ((BigInteger.One << 67) - BigInteger.One),
            ((BigInteger.One << 83) - BigInteger.One),
            ((((BigInteger.One << 61) - BigInteger.One) * ((BigInteger.One << 31) - BigInteger.One)) * 8),
            BigInteger.Pow(value: new BigInteger(value: 10), exponent: 22),
        ];

        foreach (var operand in operands) {
            var factors = BigIntegerFunctions.EnumeratePrimeFactors(value: operand).ToArray();
            var product = BigInteger.One;

            for (var index = 0; (index < factors.Length); ++index) {
                var factor = factors[index];

                product *= factor;

                if (0 < index) {
                    Assert.True(
                        condition: (factors[(index - 1)] <= factor),
                        userMessage: $"the factors of {operand} are not ascending at index {index}"
                    );
                }

                // Where a factor fits a word the oracle decides it outright; a wider one is checked by the shipped
                // arbitrary-width decision, which this law also pins, so the two statements support each other rather
                // than one carrying the other alone.
                Assert.True(
                    condition: ((factor <= ulong.MaxValue)
                        ? Oracles.ExactPrimality(value: ((ulong)factor))
                        : BigIntegerFunctions.IsPrime(value: factor)),
                    userMessage: $"{factor} is reported as a prime factor of {operand} and is not prime"
                );
            }

            Assert.Equal(actual: product, expected: operand);
        }

        // Cole's factorization of M67, published in 1903 and derived nowhere in this repository.
        Assert.Equal(
            expected: [new BigInteger(value: 193707721L), new BigInteger(value: 761838257287L)],
            actual: BigIntegerFunctions.EnumeratePrimeFactors(value: ((BigInteger.One << 67) - BigInteger.One)).ToArray()
        );

        // A prime BELOW the deterministic boundary reports ITSELF, so the length is Omega there; only below two is
        // there no factorization at all, and the negative operands the carrier admits report nothing rather than
        // throwing.
        var largestProvable = (PrimeKernels.LeastWitnessFailure - BigInteger.One);

        Assert.True(condition: (new BigInteger(value: 2305843009213693951L) < largestProvable));
        Assert.Equal(
            expected: [new BigInteger(value: 2305843009213693951L)],
            actual: BigIntegerFunctions.EnumeratePrimeFactors(value: new BigInteger(value: 2305843009213693951L)).ToArray()
        );

        // Above it, a value the bounded refutation cannot crack is REFUSED. 2^89 - 1 is a published Mersenne prime, so
        // there is nothing to find and the walk must exhaust: this reaches the exhaustion branch by construction rather
        // than by hoping an operand happens to resist. Exhausting a refutation is not a primality proof, and this
        // method promises prime factors, so it may not report what it cannot prove.
        var mersenne89 = ((BigInteger.One << 89) - BigInteger.One);

        Assert.True(condition: (mersenne89 > PrimeKernels.LeastWitnessFailure));

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => BigIntegerFunctions.EnumeratePrimeFactors(value: mersenne89).ToArray());

        Assert.Contains(expectedSubstring: mersenne89.ToString(), actualString: refusal.Message);

        foreach (var value in new BigInteger[] { 1, 0, -1, -360, -(BigInteger.One << 200) }) {
            Assert.Empty(collection: BigIntegerFunctions.EnumeratePrimeFactors(value: value));
        }

        return null;
    }
    /// <summary>Proves the factorization is sound at the ONE value the twelve-base witness set decides wrongly: the
    /// least strong pseudoprime to bases two through thirty-seven, <c>318665857834031151167461</c>. The gate calls it
    /// prime; the factorization must not, and must return its two genuine prime factors.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// The value is a published constant — A014233(12), the twelfth strong-pseudoprime threshold — so this leg is
    /// classical by provenance: nothing in this tree derives it. Its two factors are both below <c>2^64</c>, where
    /// primality is settled exactly without any witness set at all, so the whole statement rests on arithmetic no part
    /// of the subject participates in.
    /// </para>
    /// <para>
    /// It also pins the CONSTANT beside the bases. A deterministic bound is a function of the witness set, and the
    /// bound this library quoted was a decimal rounded the wrong way — up, past the counterexample — which put the one
    /// value the set gets wrong inside the range the library promised to decide exactly. Checking the constant equals
    /// this product is what makes a future edit to either the bases or the bound fail here.
    /// </para>
    /// </remarks>
    public static string? WitnessSetBoundaryFactorsExactly() {
        var lesser = new BigInteger(value: 399165290221L);
        var greater = new BigInteger(value: 798330580441L);
        var pseudoprime = (lesser * greater);

        // Both factors sit under 2^64, where the answer needs no witness set, so this is a fact about the value rather
        // than a second opinion from the same machinery that is under test.
        Assert.True(condition: BigIntegerFunctions.IsPrime(value: lesser));
        Assert.True(condition: BigIntegerFunctions.IsPrime(value: greater));

        // The recorded ceiling IS this composite. Rounding it to a decimal is the defect that let it be called prime.
        Assert.Equal(expected: pseudoprime, actual: PrimeKernels.LeastWitnessFailure);

        var factors = BigIntegerFunctions.EnumeratePrimeFactors(value: pseudoprime).ToArray();
        var product = BigInteger.One;

        foreach (var factor in factors) {
            Assert.True(condition: BigIntegerFunctions.IsPrime(value: factor), userMessage: $"{factor} was reported as a prime factor of {pseudoprime} but is not prime");
            Assert.True(condition: (factor < PrimeKernels.LeastWitnessFailure), userMessage: $"{factor} was reported as prime from at or above the deterministic bound, where nothing here can prove it");

            product *= factor;
        }

        Assert.Equal(actual: factors, expected: new BigInteger[] { lesser, greater, });
        Assert.Equal(actual: product, expected: pseudoprime);

        return null;
    }
    /// <summary>Proves the prime-counting function at EVERY argument through twenty thousand — composites included —
    /// against a sieve of Eratosthenes built here, and the nth-prime inverse across the same range.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The only other pin on this member walks eleven PRIME arguments, two through thirty-one, against a
    /// hand-enumerated prefix. Composite arguments were unreached, which matters because callers use it there: the
    /// Möbius leg of the presented family evaluates it at sixty. A sieve shares nothing with the subject — it marks
    /// multiples and counts, where the subject reads a table and interpolates — and it settles the whole contiguous
    /// range rather than a scattering of points.
    /// </remarks>
    public static string? PrimeCountingIsDenseAgainstASieve() {
        const uint Bound = 20_000U;

        var composite = new bool[(Bound + 1U)];

        for (var candidate = 2U; ((candidate * candidate) <= Bound); ++candidate) {
            if (composite[candidate]) { continue; }

            for (var multiple = (candidate * candidate); (multiple <= Bound); multiple += candidate) { composite[multiple] = true; }
        }

        var counted = 0U;
        var primes = new List<uint>();

        for (var value = 0U; (value <= Bound); ++value) {
            if ((value >= 2U) && !composite[value]) {
                ++counted;
                primes.Add(item: value);
            }

            var reported = value.PrimeCountingFunction();

            if (counted != reported) {
                return $"the prime-counting function reports {reported} at {value}, where the sieve counts {counted} prime(s) at or below it";
            }
        }

        // The inverse over the same range, so the table is read in both directions rather than only forwards.
        for (var index = 0; (index < primes.Count); ++index) {
            var nth = ((uint)index).NthPrime();

            if (primes[index] != nth) {
                return $"the nth prime at index {index} is reported as {nth}, where the sieve's {index}th prime is {primes[index]}";
            }
        }

        return null;
    }
    /// <summary>Proves a factorization's cost is heap-bounded rather than stack-bounded: an operand whose multiplicity
    /// is two hundred thousand factors deep returns them all.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// A compact operand used to control the native call stack, one frame per factor of two. <c>One &lt;&lt; 200000</c>
    /// is about 25 KiB and took the process down with a stack overflow, which no catch can recover — so this is a
    /// liveness statement, not an accuracy one, and its evidence is that the call returns at all. The odd cofactor is
    /// carried alongside so the two-peeling and the splitter worklist are both exercised in one operand.
    /// </remarks>
    public static string? DeepMultiplicityFactorsWithoutStackGrowth() {
        const int Depth = 200_000;

        var twos = BigIntegerFunctions.EnumeratePrimeFactors(value: (BigInteger.One << Depth)).ToArray();

        Assert.Equal(expected: Depth, actual: twos.Length);
        Assert.All(collection: twos, action: factor => Assert.Equal(expected: (BigInteger.One + BigInteger.One), actual: factor));

        // The same depth with an odd cofactor, so the worklist runs past the two-peeling instead of ending at it.
        var mixed = BigIntegerFunctions.EnumeratePrimeFactors(value: ((BigInteger.One << Depth) * 3)).ToArray();

        Assert.Equal(expected: (Depth + 1), actual: mixed.Length);
        Assert.Equal(expected: new BigInteger(value: 3), actual: mixed[^1]);

        return null;
    }
}
