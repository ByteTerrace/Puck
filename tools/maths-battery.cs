#:project ../src/Puck.Maths/Puck.Maths.csproj
// The Puck.Maths oracle-verification and benchmark harness. Run it whenever Puck.Maths changes:
//
//   dotnet run -c Release tools/maths-battery.cs        (~2-3 minutes; throws on any gate failure)
//
// The -c Release matters and must precede the file path: the accuracy gates hold in Debug too, but the
// benchmark numbers are only meaningful optimized.
//
// What it proves, beyond the fast Post A1 gate:
//   - mul/div/sqrt bit-identity against independent BigInteger/integer-root specifications
//   - primality, factorization, prime counting, and NthPrime against dense-sieve, trial-division, and boundary vectors
//   - 128-bit SWAR reverse/pair/unpair and wrapping exponentiation against direct bit/BigInteger specifications
//   - atan2/sincos/log2/exp2/pow accuracy in ULP against double/BigInteger oracles (dense + random sweeps)
//   - pcg32 against a verbatim transcription of the pcg-c-basic reference (published vector + 5M draws),
//     stream-decorrelation tripwires, advance/seek algebra, snapshot round-trips
//   - gaussian moments/CDF/tails, alias-table distributions and weight overloads, shuffle uniformity,
//     field-noise statistics/continuity, low-discrepancy coverage
//   - modular-group determinant/adjugate-inverse/associativity, trace classification, cusp action vs BigInteger
//     rationals, Gauss reduction into the fundamental domain (contravariant form action + discriminant + idempotence),
//     and quadratic-surd continued-fraction periods (golden [1], silver [2]) with convergents approaching the value
//   - discrete affine-floor measures across rational cadence/domain maps and quadratic aperiodic allocations, including
//     exact range additivity, normalized origins, direct inverse lookup, and randomized signed-index brackets; the
//     rational compiled kernel is checked against that oracle across full-width edges, bounded inverses, and allocation
//   - quaternion/complex/rigid-transform/dual accuracy against mathematical references, incl. the
//     exp/log bridges (bivector at the quaternion level, screw at the rigid level) and the FromTo shortest-arc
//     constructors at both planar and spatial level; vector2 wedge/dot bit-identity against an independent
//     rounding oracle
//   - throughput benchmarks for every hot operation
//
// Conventions to keep when extending:
//   - Oracles specify observable mathematics and rounding policy, never a copy of the implementation's branches.
//   - Gates on values passing through small intermediates must be SCALE-AWARE: a half-ULP absolute error is a
//     large relative error at small magnitudes.
//   - The operation without a sweep is where the bug lives.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Puck.Maths;
using Puck.Maths.Research;

// ---- constants (BigInteger, 100-digit pi) ----
var piDigits = "31415926535897932384626433832795028841971693993751058209749445923078164062862089986280348253421170679";
var pi = BigInteger.Parse(piDigits);
var pow10 = BigInteger.Pow(10, (piDigits.Length - 1));

// ---- scalar specification oracles ----
Console.WriteLine();
Console.WriteLine("---- scalar specifications ----");

// A per-second rate that cannot be represented as one Q48.16 120 Hz delta must carry its division tail instead of
// rounding the same delta 120 times. The independent oracle is simply conservation over one exact engine second:
// accumulated delta raw bits equal rate raw bits, for either sign and for every vector axis.
const long integrationTicksPerSecond = 50_400L;
const ulong integrationStepTicks = 420UL;
const int integrationStepCount = 120;
var positiveRateAccumulator = new FixedRateAccumulator(ticksPerSecond: integrationTicksPerSecond);
var negativeRateAccumulator = new FixedRateAccumulator(ticksPerSecond: integrationTicksPerSecond);
var positiveAdvanceRaw = 0L;
var negativeAdvanceRaw = 0L;

for (var stepIndex = 0; (stepIndex < integrationStepCount); ++stepIndex) {
    positiveAdvanceRaw = checked(positiveAdvanceRaw + positiveRateAccumulator.Integrate(
        ratePerSecond: FixedQ4816.One,
        elapsedTicks: integrationStepTicks
    ).Value);
    negativeAdvanceRaw = checked(negativeAdvanceRaw + negativeRateAccumulator.Integrate(
        ratePerSecond: -FixedQ4816.One,
        elapsedTicks: integrationStepTicks
    ).Value);
}

if ((positiveAdvanceRaw != FixedQ4816.One.Value) ||
    (negativeAdvanceRaw != -FixedQ4816.One.Value) ||
    (positiveRateAccumulator.Remainder != 0L) ||
    (negativeRateAccumulator.Remainder != 0L)) {
    throw new InvalidOperationException("FIXED RATE 120 HZ CONSERVATION MISMATCH");
}

var vectorRate = new FixedVector3(
    X: FixedQ4816.FromRawBits(value: 65_537L),
    Y: FixedQ4816.FromRawBits(value: -12_345L),
    Z: FixedQ4816.FromRawBits(value: 987_654L)
);
var vectorAccumulator = new FixedVector3RateAccumulator(ticksPerSecond: integrationTicksPerSecond);
var vectorAdvance = FixedVector3.Zero;

for (var stepIndex = 0; (stepIndex < integrationStepCount); ++stepIndex) {
    vectorAdvance += vectorAccumulator.Integrate(
        ratePerSecond: vectorRate,
        elapsedTicks: integrationStepTicks
    );
}

if ((vectorAdvance != vectorRate) ||
    (vectorAccumulator.XRemainder != 0L) ||
    (vectorAccumulator.YRemainder != 0L) ||
    (vectorAccumulator.ZRemainder != 0L)) {
    throw new InvalidOperationException("FIXED VECTOR RATE 120 HZ CONSERVATION MISMATCH");
}

// A snapshot restored between steps must produce the same future quotient and remainder. Reset is the explicit seam
// for teleports, clamps, assignments, and other writes that invalidate the carried fractional quantity.
var uninterruptedAccumulator = new FixedRateAccumulator(ticksPerSecond: integrationTicksPerSecond);
for (var stepIndex = 0; (stepIndex < 37); ++stepIndex) {
    _ = uninterruptedAccumulator.Integrate(
        ratePerSecond: FixedQ4816.FromRawBits(value: 100_003L),
        elapsedTicks: integrationStepTicks
    );
}
var restoredAccumulator = FixedRateAccumulator.FromRemainder(
    remainder: uninterruptedAccumulator.Remainder,
    ticksPerSecond: integrationTicksPerSecond
);

for (var stepIndex = 37; (stepIndex < integrationStepCount); ++stepIndex) {
    var uninterruptedDelta = uninterruptedAccumulator.Integrate(
        ratePerSecond: FixedQ4816.FromRawBits(value: 100_003L),
        elapsedTicks: integrationStepTicks
    );
    var restoredDelta = restoredAccumulator.Integrate(
        ratePerSecond: FixedQ4816.FromRawBits(value: 100_003L),
        elapsedTicks: integrationStepTicks
    );

    if (uninterruptedDelta != restoredDelta) {
        throw new InvalidOperationException("FIXED RATE SNAPSHOT CONTINUATION MISMATCH");
    }
}

if (uninterruptedAccumulator.Remainder != restoredAccumulator.Remainder) {
    throw new InvalidOperationException("FIXED RATE SNAPSHOT REMAINDER MISMATCH");
}

restoredAccumulator.Reset();
if (restoredAccumulator.Remainder != 0L) {
    throw new InvalidOperationException("FIXED RATE RESET MISMATCH");
}

Console.WriteLine("rate integration: exact 120 Hz conservation, signed/vector axes, snapshot continuation, and reset OK");

// M4 regression: the time base is accumulator identity — bound at construction, carried through snapshot restore, and
// never re-supplied per Integrate — so a retained remainder can never be reinterpreted under a different denominator.
var boundAccumulator = new FixedRateAccumulator(ticksPerSecond: 3L);
if (boundAccumulator.TicksPerSecond != 3L) {
    throw new InvalidOperationException("FIXED RATE DENOMINATOR NOT BOUND AT CONSTRUCTION");
}

// Integrate no longer accepts a denominator, so a mismatched-denominator continuation is unrepresentable by API shape.
// A default-initialized accumulator has no time base; Integrate must fail loudly rather than divide by zero or no-op.
var defaultAccumulatorThrew = false;
try {
    _ = new FixedRateAccumulator().Integrate(ratePerSecond: FixedQ4816.Epsilon, elapsedTicks: 2UL);
} catch (InvalidOperationException) {
    defaultAccumulatorThrew = true;
}
if (!defaultAccumulatorThrew) {
    throw new InvalidOperationException("DEFAULT FIXED RATE ACCUMULATOR INTEGRATE DID NOT THROW");
}

// FromRemainder must round-trip the denominator with the remainder, so a restored accumulator keeps its own time base.
var restoredDenominatorAccumulator = FixedRateAccumulator.FromRemainder(remainder: 2L, ticksPerSecond: 3L);
if ((restoredDenominatorAccumulator.TicksPerSecond != 3L) || (restoredDenominatorAccumulator.Remainder != 2L)) {
    throw new InvalidOperationException("FIXED RATE FROMREMAINDER DID NOT ROUND-TRIP THE DENOMINATOR");
}

// The M4 scenario: retain remainder 2/3, then integrate nothing. With the denominator bound to 3 (not silently rebound
// to 2), the next delta stays zero — no motion is fabricated from a reinterpreted numerator.
var m4Accumulator = new FixedRateAccumulator(ticksPerSecond: 3L);
if (m4Accumulator.Integrate(ratePerSecond: FixedQ4816.Epsilon, elapsedTicks: 2UL).Value != 0L) {
    throw new InvalidOperationException("M4 SETUP: EXPECTED ZERO DELTA WITH RETAINED REMAINDER");
}
if (m4Accumulator.Remainder != 2L) {
    throw new InvalidOperationException("M4 SETUP: EXPECTED RETAINED REMAINDER 2/3");
}
if (m4Accumulator.Integrate(ratePerSecond: FixedQ4816.Zero, elapsedTicks: 0UL).Value != 0L) {
    throw new InvalidOperationException("M4 REGRESSION: SPURIOUS DELTA FROM REINTERPRETED REMAINDER");
}

// The same guarantees on the vector accumulator: default-struct Integrate throws; FromRemainders carries the denominator.
var defaultVectorThrew = false;
try {
    _ = new FixedVector3RateAccumulator().Integrate(ratePerSecond: FixedVector3.Zero, elapsedTicks: 1UL);
} catch (InvalidOperationException) {
    defaultVectorThrew = true;
}
if (!defaultVectorThrew) {
    throw new InvalidOperationException("DEFAULT VECTOR RATE ACCUMULATOR INTEGRATE DID NOT THROW");
}
if (FixedVector3RateAccumulator.FromRemainders(1L, -1L, 2L, 3L).TicksPerSecond != 3L) {
    throw new InvalidOperationException("VECTOR FROMREMAINDERS DID NOT ROUND-TRIP THE DENOMINATOR");
}

Console.WriteLine("rate integration M4: denominator bound at construction, default-struct throws, FromRemainder round-trips denominator, spurious-delta closed OK");

// One affine floor measure is the common object beneath cadence, exact domain conversion, quota allocation, and the
// quadratic aperiodic words. Boundary differences must telescope exactly; inverse lookup must bracket every output
// index; and rational/quadratic construction must preserve the periodic/aperiodic distinction without approximation.
var fourThirds = DiscreteMeasure.Rational(numerator: 4, denominator: 3);
var fourThirdsExpected = new BigInteger[] { 1, 1, 2, 1, 1, 2, 1, 1, 2 };
for (var index = 0; (index < fourThirdsExpected.Length); ++index) {
    if (fourThirds.AmountAt(index: index) != fourThirdsExpected[index]) {
        throw new InvalidOperationException("DISCRETE MEASURE 4/3 CADENCE MISMATCH");
    }
}
if (!fourThirds.IsPeriodic || (fourThirds.Period != 3) ||
    (fourThirds.MinimumAmount != 1) || (fourThirds.MaximumAmount != 2)) {
    throw new InvalidOperationException("DISCRETE MEASURE RATIONAL METADATA MISMATCH");
}

var audioPerVideoFrame = DiscreteMeasure.Rational(numerator: (48_000 * 1_001), denominator: 60_000);
var audioExpected = new BigInteger[] { 800, 801, 801, 801, 801, 800, 801, 801, 801, 801 };
for (var index = 0; (index < audioExpected.Length); ++index) {
    if (audioPerVideoFrame.AmountAt(index: index) != audioExpected[index]) {
        throw new InvalidOperationException("DISCRETE MEASURE CLOCK CONVERSION MISMATCH");
    }
}
if (audioPerVideoFrame.AmountOver(start: 0, length: 5) != 4_004) {
    throw new InvalidOperationException("DISCRETE MEASURE CLOCK BLOCK CONSERVATION MISMATCH");
}

var normalizedOffset = DiscreteMeasure.Rational(
    numerator: 2,
    denominator: 5,
    offsetNumerator: 7,
    offsetDenominator: 3
);
if (normalizedOffset.Offset != QuadraticSurd.Rational(numerator: 1, denominator: 3)) {
    throw new InvalidOperationException("DISCRETE MEASURE OFFSET NORMALIZATION MISMATCH");
}

var measureRng = new Random(20260721);
for (var sample = 0; (sample < 100_000); ++sample) {
    var numerator = measureRng.Next(minValue: 0, maxValue: 2_001);
    var denominator = measureRng.Next(minValue: 1, maxValue: 257);
    var offsetNumerator = measureRng.Next(minValue: -1_000, maxValue: 1_001);
    var offsetDenominator = measureRng.Next(minValue: 1, maxValue: 257);
    var measure = DiscreteMeasure.Rational(numerator, denominator, offsetNumerator, offsetDenominator);
    if (!measure.TryCompileInt64(compiled: out var compiledMeasure, failure: out var compileFailure) ||
        (compileFailure != DiscreteMeasureCompilationFailure.None)) {
        throw new InvalidOperationException("BOUNDED DISCRETE MEASURE RATIONAL COMPILATION FAILED");
    }
    var start = measureRng.NextInt64(minValue: -100_000L, maxValue: 100_001L);
    var leftLength = measureRng.NextInt64(minValue: 0L, maxValue: 1_001L);
    var rightLength = measureRng.NextInt64(minValue: 0L, maxValue: 1_001L);
    var left = measure.AmountOver(start: start, length: leftLength);
    var right = measure.AmountOver(start: (start + leftLength), length: rightLength);
    var whole = measure.AmountOver(start: start, length: (leftLength + rightLength));
    var mapped = measure.Map(start: start, length: (leftLength + rightLength));
    var end = (start + leftLength + rightLength);

    if ((left + right) != whole) {
        throw new InvalidOperationException("DISCRETE MEASURE RANGE ADDITIVITY MISMATCH");
    }
    if ((measure.AmountBetween(start: start, end: end) != whole) ||
        (measure.MapBetween(start: start, end: end) != mapped)) {
        throw new InvalidOperationException("DISCRETE MEASURE ENDPOINT RANGE MISMATCH");
    }
    if ((mapped.Start != measure.Cumulative(index: start)) || (mapped.Length != whole)) {
        throw new InvalidOperationException("DISCRETE MEASURE RANGE MAP MISMATCH");
    }
    if ((measure.AmountAt(index: start) < measure.MinimumAmount) ||
        (measure.AmountAt(index: start) > measure.MaximumAmount)) {
        throw new InvalidOperationException("DISCRETE MEASURE UNIT BOUND MISMATCH");
    }
    if ((compiledMeasure.Cumulative(index: start) != measure.Cumulative(index: start)) ||
        (compiledMeasure.AmountAt(index: start) != measure.AmountAt(index: start)) ||
        (compiledMeasure.AmountOver(start: start, length: (leftLength + rightLength)) != whole) ||
        (compiledMeasure.AmountBetween(start: start, end: end) != whole)) {
        throw new InvalidOperationException("COMPILED DISCRETE MEASURE ORACLE MISMATCH");
    }
    var compiledMap = compiledMeasure.Map(start: start, length: (leftLength + rightLength));
    if ((compiledMap.Start != mapped.Start) || (compiledMap.Length != mapped.Length)) {
        throw new InvalidOperationException("COMPILED DISCRETE MEASURE MAP MISMATCH");
    }

    if (numerator > 0) {
        var outputIndex = measureRng.NextInt64(minValue: -100_000L, maxValue: 100_001L);
        var containing = measure.IndexContaining(outputIndex: outputIndex);
        if ((measure.Cumulative(index: containing) > outputIndex) ||
            (measure.Cumulative(index: (containing + 1)) <= outputIndex)) {
            throw new InvalidOperationException("DISCRETE MEASURE INVERSE BRACKET MISMATCH");
        }

        var next = measure.NextNonemptyIndex(start: start);
        if ((next < start) || (measure.AmountAt(index: next) <= 0)) {
            throw new InvalidOperationException("DISCRETE MEASURE NEXT NONEMPTY MISMATCH");
        }
        for (var empty = start; (empty < next); ++empty) {
            if (measure.AmountAt(index: empty) != 0) {
                throw new InvalidOperationException("DISCRETE MEASURE NEXT NONEMPTY SKIPPED WORK");
            }
        }

        if (!compiledMeasure.TryLowerBound(amount: outputIndex, index: out var compiledLower) ||
            (compiledLower != measure.LowerBound(amount: outputIndex)) ||
            !compiledMeasure.TryIndexContaining(outputIndex: outputIndex, inputIndex: out var compiledContaining) ||
            (compiledContaining != containing)) {
            throw new InvalidOperationException("COMPILED DISCRETE MEASURE INVERSE MISMATCH");
        }
    }

    var translation = measureRng.NextInt64(minValue: -1_000L, maxValue: 1_001L);
    var translated = measure.Translate(distance: translation);
    if (translated.AmountAt(index: start) != measure.AmountAt(index: (start + translation))) {
        throw new InvalidOperationException("DISCRETE MEASURE TRANSLATION MISMATCH");
    }
}

var inverseGolden = DiscreteMeasure.Create(
    rate: QuadraticSurd.Create(rationalNumerator: -1, surdNumerator: 1, radicand: 5, denominator: 2),
    offset: QuadraticSurd.Zero
);
var inverseGoldenExpected = new BigInteger[] { 0, 1, 0, 1, 1, 0, 1, 0, 1, 1 };
for (var index = 0; (index < inverseGoldenExpected.Length); ++index) {
    if (inverseGolden.AmountAt(index: index) != inverseGoldenExpected[index]) {
        throw new InvalidOperationException("DISCRETE MEASURE QUADRATIC WORD MISMATCH");
    }
}
if (inverseGolden.IsPeriodic || (inverseGolden.Period is not null) ||
    (inverseGolden.MinimumAmount != 0) || (inverseGolden.MaximumAmount != 1)) {
    throw new InvalidOperationException("DISCRETE MEASURE QUADRATIC METADATA MISMATCH");
}
for (var outputIndex = -1_000; (outputIndex <= 1_000); ++outputIndex) {
    var containing = inverseGolden.IndexContaining(outputIndex: outputIndex);
    if ((inverseGolden.Cumulative(index: containing) > outputIndex) ||
        (inverseGolden.Cumulative(index: (containing + 1)) <= outputIndex)) {
        throw new InvalidOperationException("DISCRETE MEASURE QUADRATIC INVERSE MISMATCH");
    }
}

var zeroInverseThrew = false;
try {
    _ = new DiscreteMeasure().LowerBound(amount: 1);
} catch (InvalidOperationException) {
    zeroInverseThrew = true;
}
if (!zeroInverseThrew) {
    throw new InvalidOperationException("ZERO DISCRETE MEASURE INVERSE DID NOT THROW");
}

var negativeRateThrew = false;
try {
    _ = DiscreteMeasure.Rational(numerator: -1, denominator: 2);
} catch (ArgumentOutOfRangeException) {
    negativeRateThrew = true;
}
if (!negativeRateThrew) {
    throw new InvalidOperationException("NEGATIVE DISCRETE MEASURE RATE DID NOT THROW");
}

// The compiled form must keep its advertised bounded edges honest: full-width unit lookup includes long.MaxValue's
// exclusive boundary in Int128; direct range amounts can succeed when cumulative endpoints do not fit long; and every
// unsupported/unrepresentable source reports the exact compilation failure instead of falling back to BigInteger.
var maximumRate = DiscreteMeasure.Rational(numerator: long.MaxValue, denominator: 1).CompileInt64();
if ((maximumRate.AmountAt(index: long.MinValue) != long.MaxValue) ||
    (maximumRate.AmountAt(index: long.MaxValue) != long.MaxValue) ||
    maximumRate.TryCumulative(index: 2L, cumulative: out _) ||
    !maximumRate.TryAmountOver(start: 2L, length: 1L, amount: out var maximumRangeAmount) ||
    (maximumRangeAmount != long.MaxValue) ||
    maximumRate.TryMap(start: 2L, length: 1L, mappedStart: out _, mappedLength: out _)) {
    throw new InvalidOperationException("COMPILED DISCRETE MEASURE FULL-WIDTH EDGE MISMATCH");
}

var alternatingOverflowRate = DiscreteMeasure.Rational(
    numerator: ((BigInteger.One << 64) - BigInteger.One),
    denominator: 2
).CompileInt64();
if ((alternatingOverflowRate.AmountAt(index: 0L) != long.MaxValue) ||
    alternatingOverflowRate.TryAmountAt(index: 1L, amount: out _)) {
    throw new InvalidOperationException("COMPILED DISCRETE MEASURE UNIT OVERFLOW NOT REPORTED");
}

var sparseCompiled = DiscreteMeasure.Rational(numerator: 1, denominator: long.MaxValue).CompileInt64();
if (!sparseCompiled.TryLowerBound(amount: 1L, index: out var sparseBoundary) ||
    (sparseBoundary != long.MaxValue) ||
    !sparseCompiled.TryIndexContaining(outputIndex: 0L, inputIndex: out var sparseOwner) ||
    (sparseOwner != (long.MaxValue - 1L)) ||
    sparseCompiled.TryIndexContaining(outputIndex: 1L, inputIndex: out _)) {
    throw new InvalidOperationException("COMPILED DISCRETE MEASURE BOUNDED INVERSE EDGE MISMATCH");
}

if (!inverseGolden.TryCompileInt64(compiled: out var compiledInverseGolden,
        failure: out var irrationalRateFailure) ||
    (irrationalRateFailure != DiscreteMeasureCompilationFailure.None) ||
    !compiledInverseGolden.IsQuadratic || compiledInverseGolden.IsPeriodic) {
    throw new InvalidOperationException("FULL-WIDTH GOLDEN RATE DID NOT COMPILE");
}
foreach (var goldenIndex in new long[] {
    long.MinValue, long.MinValue + 1, -1_000_000, -1, 0, 1, 1_000_000, long.MaxValue - 1, long.MaxValue
}) {
    if (compiledInverseGolden.AmountAt(goldenIndex) != inverseGolden.AmountAt(goldenIndex)) {
        throw new InvalidOperationException($"FULL-WIDTH GOLDEN UNIT MISMATCH AT {goldenIndex}");
    }
    var exactCumulative = inverseGolden.Cumulative(goldenIndex);
    var expectedFits = exactCumulative >= long.MinValue && exactCumulative <= long.MaxValue;
    var actualFits = compiledInverseGolden.TryCumulative(goldenIndex, out var actualCumulative);
    if ((actualFits != expectedFits) || (actualFits && actualCumulative != exactCumulative)) {
        throw new InvalidOperationException($"FULL-WIDTH GOLDEN CUMULATIVE MISMATCH AT {goldenIndex}");
    }
}
var irrationalOffsetMeasure = DiscreteMeasure.Create(
    rate: QuadraticSurd.Rational(numerator: 1, denominator: 2),
    offset: QuadraticSurd.Create(rationalNumerator: 0, surdNumerator: 1, radicand: 2, denominator: 1)
);
if (!irrationalOffsetMeasure.TryCompileInt64(compiled: out var compiledIrrationalOffset,
        failure: out var irrationalOffsetFailure) ||
    (irrationalOffsetFailure != DiscreteMeasureCompilationFailure.None) ||
    !compiledIrrationalOffset.IsPeriodic || (compiledIrrationalOffset.Period != 2)) {
    throw new InvalidOperationException("BOUNDED QUADRATIC OFFSET DID NOT COMPILE");
}

var zeroWithIrrationalOffset = DiscreteMeasure.Create(
    rate: QuadraticSurd.Zero,
    offset: QuadraticSurd.Create(rationalNumerator: 0, surdNumerator: 1, radicand: 2, denominator: 1)
).CompileInt64();
var zeroQuadraticLowerBoundThrew = false;
try {
    _ = zeroWithIrrationalOffset.LowerBound(amount: 0L);
} catch (InvalidOperationException) {
    zeroQuadraticLowerBoundThrew = true;
}
if (!zeroWithIrrationalOffset.IsQuadratic || !zeroWithIrrationalOffset.IsPeriodic ||
    (zeroWithIrrationalOffset.Period != 1L) || (zeroWithIrrationalOffset.AmountAt(long.MinValue) != 0L) ||
    zeroWithIrrationalOffset.TryLowerBound(amount: 0L, index: out _) ||
    zeroWithIrrationalOffset.TryIndexContaining(outputIndex: 0L, inputIndex: out _) ||
    !zeroQuadraticLowerBoundThrew) {
    throw new InvalidOperationException("COMPILED ZERO RATE WITH IRRATIONAL OFFSET MISMATCH");
}

var silverMeasure = DiscreteMeasure.Create(
    rate: QuadraticSurd.Create(rationalNumerator: 0, surdNumerator: 1, radicand: 2, denominator: 1),
    offset: QuadraticSurd.Create(rationalNumerator: -1, surdNumerator: 1, radicand: 2, denominator: 1)
);
if (!silverMeasure.TryCompileInt64(compiled: out var compiledSilver,
        failure: out var silverFailure) ||
    (silverFailure != DiscreteMeasureCompilationFailure.None) || compiledSilver.IsPeriodic) {
    throw new InvalidOperationException("BOUNDED SILVER MEASURE DID NOT COMPILE");
}
foreach (var silverIndex in new long[] {
    long.MinValue, long.MinValue + 1, -1_000_000, -1, 0, 1, 1_000_000, long.MaxValue - 1, long.MaxValue
}) {
    var expectedAmount = silverMeasure.AmountAt(silverIndex);
    if (!compiledSilver.TryAmountAt(silverIndex, out var actualAmount) || actualAmount != expectedAmount) {
        throw new InvalidOperationException($"BOUNDED SILVER UNIT MISMATCH AT {silverIndex}");
    }
    var expectedCumulative = silverMeasure.Cumulative(silverIndex);
    var expectedFits = expectedCumulative >= long.MinValue && expectedCumulative <= long.MaxValue;
    var actualFits = compiledSilver.TryCumulative(silverIndex, out var actualCumulative);
    if ((actualFits != expectedFits) || (actualFits && actualCumulative != expectedCumulative)) {
        throw new InvalidOperationException($"BOUNDED SILVER CUMULATIVE MISMATCH AT {silverIndex}");
    }
}
for (var outputIndex = -1_000L; outputIndex <= 1_000L; ++outputIndex) {
    if (!compiledSilver.TryLowerBound(outputIndex, out var lower) ||
        lower != silverMeasure.LowerBound(outputIndex) ||
        !compiledSilver.TryIndexContaining(outputIndex, out var containing) ||
        containing != silverMeasure.IndexContaining(outputIndex)) {
        throw new InvalidOperationException($"BOUNDED SILVER INVERSE MISMATCH AT {outputIndex}");
    }
}
for (var sample = 0; sample < 10_000; ++sample) {
    var silverIndex = measureRng.NextInt64(-1_000_000, 1_000_001);
    if ((compiledSilver.Cumulative(silverIndex) != silverMeasure.Cumulative(silverIndex)) ||
        (compiledSilver.AmountAt(silverIndex) != silverMeasure.AmountAt(silverIndex))) {
        throw new InvalidOperationException("BOUNDED SILVER RANDOM ORACLE MISMATCH");
    }
}
var conjugateSilverMeasure = DiscreteMeasure.Create(
    rate: QuadraticSurd.Create(rationalNumerator: 2, surdNumerator: -1, radicand: 2, denominator: 1),
    offset: QuadraticSurd.Create(rationalNumerator: -1, surdNumerator: 1, radicand: 2, denominator: 1)
);
if (!conjugateSilverMeasure.TryCompileInt64(out var compiledConjugateSilver, out var conjugateFailure) ||
    (conjugateFailure != DiscreteMeasureCompilationFailure.None)) {
    throw new InvalidOperationException($"CONJUGATE SILVER MEASURE FAILED TO COMPILE: {conjugateFailure}");
}
foreach (var index in new long[] {
    long.MinValue, long.MinValue + 1, -1_000_000, -1, 0, 1, 1_000_000, long.MaxValue - 1, long.MaxValue
}) {
    if ((compiledConjugateSilver.Cumulative(index) != conjugateSilverMeasure.Cumulative(index)) ||
        (compiledConjugateSilver.AmountAt(index) != conjugateSilverMeasure.AmountAt(index))) {
        throw new InvalidOperationException($"CONJUGATE SILVER MISMATCH AT {index}");
    }
}
for (var sample = 0; sample < 2_000; ++sample) {
    var radicand = measureRng.Next(2, 100);
    var radicandRoot = (int)Math.Sqrt(radicand);
    if ((radicandRoot * radicandRoot) == radicand) { --sample; continue; }
    var denominator = measureRng.Next(1, 9);
    var exactQuadratic = DiscreteMeasure.Create(
        rate: QuadraticSurd.Create(
            rationalNumerator: measureRng.Next(0, 9),
            surdNumerator: 1,
            radicand: radicand,
            denominator: denominator
        ),
        offset: QuadraticSurd.Create(
            rationalNumerator: measureRng.Next(-16, 17),
            surdNumerator: measureRng.Next(-1, 2),
            radicand: radicand,
            denominator: denominator
        )
    );
    if (!exactQuadratic.TryCompileInt64(out var boundedQuadratic, out var boundedQuadraticFailure)) {
        throw new InvalidOperationException($"BOUNDED QUADRATIC SAMPLE FAILED TO COMPILE: {boundedQuadraticFailure}");
    }
    var index = measureRng.NextInt64(-1_000_000, 1_000_001);
    if ((boundedQuadratic.Cumulative(index) != exactQuadratic.Cumulative(index)) ||
        (boundedQuadratic.AmountAt(index) != exactQuadratic.AmountAt(index))) {
        throw new InvalidOperationException("BOUNDED QUADRATIC RANDOM ORACLE MISMATCH");
    }
}
// Exercise the two-limb path rather than merely values whose complete radicand still fits UInt128. At the signed-long
// endpoints these square-root operands occupy roughly 190 bits, while the arbitrary-precision measure remains the
// independent specification.
foreach (var radicand in new ulong[] {
    ulong.MaxValue,
    ulong.MaxValue - 2UL,
    ((ulong)uint.MaxValue * uint.MaxValue) - 1UL,
    ((ulong)uint.MaxValue * uint.MaxValue) + 1UL,
    (1UL << 63) + 159UL,
    (ulong)long.MaxValue
}) {
    var wideQuadratic = DiscreteMeasure.Create(
        rate: QuadraticSurd.Create(
            rationalNumerator: 0,
            surdNumerator: 1,
            radicand: radicand,
            denominator: 1
        ),
        offset: QuadraticSurd.Zero
    );
    if (!wideQuadratic.TryCompileInt64(out var compiledWideQuadratic, out var wideFailure)) {
        throw new InvalidOperationException($"WIDE-RADICAND QUADRATIC FAILED TO COMPILE: {wideFailure}");
    }
    foreach (var index in new long[] {
        long.MinValue, long.MinValue + 1, -1_000_000, -1, 0, 1, 1_000_000, long.MaxValue - 1, long.MaxValue
    }) {
        if (compiledWideQuadratic.AmountAt(index) != wideQuadratic.AmountAt(index)) {
            throw new InvalidOperationException($"WIDE-RADICAND QUADRATIC UNIT MISMATCH AT d={radicand}, n={index}");
        }
        var exactCumulative = wideQuadratic.Cumulative(index);
        var expectedFits = exactCumulative >= long.MinValue && exactCumulative <= long.MaxValue;
        var actualFits = compiledWideQuadratic.TryCumulative(index, out var actualCumulative);
        if ((actualFits != expectedFits) || (actualFits && actualCumulative != exactCumulative)) {
            throw new InvalidOperationException($"WIDE-RADICAND QUADRATIC CUMULATIVE MISMATCH AT d={radicand}, n={index}");
        }
    }
}
var oversizedMeasure = DiscreteMeasure.Rational(
    numerator: ((BigInteger)long.MaxValue + BigInteger.One),
    denominator: 1
);
if (oversizedMeasure.TryCompileInt64(compiled: out _, failure: out var oversizedFailure) ||
    (oversizedFailure != DiscreteMeasureCompilationFailure.CoefficientOutOfRange)) {
    throw new InvalidOperationException("OVERSIZED DISCRETE MEASURE COEFFICIENT COMPILED");
}

var defaultCompiledThrew = false;
try {
    _ = new CompiledDiscreteMeasure64().AmountAt(index: 0L);
} catch (InvalidOperationException) {
    defaultCompiledThrew = true;
}
if (!defaultCompiledThrew) {
    throw new InvalidOperationException("DEFAULT COMPILED DISCRETE MEASURE DID NOT THROW");
}

var allocationProbe = audioPerVideoFrame.CompileInt64();
var allocationSink = 0L;
for (var index = 0L; (index < 1_000L); ++index) { allocationSink ^= allocationProbe.AmountAt(index: index); }
for (var index = 0L; (index < 1_000L); ++index) { allocationSink ^= compiledSilver.AmountAt(index: index); }
for (var index = 0L; (index < 1_000L); ++index) { allocationSink ^= compiledInverseGolden.AmountAt(index: index); }
var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0L; (index < 100_000L); ++index) {
    allocationSink ^= allocationProbe.AmountAt(index: index);
    allocationSink ^= compiledSilver.AmountAt(index: index);
    allocationSink ^= compiledInverseGolden.AmountAt(index: index);
}
var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
if (allocatedAfter != allocatedBefore) {
    throw new InvalidOperationException("COMPILED DISCRETE MEASURE QUERY ALLOCATED");
}

Console.WriteLine($"discrete measure: cadence/domain map, exact range additivity, inverse lookup, normalized origin, rational period, quadratic aperiodicity; compiled rational + bounded quadratic full-width edges and allocation-free queries OK ({allocationSink})");

var edges = new List<long> {
    0L, 1L, -1L, 2L, -2L, 3L, -3L, 0x7FFFL, -0x7FFFL, 0x8000L, -0x8000L, 0x8001L, -0x8001L,
    0xFFFFL, -0xFFFFL, 0x10000L, -0x10000L, 0x10001L, -0x10001L, 0x18000L, -0x18000L,
    (1L << 32), -(1L << 32), (1L << 47), -(1L << 47), ((1L << 47) + 1L), (1L << 48), -(1L << 48),
    ((1L << 48) - 1L), ((1L << 48) + 1L), (1L << 55), (1L << 62), -(1L << 62),
    long.MaxValue, long.MinValue, (long.MaxValue - 1L), (long.MinValue + 1L),
    0x5555555555555555L, -0x5555555555555555L, 0x3333333333333333L,
};
foreach (var a in edges) {
    foreach (var b in edges) {
        var actualProduct = (FixedQ4816.FromRawBits(value: a) * FixedQ4816.FromRawBits(value: b)).Value;
        var expectedProduct = RoundProductSumOracle(sum: ((BigInteger)a * b));

        if (actualProduct != expectedProduct) { throw new InvalidOperationException("SCALAR MULTIPLY ORACLE MISMATCH"); }

        if (b != 0L) {
            var actualQuotient = (FixedQ4816.FromRawBits(value: a) / FixedQ4816.FromRawBits(value: b)).Value;
            var expectedQuotient = RoundRatioQ16(numerator: a, denominator: BigInteger.Abs((BigInteger)b));

            if (b < 0L) { expectedQuotient = unchecked(-expectedQuotient); }
            if (actualQuotient != expectedQuotient) { throw new InvalidOperationException("SCALAR DIVIDE ORACLE MISMATCH"); }
        }
    }

    if (a >= 0L) {
        var radicand = ((BigInteger)a << FixedQ4816.FractionBitCount);
        var root = ISqrt(n: radicand);

        if (FixedQ4816.Sqrt(value: FixedQ4816.FromRawBits(value: a)).Value != (long)root) { throw new InvalidOperationException("SCALAR SQRT ORACLE MISMATCH"); }
    }
}
var rng = new Random(20260713);
for (var n = 0; (n < 2_000_000); n++) {
    var a = RandomScaled(rng);
    var b = RandomScaled(rng);
    var actualProduct = (FixedQ4816.FromRawBits(value: a) * FixedQ4816.FromRawBits(value: b)).Value;

    if (actualProduct != RoundProductSumOracle(sum: ((BigInteger)a * b))) { throw new InvalidOperationException("RANDOM SCALAR MULTIPLY ORACLE MISMATCH"); }

    if (b != 0L) {
        var actualQuotient = (FixedQ4816.FromRawBits(value: a) / FixedQ4816.FromRawBits(value: b)).Value;
        var expectedQuotient = RoundRatioQ16(numerator: a, denominator: BigInteger.Abs((BigInteger)b));

        if (b < 0L) { expectedQuotient = unchecked(-expectedQuotient); }
        if (actualQuotient != expectedQuotient) { throw new InvalidOperationException("RANDOM SCALAR DIVIDE ORACLE MISMATCH"); }
    }
}
Console.WriteLine("mul/div/sqrt: independent specification oracles OK (edges + 2M randoms)");
var maxAtan2Err = 0.0;
for (var n = 0; (n < 5_000_000); n++) {
    var a = RandomScaled(rng);
    var b = RandomScaled(rng);

    if ((a == 0L) && (b == 0L)) { continue; }

    var reference = (Math.Atan2(a, b) * 65536.0); // the ratio is scale-invariant
    var landedV = FixedQ4816.Atan2(y: new(Value: a), x: new(Value: b)).Value;
    var e = Math.Abs((landedV - reference));

    if (e > maxAtan2Err) { maxAtan2Err = e; }
}
if (FixedQ4816.Atan2(y: FixedQ4816.Zero, x: FixedQ4816.Zero).Value != 0L) { throw new InvalidOperationException("ATAN2(0,0) != 0"); }
if (FixedQ4816.Atan2(y: FixedQ4816.Zero, x: FixedQ4816.FromInteger(value: -1L)).Value != ((long)Math.Round((Math.PI * 65536.0)))) { throw new InvalidOperationException("ATAN2(0,-1) != pi"); }
Console.WriteLine($"atan2: max err = {maxAtan2Err:F4} raw ULP over 5M randoms");
if (maxAtan2Err > 0.75) { throw new InvalidOperationException("ATAN2 ACCURACY GATE FAILED"); }

// The unsigned truncating kernels must match the UInt128 reference on every input — including division
// quotient-overflow inputs (x >> 48 >= y), which previously faulted the hardware DivRem on x64 instead of wrapping.
for (var n = 0; (n < 2_000_000); n++) {
    var xu = unchecked((ulong)RandomScaled(rng));
    var yu = unchecked((ulong)RandomScaled(rng));
    var product = ((UInt128)xu * yu);
    var expectedProduct = unchecked((ulong)(product >> UFixedQ4816.FractionBitCount));
    var expectedProductRemainder = ((ulong)product & ((1UL << UFixedQ4816.FractionBitCount) - 1UL));
    var actualProduct = UFixedQ4816.MultiplyUnchecked(x: new(Value: xu), y: new(Value: yu), remainder: out var actualProductRemainder);

    if ((actualProduct.Value != expectedProduct) || (actualProductRemainder != expectedProductRemainder)) {
        throw new InvalidOperationException($"UMUL MISMATCH x={xu} y={yu}: expected=({expectedProduct},{expectedProductRemainder}) actual=({actualProduct.Value},{actualProductRemainder})");
    }

    if (yu == 0UL) { yu = 1UL; }

    var dividend = (((UInt128)xu) << 16);
    var q = (dividend / yu);
    var expectedQ = unchecked((ulong)q);
    var expectedR = ((ulong)(dividend - (q * yu)));
    var actual = UFixedQ4816.DivideUnchecked(x: new(Value: xu), y: new(Value: yu), remainder: out var actualR);

    if ((actual.Value != expectedQ) || (actualR != expectedR)) {
        throw new InvalidOperationException($"UDIV MISMATCH x={xu} y={yu}: expected=({expectedQ},{expectedR}) actual=({actual.Value},{actualR})");
    }
}
var overflowProbe = UFixedQ4816.DivideUnchecked(x: UFixedQ4816.MaxValue, y: UFixedQ4816.Epsilon, remainder: out _);
Console.WriteLine("ufixed mul: OK (2M randoms vs UInt128 reference)");
Console.WriteLine($"ufixed div: OK (2M randoms vs UInt128 reference; overflow probe wraps to {overflowProbe.Value} without faulting)");

// Q48.16 rounding boundary (M2): Ceiling/Round must throw when the true result carries past MaxValue's
// integral part rather than wrap to the opposite extreme; a fraction that still rounds DOWN in the top
// integer bucket must succeed unchanged.
var maxValueFloor = (FixedQ4816.MaxValue.Value & unchecked((long)~0xFFFFUL));
var uMaxValueFloor = (UFixedQ4816.MaxValue.Value & unchecked(~0xFFFFUL));

var signedCeilingOverflowed = false;
try {
    _ = FixedQ4816.Ceiling(value: FixedQ4816.MaxValue);
} catch (OverflowException) {
    signedCeilingOverflowed = true;
}
if (!signedCeilingOverflowed) { throw new InvalidOperationException("CEILING(MAXVALUE) WRAP REGRESSION"); }

var signedRoundOverflowed = false;
try {
    _ = FixedQ4816.Round(value: FixedQ4816.MaxValue);
} catch (OverflowException) {
    signedRoundOverflowed = true;
}
if (!signedRoundOverflowed) { throw new InvalidOperationException("ROUND(MAXVALUE) WRAP REGRESSION"); }

var unsignedCeilingOverflowed = false;
try {
    _ = UFixedQ4816.Ceiling(value: UFixedQ4816.MaxValue);
} catch (OverflowException) {
    unsignedCeilingOverflowed = true;
}
if (!unsignedCeilingOverflowed) { throw new InvalidOperationException("UCEILING(MAXVALUE) WRAP REGRESSION"); }

var unsignedRoundOverflowed = false;
try {
    _ = UFixedQ4816.Round(value: UFixedQ4816.MaxValue);
} catch (OverflowException) {
    unsignedRoundOverflowed = true;
}
if (!unsignedRoundOverflowed) { throw new InvalidOperationException("UROUND(MAXVALUE) WRAP REGRESSION"); }

if ((FixedQ4816.Ceiling(value: FixedQ4816.FromRawBits(value: maxValueFloor)).Value != maxValueFloor) ||
    (FixedQ4816.Round(value: FixedQ4816.FromRawBits(value: (maxValueFloor + 0x7FFFL))).Value != maxValueFloor) ||
    (UFixedQ4816.Ceiling(value: UFixedQ4816.FromRawBits(value: uMaxValueFloor)).Value != uMaxValueFloor) ||
    (UFixedQ4816.Round(value: UFixedQ4816.FromRawBits(value: (uMaxValueFloor + 0x7FFFUL))).Value != uMaxValueFloor)) {
    throw new InvalidOperationException("CEILING/ROUND LARGEST REPRESENTABLE REGRESSION");
}

if (FixedQ4816.Floor(value: FixedQ4816.MinValue) != FixedQ4816.MinValue) {
    throw new InvalidOperationException("FLOOR(MINVALUE) REGRESSION");
}

Console.WriteLine("rounding boundary: Ceiling/Round throw at MaxValue for both signed/unsigned, largest-representable roundings hold, Floor(MinValue) unchanged");

for (var n = 0; (n < 200_000); ++n) {
    var signed = FixedQ4816.FromRawBits(value: rng.NextInt64(long.MinValue, long.MaxValue));
    var unsigned = UFixedQ4816.FromRawBits(value: unchecked((ulong)rng.NextInt64(long.MinValue, long.MaxValue)));
    var signedDouble = ((double)signed);
    var unsignedDouble = ((double)unsigned);

    if (BitConverter.DoubleToInt64Bits(value: double.CreateChecked(value: signed)) != BitConverter.DoubleToInt64Bits(value: signedDouble) ||
        BitConverter.DoubleToInt64Bits(value: double.CreateChecked(value: unsigned)) != BitConverter.DoubleToInt64Bits(value: unsignedDouble) ||
        CreateCheckedGeneric<FixedQ4816, double>(value: signedDouble) != FixedQ4816.FromDouble(value: signedDouble) ||
        CreateCheckedGeneric<UFixedQ4816, double>(value: unsignedDouble) != UFixedQ4816.FromDouble(value: unsignedDouble)) {
        throw new InvalidOperationException("GENERIC FLOATING CONVERSION FIDELITY MISMATCH");
    }
}
Console.WriteLine("generic math: float64 conversions preserve the direct fixed-point seams (200k full-width values)");

var exactUnsignedDoubleBoundary = (18446744073709549568d / 65536d);
if ((UFixedQ4816.FromDouble(value: exactUnsignedDoubleBoundary).Value != 18446744073709549568UL) ||
    (CreateCheckedGeneric<UFixedQ4816, double>(value: exactUnsignedDoubleBoundary).Value != 18446744073709549568UL)) {
    throw new InvalidOperationException("UFIXED FLOAT64 UPPER BOUNDARY REGRESSION");
}
if ((UFixedQ0016.Parse(s: "0.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != 1U) ||
    (UFixedQ0032.Parse(s: "0.000000000116415321826934814453126", provider: CultureInfo.InvariantCulture).Value != 1U)) {
    throw new InvalidOperationException("UNIT-FRACTION LONG DECIMAL DOUBLE-ROUND REGRESSION");
}
if ((FixedQ4816.Parse(s: "100000000000000.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != 6_553_600_000_000_000_001L) ||
    (FixedQ4816.Parse(s: "-100000000000000.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != -6_553_600_000_000_000_001L) ||
    (UFixedQ4816.Parse(s: "100000000000000.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != 6_553_600_000_000_000_001UL)) {
    throw new InvalidOperationException("Q48.16 LONG DECIMAL DOUBLE-ROUND REGRESSION");
}

// Prime primitives must remain exact over the full uint domain. The dense sieve and upper-range trial division
// are independent specifications; factorization is checked by reconstruction, ordering, and oracle-prime factors.
var densePrimeOracle = SievePrimeOracle(inclusiveMaximum: 1_000_000);
for (var value = 0U; (value < densePrimeOracle.Length); ++value) {
    if (value.IsPrime() != densePrimeOracle[value]) {
        throw new InvalidOperationException($"DENSE PRIME ORACLE MISMATCH value={value}");
    }
}
var trialPrimes = PrimeListOracle(exclusiveMaximum: 65536);
var primeRng = new Random(20260716);
Span<uint> factorBuffer = stackalloc uint[32];
for (var n = 0; (n < 25_000); ++n) {
    var value = unchecked((uint)primeRng.NextInt64(0L, 1L << 32));
    var expectedPrime = IsPrimeOracle(value: value, trialPrimes: trialPrimes);

    if (value.IsPrime() != expectedPrime) {
        throw new InvalidOperationException($"FULL-WIDTH PRIME ORACLE MISMATCH value={value}");
    }

    var factorCount = value.Factorize(destination: factorBuffer);

    if (expectedPrime || (value < 2U)) {
        if (0 != factorCount) { throw new InvalidOperationException($"PRIME FACTORIZATION SHOULD BE EMPTY value={value}"); }

        continue;
    }
    if (0 == factorCount) { throw new InvalidOperationException($"COMPOSITE FACTORIZATION IS EMPTY value={value}"); }

    var product = 1UL;
    var previous = 0U;

    for (var i = 0; (i < factorCount); ++i) {
        var factor = factorBuffer[i];

        if ((factor < previous) || !IsPrimeOracle(value: factor, trialPrimes: trialPrimes)) {
            throw new InvalidOperationException($"INVALID FACTOR value={value} factor={factor}");
        }

        product *= factor;
        previous = factor;
    }

    if (product != value) { throw new InvalidOperationException($"FACTORIZATION PRODUCT MISMATCH value={value} product={product}"); }
}
(uint Index, uint Prime)[] nthPrimeVectors = [
    (0U, 2U), (1U, 3U), (2U, 5U), (5U, 13U), (100U, 547U), (1_000U, 7_927U),
    (10_000U, 104_743U), (100_000U, 1_299_721U), (1_000_000U, 15_485_867U),
    (203_280_220U, 4_294_967_291U),
];
foreach (var (index, expectedPrime) in nthPrimeVectors) {
    var actualPrime = index.NthPrime();

    if ((actualPrime != expectedPrime) || (actualPrime.PrimeCountingFunction() != (index + 1U))) {
        throw new InvalidOperationException($"NTH PRIME ORACLE MISMATCH index={index} expected={expectedPrime} actual={actualPrime}");
    }
}
Console.WriteLine("prime primitives: dense 0..1M + 25k full-width oracle/factorization + NthPrime boundary vectors OK");

// Compare the 128-bit SWAR paths with direct bit-by-bit specifications.
var binaryRng = new Random(20260717);
for (var n = 0; (n < 100_000); ++n) {
    var low = unchecked((ulong)binaryRng.NextInt64(long.MinValue, long.MaxValue));
    var high = unchecked((ulong)binaryRng.NextInt64(long.MinValue, long.MaxValue));
    var value = (((UInt128)high << 64) | low);
    var expectedReverse = ReverseBitsOracle(value: value);

    if (value.ReverseBits<UInt128>() != expectedReverse) {
        throw new InvalidOperationException($"UINT128 REVERSE-BITS ORACLE MISMATCH value={value}");
    }
    if (unchecked(((Int128)value).ReverseBits<Int128>()) != unchecked((Int128)expectedReverse)) {
        throw new InvalidOperationException($"INT128 REVERSE-BITS ORACLE MISMATCH value={value}");
    }

    var other = unchecked((ulong)binaryRng.NextInt64(long.MinValue, long.MaxValue));
    var paired = low.BitwisePair<ulong, UInt128>(other: other);
    var expectedPair = BitwisePairOracle(value: low, other: other);
    var (unpairedLow, unpairedOther) = expectedPair.BitwiseUnpair<UInt128, ulong>();

    if ((paired != expectedPair) || (unpairedLow != low) || (unpairedOther != other)) {
        throw new InvalidOperationException($"UINT128 PAIR/UNPAIR ORACLE MISMATCH left={low} right={other}");
    }

    var exponent = ((uint)binaryRng.Next(minValue: 0, maxValue: 128));
    var expectedPower = ((UInt128)BigInteger.ModPow(
        value: ((BigInteger)value),
        exponent: exponent,
        modulus: (BigInteger.One << 128)
    ));

    if (value.Exponentiate(exponent: ((UInt128)exponent)) != expectedPower) {
        throw new InvalidOperationException($"UINT128 EXPONENTIATE ORACLE MISMATCH exponent={exponent} value={value}");
    }
}
if (UInt128.Zero.Exponentiate(exponent: UInt128.Zero) != UInt128.One) {
    throw new InvalidOperationException("UINT128 EXPONENTIATE ZERO-EXPONENT REGRESSION");
}
Console.WriteLine("binary integers: 100k 128-bit reverse, Morton pair/unpair, and UInt128 wrapping-power oracles OK");

// ---- Pcg32XshRr vs the reference implementation ----
Console.WriteLine();
Console.WriteLine("---- pcg32 ----");

// Reference vector: Create(42, 54) must equal canonical srandom(42, 54) end-to-end (mapping + seeding + core).
var refRng = new PcgRef { Inc = (54UL << 1) | 1UL, State = 0UL };
_ = refRng.Next();
refRng.State += 42UL;
_ = refRng.Next();
var vecRng = Pcg32XshRr.Create(state: 42UL, stream: 54UL);
Console.Write("srandom(42, 54) first six: ");
for (var i = 0; (i < 6); i++) {
    var expected = refRng.Next();
    var actual = vecRng.NextUInt32();

    Console.Write($"0x{actual:x8} ");

    if (actual != expected) {
        throw new InvalidOperationException($"PCG VECTOR MISMATCH at {i}: ref=0x{expected:x8} puck=0x{actual:x8}");
    }
}
Console.WriteLine();

// Puck == transcribed reference over random raw states/increments.
for (var pair = 0; (pair < 50); pair++) {
    var st = unchecked((ulong)RandomScaled(rng));
    var inc = unchecked((ulong)RandomScaled(rng)) | 1UL;
    var r = new PcgRef { Inc = inc, State = st };
    var p = Pcg32XshRr.FromRawBits(increment: inc, multiplier: Pcg32XshRr.DefaultMultiplier, state: st);

    for (var n = 0; (n < 100_000); n++) {
        if (r.Next() != p.NextUInt32()) {
            throw new InvalidOperationException($"PCG MISMATCH state={st} inc={inc} draw={n}");
        }
    }
}
Console.WriteLine("core: OK (reference vector + 50x100k draws vs transcribed reference)");

// Advance(n) == n sequential draws; Advance(2^64 - n) walks back.
foreach (var steps in new ulong[] { 0UL, 1UL, 2UL, 3UL, 17UL, 1000UL, ((1UL << 33) + 5UL), }) {
    var sequential = Pcg32XshRr.Create(state: 42UL, stream: 7UL);
    var skipped = Pcg32XshRr.Create(state: 42UL, stream: 7UL);

    for (var n = 0UL; (n < steps); n++) { _ = sequential.NextUInt32(); }

    skipped.Advance(count: steps);

    if (sequential.State != skipped.State) {
        throw new InvalidOperationException($"ADVANCE MISMATCH steps={steps}");
    }

    skipped.Advance(count: unchecked((0UL - steps)));

    if (skipped.State != Pcg32XshRr.Create(state: 42UL, stream: 7UL).State) {
        throw new InvalidOperationException($"ADVANCE BACKWARD MISMATCH steps={steps}");
    }
}
Console.WriteLine("advance: OK (forward == sequential draws; backward returns to start)");

// Snapshot round-trip mid-sequence; stream independence; bounded-draw properties; fraction adapters.
var live = Pcg32XshRr.Create(state: 123456789UL, stream: 3UL);
for (var n = 0; (n < 1000); n++) { _ = live.NextUInt32(); }
var restored = Pcg32XshRr.FromRawBits(increment: live.Increment, multiplier: live.Multiplier, state: live.State);
for (var n = 0; (n < 100_000); n++) {
    if (live.NextUInt32() != restored.NextUInt32()) {
        throw new InvalidOperationException($"ROUNDTRIP MISMATCH at draw {n}");
    }
}
foreach (var (idA, idB) in new (ulong, ulong)[] { (0UL, 1UL), (1UL, 2UL), (7UL, 8UL), (100UL, 101UL), (0UL, 2UL), }) {
    var streamA = Pcg32XshRr.Create(state: 99UL, stream: idA);
    var streamB = Pcg32XshRr.Create(state: 99UL, stream: idB);
    var identical = 0;

    for (var n = 0; (n < 1000); n++) {
        if (streamA.NextUInt32() == streamB.NextUInt32()) { identical++; }
    }

    if (identical > 10) {
        throw new InvalidOperationException($"STREAMS CORRELATED: ids ({idA},{idB}) gave {identical}/1000 identical draws");
    }
}
var bounded = Pcg32XshRr.Create(state: 5UL, stream: 5UL);
var boundedSwapped = Pcg32XshRr.Create(state: 5UL, stream: 5UL);
for (var n = 0; (n < 1_000_000); n++) {
    var v = bounded.NextUInt32(minimum: 10U, maximum: 17U);
    var w = boundedSwapped.NextUInt32(minimum: 17U, maximum: 10U);

    if ((v < 10U) || (v > 17U)) { throw new InvalidOperationException($"BOUNDED OUT OF RANGE: {v}"); }
    if (v != w) { throw new InvalidOperationException("SWAPPED BOUNDS DIVERGED"); }
}
var full = Pcg32XshRr.Create(state: 5UL, stream: 5UL);
var fullTwin = Pcg32XshRr.Create(state: 5UL, stream: 5UL);
for (var n = 0; (n < 1000); n++) {
    if (full.NextUInt32(minimum: 0U, maximum: uint.MaxValue) != fullTwin.NextUInt32()) {
        throw new InvalidOperationException("FULL-RANGE BOUNDED != RAW DRAW");
    }
}
var fracRng = Pcg32XshRr.Create(state: 8UL, stream: 8UL);
var fracTwin = Pcg32XshRr.Create(state: 8UL, stream: 8UL);
for (var n = 0; (n < 1000); n++) {
    var expected16 = ((ushort)(fracTwin.NextUInt32() >> 16));

    if (fracRng.NextUFixedQ0016().Value != expected16) { throw new InvalidOperationException("UQ0016 ADAPTER MISMATCH"); }
}
for (var n = 0; (n < 1000); n++) {
    if (fracRng.NextUFixedQ0032().Value != fracTwin.NextUInt32()) { throw new InvalidOperationException("UQ0032 ADAPTER MISMATCH"); }
}
Console.WriteLine("roundtrip/streams/bounded/adapters: OK");

// ---- log2 / gaussian / alias table ----
Console.WriteLine();
Console.WriteLine("---- log2 / gaussian / alias ----");

// Exact constant: round(2 ln2 · 2^30) from a 60-digit ln2.
var ln2Digits = "693147180559945309417232121458176568075500134360255254120680";
var ln2Scaled = BigInteger.Parse(ln2Digits);
var ln2Pow10 = BigInteger.Pow(10, ln2Digits.Length);
var twoLn2Q30 = (long)RoundDiv(((2 * ln2Scaled) * (BigInteger.One << 30)), ln2Pow10);
Console.WriteLine($"TwoLn2Q30 = {twoLn2Q30} (landed constant must equal this)");
if (twoLn2Q30 != 1488522236L) {
    throw new InvalidOperationException($"TWOLN2 CONSTANT MISMATCH: computed {twoLn2Q30}, landed 1488522236");
}

// Log2: exact powers of two, then dense + random sweeps vs Math.Log2 in ULP.
for (var k = -16; (k <= 46); k++) {
    var v = FixedQ4816.FromRawBits(value: ((k >= 0) ? (65536L << k) : (65536L >> (-k))));

    if (FixedQ4816.Log2(value: v).Value != (k * 65536L)) {
        throw new InvalidOperationException($"LOG2 POWER MISMATCH at 2^{k}");
    }
}
if (FixedQ4816.Log2(value: FixedQ4816.Zero).Value != long.MinValue) { throw new InvalidOperationException("LOG2(0) != MinValue"); }
if (FixedQ4816.Log2(value: FixedQ4816.FromInteger(value: -5L)).Value != long.MinValue) { throw new InvalidOperationException("LOG2(neg) != MinValue"); }
var maxLog2Err = 0.0;
for (var raw = 1L; (raw <= 200_000L); raw++) {
    var actual = FixedQ4816.Log2(value: FixedQ4816.FromRawBits(value: raw)).Value;
    var expected = (Math.Log2((raw / 65536.0)) * 65536.0);
    var e = Math.Abs((actual - expected));

    if (e > maxLog2Err) { maxLog2Err = e; }
}
for (var n = 0; (n < 2_000_000); n++) {
    var raw = Math.Abs(RandomScaled(rng));

    if (raw == 0L) { continue; }

    var actual = FixedQ4816.Log2(value: FixedQ4816.FromRawBits(value: raw)).Value;
    var expected = (Math.Log2((raw / 65536.0)) * 65536.0);
    var e = Math.Abs((actual - expected));

    if (e > maxLog2Err) { maxLog2Err = e; }
}
Console.WriteLine($"log2: max err = {maxLog2Err:F4} raw ULP (dense 200k + 2M random)");
if (maxLog2Err > 0.75) { throw new InvalidOperationException("LOG2 ACCURACY GATE FAILED"); }

// Exp2: exact whole-number powers, dense ULP sweep where absolute ULP is representable, relative sweep above,
// saturation/underflow edges, Log2 roundtrip. Pow: exact integer path + fractional route + edge policies.
for (var k = -16; (k <= 46); k++) {
    var expected = (((k >= -16) && (k <= 46)) ? ((k >= 0) ? (65536L << k) : (65536L >> (-k))) : 0L);

    if (FixedQ4816.Exp2(value: FixedQ4816.FromInteger(value: ((long)k))).Value != expected) {
        throw new InvalidOperationException($"EXP2 POWER MISMATCH at 2^{k}");
    }
}
if (FixedQ4816.Exp2(value: FixedQ4816.FromInteger(value: 47L)).Value != long.MaxValue) { throw new InvalidOperationException("EXP2 SATURATION FAILED"); }
if (FixedQ4816.Exp2(value: FixedQ4816.FromInteger(value: -19L)).Value != 0L) { throw new InvalidOperationException("EXP2 UNDERFLOW FAILED"); }
var maxExp2Err = 0.0;
for (var raw = (-18L * 65536L); (raw <= (26L * 65536L)); raw += 3L) {
    var actual = FixedQ4816.Exp2(value: FixedQ4816.FromRawBits(value: raw)).Value;
    var reference = (Math.Pow(2.0, (raw / 65536.0)) * 65536.0);
    var e = Math.Abs((actual - reference));

    if (e > 10_000.0) {
        Console.WriteLine($"EXP2 PROBE raw={raw} actual={actual} reference={reference:F2}");

        throw new InvalidOperationException("EXP2 PROBE");
    }

    if (e > maxExp2Err) { maxExp2Err = e; }
}
Console.WriteLine($"exp2: max err = {maxExp2Err:F4} raw ULP (dense [-18, 26])");
if (maxExp2Err > 0.75) { throw new InvalidOperationException("EXP2 ACCURACY GATE FAILED"); }
var maxExp2RelErr = 0.0;
for (var n = 0; (n < 500_000); n++) {
    var raw = rng.NextInt64((26L * 65536L), (47L * 65536L));
    var actual = ((double)FixedQ4816.Exp2(value: FixedQ4816.FromRawBits(value: raw)).Value);
    var reference = (Math.Pow(2.0, (raw / 65536.0)) * 65536.0);
    var e = (Math.Abs((actual - reference)) / reference);

    if (e > maxExp2RelErr) { maxExp2RelErr = e; }
}
Console.WriteLine($"exp2: max relative err = {maxExp2RelErr:E3} (large results; gate 2^-40)");
if (maxExp2RelErr > Math.Pow(2.0, -40.0)) { throw new InvalidOperationException("EXP2 RELATIVE GATE FAILED"); }
var maxRoundTripLogErr = 0.0;
for (var n = 0; (n < 1_000_000); n++) {
    // Roundtrip precision scales with the intermediate's magnitude: the intermediate's half-ULP absolute error
    // becomes 0.5/intermediate relative, i.e. log2(e)·65536·0.51/intermediate raw in log space.
    var x = FixedQ4816.FromRawBits(value: rng.NextInt64((-10L * 65536L), (20L * 65536L)));
    var intermediate = FixedQ4816.Exp2(value: x);
    var error = Math.Abs((FixedQ4816.Log2(value: intermediate).Value - x.Value));
    var tolerance = ((((1.4427 * 65536.0) * 0.65) / Math.Max(intermediate.Value, 1L)) + 2.0);
    var normalized = (error / tolerance);

    if (normalized > maxRoundTripLogErr) { maxRoundTripLogErr = normalized; }
}
Console.WriteLine($"log2(exp2(x)) roundtrip: max err = {maxRoundTripLogErr:F3} x scale-aware tolerance");
if (maxRoundTripLogErr > 1.0) { throw new InvalidOperationException("EXP2/LOG2 ROUNDTRIP FAILED"); }
if (FixedQ4816.Pow(x: FixedQ4816.FromInteger(value: 3L), y: FixedQ4816.FromInteger(value: 2L)).Value != (9L * 65536L)) { throw new InvalidOperationException("POW(3,2) != 9"); }
if (FixedQ4816.Pow(x: FixedQ4816.FromInteger(value: 7L), y: FixedQ4816.One).Value != (7L * 65536L)) { throw new InvalidOperationException("POW(7,1) != 7"); }
if (FixedQ4816.Pow(x: FixedQ4816.Zero, y: FixedQ4816.Zero).Value != 65536L) { throw new InvalidOperationException("POW(0,0) != 1"); }
if (FixedQ4816.Pow(x: FixedQ4816.Zero, y: FixedQ4816.One).Value != 0L) { throw new InvalidOperationException("POW(0,1) != 0"); }
if (FixedQ4816.Pow(x: FixedQ4816.Zero, y: FixedQ4816.NegativeOne).Value != long.MaxValue) { throw new InvalidOperationException("POW(0,-1) != Max"); }
if (FixedQ4816.Pow(x: FixedQ4816.FromInteger(value: -2L), y: FixedQ4816.FromInteger(value: 2L)).Value != 0L) { throw new InvalidOperationException("POW(neg base) != 0"); }
if (FixedQ4816.Pow(x: FixedQ4816.FromDouble(value: 0.5d), y: FixedQ4816.FromInteger(value: -20L)).Value != (1048576L * 65536L)) { throw new InvalidOperationException("POW(0.5,-20) != 2^20"); }

// Regression cases for power and exponential edge conditions:
// (1) Pow crash: intermediate positive-power rounding collapse before inversion (DivideByZeroException).
if (FixedQ4816.Pow(x: FixedQ4816.FromDouble(value: 0.5d), y: FixedQ4816.FromInteger(value: -17L)).Value != (65536L << 17)) { throw new InvalidOperationException("POW(0.5,-17) REGRESSION"); }
if (FixedQ4816.Pow(x: FixedQ4816.FromDouble(value: 0.5d), y: FixedQ4816.FromInteger(value: -18L)).Value != (65536L << 18)) { throw new InvalidOperationException("POW(0.5,-18) REGRESSION"); }
// (2) Exp2 shift wrap for astronomically negative exponents (returned ~1 instead of 0).
if (FixedQ4816.Exp2(value: FixedQ4816.MinValue).Value != 0L) { throw new InvalidOperationException("EXP2(MinValue) REGRESSION"); }
if (FixedQ4816.Exp2(value: FixedQ4816.FromRawBits(value: -(1L << 47))).Value != 0L) { throw new InvalidOperationException("EXP2(-2^31) REGRESSION"); }
if (FixedQ4816.Exp2(value: FixedQ4816.FromRawBits(value: (long.MinValue + 12345L))).Value != 0L) { throw new InvalidOperationException("EXP2 DEEP-NEGATIVE REGRESSION"); }
var hugePowExponent = FixedQ4816.FromInteger(value: (1L << 46));
if (FixedQ4816.Pow(x: FixedQ4816.FromInteger(value: 4L), y: hugePowExponent) != FixedQ4816.MaxValue) { throw new InvalidOperationException("POW EXPONENT PRODUCT POSITIVE WRAP REGRESSION"); }
if (FixedQ4816.Pow(x: FixedQ4816.FromDouble(value: 0.25d), y: hugePowExponent) != FixedQ4816.Zero) { throw new InvalidOperationException("POW EXPONENT PRODUCT NEGATIVE WRAP REGRESSION"); }
if ((FixedQ4816.MinValue % FixedQ4816.FromRawBits(value: -1L)) != FixedQ4816.Zero) { throw new InvalidOperationException("FIXED REMAINDER MIN/-EPSILON REGRESSION"); }

// (3) The integer-exponent domain sweep that was missing: every (positive x, whole y in [-32, 32]) must not
// throw, must not wrap negative, and must agree with the double reference under a scale-aware tolerance.
for (var n = 0; (n < 300_000); n++) {
    var xRaw = Math.Abs(RandomScaled(rng)) | 1L;
    var xPow = FixedQ4816.FromRawBits(value: xRaw);
    var yPow = FixedQ4816.FromInteger(value: rng.NextInt64(-32L, 33L));
    var powResult = FixedQ4816.Pow(x: xPow, y: yPow); // must not throw

    if (powResult.Value < 0L) {
        throw new InvalidOperationException($"POW WRAPPED NEGATIVE: x={xRaw} y={(yPow.Value >> 16)}");
    }

    var refPow = Math.Pow(((double)xPow), ((double)yPow));

    if ((refPow > 1e-2) && (refPow < 1e12) && (powResult.Value != long.MaxValue)) {
        // Design budget for the negative-exponent path: |e|·(inverse rounding + mul roundings) ≈ 3e-4 at |e| = 32.
        var tolerance = ((refPow * 5e-4) + (2.0 / 65536.0));

        if (Math.Abs((((double)powResult) - refPow)) > tolerance) {
            throw new InvalidOperationException($"POW INTEGER SWEEP DIVERGED: x={xRaw} y={(yPow.Value >> 16)} got={(double)powResult} want={refPow}");
        }
    }
}

// Division-by-rounded-zero audit probes for the rest of the new code: tiny operands must not throw.
_ = new FixedQuaternion(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero).Normalize();
_ = new FixedQuaternion(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero).Inverse();
_ = new FixedComplex(Real: FixedQ4816.Epsilon, Imaginary: FixedQ4816.Zero).Normalize();
_ = FixedDual.Sqrt(value: FixedDual.Variable(value: FixedQ4816.Epsilon));
_ = FixedDual.Log2(value: FixedDual.Variable(value: FixedQ4816.Epsilon));
_ = new FixedRigidTransform(Value: new(Real: new(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero), Dual: FixedQuaternion.AdditiveIdentity)).Normalize();
Console.WriteLine("pow/exp2 regressions + integer-exponent domain sweep + tiny-operand probes: OK");
var maxPowRelErr = 0.0;
for (var n = 0; (n < 500_000); n++) {
    var xb = FixedQ4816.FromDouble(value: ((rng.NextDouble() * 15.9) + 0.1));
    var ye = FixedQ4816.FromDouble(value: ((rng.NextDouble() * 8.0) - 4.0));
    var actual = ((double)FixedQ4816.Pow(x: xb, y: ye));
    var reference = Math.Pow(((double)xb), ((double)ye));

    if (reference > 1e-3) {
        // Tolerance = exponent-quantization relative error + the result's own half-ULP as a relative term.
        var tolerance = (3e-4 + (0.65 / (reference * 65536.0)));
        var e = ((Math.Abs((actual - reference)) / reference) / tolerance);

        if (e > maxPowRelErr) { maxPowRelErr = e; }
    }
}
Console.WriteLine($"pow: max err = {maxPowRelErr:F3} x scale-aware tolerance (fractional exponents |y| <= 4)");
if (maxPowRelErr > 1.0) { throw new InvalidOperationException("POW RELATIVE GATE FAILED"); }

// Shuffle: permutation uniformity, determinism, and multiset preservation.
var shuffleCounts = new Dictionary<string, int>();
var shuffleRng = Pcg32XshRr.Create(state: 777UL, stream: 30UL);
for (var n = 0; (n < 480_000); n++) {
    Span<int> deck = [0, 1, 2, 3,];

    shuffleRng.Shuffle(values: deck);

    var key = $"{deck[0]}{deck[1]}{deck[2]}{deck[3]}";

    shuffleCounts[key] = (shuffleCounts.GetValueOrDefault(key) + 1);
}
if (shuffleCounts.Count != 24) { throw new InvalidOperationException($"SHUFFLE PRODUCED {shuffleCounts.Count} PERMUTATIONS, EXPECTED 24"); }
foreach (var (permutation, count) in shuffleCounts) {
    if (Math.Abs((count - 20_000)) > 700) { throw new InvalidOperationException($"SHUFFLE NON-UNIFORM: {permutation} = {count}"); }
}
var shuffleA = Pcg32XshRr.Create(state: 5UL, stream: 31UL);
var shuffleB = Pcg32XshRr.Create(state: 5UL, stream: 31UL);
Span<int> deckA = [0, 1, 2, 3, 4, 5, 6, 7,];
Span<int> deckB = [0, 1, 2, 3, 4, 5, 6, 7,];
shuffleA.Shuffle(values: deckA);
shuffleB.Shuffle(values: deckB);
for (var i = 0; (i < 8); i++) {
    if (deckA[i] != deckB[i]) { throw new InvalidOperationException("SHUFFLE NOT DETERMINISTIC"); }
}
var seen = 0;
for (var i = 0; (i < 8); i++) { seen |= (1 << deckA[i]); }
if (seen != 0xFF) { throw new InvalidOperationException("SHUFFLE LOST ELEMENTS"); }
Console.WriteLine($"exp2/pow/shuffle: OK (shuffle(seed 5, stream 31, n=8) = {string.Join(",", deckA.ToArray())})");

// Gaussian: moments, CDF bins, tails, exact two-advance consumption, determinism.
var gauss = Pcg32XshRr.Create(state: 2026UL, stream: 13UL);
var gaussTwin = Pcg32XshRr.Create(state: 2026UL, stream: 13UL);
const int gaussPairs = 20_000_000;
var mean = 0.0;
var m2 = 0.0;
var m3 = 0.0;
var m4 = 0.0;
var beyond3 = 0L;
var binEdges = new[] { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, };
var binCounts = new long[binEdges.Length];
for (var n = 0; (n < gaussPairs); n++) {
    var (a, b) = gauss.NextGaussianPair();

    foreach (var z in new[] { ((double)a), ((double)b), }) {
        mean += z;
        m2 += (z * z);
        m3 += ((z * z) * z);
        m4 += (((z * z) * z) * z);

        var az = Math.Abs(z);

        if (az > 3.0) { beyond3++; }

        for (var bi = 0; (bi < binEdges.Length); bi++) {
            if (az <= binEdges[bi]) { binCounts[bi]++; }
        }
    }
}
var total = (2.0 * gaussPairs);
mean /= total;
m2 /= total;
m3 /= total;
m4 /= total;
var variance = (m2 - (mean * mean));
var kurtosis = (m4 / (variance * variance));
Console.WriteLine($"gaussian: mean={mean:E3} var={variance:F5} skew~{m3:E3} kurt={kurtosis:F4} P(|z|>3)={(beyond3 / total):E4} (expect 2.7E-3)");
if ((Math.Abs(mean) > 1e-3) || (Math.Abs((variance - 1.0)) > 3e-3) || (Math.Abs(m3) > 5e-3) || (Math.Abs((kurtosis - 3.0)) > 2e-2)) {
    throw new InvalidOperationException("GAUSSIAN MOMENT GATE FAILED");
}
double[] phiTwoSided = [0.3829249, 0.6826895, 0.8663856, 0.9544997, 0.9875807, 0.9973002,]; // P(|Z| <= edge)
for (var bi = 0; (bi < binEdges.Length); bi++) {
    var empirical = (binCounts[bi] / total);

    if (Math.Abs((empirical - phiTwoSided[bi])) > 1.5e-3) {
        throw new InvalidOperationException($"GAUSSIAN CDF GATE FAILED at |z|<={binEdges[bi]}: {empirical:F5} vs {phiTwoSided[bi]:F5}");
    }
}
if (Math.Abs(((beyond3 / total) - 0.0026998)) > 3e-4) { throw new InvalidOperationException("GAUSSIAN TAIL GATE FAILED"); }
gaussTwin.Advance(count: ((ulong)(2L * gaussPairs)));
if (gaussTwin.State != gauss.State) { throw new InvalidOperationException("GAUSSIAN CONSUMPTION != 2 ADVANCES PER PAIR"); }
var gaussSeeded1 = Pcg32XshRr.Create(state: 77UL, stream: 4UL);
var gaussSeeded2 = Pcg32XshRr.Create(state: 77UL, stream: 4UL);
for (var n = 0; (n < 1000); n++) {
    var p1 = gaussSeeded1.NextGaussianPair();
    var p2 = gaussSeeded2.NextGaussianPair();

    if ((p1.First.Value != p2.First.Value) || (p1.Second.Value != p2.Second.Value)) {
        throw new InvalidOperationException("GAUSSIAN NOT DETERMINISTIC");
    }
}
Console.WriteLine("gaussian: moments/cdf/tail/consumption/determinism OK");

// InverseStandardNormalCdf/InverseNormalCdf: reject NaN and out-of-range/non-finite parameters; endpoint mapping
// and an interior value are unchanged by the M5/M6 guard reshape.
foreach (var (label, thrower) in new (string, Action)[] {
    ("nan probability", () => double.NaN.InverseStandardNormalCdf()),
    ("probability below 0", () => (-0.1d).InverseStandardNormalCdf()),
    ("probability above 1", () => 1.1d.InverseStandardNormalCdf()),
    ("positive infinity probability", () => double.PositiveInfinity.InverseStandardNormalCdf()),
    ("negative infinity probability", () => double.NegativeInfinity.InverseStandardNormalCdf()),
}) {
    try {
        thrower();

        throw new InvalidOperationException($"INVERSE STANDARD NORMAL CDF VALIDATION DID NOT THROW: {label}");
    } catch (ArgumentOutOfRangeException) { }
}
if (0.0d.InverseStandardNormalCdf() != double.NegativeInfinity) { throw new InvalidOperationException("INVERSE STANDARD NORMAL CDF(0) != -Infinity"); }
if (1.0d.InverseStandardNormalCdf() != double.PositiveInfinity) { throw new InvalidOperationException("INVERSE STANDARD NORMAL CDF(1) != +Infinity"); }
if (0.975d.InverseStandardNormalCdf() != 1.9599639845400534d) { throw new InvalidOperationException("INVERSE STANDARD NORMAL CDF(0.975) REGRESSION"); }
foreach (var (label, thrower) in new (string, Action)[] {
    ("nan mean", () => 0.5d.InverseNormalCdf(mean: double.NaN, standardDeviation: 1.0d)),
    ("negative standard deviation", () => 0.5d.InverseNormalCdf(mean: 0.0d, standardDeviation: -1.0d)),
    ("zero standard deviation", () => 0.5d.InverseNormalCdf(mean: 0.0d, standardDeviation: 0.0d)),
    ("infinite standard deviation", () => 0.5d.InverseNormalCdf(mean: 0.0d, standardDeviation: double.PositiveInfinity)),
}) {
    try {
        thrower();

        throw new InvalidOperationException($"INVERSE NORMAL CDF PARAMETER VALIDATION DID NOT THROW: {label}");
    } catch (ArgumentOutOfRangeException) { }
}
if (0.975d.InverseNormalCdf(mean: 10.0d, standardDeviation: 2.0d) != 13.919927969080106d) { throw new InvalidOperationException("INVERSE NORMAL CDF(0.975, mean=10, sd=2) REGRESSION"); }
Console.WriteLine("inverse normal cdf: validation/endpoint-mapping/spot-check OK");

// Alias table: distributions, edge shapes, exact consumption, construction determinism, index domain.
CheckAlias(weights: [1UL, 2UL, 7UL,], draws: 8_000_000, seed: 101UL);
CheckAlias(weights: [5UL, 5UL, 5UL, 5UL,], draws: 4_000_000, seed: 102UL);
CheckAlias(weights: [0UL, 5UL, 0UL, 3UL, 0UL, 2UL,], draws: 8_000_000, seed: 103UL);
CheckAlias(weights: [42UL,], draws: 100_000, seed: 104UL);
CheckAlias(weights: [1UL, (1UL << 40),], draws: 4_000_000, seed: 105UL);
var randomWeights = new ulong[257];
for (var i = 0; (i < randomWeights.Length); i++) { randomWeights[i] = ((ulong)rng.NextInt64(0L, 1_000_000L)); }
randomWeights[13] = 0UL;
CheckAlias(weights: randomWeights, draws: 12_000_000, seed: 106UL);
var aliasConsume1 = Pcg32XshRr.Create(state: 55UL, stream: 6UL);
var aliasConsume2 = Pcg32XshRr.Create(state: 55UL, stream: 6UL);
var consumeEntries = new (int, ulong)[] { (0, 3UL), (1, 1UL), (2, 4UL), };
var consumeTable = AliasTable.Create<int>(entries: consumeEntries);
for (var n = 0; (n < 100_000); n++) { _ = consumeTable.Sample(generator: ref aliasConsume1); }
aliasConsume2.Advance(count: 200_000UL);
if (aliasConsume1.State != aliasConsume2.State) { throw new InvalidOperationException("ALIAS CONSUMPTION != 2 ADVANCES PER SAMPLE"); }

// Exact uint-threshold boundary probes. These two-column weights round column zero to 2^32 and 2^32-1 respectively.
// The raw PCG state is chosen so the column draw selects zero and the threshold draw is uint.MaxValue.
const ulong AliasBoundaryState = 13_687_431_201_205_128_379UL;
var aliasBoundaryProbe = Pcg32XshRr.FromRawBits(
    increment: 1UL,
    multiplier: Pcg32XshRr.DefaultMultiplier,
    state: AliasBoundaryState
);
if (((aliasBoundaryProbe.NextUInt32() & 1U) != 0U) || (aliasBoundaryProbe.NextUInt32() != uint.MaxValue)) {
    throw new InvalidOperationException("ALIAS BOUNDARY PROBE STATE REGRESSION");
}
var aliasFullThreshold = AliasTable.Create<int>(entries: [(0, 1UL << 32), (1, (1UL << 32) + 1UL),]);
var aliasMaximumThreshold = AliasTable.Create<int>(entries: [(0, 1UL << 31), (1, (1UL << 31) + 1UL),]);
var aliasFullRng = Pcg32XshRr.FromRawBits(increment: 1UL, multiplier: Pcg32XshRr.DefaultMultiplier, state: AliasBoundaryState);
var aliasMaximumRng = Pcg32XshRr.FromRawBits(increment: 1UL, multiplier: Pcg32XshRr.DefaultMultiplier, state: AliasBoundaryState);
if (aliasFullThreshold.SampleIndex(generator: ref aliasFullRng) != 0) {
    throw new InvalidOperationException("ALIAS 2^32 THRESHOLD LOST ITS SELF COLUMN AT UINT.MAX DRAW");
}
if (aliasMaximumThreshold.SampleIndex(generator: ref aliasMaximumRng) != 1) {
    throw new InvalidOperationException("ALIAS UINT.MAX THRESHOLD FAILED TO SELECT ITS LIVE ALIAS AT UINT.MAX DRAW");
}
Console.WriteLine("alias: distributions/edges/consumption/determinism OK");

// Weight-overload construction: double weights quantize deterministically and preserve ratios; fixed-point
// weights convert exactly (sample sequences identical to the raw-ulong table).
var doubleTable = AliasTable.Create<int>(entries: [(0, 0.1d), (1, 0.2d), (2, 0.7d),]);
var doubleTwin = AliasTable.Create<int>(entries: [(0, 0.1d), (1, 0.2d), (2, 0.7d),]);
var doubleRng = Pcg32XshRr.Create(state: 301UL, stream: 21UL);
var doubleTwinRng = Pcg32XshRr.Create(state: 301UL, stream: 21UL);
var doubleCounts = new long[3];
for (var n = 0; (n < 4_000_000); n++) {
    var index = doubleTable.SampleIndex(generator: ref doubleRng);

    doubleCounts[index]++;

    if (index != doubleTwin.SampleIndex(generator: ref doubleTwinRng)) { throw new InvalidOperationException("DOUBLE-WEIGHT TABLE NOT DETERMINISTIC"); }
}
double[] doubleExpected = [0.1d, 0.2d, 0.7d,];
for (var i = 0; (i < 3); i++) {
    if (Math.Abs(((doubleCounts[i] / 4_000_000.0) - doubleExpected[i])) > 1e-3) {
        throw new InvalidOperationException($"DOUBLE-WEIGHT DISTRIBUTION FAILED entry {i}");
    }
}
var vanishing = AliasTable.Create<int>(entries: [(0, 1e-17d), (1, 1.0d),]);
var vanishingRng = Pcg32XshRr.Create(state: 302UL, stream: 21UL);
for (var n = 0; (n < 1_000_000); n++) {
    if (vanishing.SampleIndex(generator: ref vanishingRng) == 0) { throw new InvalidOperationException("VANISHING WEIGHT WAS SAMPLED"); }
}
var ufixedTable = AliasTable.Create<int>(entries: [(0, UFixedQ4816.FromInteger(value: 3UL)), (1, UFixedQ4816.FromInteger(value: 1UL)), (2, UFixedQ4816.FromInteger(value: 4UL)),]);
var fixedTable = AliasTable.Create<int>(entries: [(0, FixedQ4816.FromInteger(value: 3L)), (1, FixedQ4816.FromInteger(value: 1L)), (2, FixedQ4816.FromInteger(value: 4L)),]);
var rawTable = AliasTable.Create<int>(entries: [(0, ((ulong)FixedQ4816.FromInteger(value: 3L).Value)), (1, ((ulong)FixedQ4816.FromInteger(value: 1L).Value)), (2, ((ulong)FixedQ4816.FromInteger(value: 4L).Value)),]);
var uRng = Pcg32XshRr.Create(state: 303UL, stream: 21UL);
var fRng = Pcg32XshRr.Create(state: 303UL, stream: 21UL);
var rRng = Pcg32XshRr.Create(state: 303UL, stream: 21UL);
for (var n = 0; (n < 500_000); n++) {
    var expected = rawTable.SampleIndex(generator: ref rRng);

    if ((ufixedTable.SampleIndex(generator: ref uRng) != expected) || (fixedTable.SampleIndex(generator: ref fRng) != expected)) {
        throw new InvalidOperationException("FIXED-POINT WEIGHT TABLE != RAW TABLE");
    }
}
foreach (var (label, thrower) in new (string, Action)[] {
    ("nan", () => AliasTable.Create<int>(entries: [(0, double.NaN),])),
    ("negative", () => AliasTable.Create<int>(entries: [(0, -1.0d),])),
    ("infinity", () => AliasTable.Create<int>(entries: [(0, double.PositiveInfinity),])),
    ("all-zero", () => AliasTable.Create<int>(entries: [(0, 0.0d), (1, 0.0d),])),
    ("empty", () => AliasTable.Create<int>(entries: ReadOnlySpan<(int, double)>.Empty)),
    ("negative-fixed", () => AliasTable.Create<int>(entries: [(0, FixedQ4816.FromInteger(value: -1L)),])),
}) {
    try {
        thrower();

        throw new InvalidOperationException($"WEIGHT VALIDATION DID NOT THROW: {label}");
    } catch (ArgumentException) { }
}
Console.WriteLine("alias weight overloads: double/ufixed/fixed construction, vanishing, validation OK");

// ---- field noise + low discrepancy ----
Console.WriteLine();
Console.WriteLine("---- field noise / low discrepancy ----");
var noiseSum = 0.0;
var noiseSumSquared = 0.0;
var noiseMin = long.MaxValue;
var noiseMax = long.MinValue;
for (var n = 0; (n < 10_000_000); n++) {
    var p = new FixedVector3(
        X: FixedQ4816.FromRawBits(value: rng.NextInt64(-(1L << 40), (1L << 40))),
        Y: FixedQ4816.FromRawBits(value: rng.NextInt64(-(1L << 40), (1L << 40))),
        Z: FixedQ4816.FromRawBits(value: rng.NextInt64(-(1L << 40), (1L << 40)))
    );
    var v = FieldNoise.Sample(seed: 42UL, position: p).Value;

    if (v < noiseMin) { noiseMin = v; }
    if (v > noiseMax) { noiseMax = v; }

    noiseSum += v;
    noiseSumSquared += (((double)v) * v);

    var v4 = FieldNoise.Sample(seed: 42UL, position: p, octaves: 4).Value;

    if ((v4 < -65536L) || (v4 > 65536L)) { throw new InvalidOperationException($"OCTAVE NOISE OUT OF RANGE: {v4}"); }
}
var noiseMean = ((noiseSum / 10_000_000.0) / 65536.0);
var noiseStd = (Math.Sqrt(((noiseSumSquared / 10_000_000.0) - Math.Pow((noiseSum / 10_000_000.0), 2))) / 65536.0);
Console.WriteLine($"noise: range [{(noiseMin / 65536.0):F4}, {(noiseMax / 65536.0):F4}] mean={noiseMean:E3} std={noiseStd:F4}");
if ((noiseMin < -65536L) || (noiseMax > 65536L)) { throw new InvalidOperationException("NOISE OUT OF RANGE"); }
if ((Math.Abs(noiseMean) > 1e-3) || (noiseStd < 0.15) || (noiseStd > 0.45)) { throw new InvalidOperationException("NOISE DISTRIBUTION GATE FAILED"); }

// Continuity across lattice boundaries: adjacent raws straddling a boundary differ by a bounded step.
var maxBoundaryStep = 0L;
for (var n = 0; (n < 200_000); n++) {
    var k = rng.NextInt64(-1_000_000L, 1_000_000L);
    var yz = new { Y = RandomScaled(rng), Z = RandomScaled(rng) };
    var left = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: ((k << 16) - 1L)), Y: FixedQ4816.FromRawBits(value: yz.Y), Z: FixedQ4816.FromRawBits(value: yz.Z))).Value;
    var right = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: (k << 16)), Y: FixedQ4816.FromRawBits(value: yz.Y), Z: FixedQ4816.FromRawBits(value: yz.Z))).Value;
    var step = Math.Abs((left - right));

    if (step > maxBoundaryStep) { maxBoundaryStep = step; }
}
Console.WriteLine($"noise: max boundary step = {maxBoundaryStep} raw");
if (maxBoundaryStep > 16L) { throw new InvalidOperationException("NOISE CONTINUITY GATE FAILED"); }

const long noisePeriodX = 852_863L;
const long noisePeriodY = 1_285_698L;
const long noisePeriodZ = 183_727L;
const ulong formerNoiseCombineX = 0x9E3779B97F4A7C15UL;
var noisePeriodStillAliases = true;
var noiseSeedStillTranslates = true;
for (var noiseAliasProbe = 0; (noiseAliasProbe < 32); noiseAliasProbe++) {
    var p = new FixedVector3(
        X: FixedQ4816.FromRawBits(value: ((noiseAliasProbe * 65_537L) + 12_345L)),
        Y: FixedQ4816.FromRawBits(value: ((noiseAliasProbe * -31_337L) + 22_222L)),
        Z: FixedQ4816.FromRawBits(value: ((noiseAliasProbe * 9_973L) - 33_333L)));
    var periodP = new FixedVector3(
        X: FixedQ4816.FromRawBits(value: (p.X.Value + (noisePeriodX << FixedQ4816.FractionBitCount))),
        Y: FixedQ4816.FromRawBits(value: (p.Y.Value + (noisePeriodY << FixedQ4816.FractionBitCount))),
        Z: FixedQ4816.FromRawBits(value: (p.Z.Value + (noisePeriodZ << FixedQ4816.FractionBitCount))));
    var translatedP = p + new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
    var probeSeed = unchecked((ulong)(97 + noiseAliasProbe));

    noisePeriodStillAliases &= (FieldNoise.Sample(seed: probeSeed, position: p) == FieldNoise.Sample(seed: probeSeed, position: periodP));
    noiseSeedStillTranslates &= (FieldNoise.Sample(seed: unchecked(probeSeed + formerNoiseCombineX), position: p) == FieldNoise.Sample(seed: probeSeed, position: translatedP));
}
if (noisePeriodStillAliases || noiseSeedStillTranslates) { throw new InvalidOperationException("NOISE LINEAR HASH ALIAS REGRESSION"); }

var octaveSeamRaw = (1L << 62);
var octaveSeamLeft = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: (octaveSeamRaw - 1L)), Y: FixedQ4816.FromRawBits(value: 17_123L), Z: FixedQ4816.FromRawBits(value: -9_321L)), octaves: 5);
var octaveSeamRight = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: octaveSeamRaw), Y: FixedQ4816.FromRawBits(value: 17_123L), Z: FixedQ4816.FromRawBits(value: -9_321L)), octaves: 5);
if (Math.Abs(octaveSeamRight.Value - octaveSeamLeft.Value) > 16L) { throw new InvalidOperationException("NOISE OCTAVE WRAP SEAM REGRESSION"); }

// WorldCoord3 overload agrees with the flat overload on overlapping representations.
for (var n = 0; (n < 100_000); n++) {
    var local = new FixedVector3(
        X: FixedQ4816.FromRawBits(value: rng.NextInt64(-(1L << 35), (1L << 35))),
        Y: FixedQ4816.FromRawBits(value: rng.NextInt64(-(1L << 35), (1L << 35))),
        Z: FixedQ4816.FromRawBits(value: rng.NextInt64(-(1L << 35), (1L << 35)))
    );
    var coord = WorldCoord3.FromLocal(local: local);
    var flat = FieldNoise.Sample(seed: 9UL, position: local).Value;
    var hierarchical = FieldNoise.Sample(seed: 9UL, position: coord).Value;

    if (flat != hierarchical) { throw new InvalidOperationException($"NOISE OVERLOAD MISMATCH at {local}"); }
}
Console.WriteLine("noise: WorldCoord3 overload == flat overload (100k normalized positions)");

// R1/R2: values vs double reference; box-coverage uniformity.
if (Math.Abs((((double)LowDiscrepancy.R1(index: 1UL)) - 0.61803398874989485)) > 1e-9) { throw new InvalidOperationException("R1(1) WRONG"); }
if (LowDiscrepancy.R1(index: 0UL) == LowDiscrepancy.R1(index: (1UL << 63))) { throw new InvalidOperationException("R1 HALF-PERIOD REGRESSION"); }
var (r2x, r2y) = LowDiscrepancy.R2(index: 1UL);
if ((Math.Abs((((double)r2x) - 0.75487766624669276)) > 1e-9) || (Math.Abs((((double)r2y) - 0.56984029099805327)) > 1e-9)) { throw new InvalidOperationException("R2(1) WRONG"); }
var boxes = new int[16, 16];
for (var n = 0UL; (n < 65536UL); n++) {
    var (px, py) = LowDiscrepancy.R2(index: n);

    boxes[(px.Value >> 28), (py.Value >> 28)]++;
}
for (var bx = 0; (bx < 16); bx++) {
    for (var by = 0; (by < 16); by++) {
        if (Math.Abs((boxes[bx, by] - 256)) > 32) { throw new InvalidOperationException($"R2 COVERAGE GATE FAILED box ({bx},{by}) = {boxes[bx, by]}"); }
    }
}
var bins = new int[16];
for (var n = 0UL; (n < 4096UL); n++) { bins[(LowDiscrepancy.R1(index: n).Value >> 28)]++; }
foreach (var bin in bins) {
    if (Math.Abs((bin - 256)) > 16) { throw new InvalidOperationException("R1 COVERAGE GATE FAILED"); }
}
Console.WriteLine("low discrepancy: R1/R2 values + coverage OK");

var signedShortPair = BinaryIntegerFunctions.BitwisePair<short, uint>(value: short.MinValue, other: 0);
var signedLongPair = BinaryIntegerFunctions.BitwisePair<long, Int128>(value: long.MinValue, other: 0L);
if ((signedShortPair != (1U << 30)) ||
    (signedLongPair != (Int128.One << 126)) ||
    (BinaryIntegerFunctions.BitwiseUnpair<Int128, long>(value: signedLongPair) != (long.MinValue, 0L))) {
    throw new InvalidOperationException("SIGNED MORTON PAIRING REGRESSION");
}

// Binary-integer width-guard regressions (M3): BigInteger MSB uses value bit length, not an invented
// fixed width; operations that truly require a carrier width reject BigInteger outright.
if ((BigInteger.One << 200).MostSignificantBit() != 201) {
    throw new InvalidOperationException("BIGINTEGER MOST-SIGNIFICANT-BIT REGRESSION");
}
if (0.MostSignificantBit() != 0 || 255.MostSignificantBit() != 8 || (-1).MostSignificantBit() != 32) {
    throw new InvalidOperationException("BOUNDED MOST-SIGNIFICANT-BIT REGRESSION");
}
foreach (var (label, thrower) in new (string, Action)[] {
    ("reverse-bits", () => BigInteger.One.ReverseBits()),
    ("reflected-binary-decode", () => BigInteger.One.ReflectedBinaryDecode()),
}) {
    try {
        thrower();

        throw new InvalidOperationException($"UNBOUNDED WIDTH-GUARD DID NOT THROW: {label}");
    } catch (NotSupportedException) { }
}
if (((uint)1U).ReverseBits() != 0x80000000U) { throw new InvalidOperationException("BOUNDED REVERSE-BITS REGRESSION"); }

// RotateDigitsRight(int.MinValue) (L1): negation must widen before flipping sign, or int.MinValue wraps
// back to itself and silently rotates by the un-negated count instead of its true opposite-direction magnitude.
if (123.RotateDigitsRight(count: int.MinValue) != 312) {
    throw new InvalidOperationException("ROTATE-DIGITS-RIGHT INT.MINVALUE REGRESSION");
}
if (123.RotateDigitsLeft(count: int.MinValue) != 231) {
    throw new InvalidOperationException("ROTATE-DIGITS-LEFT INT.MINVALUE REGRESSION");
}
Console.WriteLine("binary integer: BigInteger MSB/width-guard + RotateDigits MinValue OK");

var flatLayerOverflowed = false;
try {
    _ = LayerSequence.Linear(size: 1L, seed: 0L).LayerOf(index: long.MaxValue);
} catch (OverflowException) {
    flatLayerOverflowed = true;
}
if (!flatLayerOverflowed) { throw new InvalidOperationException("FLAT LAYER-SEQUENCE EXTREMAL WRAP REGRESSION"); }

// ---- quaternion / dual ----
Console.WriteLine();
Console.WriteLine("---- quaternion / dual ----");
var maxAxisAngleErr = 0.0;
var maxMulErr = 0.0;
var maxRotateErr = 0.0;
var maxSlerpErr = 0.0;
for (var n = 0; (n < 500_000); n++) {
    // Random unit axis + angle; reference in double throughout.
    var (ax, ay, az) = RandomUnitAxis(rng);
    var angle = (((rng.NextDouble() * 4.0) * Math.PI) - (2.0 * Math.PI));
    var fixedAxis = new FixedVector3(X: FixedQ4816.FromDouble(value: ax), Y: FixedQ4816.FromDouble(value: ay), Z: FixedQ4816.FromDouble(value: az)).Normalize();
    var fixedAngle = FixedQ4816.FromDouble(value: angle);
    var q = FixedQuaternion.FromAxisAngle(axis: fixedAxis, angle: fixedAngle);
    // Reference from the QUANTIZED axis/angle so quantization is not counted as error.
    var (rx, ry, rz) = (((double)fixedAxis.X), ((double)fixedAxis.Y), ((double)fixedAxis.Z));
    var refHalf = (((double)fixedAngle) * 0.5);

    var (shalf, chalf) = Math.SinCos(refHalf);
    var e1 = Math.Max(
        Math.Max(Math.Abs((((double)q.X) - (rx * shalf))), Math.Abs((((double)q.Y) - (ry * shalf)))),
        Math.Max(Math.Abs((((double)q.Z) - (rz * shalf))), Math.Abs((((double)q.W) - chalf))));

    if ((e1 * 65536.0) > maxAxisAngleErr) { maxAxisAngleErr = (e1 * 65536.0); }

    // Hamilton product vs double.
    var (bx, by, bz) = RandomUnitAxis(rng);
    var q2 = FixedQuaternion.FromAxisAngle(
        axis: new FixedVector3(X: FixedQ4816.FromDouble(value: bx), Y: FixedQ4816.FromDouble(value: by), Z: FixedQ4816.FromDouble(value: bz)).Normalize(),
        angle: FixedQ4816.FromDouble(value: (rng.NextDouble() * Math.PI)));
    var product = (q * q2);

    var (dw, dx, dy, dz) = MulRef(q, q2);
    var e2 = Math.Max(
        Math.Max(Math.Abs((((double)product.X) - dx)), Math.Abs((((double)product.Y) - dy))),
        Math.Max(Math.Abs((((double)product.Z) - dz)), Math.Abs((((double)product.W) - dw))));

    if ((e2 * 65536.0) > maxMulErr) { maxMulErr = (e2 * 65536.0); }

    // Rotate a unit-ish vector; compare vs double rotation by the SAME quantized quaternion.
    var v = new FixedVector3(X: FixedQ4816.FromDouble(value: (rng.NextDouble() - 0.5)), Y: FixedQ4816.FromDouble(value: (rng.NextDouble() - 0.5)), Z: FixedQ4816.FromDouble(value: (rng.NextDouble() - 0.5)));
    var rotated = q.Rotate(vector: v);

    var (ex, ey, ez) = RotateRef(q, ((double)v.X), ((double)v.Y), ((double)v.Z));
    var e3 = Math.Max(Math.Abs((((double)rotated.X) - ex)), Math.Max(Math.Abs((((double)rotated.Y) - ey)), Math.Abs((((double)rotated.Z) - ez))));

    if ((e3 * 65536.0) > maxRotateErr) { maxRotateErr = (e3 * 65536.0); }

    if ((n % 10) == 0) {
        // Slerp vs a double reference implementing the same algorithm (shortest path + the same nlerp branch).
        var t = FixedQ4816.FromDouble(value: rng.NextDouble());
        var s = FixedQuaternion.Slerp(from: q, to: q2, amount: t);
        // The shortest-path flip is ambiguous when dot ≈ 0; tie-break the reference exactly like the implementation.
        var (sw, sx, sy, sz) = SlerpRef(q, q2, ((double)t), (FixedQuaternion.Dot(left: q, right: q2).Value < 0L));
        var e4 = Math.Max(
            Math.Max(Math.Abs((((double)s.X) - sx)), Math.Abs((((double)s.Y) - sy))),
            Math.Max(Math.Abs((((double)s.Z) - sz)), Math.Abs((((double)s.W) - sw))));

        if ((e4 * 65536.0) > maxSlerpErr) { maxSlerpErr = (e4 * 65536.0); }

        if ((e4 * 65536.0) > 1000.0) {
            Console.WriteLine($"SLERP CASE: q=({q.X.Value},{q.Y.Value},{q.Z.Value},{q.W.Value}) q2=({q2.X.Value},{q2.Y.Value},{q2.Z.Value},{q2.W.Value}) t={t.Value} dot={FixedQuaternion.Dot(left: q, right: q2).Value}");
            Console.WriteLine($"  fixed=({s.X.Value},{s.Y.Value},{s.Z.Value},{s.W.Value}) ref=({(long)(sx * 65536)},{(long)(sy * 65536)},{(long)(sz * 65536)},{(long)(sw * 65536)})");

            throw new InvalidOperationException("SLERP DIVERGENCE PROBE");
        }
    }
}
Console.WriteLine($"quaternion: axis-angle={maxAxisAngleErr:F2} mul={maxMulErr:F2} rotate={maxRotateErr:F2} slerp={maxSlerpErr:F2} max raw ULP (500k)");
if ((maxAxisAngleErr > 3.0) || (maxMulErr > 6.0) || (maxRotateErr > 8.0) || (maxSlerpErr > 12.0)) {
    throw new InvalidOperationException("QUATERNION ACCURACY GATE FAILED");
}

// Algebraic sanity: q · conjugate ≈ identity; rotation preserves length; identity rotates nothing.
var probe = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 0.7));
var idProbe = (probe * probe.Conjugate());
if ((Math.Abs((idProbe.W.Value - 65536L)) > 4L) || (Math.Abs(idProbe.X.Value) > 4L)) { throw new InvalidOperationException("Q*CONJ != IDENTITY"); }
var lengthProbe = probe.Rotate(vector: new FixedVector3(X: FixedQ4816.FromInteger(value: 3L), Y: FixedQ4816.FromInteger(value: 4L), Z: FixedQ4816.Zero));
if (Math.Abs((lengthProbe.Length.Value - (5L * 65536L))) > 16L) { throw new InvalidOperationException("ROTATION DID NOT PRESERVE LENGTH"); }

// Exp/Log: the bivector ↔ rotor bridge, vs double references from the QUANTIZED operands. The log gate is
// SCALE-AWARE: below the vector-length floor the rotation plane is barely represented and the half-ULP error of
// the length dominates the θ/|v| ratio — inherent quantization, not implementation error.
const long LogVectorLengthFloor = 4096L;
var maxExpErr = 0.0;
var maxLogErr = 0.0;
var maxExpLogRoundTripErr = 0L;
for (var n = 0; (n < 300_000); n++) {
    // Exp of a random bivector; magnitudes span zero → beyond π (exercising the turn-domain wrap).
    var (bax, bay, baz) = RandomUnitAxis(rng);
    var bivectorMagnitude = (rng.NextDouble() * 6.0);
    var bivector = new FixedVector3(X: FixedQ4816.FromDouble(value: (bax * bivectorMagnitude)), Y: FixedQ4816.FromDouble(value: (bay * bivectorMagnitude)), Z: FixedQ4816.FromDouble(value: (baz * bivectorMagnitude)));
    var expQ = FixedQuaternion.Exp(bivector: bivector);

    var (dbx, dby, dbz) = (((double)bivector.X), ((double)bivector.Y), ((double)bivector.Z));
    var dMag = Math.Sqrt((((dbx * dbx) + (dby * dby)) + (dbz * dbz)));

    var (dSin, dCos) = Math.SinCos(dMag);
    var dScale = ((dMag == 0.0) ? 0.0 : (dSin / dMag));
    var e5 = Math.Max(
        Math.Max(Math.Abs((((double)expQ.X) - (dbx * dScale))), Math.Abs((((double)expQ.Y) - (dby * dScale)))),
        Math.Max(Math.Abs((((double)expQ.Z) - (dbz * dScale))), Math.Abs((((double)expQ.W) - dCos))));

    if ((e5 * 65536.0) > maxExpErr) { maxExpErr = (e5 * 65536.0); }

    // Log of a random unit rotation, then the Exp round trip (which must preserve the sign of q, not just ±q).
    var (lax, lay, laz) = RandomUnitAxis(rng);
    var logQ = FixedQuaternion.FromAxisAngle(
        axis: new FixedVector3(X: FixedQ4816.FromDouble(value: lax), Y: FixedQ4816.FromDouble(value: lay), Z: FixedQ4816.FromDouble(value: laz)).Normalize(),
        angle: FixedQ4816.FromDouble(value: (((rng.NextDouble() * 4.0) * Math.PI) - (2.0 * Math.PI))));
    var logB = logQ.Log();

    var (qx, qy, qz, qw) = (((double)logQ.X), ((double)logQ.Y), ((double)logQ.Z), ((double)logQ.W));
    var dVectorLength = Math.Sqrt((((qx * qx) + (qy * qy)) + (qz * qz)));

    if ((dVectorLength * 65536.0) >= LogVectorLengthFloor) {
        var dLogScale = (Math.Atan2(dVectorLength, qw) / dVectorLength);
        var e6 = Math.Max(
            Math.Abs((((double)logB.X) - (qx * dLogScale))),
            Math.Max(Math.Abs((((double)logB.Y) - (qy * dLogScale))), Math.Abs((((double)logB.Z) - (qz * dLogScale)))));

        if ((e6 * 65536.0) > maxLogErr) { maxLogErr = (e6 * 65536.0); }

        var roundTrip = FixedQuaternion.Exp(bivector: logB);
        var e7 = Math.Max(
            Math.Max(Math.Abs((roundTrip.X.Value - logQ.X.Value)), Math.Abs((roundTrip.Y.Value - logQ.Y.Value))),
            Math.Max(Math.Abs((roundTrip.Z.Value - logQ.Z.Value)), Math.Abs((roundTrip.W.Value - logQ.W.Value))));

        if (e7 > maxExpLogRoundTripErr) { maxExpLogRoundTripErr = e7; }
    }
}
Console.WriteLine($"quat exp/log: exp={maxExpErr:F2} log={maxLogErr:F2} roundtrip={maxExpLogRoundTripErr} max raw ULP (300k)");
if ((maxExpErr > 6.0) || (maxLogErr > 48.0) || (maxExpLogRoundTripErr > 40L)) {
    throw new InvalidOperationException("QUATERNION EXP/LOG ACCURACY GATE FAILED");
}

// Exact poles: exp(0) is the identity; the vector-free quaternions log to the zero bivector.
if (FixedQuaternion.Exp(bivector: FixedVector3.Zero) != FixedQuaternion.Identity) { throw new InvalidOperationException("EXP(0) != IDENTITY"); }
if (FixedQuaternion.Identity.Log() != FixedVector3.Zero) { throw new InvalidOperationException("LOG(IDENTITY) != 0"); }
if ((-FixedQuaternion.Identity).Log() != FixedVector3.Zero) { throw new InvalidOperationException("LOG(-IDENTITY) != 0"); }

// FromTo: shortest-arc constructor — unit output that maps the from-direction onto the to-direction, at any
// input scale (directions are normalized internally). References use the QUANTIZED operands throughout.
var maxFromToAngleErr = 0.0;
var maxFromToNormErr = 0.0;
for (var n = 0; (n < 200_000); n++) {
    var (fux, fuy, fuz) = RandomUnitAxis(rng);
    var (tux, tuy, tuz) = RandomUnitAxis(rng);
    // FromTo normalizes its inputs in fixed point, so angular precision follows the smaller input's magnitude;
    // sweep adequately-scaled directions where the algorithm dominates, not the input resolution (tiny-vector
    // direction quantization is inherent, not an algorithm defect).
    var fromScale = Math.ScaleB((1.0 + rng.NextDouble()), rng.Next(-2, 6));
    var toScale = Math.ScaleB((1.0 + rng.NextDouble()), rng.Next(-2, 6));
    var from3 = new FixedVector3(X: FixedQ4816.FromDouble(value: (fux * fromScale)), Y: FixedQ4816.FromDouble(value: (fuy * fromScale)), Z: FixedQ4816.FromDouble(value: (fuz * fromScale)));
    var to3 = new FixedVector3(X: FixedQ4816.FromDouble(value: (tux * toScale)), Y: FixedQ4816.FromDouble(value: (tuy * toScale)), Z: FixedQ4816.FromDouble(value: (tuz * toScale)));

    var (dfx, dfy, dfz) = (((double)from3.X), ((double)from3.Y), ((double)from3.Z));
    var (dtx, dty, dtz) = (((double)to3.X), ((double)to3.Y), ((double)to3.Z));
    var fromLength = Math.Sqrt((((dfx * dfx) + (dfy * dfy)) + (dfz * dfz)));
    var toLength = Math.Sqrt((((dtx * dtx) + (dty * dty)) + (dtz * dtz)));

    if ((fromLength == 0.0) || (toLength == 0.0)) { continue; }

    (dfx, dfy, dfz) = ((dfx / fromLength), (dfy / fromLength), (dfz / fromLength));
    (dtx, dty, dtz) = ((dtx / toLength), (dty / toLength), (dtz / toLength));

    // Shortest-arc is ill-conditioned near antiparallel — (cross, 1+dot) both vanish, so normalizing the tiny
    // quaternion loses bits (as Slerp/ScLerp degrade near their poles). Sweep the well-conditioned region; the
    // exact-antiparallel behavior is probed separately below.
    if ((((dfx * dtx) + (dfy * dty)) + (dfz * dtz)) < -0.9) { continue; }

    var fromTo = FixedQuaternion.FromTo(from: from3, to: to3);
    var normErr = (Math.Abs((((double)fromTo.Length) - 1.0)) * 65536.0);

    if (normErr > maxFromToNormErr) { maxFromToNormErr = normErr; }

    var mapped = fromTo.Rotate(vector: new FixedVector3(X: FixedQ4816.FromDouble(value: dfx), Y: FixedQ4816.FromDouble(value: dfy), Z: FixedQ4816.FromDouble(value: dfz)));

    var (dmx, dmy, dmz) = (((double)mapped.X), ((double)mapped.Y), ((double)mapped.Z));
    var mappedLength = Math.Sqrt((((dmx * dmx) + (dmy * dmy)) + (dmz * dmz)));

    (dmx, dmy, dmz) = ((dmx / mappedLength), (dmy / mappedLength), (dmz / mappedLength));
    var crossLength = Math.Sqrt(
        (((((dmy * dtz) - (dmz * dty)) * ((dmy * dtz) - (dmz * dty))) +
        (((dmz * dtx) - (dmx * dtz)) * ((dmz * dtx) - (dmx * dtz)))) +
        (((dmx * dty) - (dmy * dtx)) * ((dmx * dty) - (dmy * dtx)))));
    var angleErr = Math.Atan2(crossLength, (((dmx * dtx) + (dmy * dty)) + (dmz * dtz)));

    if (angleErr > maxFromToAngleErr) { maxFromToAngleErr = angleErr; }
}
Console.WriteLine($"quat fromto: angle={maxFromToAngleErr:E2} rad, norm={maxFromToNormErr:F2} raw ULP (200k, scales 2^-2..2^6, |dot|<0.9)");
if ((maxFromToAngleErr > 1e-3) || (maxFromToNormErr > 8.0)) {
    throw new InvalidOperationException("QUATERNION FROMTO ACCURACY GATE FAILED");
}

// Antiparallel probes: exact opposites rotate by π onto the target; zero inputs yield the identity.
foreach (var basis in new[] {
    new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
    new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero),
    new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One),
    new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.FromDouble(value: -0.5), Z: FixedQ4816.FromDouble(value: 0.25)),
}) {
    var negated = new FixedVector3(X: -basis.X, Y: -basis.Y, Z: -basis.Z);
    var half = FixedQuaternion.FromTo(from: basis, to: negated);
    var flippedBack = half.Rotate(vector: basis);
    var flipErr = Math.Max(
        Math.Abs((flippedBack.X.Value - negated.X.Value)),
        Math.Max(Math.Abs((flippedBack.Y.Value - negated.Y.Value)), Math.Abs((flippedBack.Z.Value - negated.Z.Value))));

    if (flipErr > (16L * Math.Max(1L, (basis.Length.Value >> 16)))) { throw new InvalidOperationException("FROMTO ANTIPARALLEL PROBE FAILED"); }
    if (Math.Abs(half.W.Value) > 16L) { throw new InvalidOperationException("FROMTO ANTIPARALLEL W NOT ZERO"); }
}
if (FixedQuaternion.FromTo(from: FixedVector3.Zero, to: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)) != FixedQuaternion.Identity) {
    throw new InvalidOperationException("FROMTO ZERO INPUT NOT IDENTITY");
}

// Dual numbers: chain-rule identities vs analytic derivatives in double.
var maxDualErr = 0.0;
for (var n = 0; (n < 500_000); n++) {
    var xv = ((rng.NextDouble() * 6.0) + 0.5);
    var x = FixedDual.Variable(value: FixedQ4816.FromDouble(value: xv));
    var quantized = ((double)x.Real);

    // f(x) = sqrt(x)·sin(x) + x²/(x + 1); f' = sin/(2√x) + √x·cos + (x² + 2x)/(x+1)².
    var (sinD, _) = FixedDual.SinCos(angle: x);
    var f = ((FixedDual.Sqrt(value: x) * sinD) + FixedDual.Divide(left: (x * x), right: (x + FixedDual.Constant(value: FixedQ4816.One))));
    var expected = (((Math.Sin(quantized) / (2.0 * Math.Sqrt(quantized))) + (Math.Sqrt(quantized) * Math.Cos(quantized))) + (((quantized * quantized) + (2.0 * quantized)) / Math.Pow((quantized + 1.0), 2)));
    var e = Math.Abs((((double)f.Dual) - expected));

    if ((e * 65536.0) > maxDualErr) { maxDualErr = (e * 65536.0); }
}
Console.WriteLine($"dual: composite chain-rule max err = {maxDualErr:F2} raw ULP (500k)");
if (maxDualErr > 24.0) { throw new InvalidOperationException("DUAL ACCURACY GATE FAILED"); }

// Exact spot checks: d(x²)/dx at 3 = 6; d(√x)/dx at 4 = 1/4.
var three = FixedDual.Variable(value: FixedQ4816.FromInteger(value: 3L));
if ((three * three).Dual.Value != (6L * 65536L)) { throw new InvalidOperationException("DUAL X^2 DERIVATIVE WRONG"); }
var four = FixedDual.Variable(value: FixedQ4816.FromInteger(value: 4L));
if (FixedDual.Sqrt(value: four).Dual.Value != 16384L) { throw new InvalidOperationException("DUAL SQRT DERIVATIVE WRONG"); }
Console.WriteLine("quaternion/dual: algebraic + exact checks OK");

// ---- vector2 wedge/dot ----
var tinyVector2 = new FixedVector2(X: FixedQ4816.Epsilon, Y: FixedQ4816.Epsilon);
var fullRangeVector2Component = FixedQ4816.FromRawBits(value: (1L << 40));
var fullRangeVector2 = new FixedVector2(X: fullRangeVector2Component, Y: FixedQ4816.Zero);
if ((tinyVector2.Length != FixedQ4816.Epsilon) ||
    !fullRangeVector2.TryLength(length: out var fullRangeVector2Length) ||
    (fullRangeVector2Length != fullRangeVector2Component) ||
    fullRangeVector2.TryLengthSquared(squaredLength: out _) ||
    (fullRangeVector2.LengthSquared != FixedQ4816.MaxValue)) {
    throw new InvalidOperationException("FIXEDVECTOR2 FULL-RANGE NORM REGRESSION");
}
// Full-width raw-bit oracle: planar product sums can exceed long while the public result deliberately wraps only
// after the single Q16 rounding step.
long[] fusedEdges = [long.MinValue, (long.MinValue + 1L), -65536L, -1L, 0L, 1L, 65536L, (long.MaxValue - 1L), long.MaxValue];

foreach (var ax in fusedEdges) {
    foreach (var ay in fusedEdges) {
        foreach (var bx in fusedEdges) {
            foreach (var by in fusedEdges) {
                var a = new FixedVector2(X: FixedQ4816.FromRawBits(value: ax), Y: FixedQ4816.FromRawBits(value: ay));
                var b = new FixedVector2(X: FixedQ4816.FromRawBits(value: bx), Y: FixedQ4816.FromRawBits(value: by));
                var expectedDot = RoundProductSumOracle(sum: (((BigInteger)ax * bx) + ((BigInteger)ay * by)));
                var expectedWedge = RoundProductSumOracle(sum: (((BigInteger)ax * by) - ((BigInteger)ay * bx)));

                if (FixedVector2.Dot(left: a, right: b).Value != expectedDot) { throw new InvalidOperationException("FULL-WIDTH DOT ORACLE MISMATCH"); }
                if (FixedVector2.Wedge(left: a, right: b).Value != expectedWedge) { throw new InvalidOperationException("FULL-WIDTH WEDGE ORACLE MISMATCH"); }
            }
        }
    }
}

for (var n = 0; (n < 50_000); n++) {
    var ax = rng.NextInt64(long.MinValue, long.MaxValue);
    var ay = rng.NextInt64(long.MinValue, long.MaxValue);
    var bx = rng.NextInt64(long.MinValue, long.MaxValue);
    var by = rng.NextInt64(long.MinValue, long.MaxValue);
    var a = new FixedVector2(X: FixedQ4816.FromRawBits(value: ax), Y: FixedQ4816.FromRawBits(value: ay));
    var b = new FixedVector2(X: FixedQ4816.FromRawBits(value: bx), Y: FixedQ4816.FromRawBits(value: by));

    if (FixedVector2.Dot(left: a, right: b).Value != RoundProductSumOracle(sum: (((BigInteger)ax * bx) + ((BigInteger)ay * by)))) { throw new InvalidOperationException("RANDOM FULL-WIDTH DOT ORACLE MISMATCH"); }
    if (FixedVector2.Wedge(left: a, right: b).Value != RoundProductSumOracle(sum: (((BigInteger)ax * by) - ((BigInteger)ay * bx)))) { throw new InvalidOperationException("RANDOM FULL-WIDTH WEDGE ORACLE MISMATCH"); }
}

for (var n = 0; (n < 50_000); n++) {
    var ax = rng.NextInt64(long.MinValue, long.MaxValue);
    var ay = rng.NextInt64(long.MinValue, long.MaxValue);
    var az = rng.NextInt64(long.MinValue, long.MaxValue);
    var bx = rng.NextInt64(long.MinValue, long.MaxValue);
    var by = rng.NextInt64(long.MinValue, long.MaxValue);
    var bz = rng.NextInt64(long.MinValue, long.MaxValue);
    var a = new FixedVector3(X: FixedQ4816.FromRawBits(value: ax), Y: FixedQ4816.FromRawBits(value: ay), Z: FixedQ4816.FromRawBits(value: az));
    var b = new FixedVector3(X: FixedQ4816.FromRawBits(value: bx), Y: FixedQ4816.FromRawBits(value: by), Z: FixedQ4816.FromRawBits(value: bz));
    var dot = RoundProductSumOracle(sum: (((BigInteger)ax * bx) + ((BigInteger)ay * by) + ((BigInteger)az * bz)));
    var cross = new FixedVector3(
        X: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)ay * bz) - ((BigInteger)az * by)))),
        Y: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)az * bx) - ((BigInteger)ax * bz)))),
        Z: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)ax * by) - ((BigInteger)ay * bx))))
    );

    if (FixedVector3.Dot(left: a, right: b).Value != dot) { throw new InvalidOperationException("RANDOM FULL-WIDTH VECTOR3 DOT ORACLE MISMATCH"); }
    if (FixedVector3.Cross(left: a, right: b) != cross) { throw new InvalidOperationException("RANDOM FULL-WIDTH VECTOR3 CROSS ORACLE MISMATCH"); }
    if (FixedVector3.Cross(left: a, right: b) != -FixedVector3.Cross(left: b, right: a)) { throw new InvalidOperationException("VECTOR3 CROSS NOT ANTISYMMETRIC"); }
}

for (var n = 0; (n < 25_000); ++n) {
    var lx = rng.NextInt64(long.MinValue, long.MaxValue);
    var ly = rng.NextInt64(long.MinValue, long.MaxValue);
    var lz = rng.NextInt64(long.MinValue, long.MaxValue);
    var lw = rng.NextInt64(long.MinValue, long.MaxValue);
    var rx = rng.NextInt64(long.MinValue, long.MaxValue);
    var ry = rng.NextInt64(long.MinValue, long.MaxValue);
    var rz = rng.NextInt64(long.MinValue, long.MaxValue);
    var rw = rng.NextInt64(long.MinValue, long.MaxValue);
    var left = new FixedQuaternion(X: FixedQ4816.FromRawBits(value: lx), Y: FixedQ4816.FromRawBits(value: ly), Z: FixedQ4816.FromRawBits(value: lz), W: FixedQ4816.FromRawBits(value: lw));
    var right = new FixedQuaternion(X: FixedQ4816.FromRawBits(value: rx), Y: FixedQ4816.FromRawBits(value: ry), Z: FixedQ4816.FromRawBits(value: rz), W: FixedQ4816.FromRawBits(value: rw));
    var expectedProduct = new FixedQuaternion(
        X: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)lw * rx) + ((BigInteger)lx * rw) + ((BigInteger)ly * rz) - ((BigInteger)lz * ry)))),
        Y: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)lw * ry) - ((BigInteger)lx * rz) + ((BigInteger)ly * rw) + ((BigInteger)lz * rx)))),
        Z: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)lw * rz) + ((BigInteger)lx * ry) - ((BigInteger)ly * rx) + ((BigInteger)lz * rw)))),
        W: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)lw * rw) - ((BigInteger)lx * rx) - ((BigInteger)ly * ry) - ((BigInteger)lz * rz))))
    );
    var expectedDot = RoundProductSumOracle(sum: (((BigInteger)lx * rx) + ((BigInteger)ly * ry) + ((BigInteger)lz * rz) + ((BigInteger)lw * rw)));

    if ((left * right) != expectedProduct) { throw new InvalidOperationException("FULL-WIDTH HAMILTON PRODUCT ORACLE MISMATCH"); }
    if (FixedQuaternion.Dot(left: left, right: right).Value != expectedDot) { throw new InvalidOperationException("FULL-WIDTH QUATERNION DOT ORACLE MISMATCH"); }

    var vector = new FixedVector3(X: right.X, Y: right.Y, Z: right.Z);
    var tx = RoundProductSumOracle(sum: (((BigInteger)ly * rz) - ((BigInteger)lz * ry) + ((BigInteger)lw * rx)));
    var ty = RoundProductSumOracle(sum: (((BigInteger)lz * rx) - ((BigInteger)lx * rz) + ((BigInteger)lw * ry)));
    var tz = RoundProductSumOracle(sum: (((BigInteger)lx * ry) - ((BigInteger)ly * rx) + ((BigInteger)lw * rz)));
    var dx = RoundProductSumOracle(sum: (((BigInteger)ly * tz) - ((BigInteger)lz * ty)));
    var dy = RoundProductSumOracle(sum: (((BigInteger)lz * tx) - ((BigInteger)lx * tz)));
    var dz = RoundProductSumOracle(sum: (((BigInteger)lx * ty) - ((BigInteger)ly * tx)));
    var expectedRotation = new FixedVector3(
        X: FixedQ4816.FromRawBits(value: unchecked(rx + (dx << 1))),
        Y: FixedQ4816.FromRawBits(value: unchecked(ry + (dy << 1))),
        Z: FixedQ4816.FromRawBits(value: unchecked(rz + (dz << 1)))
    );

    if (left.Rotate(vector: vector) != expectedRotation) { throw new InvalidOperationException("FULL-WIDTH QUATERNION ROTATE ORACLE MISMATCH"); }

    var squaredNorm = (((BigInteger)lx * lx) + ((BigInteger)ly * ly) + ((BigInteger)lz * lz) + ((BigInteger)lw * lw));

    if (!squaredNorm.IsZero) {
        var expectedInverse = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: RoundRatioQ16(numerator: -((BigInteger)lx << FixedQ4816.FractionBitCount), denominator: squaredNorm)),
            Y: FixedQ4816.FromRawBits(value: RoundRatioQ16(numerator: -((BigInteger)ly << FixedQ4816.FractionBitCount), denominator: squaredNorm)),
            Z: FixedQ4816.FromRawBits(value: RoundRatioQ16(numerator: -((BigInteger)lz << FixedQ4816.FractionBitCount), denominator: squaredNorm)),
            W: FixedQ4816.FromRawBits(value: RoundRatioQ16(numerator: ((BigInteger)lw << FixedQ4816.FractionBitCount), denominator: squaredNorm))
        );

        if (left.Inverse() != expectedInverse) { throw new InvalidOperationException("FULL-WIDTH QUATERNION INVERSE ORACLE MISMATCH"); }
    }
}
Console.WriteLine("fused products: full-width BigInteger vector/complex/quaternion oracles OK");

// Bit-identity against an independent round-half-even oracle: rotation-scale raw operands keep every product sum
// exact in double (≤ 2^49 < 2^53), so the oracle is exact — plus the algebraic identities that must hold exactly.
for (var n = 0; (n < 2_000_000); n++) {
    var wa = new FixedVector2(X: FixedQ4816.FromRawBits(value: rng.Next(-(1 << 24), (1 << 24))), Y: FixedQ4816.FromRawBits(value: rng.Next(-(1 << 24), (1 << 24))));
    var wb = new FixedVector2(X: FixedQ4816.FromRawBits(value: rng.Next(-(1 << 24), (1 << 24))), Y: FixedQ4816.FromRawBits(value: rng.Next(-(1 << 24), (1 << 24))));
    var wedgeSum = ((wa.X.Value * wb.Y.Value) - (wa.Y.Value * wb.X.Value));
    var dotSum = ((wa.X.Value * wb.X.Value) + (wa.Y.Value * wb.Y.Value));

    if (FixedVector2.Wedge(left: wa, right: wb).Value != ((long)Math.Round((wedgeSum / 65536.0), MidpointRounding.ToEven))) { throw new InvalidOperationException("WEDGE ORACLE MISMATCH"); }
    if (FixedVector2.Dot(left: wa, right: wb).Value != ((long)Math.Round((dotSum / 65536.0), MidpointRounding.ToEven))) { throw new InvalidOperationException("DOT ORACLE MISMATCH"); }
    if (FixedVector2.Wedge(left: wa, right: wa).Value != 0L) { throw new InvalidOperationException("WEDGE(A, A) != 0"); }
    if (FixedVector2.Wedge(left: wa, right: wb).Value != -FixedVector2.Wedge(left: wb, right: wa).Value) { throw new InvalidOperationException("WEDGE NOT ANTISYMMETRIC"); }
    if (FixedVector2.Dot(left: wa, right: wb).Value != FixedVector2.Dot(left: wb, right: wa).Value) { throw new InvalidOperationException("DOT NOT SYMMETRIC"); }
}
Console.WriteLine("vector2 wedge/dot: oracle bit-identity + algebraic checks OK (2M)");

// ---- complex / rigid transform ----
long[] complexEdges = [long.MinValue, (long.MinValue + 1L), -(1L << 62), -(1L << 32), -1L, 0L, 1L, (1L << 32), (1L << 62), long.MaxValue,];
foreach (var ar in complexEdges) {
    foreach (var ai in complexEdges) {
        foreach (var br in complexEdges) {
            foreach (var bi in complexEdges) {
                if ((br | bi) == 0L) { continue; }

                var actual = (new FixedComplex(Real: FixedQ4816.FromRawBits(value: ar), Imaginary: FixedQ4816.FromRawBits(value: ai)) /
                              new FixedComplex(Real: FixedQ4816.FromRawBits(value: br), Imaginary: FixedQ4816.FromRawBits(value: bi)));
                var expected = ComplexDivisionOracle(ar: ar, ai: ai, br: br, bi: bi);

                if ((actual.Real.Value != expected.Real) || (actual.Imaginary.Value != expected.Imaginary)) {
                    throw new InvalidOperationException($"COMPLEX DIV EDGE ORACLE MISMATCH: ({ar},{ai}) / ({br},{bi}) = ({actual.Real.Value},{actual.Imaginary.Value}), expected ({expected.Real},{expected.Imaginary})");
                }
            }
        }
    }
}

var maxWideFromTo2dErr = 0.0;
for (var n = 0; (n < 50_000); ++n) {
    var ar = rng.NextInt64(long.MinValue, long.MaxValue);
    var ai = rng.NextInt64(long.MinValue, long.MaxValue);
    var br = rng.NextInt64(long.MinValue, long.MaxValue);
    var bi = rng.NextInt64(long.MinValue, long.MaxValue);
    var divisor = new FixedComplex(Real: FixedQ4816.FromRawBits(value: br), Imaginary: FixedQ4816.FromRawBits(value: bi));
    var dividend = new FixedComplex(Real: FixedQ4816.FromRawBits(value: ar), Imaginary: FixedQ4816.FromRawBits(value: ai));
    var actual = (dividend / divisor);
    var expected = ComplexDivisionOracle(ar: ar, ai: ai, br: br, bi: bi);

    if ((actual.Real.Value != expected.Real) || (actual.Imaginary.Value != expected.Imaginary)) {
        throw new InvalidOperationException("RANDOM FULL-WIDTH COMPLEX DIV ORACLE MISMATCH");
    }

    var expectedComplexProduct = new FixedComplex(
        Real: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)ar * br) - ((BigInteger)ai * bi)))),
        Imaginary: FixedQ4816.FromRawBits(value: RoundProductSumOracle(sum: (((BigInteger)ar * bi) + ((BigInteger)ai * br))))
    );
    var expectedComplexRotation = new FixedVector2(X: expectedComplexProduct.Real, Y: expectedComplexProduct.Imaginary);

    if ((dividend * divisor) != expectedComplexProduct) { throw new InvalidOperationException("RANDOM FULL-WIDTH COMPLEX MULTIPLY ORACLE MISMATCH"); }
    if (dividend.Rotate(vector: new FixedVector2(X: divisor.Real, Y: divisor.Imaginary)) != expectedComplexRotation) { throw new InvalidOperationException("RANDOM FULL-WIDTH COMPLEX ROTATE ORACLE MISMATCH"); }

    var from = new FixedVector2(X: FixedQ4816.FromRawBits(value: ar), Y: FixedQ4816.FromRawBits(value: ai));
    var to = new FixedVector2(X: FixedQ4816.FromRawBits(value: br), Y: FixedQ4816.FromRawBits(value: bi));
    var dot = (((BigInteger)ar * br) + ((BigInteger)ai * bi));
    var wedge = (((BigInteger)ar * bi) - ((BigInteger)ai * br));
    var rotor = FixedComplex.FromTo(from: from, to: to);
    var angle = Math.Atan2(y: ((double)wedge), x: ((double)dot));
    var error = Math.Max(Math.Abs((((double)rotor.Real) - Math.Cos(angle))), Math.Abs((((double)rotor.Imaginary) - Math.Sin(angle))));

    maxWideFromTo2dErr = Math.Max(maxWideFromTo2dErr, (error * 65536.0));
}

if (maxWideFromTo2dErr > 2.5) { throw new InvalidOperationException($"FULL-WIDTH FROMTO2D ERROR {maxWideFromTo2dErr:F3} ULP"); }

(long FX, long FY, long TX, long TY)[] fromToEdges = [
    (long.MinValue, 0L, 0L, long.MaxValue),
    (long.MinValue, long.MinValue, long.MaxValue, long.MaxValue),
    (long.MinValue, long.MinValue, long.MinValue, long.MinValue),
    (long.MaxValue, long.MinValue, long.MinValue, long.MaxValue),
];
foreach (var (fx, fy, tx, ty) in fromToEdges) {
    var dot = (((BigInteger)fx * tx) + ((BigInteger)fy * ty));
    var wedge = (((BigInteger)fx * ty) - ((BigInteger)fy * tx));
    var angle = Math.Atan2(y: ((double)wedge), x: ((double)dot));
    var rotor = FixedComplex.FromTo(
        from: new FixedVector2(X: FixedQ4816.FromRawBits(value: fx), Y: FixedQ4816.FromRawBits(value: fy)),
        to: new FixedVector2(X: FixedQ4816.FromRawBits(value: tx), Y: FixedQ4816.FromRawBits(value: ty)));
    var error = Math.Max(Math.Abs((((double)rotor.Real) - Math.Cos(angle))), Math.Abs((((double)rotor.Imaginary) - Math.Sin(angle))));

    if ((error * 65536.0) > 2.5) { throw new InvalidOperationException("FULL-WIDTH FROMTO2D EDGE ERROR"); }
}

var complexZeroThrew = false;
try {
    _ = FixedComplex.MultiplicativeIdentity / FixedComplex.AdditiveIdentity;
} catch (DivideByZeroException) {
    complexZeroThrew = true;
}
if (!complexZeroThrew) { throw new InvalidOperationException("COMPLEX DIVISION BY ZERO DID NOT THROW"); }

var epsilonX2 = new FixedVector2(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero);
if ((FixedComplex.FromTo(from: epsilonX2, to: -epsilonX2) != new FixedComplex(Real: FixedQ4816.NegativeOne, Imaginary: FixedQ4816.Zero)) ||
    (new FixedComplex(Real: FixedQ4816.Epsilon, Imaginary: FixedQ4816.Zero).Normalize() != FixedComplex.MultiplicativeIdentity) ||
    ((-new FixedComplex(Real: FixedQ4816.FromRawBits(value: long.MaxValue), Imaginary: FixedQ4816.FromRawBits(value: (1L << 46)))).Normalize() !=
     -new FixedComplex(Real: FixedQ4816.FromRawBits(value: long.MaxValue), Imaginary: FixedQ4816.FromRawBits(value: (1L << 46))).Normalize())) {
    throw new InvalidOperationException("SCALE-SAFE COMPLEX NORMALIZATION/FROMTO FAILED");
}

var extremeNormQ = new FixedQuaternion(X: FixedQ4816.FromRawBits(value: long.MaxValue), Y: FixedQ4816.FromRawBits(value: (1L << 46)), Z: FixedQ4816.Zero, W: FixedQ4816.Zero);
if ((-extremeNormQ).Normalize() != -extremeNormQ.Normalize() ||
    (new FixedQuaternion(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero).Normalize().X != FixedQ4816.One)) {
    throw new InvalidOperationException("SCALE-SAFE QUATERNION NORMALIZATION FAILED");
}

if (extremeNormQ.Normalize() != new FixedQuaternion(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero)) {
    throw new InvalidOperationException("EXTREME NORMALIZATION DID NOT RESOLVE SUB-HALF-ULP COMPONENT");
}

var allMinimumQ = new FixedQuaternion(
    X: FixedQ4816.MinValue,
    Y: FixedQ4816.MinValue,
    Z: FixedQ4816.MinValue,
    W: FixedQ4816.MinValue
);
var expectedAllMinimumQ = FixedQ4816.FromRawBits(value: -32768L);
var normalizedAllMinimumQ = allMinimumQ.Normalize();
if ((normalizedAllMinimumQ.X != expectedAllMinimumQ) ||
    (normalizedAllMinimumQ.Y != expectedAllMinimumQ) ||
    (normalizedAllMinimumQ.Z != expectedAllMinimumQ) ||
    (normalizedAllMinimumQ.W != expectedAllMinimumQ) ||
    allMinimumQ.TryLength(out _)) {
    throw new InvalidOperationException("QUATERNION FOUR-SQUARE CARRY/NORMALIZATION FAILED");
}

var lengthAtMax = new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
var lengthAboveMax = new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.FromRawBits(value: (1L << 32)), Z: FixedQ4816.Zero);
var squaredLengthFits = new FixedVector3(X: FixedQ4816.FromRawBits(value: (1L << 39)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
var squaredLengthOverflows = new FixedVector3(X: FixedQ4816.FromRawBits(value: (1L << 40)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
if (!lengthAtMax.TryLength(out var exactMaxLength) || (exactMaxLength != FixedQ4816.MaxValue) ||
    lengthAboveMax.TryLength(out _) || (lengthAboveMax.Length != FixedQ4816.MaxValue) ||
    !squaredLengthFits.TryLengthSquared(out _) || squaredLengthOverflows.TryLengthSquared(out _) ||
    (squaredLengthOverflows.LengthSquared != FixedQ4816.MaxValue)) {
    throw new InvalidOperationException("FULL-WIDTH LENGTH OVERFLOW POLICY FAILED");
}

var maxNormalizationError = 0L;
for (var n = 0; (n < 50_000); ++n) {
    var x = rng.NextInt64(long.MinValue, long.MaxValue);
    var y = rng.NextInt64(long.MinValue, long.MaxValue);
    var z = rng.NextInt64(long.MinValue, long.MaxValue);
    var w = rng.NextInt64(long.MinValue, long.MaxValue);
    var vector = new FixedVector3(X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z)).Normalize();
    var quaternion = new FixedQuaternion(X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z), W: FixedQ4816.FromRawBits(value: w)).Normalize();
    var vectorExpected = NormalizeOracle([x, y, z]);
    var quaternionExpected = NormalizeOracle([x, y, z, w]);

    maxNormalizationError = Math.Max(maxNormalizationError, Math.Abs(vector.X.Value - vectorExpected[0]));
    maxNormalizationError = Math.Max(maxNormalizationError, Math.Abs(vector.Y.Value - vectorExpected[1]));
    maxNormalizationError = Math.Max(maxNormalizationError, Math.Abs(vector.Z.Value - vectorExpected[2]));
    maxNormalizationError = Math.Max(maxNormalizationError, Math.Abs(quaternion.X.Value - quaternionExpected[0]));
    maxNormalizationError = Math.Max(maxNormalizationError, Math.Abs(quaternion.Y.Value - quaternionExpected[1]));
    maxNormalizationError = Math.Max(maxNormalizationError, Math.Abs(quaternion.Z.Value - quaternionExpected[2]));
    maxNormalizationError = Math.Max(maxNormalizationError, Math.Abs(quaternion.W.Value - quaternionExpected[3]));
}
if (maxNormalizationError > 1L) { throw new InvalidOperationException($"NORMALIZATION ORACLE ERROR {maxNormalizationError} RAW ULP"); }

Console.WriteLine($"complex full-width: division exact (10k edges + 50k random), FromTo max {maxWideFromTo2dErr:F3} ULP; normalization max {maxNormalizationError} raw ULP");

var maxComplexMulErr = 0.0;
var maxComplexRotErr = 0.0;
var maxComplexDivErr = 0.0;
var maxFromTo2dErr = 0.0;
var maxComposeErr = 0L;
var maxRoundTripErr = 0L;
var maxInverseErr = 0L;
var maxRigidExpLogErr = 0L;
var maxScLerpErr = 0.0;
for (var n = 0; (n < 300_000); n++) {
    // Complex: FromAngle, multiply, rotate vs double.
    var ca = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 6.0) - 3.0)));
    var cb = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 6.0) - 3.0)));
    var cp = (ca * cb);

    var (car, cai, cbr, cbi) = (((double)ca.Real), ((double)ca.Imaginary), ((double)cb.Real), ((double)cb.Imaginary));
    var em = Math.Max(Math.Abs((((double)cp.Real) - ((car * cbr) - (cai * cbi)))), Math.Abs((((double)cp.Imaginary) - ((car * cbi) + (cai * cbr)))));

    if ((em * 65536.0) > maxComplexMulErr) { maxComplexMulErr = (em * 65536.0); }

    var v2 = new FixedVector2(X: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 8.0) - 4.0)), Y: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 8.0) - 4.0)));
    var rotated2 = ca.Rotate(vector: v2);

    var (vx2, vy2) = (((double)v2.X), ((double)v2.Y));
    var er = Math.Max(Math.Abs((((double)rotated2.X) - ((car * vx2) - (cai * vy2)))), Math.Abs((((double)rotated2.Y) - ((car * vy2) + (cai * vx2)))));

    if ((er * 65536.0) > maxComplexRotErr) { maxComplexRotErr = (er * 65536.0); }

    // Division, including small-magnitude divisors (the relative-precision hazard).
    var scale = Math.Pow(2.0, ((rng.NextDouble() * 12.0) - 6.0));
    var divisor = new FixedComplex(Real: FixedQ4816.FromDouble(value: (cbr * scale)), Imaginary: FixedQ4816.FromDouble(value: (cbi * scale)));

    if ((divisor.Real.Value != 0L) || (divisor.Imaginary.Value != 0L)) {
        var numerator = new FixedComplex(Real: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 4.0) - 2.0)), Imaginary: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 4.0) - 2.0)));
        var q2c = (numerator / divisor);

        var (nr, ni, dr, di) = (((double)numerator.Real), ((double)numerator.Imaginary), ((double)divisor.Real), ((double)divisor.Imaginary));
        var dd = ((dr * dr) + (di * di));
        var ed = Math.Max(Math.Abs((((double)q2c.Real) - (((nr * dr) + (ni * di)) / dd))), Math.Abs((((double)q2c.Imaginary) - (((ni * dr) - (nr * di)) / dd))));

        if ((ed * 65536.0) > maxComplexDivErr) { maxComplexDivErr = (ed * 65536.0); }
    }

    // Rigid transforms: build two random transforms, check composition consistency, roundtrip, inverse.
    var (a1x, a1y, a1z) = RandomUnitAxis(rng);
    var rotA = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.FromDouble(value: a1x), Y: FixedQ4816.FromDouble(value: a1y), Z: FixedQ4816.FromDouble(value: a1z)).Normalize(), angle: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 6.0) - 3.0)));
    var trA = new FixedVector3(X: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 20.0) - 10.0)), Y: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 20.0) - 10.0)), Z: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 20.0) - 10.0)));

    var (a2x, a2y, a2z) = RandomUnitAxis(rng);
    var rotB = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.FromDouble(value: a2x), Y: FixedQ4816.FromDouble(value: a2y), Z: FixedQ4816.FromDouble(value: a2z)).Normalize(), angle: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 6.0) - 3.0)));
    var trB = new FixedVector3(X: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 20.0) - 10.0)), Y: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 20.0) - 10.0)), Z: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 20.0) - 10.0)));
    var ta = FixedRigidTransform.FromRotationTranslation(rotation: rotA, translation: trA);
    var tb = FixedRigidTransform.FromRotationTranslation(rotation: rotB, translation: trB);
    var p = new FixedVector3(X: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 4.0) - 2.0)), Y: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 4.0) - 2.0)), Z: FixedQ4816.FromDouble(value: ((rng.NextDouble() * 4.0) - 2.0)));

    // (A ∘ B)(p) == A(B(p)) — the dual-quaternion product composes correctly.
    var composed = (ta * tb).TransformPoint(point: p);
    var sequential = ta.TransformPoint(point: tb.TransformPoint(point: p));
    var ec = Math.Max(Math.Abs((composed.X.Value - sequential.X.Value)), Math.Max(Math.Abs((composed.Y.Value - sequential.Y.Value)), Math.Abs((composed.Z.Value - sequential.Z.Value))));

    if (ec > maxComposeErr) { maxComposeErr = ec; }

    // FromTo (2D): the scale-free geometric-product constructor — angles from tiny (2^-12) through large (2^6)
    // magnitudes must all match the double reference computed from the QUANTIZED raw sums (exact: products stay
    // below 2^53).
    var fromToMagnitudeA = Math.ScaleB((1.0 + rng.NextDouble()), rng.Next(-12, 7));
    var fromToMagnitudeB = Math.ScaleB((1.0 + rng.NextDouble()), rng.Next(-12, 7));
    var fromToAngleA = ((rng.NextDouble() * Math.Tau) - Math.PI);
    var fromToAngleB = ((rng.NextDouble() * Math.Tau) - Math.PI);
    var planarFrom = new FixedVector2(X: FixedQ4816.FromDouble(value: (fromToMagnitudeA * Math.Cos(fromToAngleA))), Y: FixedQ4816.FromDouble(value: (fromToMagnitudeA * Math.Sin(fromToAngleA))));
    var planarTo = new FixedVector2(X: FixedQ4816.FromDouble(value: (fromToMagnitudeB * Math.Cos(fromToAngleB))), Y: FixedQ4816.FromDouble(value: (fromToMagnitudeB * Math.Sin(fromToAngleB))));
    var planarDot = ((((double)planarFrom.X.Value) * planarTo.X.Value) + (((double)planarFrom.Y.Value) * planarTo.Y.Value));
    var planarWedge = ((((double)planarFrom.X.Value) * planarTo.Y.Value) - (((double)planarFrom.Y.Value) * planarTo.X.Value));

    if ((planarDot != 0.0) || (planarWedge != 0.0)) {
        var expectedAngle = Math.Atan2(planarWedge, planarDot);
        var planarRotor = FixedComplex.FromTo(from: planarFrom, to: planarTo);
        var eft = Math.Max(
            Math.Abs((((double)planarRotor.Real) - Math.Cos(expectedAngle))),
            Math.Abs((((double)planarRotor.Imaginary) - Math.Sin(expectedAngle))));

        if ((eft * 65536.0) > maxFromTo2dErr) { maxFromTo2dErr = (eft * 65536.0); }
    }

    // Rotation/Translation extraction roundtrip.
    var extractedT = ta.Translation;
    var et = Math.Max(Math.Abs((extractedT.X.Value - trA.X.Value)), Math.Max(Math.Abs((extractedT.Y.Value - trA.Y.Value)), Math.Abs((extractedT.Z.Value - trA.Z.Value))));

    if (et > maxRoundTripErr) { maxRoundTripErr = et; }

    // Inverse roundtrip on points.
    var back = ta.Inverse().TransformPoint(point: ta.TransformPoint(point: p));
    var ei = Math.Max(Math.Abs((back.X.Value - p.X.Value)), Math.Max(Math.Abs((back.Y.Value - p.Y.Value)), Math.Abs((back.Z.Value - p.Z.Value))));

    if (ei > maxInverseErr) { maxInverseErr = ei; }

    // Exp/Log roundtrip on the screw, gated above the quaternion log's vector-length floor (the screw division
    // amplifies below it) and scale-aware to the ±10-unit translations like the other rigid gates.
    if (VectorNormLocal(ta.Value.Real.X.Value, ta.Value.Real.Y.Value, ta.Value.Real.Z.Value) >= 4096L) {
        var (screwReal, screwDual) = ta.Log();
        var expBack = FixedRigidTransform.Exp(real: screwReal, dual: screwDual);
        var eel = Math.Max(
            Math.Max(
                Math.Max(Math.Abs((expBack.Value.Real.X.Value - ta.Value.Real.X.Value)), Math.Abs((expBack.Value.Real.Y.Value - ta.Value.Real.Y.Value))),
                Math.Max(Math.Abs((expBack.Value.Real.Z.Value - ta.Value.Real.Z.Value)), Math.Abs((expBack.Value.Real.W.Value - ta.Value.Real.W.Value)))),
            Math.Max(
                Math.Max(Math.Abs((expBack.Value.Dual.X.Value - ta.Value.Dual.X.Value)), Math.Abs((expBack.Value.Dual.Y.Value - ta.Value.Dual.Y.Value))),
                Math.Max(Math.Abs((expBack.Value.Dual.Z.Value - ta.Value.Dual.Z.Value)), Math.Abs((expBack.Value.Dual.W.Value - ta.Value.Dual.W.Value)))));

        if (eel > maxRigidExpLogErr) { maxRigidExpLogErr = eel; }
    }

    if ((n % 10) == 0) {
        // ScLerp vs a double reference implementing the same algorithm, branch decisions driven by the fixed side.
        var t = FixedQ4816.FromDouble(value: rng.NextDouble());
        var s = FixedRigidTransform.ScLerp(from: ta, to: tb, amount: t);
        var fixedDot = FixedQuaternion.Dot(left: ta.Value.Real, right: tb.Value.Real);
        var flip = (fixedDot.Value < 0L);
        // Reconstruct the fixed-side blend decision so the reference takes the same branch.
        var blend = (FixedQ4816.Abs(value: fixedDot).Value > 65534L);
        var probePoint = p;
        var fixedResult = s.TransformPoint(point: probePoint);

        var (mx, my, mz) = ScLerpRef(ta, tb, ((double)t), flip, blend, ((double)probePoint.X), ((double)probePoint.Y), ((double)probePoint.Z));
        var es = Math.Max(Math.Abs((((double)fixedResult.X) - mx)), Math.Max(Math.Abs((((double)fixedResult.Y) - my)), Math.Abs((((double)fixedResult.Z) - mz))));

        if ((es * 65536.0) > maxScLerpErr) { maxScLerpErr = (es * 65536.0); }
    }
}
Console.WriteLine($"complex: mul={maxComplexMulErr:F2} rotate={maxComplexRotErr:F2} div={maxComplexDivErr:F2} fromto={maxFromTo2dErr:F2} max raw ULP (300k)");
Console.WriteLine($"rigid: compose={maxComposeErr} extract={maxRoundTripErr} inverse={maxInverseErr} explog={maxRigidExpLogErr} sclerp={maxScLerpErr:F2} max raw ULP (300k)");
if ((maxComplexMulErr > 2.0) || (maxComplexRotErr > 2.0) || (maxComplexDivErr > 3.0) || (maxFromTo2dErr > 6.0)) { throw new InvalidOperationException("COMPLEX ACCURACY GATE FAILED"); }

// Rigid-transform error scales with translation magnitude (~2^-15 relative, the Q16 unit-quaternion norm
// quantization): ±10-unit translations put the honest ceiling near 100-200 raw; sclerp adds screw-division
// amplification.
if ((maxComposeErr > 160L) || (maxRoundTripErr > 96L) || (maxInverseErr > 224L) || (maxRigidExpLogErr > 320L) || (maxScLerpErr > 768.0)) { throw new InvalidOperationException("RIGID ACCURACY GATE FAILED"); }

var tinyRealDualQuaternion = new FixedDual<FixedQuaternion>(
    Real: new FixedQuaternion(X: FixedQ4816.Epsilon, Y: FixedQ4816.Epsilon, Z: FixedQ4816.Zero, W: FixedQ4816.Zero),
    Dual: FixedQuaternion.AdditiveIdentity);
if (!FixedRigidTransform.TryFromDualQuaternion(value: tinyRealDualQuaternion, result: out var tinyNormalizedRigid) ||
    (Math.Abs(((double)tinyNormalizedRigid.Value.Real.X - Math.Sqrt(0.5d))) > 2e-5d) ||
    (Math.Abs(((double)tinyNormalizedRigid.Value.Real.Y - Math.Sqrt(0.5d))) > 2e-5d)) {
    throw new InvalidOperationException("RIGID TINY-REAL NORMALIZATION REGRESSION");
}

// Pure translation is the screw's exact pole: log = (0, t/2) and exp reproduces the transform bit-for-bit.
var pureTranslation = FixedRigidTransform.FromRotationTranslation(
    rotation: FixedQuaternion.Identity,
    translation: new FixedVector3(X: FixedQ4816.FromInteger(value: 2L), Y: FixedQ4816.FromInteger(value: -3L), Z: FixedQ4816.FromDouble(value: 0.5)));
var (pureReal, pureDual) = pureTranslation.Log();
if (pureReal != FixedVector3.Zero) { throw new InvalidOperationException("PURE TRANSLATION LOG HAS ROTATION"); }
if (FixedRigidTransform.Exp(real: pureReal, dual: pureDual) != pureTranslation) { throw new InvalidOperationException("PURE TRANSLATION EXP/LOG NOT EXACT"); }

// ScLerp endpoints return the operands (within rounding).
var endA = FixedRigidTransform.FromRotationTranslation(rotation: FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 0.9)), translation: new FixedVector3(X: FixedQ4816.FromInteger(value: 2L), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: -1L)));
var endB = FixedRigidTransform.FromRotationTranslation(rotation: FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: -1.3)), translation: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: 3L), Z: FixedQ4816.One));
var endProbe = new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.One, Z: FixedQ4816.One);
var atZero = FixedRigidTransform.ScLerp(from: endA, to: endB, amount: FixedQ4816.Zero).TransformPoint(point: endProbe);
var atOne = FixedRigidTransform.ScLerp(from: endA, to: endB, amount: FixedQ4816.One).TransformPoint(point: endProbe);
var expectedZero = endA.TransformPoint(point: endProbe);
var expectedOne = endB.TransformPoint(point: endProbe);
if ((Math.Abs((atZero.X.Value - expectedZero.X.Value)) > 32L) || (Math.Abs((atOne.X.Value - expectedOne.X.Value)) > 32L)) {
    throw new InvalidOperationException("SCLERP ENDPOINTS FAILED");
}
Console.WriteLine("complex/rigid: gates + sclerp endpoints OK");

// ---- CyclicRotation (deterministic order-30 looping rotation) ----
// The clock must close its cycle bit-exactly (indexed, not accumulated), advance each plane at the speeds {1,7,11,13}
// steps/tick, and hold the thirty baked rotations to the 30th roots of unity. Oracles are the residue arithmetic and an
// independent double cos/sin, never the implementation's own table.
int[] cyclicSpeeds = [1, 7, 11, 13];
var cyclicMaxRotorUlp = 0L;
var cyclicProbe = new FixedVector2(X: FixedQ4816.FromInteger(value: 1L), Y: FixedQ4816.FromInteger(value: 3L));
for (var plane = 0; (plane < CyclicRotation.PlaneCount); ++plane) {
    if (CyclicRotation.Step(plane: plane, tick: 1L) != cyclicSpeeds[plane]) {
        throw new InvalidOperationException("CYCLIC ROTATION PLANE SPEED IS WRONG");
    }

    for (var tick = -240L; (tick < 480L); ++tick) {
        var phase = ((int)(((tick % 30L) + 30L) % 30L));
        var expectedStep = ((cyclicSpeeds[plane] * phase) % 30);

        if (CyclicRotation.Step(plane: plane, tick: tick) != expectedStep) {
            throw new InvalidOperationException("CYCLIC ROTATION STEP DISAGREES WITH RESIDUE ORACLE");
        }
        if (CyclicRotation.At(plane: plane, tick: tick) != CyclicRotation.At(plane: plane, tick: (tick + 30L))) {
            throw new InvalidOperationException("CYCLIC ROTATION CYCLE DID NOT CLOSE EXACTLY AT PERIOD");
        }
        if ((phase == 0) && (CyclicRotation.At(plane: plane, tick: tick) != FixedComplex.MultiplicativeIdentity)) {
            throw new InvalidOperationException("CYCLIC ROTATION DID NOT RETURN TO IDENTITY AT A MULTIPLE OF PERIOD");
        }
        if (CyclicRotation.Rotate(plane: plane, tick: tick, vector: cyclicProbe) != CyclicRotation.At(plane: plane, tick: tick).Rotate(vector: cyclicProbe)) {
            throw new InvalidOperationException("CYCLIC ROTATION ROTATE DISAGREES WITH ITS ROTATION");
        }
    }
}
for (var step = 0; (step < 30); ++step) {
    var rotation = CyclicRotation.At(plane: 0, tick: step);   // plane 0 turns one step per tick
    var expectedCos = ((long)Math.Round((Math.Cos(((2.0 * Math.PI * step) / 30.0)) * 65536.0)));
    var expectedSin = ((long)Math.Round((Math.Sin(((2.0 * Math.PI * step) / 30.0)) * 65536.0)));

    cyclicMaxRotorUlp = Math.Max(cyclicMaxRotorUlp, Math.Max(Math.Abs((rotation.Real.Value - expectedCos)), Math.Abs((rotation.Imaginary.Value - expectedSin))));
}
if (cyclicMaxRotorUlp > 2L) {
    throw new InvalidOperationException("CYCLIC ROTATION ROTORS DRIFTED FROM THE 30TH ROOTS OF UNITY");
}
Console.WriteLine($"cyclic rotation: exact 30-tick loop + identity resync, {{1,7,11,13}} plane speeds, rotors {cyclicMaxRotorUlp} raw ULP from unity OK");

// ---- SymmetryLattice (the exact E8 root system CyclicRotation is the heartbeat of) ----
// The 240 nodes must close under reflection (each an involution), those reflections must act transitively on all of them
// (so composing them realizes the whole symmetry group W(E8)), the cycle must be an order-30 permutation cutting eight
// rings of thirty (== CyclicRotation's period), and the projection must resolve those rings with radii in four
// golden-ratio pairs, one cycle step turning a node a twelfth of a turn. Oracles: exact index arithmetic and
// independent double geometry, never the implementation's own tables.
if (SymmetryLattice.RingSize != CyclicRotation.Period) {
    throw new InvalidOperationException("SYMMETRY LATTICE RING SIZE IS NOT THE CYCLIC ROTATION PERIOD");
}
if ((SymmetryLattice.RayCount != 120) || (SymmetryLattice.RayCycleOrder != 15)) {
    throw new InvalidOperationException("SYMMETRY LATTICE RAY QUOTIENT CONTRACT CHANGED");
}
var latticeRayFactorProduct = SymmetryLattice.RayCycleFactors.ToArray().Aggregate(
    seed: new BinaryPolynomial(bits: 1UL),
    func: (product, factor) => (product * factor)
);
if (latticeRayFactorProduct.Bits != ((1UL << SymmetryLattice.RayCycleOrder) | 1UL)) {
    throw new InvalidOperationException("SYMMETRY LATTICE RAY-CYCLE FACTORIZATION CHANGED");
}
foreach (var invalidNode in new[] { -1, SymmetryLattice.NodeCount, 268_435_456 }) {
    ExpectArgumentOutOfRange(parameterName: "node", operation: () => SymmetryLattice.Reflect(node: invalidNode, mirror: 0));
    ExpectArgumentOutOfRange(parameterName: "mirror", operation: () => SymmetryLattice.Reflect(node: 0, mirror: invalidNode));
    ExpectArgumentOutOfRange(parameterName: "node", operation: () => SymmetryLattice.Cycle(node: invalidNode));
    ExpectArgumentOutOfRange(parameterName: "node", operation: () => SymmetryLattice.Ring(node: invalidNode));
    ExpectArgumentOutOfRange(parameterName: "node", operation: () => SymmetryLattice.Project(node: invalidNode));
    ExpectArgumentOutOfRange(parameterName: "node", operation: () => SymmetryLattice.Antipode(node: invalidNode));
    ExpectArgumentOutOfRange(parameterName: "node", operation: () => SymmetryLattice.CanonicalRay(node: invalidNode));
    ExpectArgumentOutOfRange(parameterName: "first", operation: () => SymmetryLattice.AreOrthogonal(first: invalidNode, second: 0));
    ExpectArgumentOutOfRange(parameterName: "second", operation: () => SymmetryLattice.AreOrthogonal(first: 0, second: invalidNode));
}
var latticeRingSizes = new int[SymmetryLattice.RingCount];
var latticeReached = new bool[SymmetryLattice.NodeCount];
var latticeWorklist = new int[SymmetryLattice.NodeCount];
for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
    if (SymmetryLattice.Ring(node: SymmetryLattice.Cycle(node: node)) != SymmetryLattice.Ring(node: node)) {
        throw new InvalidOperationException("SYMMETRY LATTICE CYCLE STEP LEFT THE RING");
    }

    latticeRingSizes[SymmetryLattice.Ring(node: node)]++;

    for (var mirror = 0; (mirror < SymmetryLattice.NodeCount); ++mirror) {
        if (SymmetryLattice.Reflect(node: SymmetryLattice.Reflect(node: node, mirror: mirror), mirror: mirror) != node) {
            throw new InvalidOperationException("SYMMETRY LATTICE REFLECTION IS NOT AN INVOLUTION");
        }
        if (SymmetryLattice.AreOrthogonal(first: node, second: mirror) != (SymmetryLattice.Reflect(node: node, mirror: mirror) == node)) {
            throw new InvalidOperationException("SYMMETRY LATTICE ORTHOGONALITY DISAGREES WITH REFLECTION");
        }
    }

    // Every E8 exponent is odd, so the fifteenth power of this Coxeter element is central inversion. Reflecting a root
    // through its own hyperplane is the same negation, giving a coordinate-free exact oracle for the half-cycle.
    var latticeOpposite = node;
    for (var step = 0; (step < (SymmetryLattice.RingSize / 2)); ++step) { latticeOpposite = SymmetryLattice.Cycle(node: latticeOpposite); }
    if (latticeOpposite != SymmetryLattice.Reflect(node: node, mirror: node)) {
        throw new InvalidOperationException("SYMMETRY LATTICE COXETER HALF-CYCLE IS NOT CENTRAL INVERSION");
    }
    if ((SymmetryLattice.Antipode(node: node) != latticeOpposite) ||
        (SymmetryLattice.CanonicalRay(node: node) != SymmetryLattice.CanonicalRay(node: latticeOpposite))) {
        throw new InvalidOperationException("SYMMETRY LATTICE ANTIPODAL RAY QUOTIENT CHANGED");
    }
}
for (var ring = 0; (ring < SymmetryLattice.RingCount); ++ring) {
    if (latticeRingSizes[ring] != SymmetryLattice.RingSize) {
        throw new InvalidOperationException("SYMMETRY LATTICE CYCLE ORBIT IS NOT A RING OF THIRTY");
    }
}
var latticeCycleOrbit = 0;
for (var cursor = SymmetryLattice.Cycle(node: 0); (cursor != 0); cursor = SymmetryLattice.Cycle(node: cursor)) {
    if ((++latticeCycleOrbit) > SymmetryLattice.RingSize) {
        throw new InvalidOperationException("SYMMETRY LATTICE CYCLE IS NOT ORDER THIRTY");
    }
}
// Reflections act transitively on all 240 nodes: the reflection group is the full symmetry group, not the order-30 cycle.
latticeReached[0] = true;
latticeWorklist[0] = 0;
var latticePending = 1;
var latticeReachedCount = 1;
while (latticePending > 0) {
    var node = latticeWorklist[--latticePending];

    for (var mirror = 0; (mirror < SymmetryLattice.NodeCount); ++mirror) {
        var image = SymmetryLattice.Reflect(node: node, mirror: mirror);

        if (!latticeReached[image]) {
            latticeReached[image] = true;
            latticeWorklist[latticePending++] = image;
            ++latticeReachedCount;
        }
    }
}
if (latticeReachedCount != SymmetryLattice.NodeCount) {
    throw new InvalidOperationException("SYMMETRY LATTICE REFLECTIONS ARE NOT TRANSITIVE ON THE NODES");
}
// Projection geometry: eight rings whose radii pair off by the golden ratio, one cycle step of a twelfth of a turn.
var latticeGolden = ((1.0 + Math.Sqrt(5.0)) / 2.0);
var latticeRingMinimumRadius = new double[SymmetryLattice.RingCount];
var latticeRingMaximumRadius = new double[SymmetryLattice.RingCount];
Array.Fill(latticeRingMinimumRadius, double.PositiveInfinity);
for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
    var point = SymmetryLattice.Project(node: node);
    var radius = Math.Sqrt((((double)point.X * (double)point.X) + ((double)point.Y * (double)point.Y)));
    var ring = SymmetryLattice.Ring(node: node);

    latticeRingMinimumRadius[ring] = Math.Min(latticeRingMinimumRadius[ring], radius);
    latticeRingMaximumRadius[ring] = Math.Max(latticeRingMaximumRadius[ring], radius);
}
var latticeRingRadius = new double[SymmetryLattice.RingCount];
for (var ring = 0; (ring < SymmetryLattice.RingCount); ++ring) {
    if ((latticeRingMaximumRadius[ring] - latticeRingMinimumRadius[ring]) > (4.0 / (1L << FixedQ4816.FractionBitCount))) {
        throw new InvalidOperationException("SYMMETRY LATTICE PROJECTED ORBIT IS NOT CONCENTRIC WITHIN FIXED-POINT PRECISION");
    }

    latticeRingRadius[ring] = ((latticeRingMinimumRadius[ring] + latticeRingMaximumRadius[ring]) / 2.0);
}
Array.Sort(latticeRingRadius);
var latticeGoldenPairs = 0;
for (var inner = 0; (inner < SymmetryLattice.RingCount); ++inner) {
    for (var outer = (inner + 1); (outer < SymmetryLattice.RingCount); ++outer) {
        if (Math.Abs(((latticeRingRadius[outer] / latticeRingRadius[inner]) - latticeGolden)) < 0.002) { ++latticeGoldenPairs; }
    }
}
if (latticeGoldenPairs != (SymmetryLattice.RingCount / 2)) {
    throw new InvalidOperationException("SYMMETRY LATTICE PROJECTION RINGS ARE NOT IN GOLDEN-RATIO PAIRS");
}
// The normalized ring radii are independently known as the eight E8/Ising mass ratios.
var latticeMassRatios = new[] {
    1.0,
    latticeGolden,
    (2.0 * Math.Cos(Math.PI / 30.0)),
    (2.0 * latticeGolden * Math.Cos(7.0 * Math.PI / 30.0)),
    (2.0 * latticeGolden * Math.Cos(2.0 * Math.PI / 15.0)),
    (2.0 * latticeGolden * Math.Cos(Math.PI / 30.0)),
    (4.0 * latticeGolden * Math.Cos(Math.PI / 5.0) * Math.Cos(7.0 * Math.PI / 30.0)),
    (4.0 * latticeGolden * Math.Cos(Math.PI / 5.0) * Math.Cos(2.0 * Math.PI / 15.0)),
};
for (var ring = 0; (ring < SymmetryLattice.RingCount); ++ring) {
    if (Math.Abs(((latticeRingRadius[ring] / latticeRingRadius[0]) - latticeMassRatios[ring])) > 0.0001) {
        throw new InvalidOperationException("SYMMETRY LATTICE PROJECTED RADII DO NOT MATCH THE E8 MASS SPECTRUM");
    }
}
for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
    var latticeBefore = SymmetryLattice.Project(node: node);
    var latticeAfter = SymmetryLattice.Project(node: SymmetryLattice.Cycle(node: node));
    var latticeTurn = (((Math.Atan2((double)latticeAfter.Y, (double)latticeAfter.X) - Math.Atan2((double)latticeBefore.Y, (double)latticeBefore.X)) * 180.0) / Math.PI);
    latticeTurn = (((latticeTurn % 360.0) + 360.0) % 360.0);
    if ((Math.Abs((latticeTurn - 12.0)) > 0.1) && (Math.Abs((latticeTurn - 348.0)) > 0.1)) {
        throw new InvalidOperationException("SYMMETRY LATTICE CYCLE STEP DID NOT TURN A TWELFTH OF A TURN");
    }
}
Console.WriteLine($"symmetry lattice: 240 nodes, reflections transitive (full group), order-30 cycle into 8 rings of 30, projection {latticeGoldenPairs} golden pairs OK");

// ---- HilbertCurve (locality-preserving space-filling curve) ----
// Encode/Decode must be inverse, a bijection onto [0, 4^order), and map consecutive distances to grid neighbours (the
// defining locality property). Oracles: the bijection/inverse definitions and Manhattan distance, never the code.
for (var order = 1; (order <= 9); ++order) {
    var side = (1U << order);
    var cells = ((ulong)side * side);
    var hilbertSeen = new bool[cells];
    var previous = (X: 0U, Y: 0U);

    for (var distance = 0UL; (distance < cells); ++distance) {
        var point = HilbertCurve.Decode(order: order, distance: distance);

        if (hilbertSeen[distance]) { throw new InvalidOperationException("HILBERT CURVE IS NOT A BIJECTION"); }
        hilbertSeen[distance] = true;

        if (HilbertCurve.Encode(order: order, x: point.X, y: point.Y) != distance) {
            throw new InvalidOperationException("HILBERT ENCODE IS NOT THE INVERSE OF DECODE");
        }
        if ((distance > 0UL) && ((Math.Abs(((int)point.X - (int)previous.X)) + Math.Abs(((int)point.Y - (int)previous.Y))) != 1)) {
            throw new InvalidOperationException("HILBERT CONSECUTIVE POINTS ARE NOT GRID NEIGHBOURS");
        }

        previous = point;
    }
}
var hilbertProbe = 0x9E3779B97F4A7C15UL;
for (var order = 1; (order <= 31); ++order) {
    var mask = ((1U << order) - 1U);

    for (var sample = 0; (sample < 4096); ++sample) {
        hilbertProbe = unchecked(((hilbertProbe * 6364136223846793005UL) + 1442695040888963407UL));

        var x = (((uint)(hilbertProbe >> 33)) & mask);
        var y = (((uint)hilbertProbe) & mask);

        if (HilbertCurve.Decode(order: order, distance: HilbertCurve.Encode(order: order, x: x, y: y)) != (x, y)) {
            throw new InvalidOperationException("HILBERT ROUND TRIP FAILED AT HIGH ORDER");
        }
    }
}
Console.WriteLine("hilbert curve: bijection + inverse + neighbour adjacency (order 1-9), 31-bit round-trips OK");

// ---- HexCoord (exact Eisenstein-integer hex grid) ----
// Six distinct unit neighbours at distance one; 60° rotation (a unit multiply) has order six and preserves both Length
// and Norm, with RotatedLeft/RotatedRight inverse; the Eisenstein product is an associative ring with ω²+ω+1=0; Length
// is the true graph distance; Round snaps to the nearest cell. Oracles: BFS distance, the ring laws, Euclidean search.
for (var direction = 0; (direction < HexCoord.NeighborCount); ++direction) {
    if (HexCoord.Direction(direction: direction).Length != 1) {
        throw new InvalidOperationException("HEXCOORD UNIT IS NOT AT DISTANCE ONE");
    }

    for (var other = (direction + 1); (other < HexCoord.NeighborCount); ++other) {
        if (HexCoord.Direction(direction: direction) == HexCoord.Direction(direction: other)) {
            throw new InvalidOperationException("HEXCOORD UNITS ARE NOT DISTINCT");
        }
    }
}
var hexOmega = new HexCoord(Q: 0, R: 1);
if ((((hexOmega * hexOmega) + hexOmega) + HexCoord.MultiplicativeIdentity) != HexCoord.AdditiveIdentity) {
    throw new InvalidOperationException("HEXCOORD RING RELATION w^2+w+1=0 FAILED");
}
for (var q = -24; (q <= 24); ++q) {
    for (var r = -24; (r <= 24); ++r) {
        var hex = new HexCoord(Q: q, R: r);

        if ((hex.RotatedLeft().Length != hex.Length) || (hex.RotatedLeft().Norm != hex.Norm)) {
            throw new InvalidOperationException("HEXCOORD ROTATION CHANGED LENGTH OR NORM");
        }
        if (hex.RotatedLeft().RotatedRight() != hex) {
            throw new InvalidOperationException("HEXCOORD ROTATIONS ARE NOT INVERSE");
        }

        var spun = hex;
        for (var i = 0; (i < 6); ++i) { spun = spun.RotatedLeft(); }
        if (spun != hex) { throw new InvalidOperationException("HEXCOORD ROTATION IS NOT ORDER SIX"); }

        var scaled = new HexCoord(Q: (q % 6), R: (r % 6));
        if (((hex * hexOmega) * scaled) != (hex * (hexOmega * scaled))) {
            throw new InvalidOperationException("HEXCOORD RING PRODUCT IS NOT ASSOCIATIVE");
        }
    }
}
// Length equals the breadth-first graph distance out to radius 12 (array BFS, no generic collections).
const int hexRadius = 12;
const int hexOffset = (hexRadius + 1);
const int hexSpan = ((2 * hexOffset) + 1);
var hexDistance = new int[hexSpan * hexSpan];
var hexFrontier = new int[hexSpan * hexSpan];
Array.Fill(array: hexDistance, value: -1);
int[] hexDirQ = [1, 1, 0, -1, -1, 0];
int[] hexDirR = [0, 1, 1, 0, -1, -1];
var hexOrigin = ((hexOffset * hexSpan) + hexOffset);
hexDistance[hexOrigin] = 0;
var hexHead = 0;
var hexTail = 0;
hexFrontier[hexTail++] = hexOrigin;
while (hexHead < hexTail) {
    var packed = hexFrontier[hexHead++];
    var cellQ = ((packed / hexSpan) - hexOffset);
    var cellR = ((packed % hexSpan) - hexOffset);
    var cellDistance = hexDistance[packed];

    if (cellDistance >= hexRadius) { continue; }

    for (var k = 0; (k < 6); ++k) {
        var stepQ = (cellQ + hexDirQ[k]);
        var stepR = (cellR + hexDirR[k]);

        if ((stepQ < -hexOffset) || (stepQ > hexOffset) || (stepR < -hexOffset) || (stepR > hexOffset)) { continue; }

        var stepPacked = (((stepQ + hexOffset) * hexSpan) + (stepR + hexOffset));

        if (hexDistance[stepPacked] < 0) {
            hexDistance[stepPacked] = (cellDistance + 1);
            hexFrontier[hexTail++] = stepPacked;
        }
    }
}
for (var q = -hexRadius; (q <= hexRadius); ++q) {
    for (var r = -hexRadius; (r <= hexRadius); ++r) {
        var packed = (((q + hexOffset) * hexSpan) + (r + hexOffset));

        if ((hexDistance[packed] >= 0) && (new HexCoord(Q: q, R: r).Length != hexDistance[packed])) {
            throw new InvalidOperationException("HEXCOORD LENGTH DISAGREES WITH GRAPH DISTANCE");
        }
    }
}
// Round lands on the nearest cell, checked against an independent Euclidean search.
for (var stepQ = -80; (stepQ <= 80); ++stepQ) {
    for (var stepR = -80; (stepR <= 80); ++stepR) {
        var fractionalQ = (stepQ * 0.15);
        var fractionalR = (stepR * 0.15);
        var rounded = HexCoord.Round(q: FixedQ4816.FromDouble(value: fractionalQ), r: FixedQ4816.FromDouble(value: fractionalR));
        var best = double.MaxValue;

        for (var a = -14; (a <= 14); ++a) {
            for (var b = -14; (b <= 14); ++b) {
                var dx = ((a - fractionalQ) - ((b - fractionalR) * 0.5));
                var dy = ((b - fractionalR) * 0.8660254037844386);
                best = Math.Min(best, ((dx * dx) + (dy * dy)));
            }
        }

        var gx = ((rounded.Q - fractionalQ) - ((rounded.R - fractionalR) * 0.5));
        var gy = ((rounded.R - fractionalR) * 0.8660254037844386);

        if (((gx * gx) + (gy * gy)) > (best + 0.001)) {
            throw new InvalidOperationException("HEXCOORD ROUND IS NOT THE NEAREST CELL");
        }
    }
}
Console.WriteLine("hexcoord: 6 unit neighbours, order-6 exact 60° rotation, associative ring w^2+w+1=0, Length = graph distance, Round to nearest cell OK");

// ---- MetallicQuasicrystal random access (the general cut-and-project; subsumes the retired golden/silver files) ----
// For each index n the ring-coordinate chain a + b·δₙ from the origin must stay in the set, invert under Previous, step
// by exactly δₙ or δₙ² ((0,1) or (1,n)), advance monotonically, avoid the forbidden factors SS and Lⁿ⁺², and reach
// density δₙ. Contains must equal the walked vertex set exactly over a coordinate box (a denser window would pass the walk
// yet admit ghost points), and the ring-coordinate word must match the independently streamed substitution word — two
// implementations of one tiling. Golden is n=1 (verified elsewhere to equal the former GoldenQuasicrystal coordinate for
// coordinate) and silver is n=2.
for (var metallicIndex = 1; (metallicIndex <= 6); ++metallicIndex) {
    if (!MetallicQuasicrystal.Contains(n: metallicIndex, a: 0L, b: 0L)) {
        throw new InvalidOperationException($"METALLIC QUASICRYSTAL ORIGIN IS NOT A MEMBER n={metallicIndex}");
    }

    var metallicPoint = (A: 0L, B: 0L);
    var metallicLong = 0L;
    var metallicShort = 0L;
    var metallicRun = 0;
    var metallicPrevLong = false;
    var metallicWalkWord = new bool[6000];
    var metallicVisited = new HashSet<(long A, long B)>();

    for (var step = 0; (step < metallicWalkWord.Length); ++step) {
        metallicVisited.Add(item: metallicPoint);

        var isLong = MetallicQuasicrystal.StartsLongTile(n: metallicIndex, a: metallicPoint.A, b: metallicPoint.B);
        var next = MetallicQuasicrystal.Next(n: metallicIndex, a: metallicPoint.A, b: metallicPoint.B);

        metallicWalkWord[step] = isLong;

        if (!MetallicQuasicrystal.Contains(n: metallicIndex, a: next.A, b: next.B)) {
            throw new InvalidOperationException($"METALLIC QUASICRYSTAL WALK LEFT THE SET n={metallicIndex}");
        }
        if (MetallicQuasicrystal.Previous(n: metallicIndex, a: next.A, b: next.B) != metallicPoint) {
            throw new InvalidOperationException($"METALLIC QUASICRYSTAL PREVIOUS IS NOT THE INVERSE OF NEXT n={metallicIndex}");
        }
        if (isLong ? (((next.A - metallicPoint.A) != 1L) || ((next.B - metallicPoint.B) != metallicIndex)) : (((next.A - metallicPoint.A) != 0L) || ((next.B - metallicPoint.B) != 1L))) {
            throw new InvalidOperationException($"METALLIC QUASICRYSTAL STEP IS NOT DELTA OR DELTA-SQUARED n={metallicIndex}");
        }
        if (MetallicQuasicrystal.Position(n: metallicIndex, a: next.A, b: next.B) <= MetallicQuasicrystal.Position(n: metallicIndex, a: metallicPoint.A, b: metallicPoint.B)) {
            throw new InvalidOperationException($"METALLIC QUASICRYSTAL POSITIONS ARE NOT INCREASING n={metallicIndex}");
        }

        if (isLong) {
            ++metallicLong;
            metallicRun = (((step > 0) && metallicPrevLong) ? (metallicRun + 1) : 1);

            if (metallicRun >= (metallicIndex + 2)) {
                throw new InvalidOperationException($"METALLIC QUASICRYSTAL HAS A FORBIDDEN LONG RUN n={metallicIndex}");
            }
        } else {
            ++metallicShort;

            if ((step > 0) && !metallicPrevLong) {
                throw new InvalidOperationException($"METALLIC QUASICRYSTAL HAS THE FORBIDDEN FACTOR SS n={metallicIndex}");
            }

            metallicRun = 0;
        }

        metallicPrevLong = isLong;
        metallicPoint = next;
    }

    if (Math.Abs(((double)metallicLong / metallicShort) - ((double)MetallicQuasicrystal.InflationFactor(n: metallicIndex))) > 0.02) {
        throw new InvalidOperationException($"METALLIC QUASICRYSTAL DENSITY IS NOT DELTA n={metallicIndex}");
    }

    // Contains must equal the walked set exactly over a coordinate box the walk fully covers — no ghost members admitted.
    for (var boxA = 0L; (boxA <= 80L); ++boxA) {
        for (var boxB = 0L; (boxB <= 80L); ++boxB) {
            if (MetallicQuasicrystal.Contains(n: metallicIndex, a: boxA, b: boxB) != metallicVisited.Contains(item: (boxA, boxB))) {
                throw new InvalidOperationException($"METALLIC QUASICRYSTAL CONTAINS DISAGREES WITH THE WALK n={metallicIndex} ({boxA},{boxB})");
            }
        }
    }

    // Two independent implementations agree: the cut-and-project walk word is a factor of the streamed substitution word.
    var metallicStreamed = new bool[24000];
    QuadraticQuasicrystal.Word(p: metallicIndex, q: 1L, d: (((long)metallicIndex * metallicIndex) + 4L), r: 2L, tiles: metallicStreamed);

    if (!IsFactorOfWord(haystack: metallicStreamed, needle: metallicWalkWord.AsSpan(0, 1500))) {
        throw new InvalidOperationException($"METALLIC QUASICRYSTAL RANDOM ACCESS != STREAMED WORD n={metallicIndex}");
    }
}
Console.WriteLine("metallic quasicrystal: ring-coordinate chain n=1..6 (Contains==walk, Next/Previous inverse, delta/delta^2 steps, monotone, no SS/L^(n+2), density -> delta) == streamed word OK");

// Width-boundary regressions: membership must widen before arithmetic, traversal must reject an unrepresentable result,
// and a huge metallic index must remain cheap and representable when only its leading run is requested.
if (MetallicQuasicrystal.Contains(n: 1, a: long.MinValue, b: 0L)) {
    throw new InvalidOperationException("METALLIC QUASICRYSTAL LONG-MIN MEMBERSHIP WRAPPED");
}
ExpectOverflow(operation: () => MetallicQuasicrystal.Next(n: 1, a: long.MaxValue, b: long.MaxValue));
ExpectArgumentOutOfRange(parameterName: "value", operation: () => MetallicQuasicrystal.Position(n: 1, a: long.MaxValue, b: 0L));
if (MetallicQuasicrystal.InflationFactor(n: int.MaxValue) != FixedQ4816.FromInteger(value: int.MaxValue)) {
    throw new InvalidOperationException("METALLIC QUASICRYSTAL LARGE-INDEX FACTOR DID NOT ROUND CORRECTLY");
}
var metallicHugeIndexPrefix = new bool[16];
MetallicQuasicrystal.Word(n: int.MaxValue, tiles: metallicHugeIndexPrefix);
if (metallicHugeIndexPrefix.Contains(value: false)) {
    throw new InvalidOperationException("METALLIC QUASICRYSTAL LARGE-INDEX PREFIX IS WRONG");
}

// ---- ModularTransform + ContinuedFraction (the modular group beneath the three motions) ----
// The four canonical elements land in the three conjugacy classes; SL2(Z) has determinant one and adjugate inverse;
// the Mobius action on cusps is a group action agreeing with exact BigInteger rational arithmetic; Gauss reduction
// carries every positive-definite form into the fundamental domain by a determinant-one word verified against the
// independent contravariant form action; and quadratic surds expand to eventually periodic continued fractions, the
// golden and silver ratios coding the two shortest closed geodesics [1] and [2]. Oracles: the trace, BigInteger rational
// reduction, the substitution form action, and the convergent recurrence -- never the implementation's own branches.
if (ModularTransform.S.Classify() != ModularClass.Elliptic) {
    throw new InvalidOperationException("MODULAR S IS NOT ELLIPTIC");
}
if ((ModularTransform.S * ModularTransform.T).Classify() != ModularClass.Elliptic) {
    throw new InvalidOperationException("MODULAR S*T IS NOT ELLIPTIC");
}
if (ModularTransform.T.Classify() != ModularClass.Parabolic) {
    throw new InvalidOperationException("MODULAR T IS NOT PARABOLIC");
}
if (ModularTransform.Create(a: 2L, b: 1L, c: 1L, d: 1L).Classify() != ModularClass.Hyperbolic) {
    throw new InvalidOperationException("MODULAR [2,1,1,1] IS NOT HYPERBOLIC");
}
// S is order four and S*T order six in SL2(Z) (the elliptic orders); neither returns to the identity earlier.
var modularSpin = ModularTransform.Identity;
for (var power = 1; (power <= 4); ++power) {
    modularSpin = (modularSpin * ModularTransform.S);

    if ((modularSpin == ModularTransform.Identity) != (power == 4)) {
        throw new InvalidOperationException("MODULAR S IS NOT ORDER FOUR");
    }
}
var modularHex = ModularTransform.Identity;
for (var power = 1; (power <= 6); ++power) {
    modularHex = (modularHex * (ModularTransform.S * ModularTransform.T));

    if ((modularHex == ModularTransform.Identity) != (power == 6)) {
        throw new InvalidOperationException("MODULAR S*T IS NOT ORDER SIX");
    }
}
// Random elements of SL2(Z), built as words in S and T: determinant one, adjugate inverse, associativity, class boundary.
var modularRng = new Random(20260720);
var modularWords = new ModularTransform[512];
for (var index = 0; (index < modularWords.Length); ++index) {
    var word = ModularTransform.Identity;

    for (var step = 0; (step < 7); ++step) {
        word = ((modularRng.Next(2) == 0)
            ? (ModularTransform.S * word)
            : (ModularTransform.Create(a: 1L, b: modularRng.Next(-3, 4), c: 0L, d: 1L) * word));
    }

    modularWords[index] = word;

    if (Int128.One != (((Int128)word.A * word.D) - ((Int128)word.B * word.C))) {
        throw new InvalidOperationException("MODULAR WORD DETERMINANT IS NOT ONE");
    }
    if ((word * word.Inverse) != ModularTransform.Identity) {
        throw new InvalidOperationException("MODULAR INVERSE IS NOT THE ADJUGATE INVERSE");
    }

    var absoluteTrace = Int128.Abs(value: ((Int128)word.A + word.D));
    var expectedClass = ((absoluteTrace < 2)
        ? ModularClass.Elliptic
        : ((absoluteTrace == 2) ? ModularClass.Parabolic : ModularClass.Hyperbolic));

    if (word.Classify() != expectedClass) {
        throw new InvalidOperationException("MODULAR CLASSIFY DISAGREES WITH THE TRACE");
    }
}
for (var index = 0; (index < 200); ++index) {
    var x = modularWords[modularRng.Next(modularWords.Length)];
    var y = modularWords[modularRng.Next(modularWords.Length)];
    var z = modularWords[modularRng.Next(modularWords.Length)];

    if (((x * y) * z) != (x * (y * z))) {
        throw new InvalidOperationException("MODULAR COMPOSITION IS NOT ASSOCIATIVE");
    }
}
// Cusp action: agrees with exact BigInteger rational reduction, is a group action, and S acts as an involution on cusps.
for (var index = 0; (index < modularWords.Length); ++index) {
    var word = modularWords[index];

    for (var trial = 0; (trial < 6); ++trial) {
        var p = modularRng.Next(-40, 41);
        var q = modularRng.Next(0, 41);

        if ((p == 0) && (q == 0)) { q = 1; }

        if (word.Apply(numerator: p, denominator: q) != ModularCuspOracle(g: word, p: p, q: q)) {
            throw new InvalidOperationException("MODULAR CUSP ACTION DISAGREES WITH THE RATIONAL ORACLE");
        }

        var other = modularWords[modularRng.Next(modularWords.Length)];
        var composed = (word * other).Apply(numerator: p, denominator: q);
        var (innerP, innerQ) = other.Apply(numerator: p, denominator: q);

        if (composed != word.Apply(numerator: innerP, denominator: innerQ)) {
            throw new InvalidOperationException("MODULAR CUSP ACTION IS NOT A GROUP ACTION");
        }

        var (sP, sQ) = ModularTransform.S.Apply(numerator: p, denominator: q);

        if (ModularTransform.S.Apply(numerator: sP, denominator: sQ) != ModularCuspOracle(g: ModularTransform.Identity, p: p, q: q)) {
            throw new InvalidOperationException("MODULAR S IS NOT A CUSP INVOLUTION");
        }
    }
}
// Gauss reduction: reduced inequalities, discriminant preserved, the word carries the form (exact form action), idempotence,
// and the reduced root lands in the fundamental domain through the approximate FixedComplex seam.
var modularReductions = 0;
for (var reduceA = 1L; (reduceA <= 24L); ++reduceA) {
    for (var reduceB = -24L; (reduceB <= 24L); ++reduceB) {
        for (var reduceC = 1L; (reduceC <= 24L); ++reduceC) {
            if ((((Int128)reduceB * reduceB) - (((Int128)reduceA * reduceC) * 4)) >= Int128.Zero) { continue; }

            var reduction = ModularTransform.GaussReduce(a: reduceA, b: reduceB, c: reduceC);

            if (!((-reduction.A < reduction.B) && (reduction.B <= reduction.A) && (reduction.A <= reduction.C))) {
                throw new InvalidOperationException("MODULAR REDUCED FORM VIOLATES -A < B <= A <= C");
            }
            if (Int128.One != (((Int128)reduction.Transform.A * reduction.Transform.D) - ((Int128)reduction.Transform.B * reduction.Transform.C))) {
                throw new InvalidOperationException("MODULAR REDUCTION TRANSFORM IS NOT DETERMINANT ONE");
            }
            if ((((Int128)reduceB * reduceB) - (((Int128)reduceA * reduceC) * 4)) != (((Int128)reduction.B * reduction.B) - (((Int128)reduction.A * reduction.C) * 4))) {
                throw new InvalidOperationException("MODULAR REDUCTION DID NOT PRESERVE THE DISCRIMINANT");
            }
            if (ModularFormAction(a: reduceA, b: reduceB, c: reduceC, g: reduction.Transform.Inverse) != (reduction.A, reduction.B, reduction.C)) {
                throw new InvalidOperationException("MODULAR REDUCTION TRANSFORM DOES NOT CARRY THE FORM");
            }
            if (ModularTransform.GaussReduce(a: reduction.A, b: reduction.B, c: reduction.C).Transform != ModularTransform.Identity) {
                throw new InvalidOperationException("MODULAR REDUCTION IS NOT IDEMPOTENT");
            }

            // Approximate seam: the transform applied to the original root approximates the reduced root, which lies in F.
            if ((reduceA <= 8L) && (reduceB >= -8L) && (reduceB <= 8L) && (reduceC <= 8L)) {
                var sourceRoot = FormRoot(a: reduceA, b: reduceB, c: reduceC);
                var reducedRoot = FormRoot(a: reduction.A, b: reduction.B, c: reduction.C);
                var mapped = reduction.Transform.Apply(point: sourceRoot);
                var realError = Math.Abs((double)(mapped.Real - reducedRoot.Real));
                var imaginaryError = Math.Abs((double)(mapped.Imaginary - reducedRoot.Imaginary));

                if ((realError > 0.02) || (imaginaryError > 0.02)) {
                    throw new InvalidOperationException("MODULAR REDUCTION SEAM DID NOT MAP THE ROOT INTO THE FUNDAMENTAL DOMAIN");
                }
            }

            ++modularReductions;
        }
    }
}
// Continued fractions: the golden and silver periods, a table of surd expansions, and convergents that approach the value.
Span<long> continuedFractionTerms = stackalloc long[64];
(long P, long Q, long D, long R, int Start, long[] Period)[] continuedFractionCases = [
    (1L, 1L, 5L, 2L, 0, [1L]),                       // golden ratio (1 + sqrt 5) / 2
    (1L, 1L, 2L, 1L, 0, [2L]),                       // silver ratio 1 + sqrt 2
    (0L, 1L, 2L, 1L, 1, [2L]),                       // sqrt 2 = [1; (2)]
    (0L, 1L, 3L, 1L, 1, [1L, 2L]),                   // sqrt 3 = [1; (1, 2)]
    (0L, 1L, 7L, 1L, 1, [1L, 1L, 1L, 4L]),           // sqrt 7 = [2; (1, 1, 1, 4)]
    (0L, 1L, 13L, 1L, 1, [1L, 1L, 1L, 1L, 6L]),      // sqrt 13 = [3; (1, 1, 1, 1, 6)]
];
foreach (var continuedFractionCase in continuedFractionCases) {
    var written = ContinuedFraction.Expand(
        p: continuedFractionCase.P,
        q: continuedFractionCase.Q,
        d: continuedFractionCase.D,
        r: continuedFractionCase.R,
        terms: continuedFractionTerms,
        periodStart: out var expansionStart,
        periodLength: out var expansionLength
    );

    if ((expansionStart != continuedFractionCase.Start) || (expansionLength != continuedFractionCase.Period.Length)) {
        throw new InvalidOperationException("CONTINUED FRACTION PERIOD STRUCTURE IS WRONG");
    }

    for (var offset = 0; (offset < expansionLength); ++offset) {
        if (continuedFractionTerms[expansionStart + offset] != continuedFractionCase.Period[offset]) {
            throw new InvalidOperationException("CONTINUED FRACTION PERIOD BLOCK IS WRONG");
        }
    }

    // Independent convergence oracle: unfold head + several periods, run the convergent recurrence, approach the true value.
    var value = ((continuedFractionCase.P + (continuedFractionCase.Q * Math.Sqrt(continuedFractionCase.D))) / continuedFractionCase.R);
    double previousNumerator = 0.0, numerator = 1.0;
    double previousDenominator = 1.0, denominator = 0.0;

    for (var repeat = 0; (repeat < 24); ++repeat) {
        var term = ((repeat < expansionStart) ? continuedFractionTerms[repeat] : continuedFractionTerms[expansionStart + ((repeat - expansionStart) % expansionLength)]);
        (previousNumerator, numerator) = (numerator, ((term * numerator) + previousNumerator));
        (previousDenominator, denominator) = (denominator, ((term * denominator) + previousDenominator));
    }

    if (Math.Abs(((numerator / denominator) - value)) > 1e-9) {
        throw new InvalidOperationException("CONTINUED FRACTION CONVERGENTS DO NOT APPROACH THE VALUE");
    }
}

// Full-width exactness: algebraically identical representations must have identical expansions even when q²d exceeds
// Int128 and the canonical normalization additionally multiplies by r². Oversized partial quotients must fail explicitly
// rather than narrowing modulo 2^64.
var continuedFractionSmallEquivalent = new long[32];
var continuedFractionWideEquivalent = new long[32];
var smallEquivalentWritten = ContinuedFraction.Expand(
    p: 0L,
    q: 1L,
    d: 3L,
    r: 1L,
    terms: continuedFractionSmallEquivalent,
    periodStart: out var smallEquivalentStart,
    periodLength: out var smallEquivalentPeriod
);
var wideEquivalentWritten = ContinuedFraction.Expand(
    p: 0L,
    q: long.MaxValue,
    d: 3L,
    r: long.MaxValue,
    terms: continuedFractionWideEquivalent,
    periodStart: out var wideEquivalentStart,
    periodLength: out var wideEquivalentPeriod
);
if ((smallEquivalentWritten != wideEquivalentWritten) ||
    (smallEquivalentStart != wideEquivalentStart) ||
    (smallEquivalentPeriod != wideEquivalentPeriod) ||
    !continuedFractionSmallEquivalent.AsSpan(0, smallEquivalentWritten).SequenceEqual(continuedFractionWideEquivalent.AsSpan(0, wideEquivalentWritten))) {
    throw new InvalidOperationException("CONTINUED FRACTION FULL-WIDTH COMMON SCALE CHANGED THE EXPANSION");
}
const long continuedFractionScale = (long.MaxValue / 6L);
smallEquivalentWritten = ContinuedFraction.Expand(
    p: 0L,
    q: 5L,
    d: 3L,
    r: 6L,
    terms: continuedFractionSmallEquivalent,
    periodStart: out smallEquivalentStart,
    periodLength: out smallEquivalentPeriod
);
wideEquivalentWritten = ContinuedFraction.Expand(
    p: 0L,
    q: (5L * continuedFractionScale),
    d: 3L,
    r: (6L * continuedFractionScale),
    terms: continuedFractionWideEquivalent,
    periodStart: out wideEquivalentStart,
    periodLength: out wideEquivalentPeriod
);
if ((smallEquivalentWritten != wideEquivalentWritten) ||
    (smallEquivalentStart != wideEquivalentStart) ||
    (smallEquivalentPeriod != wideEquivalentPeriod) ||
    !continuedFractionSmallEquivalent.AsSpan(0, smallEquivalentWritten).SequenceEqual(continuedFractionWideEquivalent.AsSpan(0, wideEquivalentWritten))) {
    throw new InvalidOperationException("CONTINUED FRACTION FULL-WIDTH NORMALIZATION CHANGED THE EXPANSION");
}
ExpectOverflow(operation: () => ContinuedFraction.Expand(
    p: long.MaxValue,
    q: long.MaxValue,
    d: long.MaxValue,
    r: 1L,
    terms: new long[8],
    periodStart: out _,
    periodLength: out _
));
Console.WriteLine($"modular: 3 classes + orders {{4,6,inf}}, det-1 adjugate inverse, cusp group action, {modularReductions} Gauss reductions into F, CF periods [1]/[2] (golden/silver) + surd table OK");

// ---- QuadraticInflation + MetallicQuasicrystal (the inflation lens beneath the quasicrystal chains) ----
// The lens reads a quadratic irrational's CF period as a substitution matrix; its trace, determinant, and discriminant
// are exact conjugacy invariants, and the Perron eigenvalue is the chain's inflation factor. Golden and silver fall out
// as the smallest members, with discriminants 5 and 8 tying back to the golden (sqrt 5) and silver (sqrt 8) chains —
// read from the continued fraction, not fed in.
(long P, long Q, long D, long R, int Period, long Det, long Disc, double Factor)[] inflationCases = [
    (1L, 1L, 5L, 2L, 1, -1L, 5L, ((1.0 + Math.Sqrt(5.0)) / 2.0)),        // golden phi
    (1L, 1L, 2L, 1L, 1, -1L, 8L, (1.0 + Math.Sqrt(2.0))),               // silver 1 + sqrt 2
    (0L, 1L, 2L, 1L, 1, -1L, 8L, (1.0 + Math.Sqrt(2.0))),               // sqrt 2, same geodesic as silver
    (0L, 1L, 3L, 1L, 2, 1L, 12L, (2.0 + Math.Sqrt(3.0))),               // sqrt 3 (even period, det +1)
    (0L, 1L, 7L, 1L, 4, 1L, 252L, (8.0 + (3.0 * Math.Sqrt(7.0)))),      // sqrt 7 (even period, det +1)
    (0L, 1L, 13L, 1L, 5, -1L, 1300L, (18.0 + (5.0 * Math.Sqrt(13.0)))), // sqrt 13 (odd period, det -1)
];
foreach (var inflationCase in inflationCases) {
    var inflation = QuadraticInflation.FromQuadraticIrrational(p: inflationCase.P, q: inflationCase.Q, d: inflationCase.D, r: inflationCase.R);

    if ((inflation.PeriodLength != inflationCase.Period) || (inflation.Determinant != inflationCase.Det) || (inflation.Discriminant != inflationCase.Disc)) {
        throw new InvalidOperationException($"QUADRATIC INFLATION INVARIANTS WRONG d={inflationCase.D}");
    }

    // Determinant is exactly (-1)^period, the geodesic is a hyperbolic translation, and its axis is unimodular.
    if (inflation.Determinant != (((inflation.PeriodLength & 1) == 0) ? 1L : -1L)) {
        throw new InvalidOperationException($"QUADRATIC INFLATION DETERMINANT SIGN WRONG d={inflationCase.D}");
    }
    if ((inflation.GeodesicClass != ModularClass.Hyperbolic) || (inflation.Axis.Classify() != ModularClass.Hyperbolic)) {
        throw new InvalidOperationException($"QUADRATIC INFLATION GEODESIC NOT HYPERBOLIC d={inflationCase.D}");
    }
    if ((inflation.Axis * inflation.Axis.Inverse) != ModularTransform.Identity) {
        throw new InvalidOperationException($"QUADRATIC INFLATION AXIS NOT UNIMODULAR d={inflationCase.D}");
    }

    // The exact surd (trace + sqrt disc)/2 equals the double reference and is a root of the matrix characteristic
    // polynomial lambda^2 - trace*lambda + det; the fixed-point factor lands within the Q48.16 square-root seam.
    var referenceFactor = ((inflation.Trace + Math.Sqrt(inflation.Discriminant)) / 2.0);

    if (Math.Abs(referenceFactor - inflationCase.Factor) > 1e-9) {
        throw new InvalidOperationException($"QUADRATIC INFLATION FACTOR REFERENCE WRONG d={inflationCase.D}");
    }
    if (Math.Abs(((referenceFactor * referenceFactor) - (inflation.Trace * referenceFactor)) + inflation.Determinant) > 1e-6) {
        throw new InvalidOperationException($"QUADRATIC INFLATION FACTOR NOT A CHARACTERISTIC ROOT d={inflationCase.D}");
    }
    if (Math.Abs(((double)inflation.InflationFactor()) - referenceFactor) > 1e-3) {
        throw new InvalidOperationException($"QUADRATIC INFLATION FIXED-POINT FACTOR OFF d={inflationCase.D}");
    }
}

// Golden discriminant 5 and silver discriminant 8 are exactly the surds the golden and silver chains are built on, and
// the two share their smallest-trace geodesics with the metallic family.
if ((QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L) != QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L)) ||
    (QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L).Discriminant != 5L) ||
    (QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 2L, r: 1L).Discriminant != 8L)) {
    throw new InvalidOperationException("QUADRATIC INFLATION GOLDEN/SILVER DISCRIMINANTS WRONG");
}

// The general positive polynomial tail analyzer subsumes the analytic half of the metallic BDS family, but also handles
// regimes r>>p^2 where a crude one-step lower bound cannot establish contraction. Its returned witness is exactly
// recheckable, and the formal-series solver has no fixed order ceiling.
var goldenPolynomialTail = MetallicPolynomialContinuedFraction.Analyze(metallicIndex: BigInteger.One);
if ((goldenPolynomialTail.Slope != QuadraticSurd.Create(1, 1, 5, 2)) ||
    (goldenPolynomialTail.Offset != QuadraticSurd.Create(-5, -3, 5, 10)) ||
    !goldenPolynomialTail.VerifyIntervalCertificate()) {
    throw new InvalidOperationException("GENERAL POLYNOMIAL TAIL GOLDEN SPECIALIZATION WRONG");
}
var goldenAsymptotics = goldenPolynomialTail.AsymptoticCoefficients(termCount: 16);
if ((goldenAsymptotics.Count != 16) || (goldenAsymptotics[0] != goldenPolynomialTail.Offset)) {
    throw new InvalidOperationException("GENERAL POLYNOMIAL TAIL ASYMPTOTIC SERIES WRONG");
}
var widePolynomialTail = PolynomialContinuedFractionTail.Analyze(
    linear: 1,
    constant: 0,
    numeratorQuadratic: 100,
    numeratorLinear: -3,
    numeratorConstant: 7
);
if (!widePolynomialTail.VerifyIntervalCertificate() ||
    (widePolynomialTail.CertifiedInterval(widePolynomialTail.IntervalCertificate.Cutoff).Lower.Sign <= 0)) {
    throw new InvalidOperationException("GENERAL POLYNOMIAL TAIL WIDE-CONTRACTION CERTIFICATE WRONG");
}
ExpectArgumentOutOfRange(parameterName: "constant", operation: () =>
    PolynomialContinuedFractionTail.Analyze(1, -2, 1, 0, 0));
ExpectArgumentOutOfRange(parameterName: "numeratorConstant", operation: () =>
    PolynomialContinuedFractionTail.Analyze(1, 0, 1, 0, -2));
Console.WriteLine("polynomial continued-fraction tails: positive-family existence/uniqueness certificate, exact golden affine model, wide r>>p^2 trap, and 16 exact asymptotic terms OK");

// MetallicQuasicrystal unifies golden (n=1) and silver (n=2) as one substitution generator: the streamed word contains
// its own random-access walk word as a factor (same language, phase aside), long:short frequency approaches δₙ, and
// sigma(word) reproduces the word (the fixed-point identity).
var metallicGolden = new bool[8192];
var metallicSilver = new bool[8192];
MetallicQuasicrystal.Word(n: 1, tiles: metallicGolden);
MetallicQuasicrystal.Word(n: 2, tiles: metallicSilver);

var goldenFromOrigin = new bool[1500];
var silverFromOrigin = new bool[1500];
var goldenWalk = (A: 0L, B: 0L);
var silverWalk = (A: 0L, B: 0L);
for (var i = 0; (i < goldenFromOrigin.Length); ++i) {
    goldenFromOrigin[i] = MetallicQuasicrystal.StartsLongTile(n: 1, a: goldenWalk.A, b: goldenWalk.B);
    goldenWalk = MetallicQuasicrystal.Next(n: 1, a: goldenWalk.A, b: goldenWalk.B);
}
for (var i = 0; (i < silverFromOrigin.Length); ++i) {
    silverFromOrigin[i] = MetallicQuasicrystal.StartsLongTile(n: 2, a: silverWalk.A, b: silverWalk.B);
    silverWalk = MetallicQuasicrystal.Next(n: 2, a: silverWalk.A, b: silverWalk.B);
}

if (!IsFactorOfWord(haystack: metallicGolden, needle: goldenFromOrigin) ||
    !IsFactorOfWord(haystack: metallicSilver, needle: silverFromOrigin)) {
    throw new InvalidOperationException("METALLIC QUASICRYSTAL DOES NOT REPRODUCE THE GOLDEN/SILVER WORD");
}
if (IsFactorOfWord(haystack: metallicSilver, needle: goldenFromOrigin.AsSpan(0, 256))) {
    throw new InvalidOperationException("METALLIC QUASICRYSTAL SILVER GENERATOR MATCHED THE GOLDEN WORD");
}

for (var n = 1; (n <= 6); ++n) {
    var metallicWord = new bool[20000];
    MetallicQuasicrystal.Word(n: n, tiles: metallicWord);

    var longCount = 0;

    foreach (var isLong in metallicWord) { if (isLong) { ++longCount; } }

    if (Math.Abs(((double)longCount / (metallicWord.Length - longCount)) - ((double)MetallicQuasicrystal.InflationFactor(n: n))) > 0.02) {
        throw new InvalidOperationException($"METALLIC QUASICRYSTAL FREQUENCY OFF n={n}");
    }

    // sigma(word) == word: expand each tile (long -> long^n short, short -> long) and match the word in place.
    var cursor = 0;

    foreach (var isLong in metallicWord) {
        if (cursor >= (metallicWord.Length - (n + 1))) { break; }

        if (isLong) {
            for (var repeat = 0; (repeat < n); ++repeat) {
                if (!metallicWord[cursor++]) { throw new InvalidOperationException($"METALLIC QUASICRYSTAL NOT A FIXED POINT n={n}"); }
            }

            if (metallicWord[cursor++]) { throw new InvalidOperationException($"METALLIC QUASICRYSTAL NOT A FIXED POINT n={n}"); }
        } else if (!metallicWord[cursor++]) {
            throw new InvalidOperationException($"METALLIC QUASICRYSTAL NOT A FIXED POINT n={n}");
        }
    }
}
Console.WriteLine("inflation lens: golden/silver recovered (disc 5/8, hyperbolic unimodular axes), surd = characteristic root; metallic family reproduces both chains, frequency -> delta_n, sigma(word) == word OK");

// Is `needle` a contiguous factor of `haystack`? A phase-independent witness that two tiling words share a language.
static bool IsFactorOfWord(ReadOnlySpan<bool> haystack, ReadOnlySpan<bool> needle) {
    for (var start = 0; (start <= (haystack.Length - needle.Length)); ++start) {
        if (haystack.Slice(start, needle.Length).SequenceEqual(needle)) { return true; }
    }

    return false;
}

// ---- QuadraticQuasicrystal (the general chain: arbitrary CF period, not just metallic [n]) ----
// The general generator streams the tiling word for any quadratic irrational. Correctness with no reference impl: the
// word must be Sturmian — exactly k+1 distinct factors of every length k — and the tile lengths must satisfy the
// inflation identity λ·ℓ_long = A·ℓ_long + C·ℓ_short. Golden and silver are the single-term specializations.
var nonCanonicalSilver = QuadraticSurd.Create(2, 1, 8, 2);
var canonicalSilver = QuadraticSurd.Create(1, 1, 2, 1);
var equivalentSurds = new HashSet<QuadraticSurd> { nonCanonicalSilver, canonicalSilver };
var sortableEquivalentSurds = new List<QuadraticSurd> { nonCanonicalSilver, canonicalSilver };
sortableEquivalentSurds.Sort();
if ((nonCanonicalSilver != canonicalSilver) ||
    (nonCanonicalSilver.CompareTo(canonicalSilver) != 0) ||
    (nonCanonicalSilver.GetHashCode() != canonicalSilver.GetHashCode()) ||
    (equivalentSurds.Count != 1) ||
    ((nonCanonicalSilver + canonicalSilver) != (QuadraticSurd.Rational(2) * canonicalSilver))) {
    throw new InvalidOperationException("QUADRATIC SURD SQUARE-EQUIVALENT REPRESENTATIONS DISAGREE");
}
var silverIndex = QuadraticQuasicrystal.Compile(1, 1, 2, 1);
if ((silverIndex.ExactLongTileLength != canonicalSilver) ||
    (silverIndex.PositionAt(4096) !=
        (QuadraticSurd.Rational(4096 - silverIndex.CountLongTiles(4096)) +
            (QuadraticSurd.Rational(silverIndex.CountLongTiles(4096)) * canonicalSilver)))) {
    throw new InvalidOperationException("QUADRATIC QUASICRYSTAL INDEPENDENT SURD IDENTITY WRONG");
}
(long P, long Q, long D, long R)[] quasicrystalCases = [
    (1L, 1L, 5L, 2L), (1L, 1L, 2L, 1L), (0L, 1L, 2L, 1L), (0L, 1L, 3L, 1L), (0L, 1L, 7L, 1L), (0L, 1L, 13L, 1L), (0L, 1L, 23L, 1L),
];
var quasicrystalWord = new bool[200_000];
foreach (var quasicrystalCase in quasicrystalCases) {
    QuadraticQuasicrystal.Word(p: quasicrystalCase.P, q: quasicrystalCase.Q, d: quasicrystalCase.D, r: quasicrystalCase.R, tiles: quasicrystalWord);
    var quasicrystalIndex = QuadraticQuasicrystal.Compile(
        p: quasicrystalCase.P,
        q: quasicrystalCase.Q,
        d: quasicrystalCase.D,
        r: quasicrystalCase.R
    );
    var streamedLongCount = BigInteger.Zero;
    var exactPosition = QuadraticSurd.Zero;
    for (var index = 0; index < 4096; ++index) {
        if ((quasicrystalIndex.TileAt(index) != quasicrystalWord[index]) ||
            (quasicrystalIndex.CountLongTiles(index) != streamedLongCount) ||
            (quasicrystalIndex.PositionAt(index) != exactPosition)) {
            throw new InvalidOperationException($"QUADRATIC QUASICRYSTAL RANDOM ACCESS WRONG d={quasicrystalCase.D} index={index}");
        }
        if (quasicrystalWord[index]) {
            ++streamedLongCount;
            exactPosition += quasicrystalIndex.ExactLongTileLength;
        } else {
            exactPosition += QuadraticSurd.One;
        }
    }
    var remoteIndex = (BigInteger.One << 512) + 12345;
    var remoteLongs = quasicrystalIndex.CountLongTiles(remoteIndex);
    var remoteAdvance = quasicrystalIndex.CountLongTiles(remoteIndex + 1) - remoteLongs;
    if (remoteAdvance != (quasicrystalIndex.TileAt(remoteIndex) ? BigInteger.One : BigInteger.Zero)) {
        throw new InvalidOperationException($"QUADRATIC QUASICRYSTAL REMOTE PREFIX IDENTITY WRONG d={quasicrystalCase.D}");
    }

    for (var k = 1; (k <= 24); ++k) {
        if (WordComplexity(word: quasicrystalWord, k: k) != (k + 1)) {
            throw new InvalidOperationException($"QUADRATIC QUASICRYSTAL NOT STURMIAN d={quasicrystalCase.D} k={k}");
        }
    }

    // The tile lengths are the left Perron eigenvector: λ·ℓ_long must equal the length of σ(long) = A longs plus C shorts.
    var quasicrystalInflation = QuadraticInflation.FromQuadraticIrrational(p: quasicrystalCase.P, q: quasicrystalCase.Q, d: quasicrystalCase.D, r: quasicrystalCase.R);
    var quasicrystalLambda = ((quasicrystalInflation.Trace + Math.Sqrt(quasicrystalInflation.Discriminant)) / 2.0);
    var quasicrystalLongLength = (quasicrystalInflation.C / (quasicrystalLambda - quasicrystalInflation.A));

    if (Math.Abs((quasicrystalLambda * quasicrystalLongLength) - ((quasicrystalInflation.A * quasicrystalLongLength) + quasicrystalInflation.C)) > 1e-9) {
        throw new InvalidOperationException($"QUADRATIC QUASICRYSTAL TILE LENGTH IDENTITY WRONG d={quasicrystalCase.D}");
    }
    if (Math.Abs(((double)QuadraticQuasicrystal.LongTileLength(p: quasicrystalCase.P, q: quasicrystalCase.Q, d: quasicrystalCase.D, r: quasicrystalCase.R)) - quasicrystalLongLength) > 1e-3) {
        throw new InvalidOperationException($"QUADRATIC QUASICRYSTAL FIXED-POINT TILE LENGTH OFF d={quasicrystalCase.D}");
    }
}

// The general generator reproduces the hand-coded golden word, and the complexity oracle has teeth (a periodic word fails).
var generalGoldenWord = new bool[8192];
QuadraticQuasicrystal.Word(p: 1L, q: 1L, d: 5L, r: 2L, tiles: generalGoldenWord);
if (!IsFactorOfWord(haystack: generalGoldenWord, needle: goldenFromOrigin)) {
    throw new InvalidOperationException("QUADRATIC QUASICRYSTAL DOES NOT REPRODUCE THE GOLDEN WORD");
}
for (var i = 0; (i < quasicrystalWord.Length); ++i) { quasicrystalWord[i] = ((i % 3) == 0); }
if (WordComplexity(word: quasicrystalWord, k: 10) == 11) {
    throw new InvalidOperationException("WORD COMPLEXITY ORACLE HAS NO TEETH");
}
Console.WriteLine("quadratic quasicrystal: Sturmian p(k)=k+1 across 7 periods, exact 2^512 random access/counts, tile-length inflation identity, reproduces golden, oracle has teeth OK");

// Contract and scale regressions outside the ordinary small-period table. Empty outputs still validate; a period longer
// than the former 128-term scratch buffer streams correctly; and a huge one-term quotient neither allocates its complete
// image nor loses its representable Perron factor/tile length to Q48.16 cancellation.
ExpectArgumentOutOfRange(parameterName: "d", operation: () => QuadraticQuasicrystal.Word(p: 0L, q: 1L, d: 4L, r: 1L, tiles: []));
var quadraticLongPeriodWord = new bool[512];
QuadraticQuasicrystal.Word(p: 0L, q: 1L, d: 9949L, r: 1L, tiles: quadraticLongPeriodWord); // period length 217
var quadraticLongPeriodIndex = QuadraticQuasicrystal.Compile(p: 0L, q: 1L, d: 9949L, r: 1L);
if (quadraticLongPeriodIndex.PeriodLength != 217) {
    throw new InvalidOperationException("QUADRATIC QUASICRYSTAL LONG-PERIOD INDEX LOST THE PERIOD");
}
for (var index = 0; index < quadraticLongPeriodWord.Length; ++index) {
    if (quadraticLongPeriodIndex.TileAt(index) != quadraticLongPeriodWord[index]) {
        throw new InvalidOperationException($"QUADRATIC QUASICRYSTAL LONG-PERIOD RANDOM ACCESS WRONG index={index}");
    }
}
for (var k = 1; (k <= 12); ++k) {
    if (WordComplexity(word: quadraticLongPeriodWord, k: k) != (k + 1)) {
        throw new InvalidOperationException($"QUADRATIC QUASICRYSTAL LONG-PERIOD WORD NOT STURMIAN k={k}");
    }
}
ExpectOverflow(operation: () => QuadraticInflation.FromQuadraticIrrational(p: 0L, q: 1L, d: 9949L, r: 1L));
const long quadraticLargeQuotient = 3_000_000_000L;
const long quadraticLargeDiscriminant = 9_000_000_000_000_000_004L;
var quadraticLargePrefix = new bool[16];
QuadraticQuasicrystal.Word(p: quadraticLargeQuotient, q: 1L, d: quadraticLargeDiscriminant, r: 2L, tiles: quadraticLargePrefix);
if (quadraticLargePrefix.Contains(value: false)) {
    throw new InvalidOperationException("QUADRATIC QUASICRYSTAL LARGE-QUOTIENT PREFIX IS WRONG");
}
if (Math.Abs(((double)QuadraticQuasicrystal.LongTileLength(p: quadraticLargeQuotient, q: 1L, d: quadraticLargeDiscriminant, r: 2L)) - quadraticLargeQuotient) > 0.001) {
    throw new InvalidOperationException("QUADRATIC QUASICRYSTAL LARGE-QUOTIENT TILE LENGTH LOST PRECISION");
}
var quadraticOverflowTiles = Enumerable.Repeat(element: true, count: 50_000).ToArray();
ExpectOverflow(operation: () => QuadraticQuasicrystal.Positions(
    p: quadraticLargeQuotient,
    q: 1L,
    d: quadraticLargeDiscriminant,
    r: 2L,
    tiles: quadraticOverflowTiles,
    positions: new FixedQ4816[quadraticOverflowTiles.Length]
));

// The number of distinct length-k factors of a word: exactly k+1 for a Sturmian word, bounded for a periodic one.
static int WordComplexity(ReadOnlySpan<bool> word, int k) {
    var seen = new HashSet<ulong>();
    var mask = ((k == 64) ? ~0UL : ((1UL << k) - 1UL));
    var window = 0UL;

    for (var i = 0; (i < word.Length); ++i) {
        window = (((window << 1) | (word[i] ? 1UL : 0UL)) & mask);

        if (i >= (k - 1)) { seen.Add(item: window); }
    }

    return seen.Count;
}

static Puck.Maths.FixedComplex FormRoot(long a, long b, long c) {
    // The upper-half-plane root of the positive-definite form: (-b + i*sqrt(4ac - b^2)) / (2a). Reference double build.
    var twiceA = (2.0 * a);

    return new Puck.Maths.FixedComplex(
        Real: FixedQ4816.FromDouble(value: (-b / twiceA)),
        Imaginary: FixedQ4816.FromDouble(value: (Math.Sqrt((((4.0 * a) * c) - ((double)b * b))) / twiceA))
    );
}
static (long A, long B, long C) ModularFormAction(long a, long b, long c, ModularTransform g) {
    // The substitution action f(alpha*x + beta*y, gamma*x + delta*y): the contravariant right action that carries a root.
    var alpha = ((Int128)g.A);
    var beta = ((Int128)g.B);
    var gamma = ((Int128)g.C);
    var delta = ((Int128)g.D);
    var actedA = ((((Int128)a * alpha) * alpha) + (((Int128)b * alpha) * gamma) + (((Int128)c * gamma) * gamma));
    var actedB = ((((2 * (Int128)a) * alpha) * beta) + ((Int128)b * ((alpha * delta) + (beta * gamma))) + (((2 * (Int128)c) * gamma) * delta));
    var actedC = ((((Int128)a * beta) * beta) + (((Int128)b * beta) * delta) + (((Int128)c * delta) * delta));

    return (checked((long)actedA), checked((long)actedB), checked((long)actedC));
}
static (long Numerator, long Denominator) ModularCuspOracle(ModularTransform g, long p, long q) {
    var numerator = (((BigInteger)g.A * p) + ((BigInteger)g.B * q));
    var denominator = (((BigInteger)g.C * p) + ((BigInteger)g.D * q));

    if (denominator.IsZero) { return (1L, 0L); }
    if (numerator.IsZero) { return (0L, 1L); }

    var divisor = BigInteger.GreatestCommonDivisor(left: numerator, right: denominator);

    numerator /= divisor;
    denominator /= divisor;

    if (denominator.Sign < 0) {
        numerator = -numerator;
        denominator = -denominator;
    }

    return ((long)numerator, (long)denominator);
}

static long VectorNormLocal(long x, long y, long z) {
    // Mirror of the internal FixedQuaternion.VectorNorm: nearest integer sqrt of the exact raw Q32 product sum
    // (no rounded Q16 intermediate).
    var s = unchecked((ulong)(((x * x) + (y * y)) + (z * z)));
    var r = (ulong)Math.Sqrt(s);

    while ((r * r) > s) { --r; }
    while (((r + 1UL) * (r + 1UL)) <= s) { ++r; }
    if ((s - (r * r)) > r) { ++r; }

    return unchecked((long)r);
}
static (double X, double Y, double Z) ScLerpRef(FixedRigidTransform from, FixedRigidTransform to, double t, bool flip, bool blend, double px, double py, double pz) {
    // Double mirror of FixedRigidTransform.ScLerp, branch decisions passed in from the fixed side where ambiguous.
    var (frw, frx, fry, frz) = (((double)from.Value.Real.W), ((double)from.Value.Real.X), ((double)from.Value.Real.Y), ((double)from.Value.Real.Z));
    var (fdw, fdx, fdy, fdz) = (((double)from.Value.Dual.W), ((double)from.Value.Dual.X), ((double)from.Value.Dual.Y), ((double)from.Value.Dual.Z));
    var (trw, trx, trY, trz) = (((double)to.Value.Real.W), ((double)to.Value.Real.X), ((double)to.Value.Real.Y), ((double)to.Value.Real.Z));
    var (tdw, tdx, tdy, tdz) = (((double)to.Value.Dual.W), ((double)to.Value.Dual.X), ((double)to.Value.Dual.Y), ((double)to.Value.Dual.Z));

    if (flip) { (trw, trx, trY, trz, tdw, tdx, tdy, tdz) = (-trw, -trx, -trY, -trz, -tdw, -tdx, -tdy, -tdz); }

    // delta = conj(from) * to (dual-quaternion product with conjugated parts).
    (double W, double X, double Y, double Z) QMul((double W, double X, double Y, double Z) a, (double W, double X, double Y, double Z) b) =>
        (((((a.W * b.W) - (a.X * b.X)) - (a.Y * b.Y)) - (a.Z * b.Z)),
         ((((a.W * b.X) + (a.X * b.W)) + (a.Y * b.Z)) - (a.Z * b.Y)),
         ((((a.W * b.Y) - (a.X * b.Z)) + (a.Y * b.W)) + (a.Z * b.X)),
         ((((a.W * b.Z) + (a.X * b.Y)) - (a.Y * b.X)) + (a.Z * b.W)));

    var fRealC = (frw, -frx, -fry, -frz);
    var fDualC = (fdw, -fdx, -fdy, -fdz);
    var dReal = QMul(fRealC, (trw, trx, trY, trz));
    var dDual = ((QMul(fRealC, (tdw, tdx, tdy, tdz)).W + QMul(fDualC, (trw, trx, trY, trz)).W),
                 (QMul(fRealC, (tdw, tdx, tdy, tdz)).X + QMul(fDualC, (trw, trx, trY, trz)).X),
                 (QMul(fRealC, (tdw, tdx, tdy, tdz)).Y + QMul(fDualC, (trw, trx, trY, trz)).Y),
                 (QMul(fRealC, (tdw, tdx, tdy, tdz)).Z + QMul(fDualC, (trw, trx, trY, trz)).Z));
    var s = Math.Sqrt((((dReal.X * dReal.X) + (dReal.Y * dReal.Y)) + (dReal.Z * dReal.Z)));
    (double W, double X, double Y, double Z) pReal, pDual;

    if (blend) {
        // Normalized linear blend of the dual quaternions (the fixed side's small-rotation fallback).
        var bReal = ((frw + ((trw - frw) * t)), (frx + ((trx - frx) * t)), (fry + ((trY - fry) * t)), (frz + ((trz - frz) * t)));
        var bDual = ((fdw + ((tdw - fdw) * t)), (fdx + ((tdx - fdx) * t)), (fdy + ((tdy - fdy) * t)), (fdz + ((tdz - fdz) * t)));
        var bNorm = Math.Sqrt(((((bReal.Item1 * bReal.Item1) + (bReal.Item2 * bReal.Item2)) + (bReal.Item3 * bReal.Item3)) + (bReal.Item4 * bReal.Item4)));
        var blReal = ((bReal.Item1 / bNorm), (bReal.Item2 / bNorm), (bReal.Item3 / bNorm), (bReal.Item4 / bNorm));
        var blDual = ((bDual.Item1 / bNorm), (bDual.Item2 / bNorm), (bDual.Item3 / bNorm), (bDual.Item4 / bNorm));
        var bProj = ((((blReal.Item1 * blDual.Item1) + (blReal.Item2 * blDual.Item2)) + (blReal.Item3 * blDual.Item3)) + (blReal.Item4 * blDual.Item4));
        var boReal = (blReal.Item1, blReal.Item2, blReal.Item3, blReal.Item4);
        var boDual = ((blDual.Item1 - (blReal.Item1 * bProj)), (blDual.Item2 - (blReal.Item2 * bProj)), (blDual.Item3 - (blReal.Item3 * bProj)), (blDual.Item4 - (blReal.Item4 * bProj)));
        var btQ = QMul((boDual.Item1, boDual.Item2, boDual.Item3, boDual.Item4), (boReal.Item1, -boReal.Item2, -boReal.Item3, -boReal.Item4));

        var (btx, bty, btz) = ((2.0 * btQ.X), (2.0 * btQ.Y), (2.0 * btQ.Z));
        var (b1x, b1y, b1z) = ((((boReal.Item3 * pz) - (boReal.Item4 * py)) + (boReal.Item1 * px)), (((boReal.Item4 * px) - (boReal.Item2 * pz)) + (boReal.Item1 * py)), (((boReal.Item2 * py) - (boReal.Item3 * px)) + (boReal.Item1 * pz)));

        return (
            ((px + (2.0 * ((boReal.Item3 * b1z) - (boReal.Item4 * b1y)))) + btx),
            ((py + (2.0 * ((boReal.Item4 * b1x) - (boReal.Item2 * b1z)))) + bty),
            ((pz + (2.0 * ((boReal.Item2 * b1y) - (boReal.Item3 * b1x)))) + btz));
    }

    {
        var (ux, uy, uz) = ((dReal.X / s), (dReal.Y / s), (dReal.Z / s));
        var halfPitch = (-dDual.Item1 / s);

        var (mx2, my2, mz2) = (((dDual.Item2 - ((ux * halfPitch) * dReal.W)) / s), ((dDual.Item3 - ((uy * halfPitch) * dReal.W)) / s), ((dDual.Item4 - ((uz * halfPitch) * dReal.W)) / s));
        var half = (t * Math.Atan2(s, dReal.W));

        var (sh, ch) = Math.SinCos(half);
        var pitchScaled = (t * halfPitch);

        pReal = (ch, (ux * sh), (uy * sh), (uz * sh));
        pDual = ((-(pitchScaled * sh)), ((mx2 * sh) + ((ux * pitchScaled) * ch)), ((my2 * sh) + ((uy * pitchScaled) * ch)), ((mz2 * sh) + ((uz * pitchScaled) * ch)));
    }

    var rReal = QMul((frw, frx, fry, frz), pReal);
    var rDualA = QMul((frw, frx, fry, frz), pDual);
    var rDualB = QMul((fdw, fdx, fdy, fdz), pReal);
    var rDual = ((rDualA.W + rDualB.W), (rDualA.X + rDualB.X), (rDualA.Y + rDualB.Y), (rDualA.Z + rDualB.Z));
    // Normalize by the real norm, project the dual part, then transform the probe: rotate + translate.
    var norm = Math.Sqrt(((((rReal.W * rReal.W) + (rReal.X * rReal.X)) + (rReal.Y * rReal.Y)) + (rReal.Z * rReal.Z)));

    rReal = ((rReal.W / norm), (rReal.X / norm), (rReal.Y / norm), (rReal.Z / norm));
    rDual = ((rDual.Item1 / norm), (rDual.Item2 / norm), (rDual.Item3 / norm), (rDual.Item4 / norm));

    var proj = ((((rReal.W * rDual.Item1) + (rReal.X * rDual.Item2)) + (rReal.Y * rDual.Item3)) + (rReal.Z * rDual.Item4));

    rDual = ((rDual.Item1 - (rReal.W * proj)), (rDual.Item2 - (rReal.X * proj)), (rDual.Item3 - (rReal.Y * proj)), (rDual.Item4 - (rReal.Z * proj)));

    // translation = 2 * dual * conj(real); rotated point = sandwich via the rotate formula.
    var trQ = QMul((rDual.Item1, rDual.Item2, rDual.Item3, rDual.Item4), (rReal.W, -rReal.X, -rReal.Y, -rReal.Z));

    var (tx2, ty2, tz2) = ((2.0 * trQ.X), (2.0 * trQ.Y), (2.0 * trQ.Z));
    var (t1x, t1y, t1z) = ((((rReal.Y * pz) - (rReal.Z * py)) + (rReal.W * px)), (((rReal.Z * px) - (rReal.X * pz)) + (rReal.W * py)), (((rReal.X * py) - (rReal.Y * px)) + (rReal.W * pz)));

    return (
        ((px + (2.0 * ((rReal.Y * t1z) - (rReal.Z * t1y)))) + tx2),
        ((py + (2.0 * ((rReal.Z * t1x) - (rReal.X * t1z)))) + ty2),
        ((pz + (2.0 * ((rReal.X * t1y) - (rReal.Y * t1x)))) + tz2));
}
static (double X, double Y, double Z) RandomUnitAxis(Random rng) {
    while (true) {
        var x = ((rng.NextDouble() * 2.0) - 1.0);
        var y = ((rng.NextDouble() * 2.0) - 1.0);
        var z = ((rng.NextDouble() * 2.0) - 1.0);
        var norm = Math.Sqrt((((x * x) + (y * y)) + (z * z)));

        if ((norm > 0.1) && (norm <= 1.0)) { return ((x / norm), (y / norm), (z / norm)); }
    }
}
static (double W, double X, double Y, double Z) MulRef(FixedQuaternion a, FixedQuaternion b) {
    var (aw, ax, ay, az) = (((double)a.W), ((double)a.X), ((double)a.Y), ((double)a.Z));
    var (bw, bx, by, bz) = (((double)b.W), ((double)b.X), ((double)b.Y), ((double)b.Z));

    return (
        ((((aw * bw) - (ax * bx)) - (ay * by)) - (az * bz)),
        ((((aw * bx) + (ax * bw)) + (ay * bz)) - (az * by)),
        ((((aw * by) - (ax * bz)) + (ay * bw)) + (az * bx)),
        ((((aw * bz) + (ax * by)) - (ay * bx)) + (az * bw)));
}
static (double X, double Y, double Z) RotateRef(FixedQuaternion q, double vx, double vy, double vz) {
    var (w, x, y, z) = (((double)q.W), ((double)q.X), ((double)q.Y), ((double)q.Z));
    // t = u×v + w·v; v' = v + 2·u×t
    var (t1x, t1y, t1z) = ((((y * vz) - (z * vy)) + (w * vx)), (((z * vx) - (x * vz)) + (w * vy)), (((x * vy) - (y * vx)) + (w * vz)));

    return (
        (vx + (2.0 * ((y * t1z) - (z * t1y)))),
        (vy + (2.0 * ((z * t1x) - (x * t1z)))),
        (vz + (2.0 * ((x * t1y) - (y * t1x)))));
}
static (double W, double X, double Y, double Z) SlerpRef(FixedQuaternion a, FixedQuaternion b, double t, bool flip) {
    var (aw, ax, ay, az) = (((double)a.W), ((double)a.X), ((double)a.Y), ((double)a.Z));
    var (bw, bx, by, bz) = (((double)b.W), ((double)b.X), ((double)b.Y), ((double)b.Z));

    if (flip) { (bw, bx, by, bz) = (-bw, -bx, -by, -bz); }

    var dot = ((((ax * bx) + (ay * by)) + (az * bz)) + (aw * bw));

    double ww, wx, wy, wz;

    if (dot > (65503.0 / 65536.0)) {
        (ww, wx, wy, wz) = ((aw + ((bw - aw) * t)), (ax + ((bx - ax) * t)), (ay + ((by - ay) * t)), (az + ((bz - az) * t)));
    } else {
        var theta = Math.Atan2(Math.Sqrt((1.0 - (dot * dot))), dot);

        var (fa, fb) = ((Math.Sin(((1.0 - t) * theta)) / Math.Sin(theta)), (Math.Sin((t * theta)) / Math.Sin(theta)));

        (ww, wx, wy, wz) = (((aw * fa) + (bw * fb)), ((ax * fa) + (bx * fb)), ((ay * fa) + (by * fb)), ((az * fa) + (bz * fb)));
    }

    var norm = Math.Sqrt(((((ww * ww) + (wx * wx)) + (wy * wy)) + (wz * wz)));

    return ((ww / norm), (wx / norm), (wy / norm), (wz / norm));
}
static void CheckAlias(ulong[] weights, int draws, ulong seed) {
    var rng = Pcg32XshRr.Create(state: seed, stream: 21UL);
    var entries = new (int Element, ulong Weight)[weights.Length];

    for (var i = 0; (i < weights.Length); i++) { entries[i] = (i, weights[i]); }

    var table = AliasTable.Create<int>(entries: entries);
    var tableTwin = AliasTable.Create<int>(entries: entries);
    var counts = new long[weights.Length];
    var totalWeight = 0.0;

    foreach (var w in weights) { totalWeight += w; }

    for (var n = 0; (n < draws); n++) {
        var index = table.SampleIndex(generator: ref rng);

        if ((index < 0) || (index >= weights.Length)) { throw new InvalidOperationException($"ALIAS INDEX OUT OF DOMAIN: {index}"); }

        counts[index]++;
    }

    for (var i = 0; (i < weights.Length); i++) {
        var p = (weights[i] / totalWeight);
        var empirical = (counts[i] / ((double)draws));

        if ((weights[i] == 0UL) && (counts[i] != 0L)) { throw new InvalidOperationException($"ALIAS SAMPLED ZERO-WEIGHT ENTRY {i}"); }

        var sigma = Math.Sqrt(Math.Max(((p * (1.0 - p)) / draws), 1e-18));

        if (Math.Abs((empirical - p)) > Math.Max((8.0 * sigma), 2e-9)) {
            throw new InvalidOperationException($"ALIAS DISTRIBUTION GATE FAILED entry {i}: {empirical:E5} vs {p:E5}");
        }
    }

    // Construction determinism: rebuild from identical input, spot-check identical sampling.
    var probe1 = Pcg32XshRr.Create(state: 9UL, stream: 2UL);
    var probe2 = Pcg32XshRr.Create(state: 9UL, stream: 2UL);

    for (var n = 0; (n < 10_000); n++) {
        if (table.SampleIndex(generator: ref probe1) != tableTwin.SampleIndex(generator: ref probe2)) {
            throw new InvalidOperationException("ALIAS CONSTRUCTION NOT DETERMINISTIC");
        }
    }
}

// SinCos verification uses the oracle accuracy section below.

// ---- SinCos accuracy vs BigInteger-reduced double reference ----
Console.WriteLine();
Console.WriteLine("---- sincos accuracy ----");
var maxErrSmall = 0.0;
var maxErrSmallAt = 0L;
for (var raw = -411775L; (raw <= 411775L); raw++) { // dense sweep over [-2pi, 2pi]
    var e = SinCosError(raw, pi, pow10);

    if (e > maxErrSmall) { maxErrSmall = e; maxErrSmallAt = raw; }
}
Console.WriteLine($"dense [-2pi,2pi] sweep: max err = {maxErrSmall:F4} raw ULP (at raw={maxErrSmallAt})");
var maxErrMid = 0.0;
for (var n = 0; (n < 2_000_000); n++) {
    var raw = ((long)rng.Next() << 17) ^ rng.Next(); // up to ~2^48 raw (~4.3e9 rad)

    if ((rng.Next() & 1) == 0) { raw = -raw; }

    var e = SinCosError(raw, pi, pow10);

    if (e > maxErrMid) { maxErrMid = e; }
}
Console.WriteLine($"random |raw| < 2^48:    max err = {maxErrMid:F4} raw ULP");
var maxErrHuge = 0.0;
for (var n = 0; (n < 500_000); n++) {
    var raw = unchecked((long)(((ulong)(uint)rng.Next() << 32) | (uint)rng.Next())); // full 64-bit range

    var e = SinCosError(raw, pi, pow10);

    if (e > maxErrHuge) { maxErrHuge = e; }
}
Console.WriteLine($"random full-range raw:  max err = {maxErrHuge:F4} raw ULP");
if ((maxErrSmall > 0.75) || (maxErrMid > 1.0) || (maxErrHuge > 2.5)) {
    throw new InvalidOperationException("SINCOS ACCURACY GATE FAILED");
}
Console.WriteLine("accuracy gates passed (<=0.75 near, <=1.0 mid, <=2.5 full-range)");

// ---- benchmarks ----
Console.WriteLine();
Console.WriteLine("---- benchmarks (ns/op, throughput over 4096-element operand sets) ----");
var opsA = new long[4096];
var opsB = new long[4096];
var opsTypicalA = new long[4096];
var opsTypicalB = new long[4096];
var opsSqrtSmall = new long[4096];
var opsSqrtLarge = new long[4096];
for (var i = 0; (i < 4096); i++) {
    opsA[i] = RandomScaled(rng);
    opsB[i] = RandomScaled(rng);

    if (opsB[i] == 0L) { opsB[i] = 1L; }

    opsTypicalA[i] = rng.NextInt64(-(1L << 40), (1L << 40));
    opsTypicalB[i] = (rng.NextInt64(655L, 65_536_000L) * ((rng.Next(2) == 0) ? 1L : -1L));
    opsSqrtSmall[i] = rng.NextInt64(0L, (1L << 48));
    opsSqrtLarge[i] = rng.NextInt64((1L << 48), long.MaxValue);
}
var sink = 0L;
sink ^= Bench("fixed multiply       ", opsA, opsB, static (a, b) => (FixedQ4816.FromRawBits(value: a) * FixedQ4816.FromRawBits(value: b)).Value, 20_000);
sink ^= Bench("ufixed multiply      ", opsA, opsB, static (a, b) => {
    var product = UFixedQ4816.MultiplyUnchecked(
        x: UFixedQ4816.FromRawBits(value: unchecked((ulong)a)),
        y: UFixedQ4816.FromRawBits(value: unchecked((ulong)b)),
        remainder: out var remainder
    );

    return unchecked((long)(product.Value ^ remainder));
}, 20_000);
sink ^= Bench("fixed divide general ", opsA, opsB, static (a, b) => (FixedQ4816.FromRawBits(value: a) / FixedQ4816.FromRawBits(value: b)).Value, 3_000);
sink ^= Bench("fixed divide typical ", opsTypicalA, opsTypicalB, static (a, b) => (FixedQ4816.FromRawBits(value: a) / FixedQ4816.FromRawBits(value: b)).Value, 3_000);
sink ^= Bench("sqrt small           ", opsSqrtSmall, opsSqrtSmall, static (a, _) => FixedQ4816.Sqrt(value: FixedQ4816.FromRawBits(value: a)).Value, 3_000);
sink ^= Bench("sqrt large           ", opsSqrtLarge, opsSqrtLarge, static (a, _) => FixedQ4816.Sqrt(value: FixedQ4816.FromRawBits(value: a)).Value, 3_000);
sink ^= Bench("atan2                ", opsA, opsB, static (a, b) => FixedQ4816.Atan2(y: new(Value: a), x: new(Value: b)).Value, 5_000);
sink ^= Bench("sincos               ", opsA, opsB, static (a, _) => FixedQ4816.SinCos(angle: new(Value: a)).Sin.Value, 5_000);
var compiledMeasureBench = DiscreteMeasure.Rational(numerator: 4_004, denominator: 5).CompileInt64();
sink ^= Bench("measure64 rational  ", opsA, opsB, (a, _) => compiledMeasureBench.AmountAt(index: a), 5_000);
sink ^= Bench("measure64 quadratic ", opsA, opsB, (a, _) => compiledInverseGolden.AmountAt(index: a), 500);
var benchRng = Pcg32XshRr.Create(state: 1UL, stream: 1UL);
var pcgSink = 0UL;
var pcgTimer = Stopwatch.StartNew();
for (var n = 0; (n < 100_000_000); n++) { pcgSink ^= benchRng.NextUInt32(); }
pcgTimer.Stop();
Console.WriteLine($"pcg32 next           : {(pcgTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
pcgTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { pcgSink ^= benchRng.NextUInt32(minimum: 0U, maximum: 999U); }
pcgTimer.Stop();
Console.WriteLine($"pcg32 bounded        : {(pcgTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
sink ^= unchecked((long)pcgSink);
var logBenchValues = new long[4096];
for (var i = 0; (i < 4096); i++) { logBenchValues[i] = Math.Abs(RandomScaled(rng)) | 1L; }
sink ^= Bench("log2                 ", logBenchValues, logBenchValues, static (a, _) => FixedQ4816.Log2(value: FixedQ4816.FromRawBits(value: a)).Value, 5_000);
var gaussBench = Pcg32XshRr.Create(state: 1UL, stream: 2UL);
var gaussTimer = Stopwatch.StartNew();
var gaussSink = 0L;
for (var n = 0; (n < 10_000_000); n++) {
    var (a, b) = gaussBench.NextGaussianPair();

    gaussSink ^= a.Value ^ b.Value;
}
gaussTimer.Stop();
Console.WriteLine($"gaussian pair        : {(gaussTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/pair ({(gaussTimer.Elapsed.TotalNanoseconds / 20_000_000d),6:F2} ns/value)");
sink ^= gaussSink;

// Alias: small (3-entry, L1-resident) and large (65536-entry, cache-pressured packed columns).
var aliasSmallRng = Pcg32XshRr.Create(state: 11UL, stream: 3UL);
var aliasSmall = AliasTable.Create<int>(entries: [(0, 3UL), (1, 1UL), (2, 4UL),]);
var aliasTimer = Stopwatch.StartNew();
var aliasSink = 0L;
for (var n = 0; (n < 50_000_000); n++) { aliasSink ^= aliasSmall.SampleIndex(generator: ref aliasSmallRng); }
aliasTimer.Stop();
Console.WriteLine($"alias small (3)      : {(aliasTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/sample");
var largeEntries = new (int Element, ulong Weight)[65536];
for (var i = 0; (i < 65536); i++) {
    largeEntries[i] = (i, ((ulong)rng.NextInt64(1L, 1_000_000L)));
}
var aliasLarge = AliasTable.Create<int>(entries: largeEntries);
var aliasLargeRng = Pcg32XshRr.Create(state: 12UL, stream: 3UL);
aliasTimer.Restart();
for (var n = 0; (n < 20_000_000); n++) { aliasSink ^= aliasLarge.SampleIndex(generator: ref aliasLargeRng); }
aliasTimer.Stop();
Console.WriteLine($"alias large (64Ki)   : {(aliasTimer.Elapsed.TotalNanoseconds / 20_000_000d),8:F2} ns/sample");
sink ^= aliasSink;
var noiseBenchPositions = new FixedVector3[4096];
for (var i = 0; (i < 4096); i++) {
    noiseBenchPositions[i] = new(
        X: FixedQ4816.FromRawBits(value: RandomScaled(rng)),
        Y: FixedQ4816.FromRawBits(value: RandomScaled(rng)),
        Z: FixedQ4816.FromRawBits(value: RandomScaled(rng))
    );
}
var noiseTimer = Stopwatch.StartNew();
var noiseSink = 0L;
for (var r = 0; (r < 5_000); r++) {
    for (var i = 0; (i < 4096); i++) { noiseSink ^= FieldNoise.Sample(seed: 1UL, position: noiseBenchPositions[i]).Value; }
}
noiseTimer.Stop();
Console.WriteLine($"noise 1 octave       : {(noiseTimer.Elapsed.TotalNanoseconds / (5_000d * 4096d)),8:F2} ns/sample");
noiseTimer.Restart();
for (var r = 0; (r < 2_000); r++) {
    for (var i = 0; (i < 4096); i++) { noiseSink ^= FieldNoise.Sample(seed: 1UL, position: noiseBenchPositions[i], octaves: 4).Value; }
}
noiseTimer.Stop();
Console.WriteLine($"noise 4 octaves      : {(noiseTimer.Elapsed.TotalNanoseconds / (2_000d * 4096d)),8:F2} ns/sample");
noiseTimer.Restart();
for (var n = 0UL; (n < 200_000_000UL); n++) { noiseSink ^= LowDiscrepancy.R2(index: n).X.Value; }
noiseTimer.Stop();
Console.WriteLine($"r2 point             : {(noiseTimer.Elapsed.TotalNanoseconds / 200_000_000d),8:F2} ns/point");
sink ^= noiseSink;
var qa = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 0.6));
var qb = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 1.1));
var qv = new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.FromDouble(value: 0.5), Z: FixedQ4816.FromDouble(value: -0.25));
var quatTimer = Stopwatch.StartNew();
var quatSink = 0L;
for (var n = 0; (n < 20_000_000); n++) { quatSink ^= (qa * qb).W.Value; }
quatTimer.Stop();
Console.WriteLine($"quat multiply        : {(quatTimer.Elapsed.TotalNanoseconds / 20_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 20_000_000); n++) { quatSink ^= qa.Rotate(vector: qv).X.Value; }
quatTimer.Stop();
Console.WriteLine($"quat rotate          : {(quatTimer.Elapsed.TotalNanoseconds / 20_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= qa.Normalize().W.Value; }
quatTimer.Stop();
Console.WriteLine($"quat normalize unit  : {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 5_000_000); n++) { quatSink ^= extremeNormQ.Normalize().X.Value; }
quatTimer.Stop();
Console.WriteLine($"quat normalize wide  : {(quatTimer.Elapsed.TotalNanoseconds / 5_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 5_000_000); n++) { quatSink ^= FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), angle: FixedQ4816.FromRawBits(value: n)).W.Value; }
quatTimer.Stop();
Console.WriteLine($"quat from-axis-angle : {(quatTimer.Elapsed.TotalNanoseconds / 5_000_000d),8:F2} ns/op");
quatTimer.Restart();
var slerpT = FixedQ4816.FromDouble(value: 0.35);
for (var n = 0; (n < 2_000_000); n++) { quatSink ^= FixedQuaternion.Slerp(from: qa, to: qb, amount: slerpT).W.Value; }
quatTimer.Stop();
Console.WriteLine($"quat slerp           : {(quatTimer.Elapsed.TotalNanoseconds / 2_000_000d),8:F2} ns/op");
quatTimer.Restart();
var benchBivector = qa.Log();
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= FixedQuaternion.Exp(bivector: benchBivector).W.Value; }
quatTimer.Stop();
Console.WriteLine($"quat exp             : {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= qa.Log().X.Value; }
quatTimer.Stop();
Console.WriteLine($"quat log             : {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
quatTimer.Restart();
var fromToTarget = new FixedVector3(X: FixedQ4816.FromDouble(value: -0.25), Y: FixedQ4816.One, Z: FixedQ4816.FromDouble(value: 0.5));
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= FixedQuaternion.FromTo(from: qv, to: fromToTarget).W.Value; }
quatTimer.Stop();
Console.WriteLine($"quat fromto          : {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
var benchComplexA = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.6));
var benchComplexB = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 1.1));
var benchV2 = new FixedVector2(X: FixedQ4816.One, Y: FixedQ4816.FromDouble(value: 0.5));
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= (benchComplexA * benchComplexB).Real.Value; }
quatTimer.Stop();
Console.WriteLine($"complex multiply     : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= benchComplexA.Rotate(vector: benchV2).X.Value; }
quatTimer.Stop();
Console.WriteLine($"complex rotate       : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 20_000_000); n++) { quatSink ^= (benchComplexA / benchComplexB).Real.Value; }
quatTimer.Stop();
Console.WriteLine($"complex divide narrow: {(quatTimer.Elapsed.TotalNanoseconds / 20_000_000d),8:F2} ns/op");
var benchWideComplex = new FixedComplex(Real: FixedQ4816.MaxValue, Imaginary: FixedQ4816.FromRawBits(value: long.MinValue));
quatTimer.Restart();
for (var n = 0; (n < 5_000_000); n++) { quatSink ^= (benchWideComplex / benchWideComplex).Real.Value; }
quatTimer.Stop();
Console.WriteLine($"complex divide wide  : {(quatTimer.Elapsed.TotalNanoseconds / 5_000_000d),8:F2} ns/op");
var benchV2B = new FixedVector2(X: FixedQ4816.FromDouble(value: -0.75), Y: FixedQ4816.FromDouble(value: 1.25));
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= FixedVector2.Wedge(left: benchV2, right: benchV2B).Value; }
quatTimer.Stop();
Console.WriteLine($"vector2 wedge        : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 20_000_000); n++) { quatSink ^= FixedComplex.FromTo(from: benchV2, to: benchV2B).Real.Value; }
quatTimer.Stop();
Console.WriteLine($"complex fromto       : {(quatTimer.Elapsed.TotalNanoseconds / 20_000_000d),8:F2} ns/op");
var benchRigidA = FixedRigidTransform.FromRotationTranslation(rotation: qa, translation: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.FromInteger(value: 2L), Z: FixedQ4816.FromInteger(value: 3L)));
var benchRigidB = FixedRigidTransform.FromRotationTranslation(rotation: qb, translation: new FixedVector3(X: FixedQ4816.FromInteger(value: -1L), Y: FixedQ4816.Zero, Z: FixedQ4816.One));
quatTimer.Restart();
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= (benchRigidA * benchRigidB).Value.Real.W.Value; }
quatTimer.Stop();
Console.WriteLine($"rigid compose        : {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= benchRigidA.TransformPoint(point: qv).X.Value; }
quatTimer.Stop();
Console.WriteLine($"rigid transform point: {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 1_000_000); n++) { quatSink ^= FixedRigidTransform.ScLerp(from: benchRigidA, to: benchRigidB, amount: slerpT).Value.Real.W.Value; }
quatTimer.Stop();
Console.WriteLine($"rigid sclerp         : {(quatTimer.Elapsed.TotalNanoseconds / 1_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= benchRigidA.Log().Real.X.Value; }
quatTimer.Stop();
Console.WriteLine($"rigid log            : {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
quatTimer.Restart();
var (benchScrewReal, benchScrewDual) = benchRigidA.Log();
for (var n = 0; (n < 10_000_000); n++) { quatSink ^= FixedRigidTransform.Exp(real: benchScrewReal, dual: benchScrewDual).Value.Real.W.Value; }
quatTimer.Stop();
Console.WriteLine($"rigid exp            : {(quatTimer.Elapsed.TotalNanoseconds / 10_000_000d),8:F2} ns/op");
var benchDualA = FixedDual.Variable(value: FixedQ4816.FromDouble(value: 2.5));
var benchDualB = FixedDual.Constant(value: FixedQ4816.FromDouble(value: 1.75));
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= (benchDualA * benchDualB).Dual.Value; }
quatTimer.Stop();
Console.WriteLine($"dual multiply        : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
var benchCyclicV2 = new FixedVector2(X: FixedQ4816.One, Y: FixedQ4816.FromInteger(value: 3L));
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= CyclicRotation.Step(plane: (n & 3), tick: n); }
quatTimer.Stop();
Console.WriteLine($"cyclic step          : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= CyclicRotation.At(plane: (n & 3), tick: n).Real.Value; }
quatTimer.Stop();
Console.WriteLine($"cyclic at            : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 20_000_000); n++) { quatSink ^= CyclicRotation.Rotate(plane: (n & 3), tick: n, vector: benchCyclicV2).X.Value; }
quatTimer.Stop();
Console.WriteLine($"cyclic rotate        : {(quatTimer.Elapsed.TotalNanoseconds / 20_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= SymmetryLattice.Reflect(node: (n % 240), mirror: ((n * 7) % 240)); }
quatTimer.Stop();
Console.WriteLine($"lattice reflect      : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= SymmetryLattice.Cycle(node: (n % 240)); }
quatTimer.Stop();
Console.WriteLine($"lattice cycle        : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= SymmetryLattice.Project(node: (n % 240)).X.Value; }
quatTimer.Stop();
Console.WriteLine($"lattice project      : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= (long)Puck.Maths.HilbertCurve.Encode(order: 20, x: ((uint)n & 0xFFFFFU), y: (((uint)n * 2654435761U) & 0xFFFFFU)); }
quatTimer.Stop();
Console.WriteLine($"hilbert encode       : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= (long)Puck.Maths.HilbertCurve.Decode(order: 20, distance: ((ulong)n & 0x3FFFFFFFFUL)).X; }
quatTimer.Stop();
Console.WriteLine($"hilbert decode       : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
var benchHexA = new Puck.Maths.HexCoord(Q: 5, R: -3);
var benchHexB = new Puck.Maths.HexCoord(Q: 2, R: 4);
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= (benchHexA * benchHexB).Q + n; }
quatTimer.Stop();
Console.WriteLine($"hex product          : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= new Puck.Maths.HexCoord(Q: n, R: (n * 3)).Length; }
quatTimer.Stop();
Console.WriteLine($"hex length           : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= Puck.Maths.HexCoord.Direction(direction: n).Q; }
quatTimer.Stop();
Console.WriteLine($"hex direction        : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= (Puck.Maths.MetallicQuasicrystal.Contains(n: 1, a: n, b: (n / 2)) ? 1 : 0); }
quatTimer.Stop();
Console.WriteLine($"quasicrystal contains: {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= Puck.Maths.MetallicQuasicrystal.Next(n: 1, a: n, b: (n / 2)).A; }
quatTimer.Stop();
Console.WriteLine($"quasicrystal next    : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
var benchModularA = ModularTransform.Create(a: 2L, b: 1L, c: 1L, d: 1L);
var benchModularB = ModularTransform.Create(a: 1L, b: 1L, c: 0L, d: 1L);
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= (long)(benchModularA * benchModularB).A + n; }
quatTimer.Stop();
Console.WriteLine($"modular compose      : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 100_000_000); n++) { quatSink ^= (long)benchModularA.Classify(); }
quatTimer.Stop();
Console.WriteLine($"modular classify     : {(quatTimer.Elapsed.TotalNanoseconds / 100_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 50_000_000); n++) { quatSink ^= benchModularA.Apply(numerator: n, denominator: (n | 1)).Numerator; }
quatTimer.Stop();
Console.WriteLine($"modular cusp apply   : {(quatTimer.Elapsed.TotalNanoseconds / 50_000_000d),8:F2} ns/op");
quatTimer.Restart();
for (var n = 0; (n < 5_000_000); n++) { quatSink ^= ModularTransform.GaussReduce(a: (30L + (n & 15)), b: 1L, c: 1L).A; }
quatTimer.Stop();
Console.WriteLine($"modular gauss reduce : {(quatTimer.Elapsed.TotalNanoseconds / 5_000_000d),8:F2} ns/op");
sink ^= quatSink;
Console.WriteLine($"(sink {sink})");
Console.WriteLine();
Console.WriteLine("ALL CHECKS PASSED");

// ---- helpers ----
static void ExpectArgumentOutOfRange(string parameterName, Action operation) {
    try {
        operation();
    } catch (ArgumentOutOfRangeException exception) when (exception.ParamName == parameterName) {
        return;
    }

    throw new InvalidOperationException($"EXPECTED ARGUMENT-OUT-OF-RANGE FOR {parameterName}");
}
static void ExpectOverflow(Action operation) {
    try {
        operation();
    } catch (OverflowException) {
        return;
    }

    throw new InvalidOperationException("EXPECTED OVERFLOW");
}
static BigInteger RoundDiv(BigInteger numerator, BigInteger denominator) =>
    (((2 * numerator) + denominator) / (2 * denominator));
static TResult CreateCheckedGeneric<TResult, TValue>(TValue value)
    where TResult : INumberBase<TResult>
    where TValue : INumberBase<TValue> =>
    TResult.CreateChecked(value: value);
static bool[] SievePrimeOracle(int inclusiveMaximum) {
    var prime = new bool[(inclusiveMaximum + 1)];

    Array.Fill(array: prime, value: true, startIndex: 2, count: (prime.Length - 2));

    for (var candidate = 2; (((long)candidate * candidate) <= inclusiveMaximum); ++candidate) {
        if (!prime[candidate]) { continue; }

        for (var composite = (candidate * candidate); (composite <= inclusiveMaximum); composite += candidate) { prime[composite] = false; }
    }

    return prime;
}
static uint[] PrimeListOracle(int exclusiveMaximum) {
    var sieve = SievePrimeOracle(inclusiveMaximum: (exclusiveMaximum - 1));
    var primes = new List<uint>();

    for (var value = 2; (value < sieve.Length); ++value) {
        if (sieve[value]) { primes.Add(item: ((uint)value)); }
    }

    return [.. primes];
}
static bool IsPrimeOracle(uint value, ReadOnlySpan<uint> trialPrimes) {
    if (value < 2U) { return false; }

    foreach (var prime in trialPrimes) {
        if ((((ulong)prime) * prime) > value) { break; }
        if (0U == (value % prime)) { return (value == prime); }
    }

    return true;
}
static UInt128 ReverseBitsOracle(UInt128 value) {
    var reversed = UInt128.Zero;

    for (var bit = 0; (bit < 128); ++bit) {
        reversed = (reversed << 1) | (value & UInt128.One);
        value >>= 1;
    }

    return reversed;
}
static UInt128 BitwisePairOracle(ulong value, ulong other) {
    var paired = UInt128.Zero;

    for (var bit = 0; (bit < 64); ++bit) {
        paired |= ((UInt128)((value >> bit) & 1UL) << (bit << 1));
        paired |= ((UInt128)((other >> bit) & 1UL) << ((bit << 1) + 1));
    }

    return paired;
}
static long RoundProductSumOracle(BigInteger sum) {
    var negative = (sum.Sign < 0);
    var magnitude = BigInteger.Abs(value: sum);
    var truncated = BigInteger.DivRem(dividend: magnitude, divisor: (BigInteger.One << FixedQ4816.FractionBitCount), remainder: out var remainder);
    var half = (BigInteger.One << (FixedQ4816.FractionBitCount - 1));

    if ((remainder > half) || ((remainder == half) && !truncated.IsEven)) {
        ++truncated;
    }

    var signed = (negative ? -truncated : truncated);
    var wrapped = (signed & ((BigInteger.One << 64) - BigInteger.One));

    return ((wrapped >= (BigInteger.One << 63))
        ? (long)(wrapped - (BigInteger.One << 64))
        : (long)wrapped);
}
static long[] NormalizeOracle(long[] values) {
    var squaredSum = BigInteger.Zero;

    foreach (var value in values) {
        var magnitude = BigInteger.Abs(new BigInteger(value));

        squaredSum += (magnitude * magnitude);
    }

    var result = new long[values.Length];

    if (squaredSum.IsZero) { return result; }

    for (var i = 0; (i < values.Length); ++i) {
        var numerator = (BigInteger.Abs(new BigInteger(values[i])) << FixedQ4816.FractionBitCount);
        var numeratorSquared = (numerator * numerator);
        var low = 0L;
        var high = (FixedQ4816.One.Value + 1L);

        while ((low + 1L) < high) {
            var middle = ((low + high) >> 1);

            if (((BigInteger)middle * middle * squaredSum) <= numeratorSquared) {
                low = middle;
            } else {
                high = middle;
            }
        }

        var doubledNumeratorSquared = (4 * numeratorSquared);
        var midpoint = ((2 * (BigInteger)low) + BigInteger.One);
        var midpointSquared = (midpoint * midpoint * squaredSum);

        if ((doubledNumeratorSquared > midpointSquared) ||
            ((doubledNumeratorSquared == midpointSquared) && ((low & 1L) != 0L))) {
            ++low;
        }

        result[i] = (values[i] < 0L ? -low : low);
    }

    return result;
}
static (long Real, long Imaginary) ComplexDivisionOracle(long ar, long ai, long br, long bi) {
    var denominator = (((BigInteger)br * br) + ((BigInteger)bi * bi));
    var realNumerator = (((BigInteger)ar * br) + ((BigInteger)ai * bi));
    var imaginaryNumerator = (((BigInteger)ai * br) - ((BigInteger)ar * bi));

    return (RoundRatioQ16(numerator: realNumerator, denominator: denominator),
            RoundRatioQ16(numerator: imaginaryNumerator, denominator: denominator));
}
static long RoundRatioQ16(BigInteger numerator, BigInteger denominator) {
    var negative = (numerator.Sign < 0);
    var quotient = BigInteger.DivRem(dividend: (BigInteger.Abs(value: numerator) << FixedQ4816.FractionBitCount), divisor: denominator, remainder: out var remainder);
    var distanceToNext = (denominator - remainder);

    if ((remainder > distanceToNext) || ((remainder == distanceToNext) && !quotient.IsEven)) { ++quotient; }

    if (negative) { quotient = -quotient; }

    var wrapped = (quotient & ((BigInteger.One << 64) - BigInteger.One));

    return ((wrapped >= (BigInteger.One << 63))
        ? ((long)(wrapped - (BigInteger.One << 64)))
        : ((long)wrapped));
}
static BigInteger ISqrt(BigInteger n) {
    if (n < 2) { return n; }

    var x = (BigInteger.One << ((int)((n.GetBitLength() + 1) / 2)));

    while (true) {
        var y = ((x + (n / x)) >> 1);

        if (y >= x) { return x; }

        x = y;
    }
}
static long RandomScaled(Random rng) {
    var raw = unchecked((long)(((ulong)(uint)rng.Next() << 32) | (uint)rng.Next()));

    return (raw >> rng.Next(64));
}
static double SinCosError(long raw, BigInteger pi, BigInteger pow10) {
    var (sinFixed, cosFixed) = FixedQ4816.SinCos(angle: new(Value: raw));
    var (sinRaw, cosRaw) = (sinFixed.Value, cosFixed.Value);
    // reference: reduce with 100-digit pi, then double sin/cos of the residual
    var num = (new BigInteger(raw) * pow10);
    var den = ((65536L * 2) * pi);
    var q = BigInteger.Divide(num, den);
    var rem = (num - (q * den));

    if (rem.Sign < 0) { rem += den; }

    var theta = (((double)rem / (double)den) * (2.0 * Math.PI));

    var (refSin, refCos) = Math.SinCos(theta);
    var errS = Math.Abs((sinRaw - (refSin * 65536.0)));
    var errC = Math.Abs((cosRaw - (refCos * 65536.0)));

    return Math.Max(errS, errC);
}
static long Bench(string label, long[] a, long[] b, Func<long, long, long> op, int reps) {
    var sink = 0L;

    for (var i = 0; (i < a.Length); i++) { sink ^= op(a[i], b[i]); } // warmup

    var t0 = Stopwatch.StartNew();

    for (var r = 0; (r < reps); r++) {
        for (var i = 0; (i < a.Length); i++) {
            sink ^= op(a[i], b[i]);
        }
    }

    t0.Stop();

    var ops = (((double)reps) * a.Length);

    Console.WriteLine($"{label}: {(t0.Elapsed.TotalNanoseconds / ops),8:F2} ns/op");

    return sink;
}

// Verbatim transcription of pcg32_random_r from pcg-c-basic, used as an independent oracle.
internal struct PcgRef {
    public ulong State;
    public ulong Inc;

    public uint Next() {
        var old = State;

        State = unchecked(((old * 6364136223846793005UL) + Inc));

        var xorshifted = unchecked((uint)(((old >> 18) ^ old) >> 27));
        var rot = ((int)(old >> 59));

        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }
}
