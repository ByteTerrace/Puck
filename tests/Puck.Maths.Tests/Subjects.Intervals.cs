using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="FixedInterval"/> to the law suite.</summary>
internal static partial class Subjects {
    private static readonly FixedInterval UnitBox = new(
        lower: FixedQ4816.FromRawBits(value: -(1L << 16)),
        upper: FixedQ4816.FromRawBits(value: (1L << 16))
    );

    /// <summary>The interval sum, difference, negation, product, quotient, absolute value, square, root, minimum,
    /// maximum and norm are each EXACTLY the directed hull of the exact results, and hold every round-to-nearest point
    /// result at operands inside them.</summary>
    /// <param name="left">Four raws: two endpoints for each of two intervals.</param>
    /// <param name="right">Four raws: the magnitude fold and the fractions placing the sampled points.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedIntervalArithmeticIsTheDirectedHull(long[] left, long[] right) {
        var fold = FoldShift(bits: right[0]);
        var a = IntervalOf(first: (left[0] >> fold), second: (left[1] >> fold));
        var b = IntervalOf(first: (left[2] >> fold), second: (left[3] >> fold));
        var c = IntervalOf(first: (right[1] >> fold), second: (right[2] >> fold));
        // Hull is a covered subject, not a trusted input factory. Check it against the drawn raws before using its
        // endpoints in any oracle: a Hull that collapses every input to zero must not silently shrink this domain.
        if (Mismatch(name: "Hull of A", actual: a, expected: (Math.Min(val1: (left[0] >> fold), val2: (left[1] >> fold)), Math.Max(val1: (left[0] >> fold), val2: (left[1] >> fold)))) is { } hullA) { return hullA; }
        if (Mismatch(name: "Hull of B", actual: b, expected: (Math.Min(val1: (left[2] >> fold), val2: (left[3] >> fold)), Math.Max(val1: (left[2] >> fold), val2: (left[3] >> fold)))) is { } hullB) { return hullB; }
        if (Mismatch(name: "Hull of C", actual: c, expected: (Math.Min(val1: (right[1] >> fold), val2: (right[2] >> fold)), Math.Max(val1: (right[1] >> fold), val2: (right[2] >> fold)))) is { } hullC) { return hullC; }
        var aLower = a.Lower.Value;
        var aUpper = a.Upper.Value;
        var bLower = b.Lower.Value;
        var bUpper = b.Upper.Value;
        var name = $"A = {a}, B = {b}";
        BigInteger exactALower = aLower, exactAUpper = aUpper, exactBLower = bLower, exactBUpper = bUpper;

        if (Mismatch(name: $"A + B for {name}", actual: (a + b), expected: Oracles.ExactHull(lower: (exactALower + exactBLower), upper: (exactAUpper + exactBUpper))) is { } sum) { return sum; }
        if (Mismatch(name: $"−A for {name}", actual: (-a), expected: Oracles.ExactHull(lower: -exactAUpper, upper: -exactALower)) is { } negation) { return negation; }
        if (Mismatch(name: $"A − B for {name}", actual: (a - b), expected: Oracles.ExactHull(lower: (exactALower - exactBUpper), upper: (exactAUpper - exactBLower))) is { } difference) { return difference; }
        if (Mismatch(name: $"A × B for {name}", actual: (a * b), expected: Oracles.IntervalProduct(leftLower: aLower, leftUpper: aUpper, rightLower: bLower, rightUpper: bUpper)) is { } product) { return product; }
        if (Mismatch(name: $"A ÷ B for {name}", actual: (a / b), expected: Oracles.IntervalQuotient(leftLower: aLower, leftUpper: aUpper, rightLower: bLower, rightUpper: bUpper)) is { } quotient) { return quotient; }

        // |A| over the exact endpoints: the nearer magnitude below (zero when A straddles zero), the farther above.
        var absoluteLow = (((aLower <= 0L) && (aUpper >= 0L)) ? BigInteger.Zero : BigInteger.Min(left: BigInteger.Abs(value: exactALower), right: BigInteger.Abs(value: exactAUpper)));
        var absoluteHigh = BigInteger.Max(left: BigInteger.Abs(value: exactALower), right: BigInteger.Abs(value: exactAUpper));

        if (Mismatch(name: $"|A| for {name}", actual: FixedInterval.Abs(value: a), expected: Oracles.ExactHull(lower: absoluteLow, upper: absoluteHigh)) is { } magnitude) { return magnitude; }
        if (Mismatch(name: $"A² for {name}", actual: FixedInterval.Square(value: a), expected: ((absoluteHigh > long.MaxValue)
            ? null
            : Oracles.DirectedHull(denominator: (BigInteger.One << 16), lower: (absoluteLow * absoluteLow), upper: (absoluteHigh * absoluteHigh)))) is { } square) { return square; }
        if (Mismatch(name: $"√A for {name}", actual: FixedInterval.Sqrt(value: a), expected: Oracles.IntervalRoot(lower: aLower, upper: aUpper)) is { } root) { return root; }
        if (Mismatch(name: $"round(A) for {name}", actual: FixedInterval.Round(value: a), expected: Oracles.ExactHull(lower: (RoundRationalTiesToEvenOracle(raw: aLower) << 16), upper: (RoundRationalTiesToEvenOracle(raw: aUpper) << 16))) is { } round) { return round; }
        if (Mismatch(name: $"floor(A) for {name}", actual: FixedInterval.Floor(value: a), expected: Oracles.ExactHull(lower: (exactALower - (((exactALower % 65536) + 65536) % 65536)), upper: (exactAUpper - (((exactAUpper % 65536) + 65536) % 65536)))) is { } floor) { return floor; }
        if (Mismatch(name: $"clamp(A, B) for {name}", actual: FixedInterval.Clamp(value: a, minimum: b.Lower, maximum: b.Upper), expected: (Math.Clamp(max: bUpper, min: bLower, value: aLower), Math.Clamp(max: bUpper, min: bLower, value: aUpper))) is { } clamp) { return clamp; }
        if (Mismatch(name: $"min(A, B) for {name}", actual: FixedInterval.Min(first: a, second: b), expected: (Math.Min(val1: aLower, val2: bLower), Math.Min(val1: aUpper, val2: bUpper))) is { } minimum) { return minimum; }
        if (Mismatch(name: $"max(A, B) for {name}", actual: FixedInterval.Max(first: a, second: b), expected: (Math.Max(val1: aLower, val2: bLower), Math.Max(val1: aUpper, val2: bUpper))) is { } maximum) { return maximum; }
        if (Mismatch(name: $"|(A, B, C)| for {name}, C = {c}", actual: FixedInterval.Magnitude(x: a, y: b, z: c), expected: Oracles.IntervalMagnitude(sides: [(aLower, aUpper), (bLower, bUpper), (c.Lower.Value, c.Upper.Value)])) is { } norm) { return norm; }
        if (Mismatch(name: $"|(A, B)| for {name}", actual: FixedInterval.Magnitude(x: a, y: b), expected: Oracles.IntervalMagnitude(sides: [(aLower, aUpper), (bLower, bUpper)])) is { } planar) { return planar; }

        // The top absorbs: every operation given the unbounded interval answers it, whatever the other operand.
        (string Name, Func<FixedInterval, FixedInterval, FixedInterval> Operation)[] absorbing = [
            ("+", (x, y) => (x + y)), ("−", (x, y) => (x - y)), ("×", (x, y) => (x * y)), ("÷", (x, y) => (x / y)),
            ("min", FixedInterval.Min), ("max", FixedInterval.Max), ("union", FixedInterval.Union),
            ("norm", (x, y) => FixedInterval.Magnitude(x: x, y: y)), ("atan2", (x, y) => FixedInterval.Atan2(x: y, y: x)),
            ("neg", (x, _) => (-x)), ("abs", (x, _) => FixedInterval.Abs(value: x)), ("square", (x, _) => FixedInterval.Square(value: x)),
            ("sqrt", (x, _) => FixedInterval.Sqrt(value: x)), ("round", (x, _) => FixedInterval.Round(value: x)), ("floor", (x, _) => FixedInterval.Floor(value: x)),
            ("clamp", (x, _) => FixedInterval.Clamp(value: x, minimum: FixedQ4816.Zero, maximum: FixedQ4816.One)), ("sin", (x, _) => FixedInterval.Sin(angle: x)),
            ("cos", (x, _) => FixedInterval.Cos(angle: x)), ("asin", (x, _) => FixedInterval.Asin(value: x)), ("acos", (x, _) => FixedInterval.Acos(value: x)),
        ];

        foreach (var (operationName, operation) in absorbing) {
            if (!operation(FixedInterval.Entire, b).IsUnbounded) { return $"{operationName} of the unbounded interval and {b} is bounded"; }
        }

        foreach (var (operationName, operation) in absorbing[..9]) {
            if (!operation(a, FixedInterval.Entire).IsUnbounded) { return $"{operationName} of {a} and the unbounded interval is bounded"; }
        }

        // Round-to-nearest point results at operands inside the intervals, wherever the exact result stays inside the
        // carrier (a wrapped point result leaves every interval's guarantee, by the type's own contract).
        foreach (var fraction in ((long[])[0L, right[3], -1L])) {
            var x = PointInside(fraction: fraction, interval: a);
            var y = PointInside(fraction: unchecked((fraction * ((long)0x9E3779B97F4A7C15UL))), interval: b);
            var z = PointInside(fraction: unchecked(fraction ^ 0x5DEECE66DL), interval: c);
            var exactProduct = ((((BigInteger)x.Value) * y.Value) >> 16);

            if (Fits(value: (((BigInteger)x.Value) + y.Value)) && !(a + b).Contains(value: (x + y))) { return $"{x} + {y} lies outside A + B for {name}"; }
            if (Fits(value: (((BigInteger)x.Value) - y.Value)) && !(a - b).Contains(value: (x - y))) { return $"{x} − {y} lies outside A − B for {name}"; }
            if (Fits(value: (exactProduct + 1)) && Fits(value: (exactProduct - 1)) && !(a * b).Contains(value: (x * y))) { return $"{x} × {y} lies outside A × B for {name}"; }
            if ((x.Value != long.MinValue) && !FixedInterval.Abs(value: a).Contains(value: FixedQ4816.Abs(value: x))) { return $"|{x}| lies outside |A| for {name}"; }
            if (!FixedInterval.Sqrt(value: a).Contains(value: FixedQ4816.Sqrt(value: x))) { return $"√{x} lies outside √A for {name}"; }
            if (!FixedInterval.Min(first: a, second: b).Contains(value: FixedQ4816.Min(x: x, y: y))) { return $"min({x}, {y}) lies outside min(A, B) for {name}"; }

            if (
                !b.Contains(value: FixedQ4816.Zero) &&
                Fits(value: (((((BigInteger)x.Value) << 16) / y.Value) + 1)) &&
                Fits(value: (((((BigInteger)x.Value) << 16) / y.Value) - 1)) &&
                !(a / b).Contains(value: (x / y))
            ) { return $"{x} ÷ {y} lies outside A ÷ B for {name}"; }

            if (!FixedInterval.Magnitude(x: a, y: b, z: c).Contains(value: new FixedVector3(X: x, Y: y, Z: z).Length)) { return $"|({x}, {y}, {z})| lies outside the box's norm for {name}, C = {c}"; }
        }

        return null;
    }
    /// <summary>Shrinking any operand never widens a result: for every operation, the interval at operands inside the
    /// drawn ones lies inside the interval at the drawn ones.</summary>
    /// <param name="left">Four raws: two endpoints for each of two intervals.</param>
    /// <param name="right">Four raws: the magnitude fold and the fractions placing the inner intervals.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedIntervalIsInclusionIsotonic(long[] left, long[] right) {
        var fold = FoldShift(bits: right[0]);
        var a = IntervalOf(first: (left[0] >> fold), second: (left[1] >> fold));
        var b = IntervalOf(first: (left[2] >> fold), second: (left[3] >> fold));
        var innerA = IntervalOf(first: PointInside(fraction: right[1], interval: a).Value, second: PointInside(fraction: right[2], interval: a).Value);
        var innerB = IntervalOf(first: PointInside(fraction: right[3], interval: b).Value, second: PointInside(fraction: unchecked(right[1] ^ right[2]), interval: b).Value);
        (string Name, Func<FixedInterval, FixedInterval, FixedInterval> Operation)[] operations = [
            ("A + B", (x, y) => (x + y)),
            ("A − B", (x, y) => (x - y)),
            ("A × B", (x, y) => (x * y)),
            ("A ÷ B", (x, y) => (x / y)),
            ("|A|", (x, _) => FixedInterval.Abs(value: x)),
            ("A²", (x, _) => FixedInterval.Square(value: x)),
            ("√A", (x, _) => FixedInterval.Sqrt(value: x)),
            ("min(A, B)", FixedInterval.Min),
            ("max(A, B)", FixedInterval.Max),
            ("|(A, B)|", (x, y) => FixedInterval.Magnitude(x: x, y: y)),
            ("sin A", (x, _) => FixedInterval.Sin(angle: x)),
            ("cos A", (x, _) => FixedInterval.Cos(angle: x)),
            ("atan2(A, B)", (x, y) => FixedInterval.Atan2(x: y, y: x)),
            ("asin A", (x, _) => FixedInterval.Asin(value: x)),
            ("acos A", (x, _) => FixedInterval.Acos(value: x)),
        ];

        foreach (var (name, operation) in operations) {
            var outer = operation(a, b);
            var inner = operation(innerA, innerB);

            if (!outer.Contains(other: inner)) {
                return $"{name} widens as its operands shrink: {inner} at A = {innerA}, B = {innerB} is not inside {outer} at A = {a}, B = {b}";
            }
        }

        return null;
    }
    /// <summary>The interval sine and cosine hold the series enclosure at every sampled angle and every crest or trough
    /// inside, hold the shipped point value there, reach ±1 wherever an extremum provably lies inside, and sit within
    /// two raws of the exact extremes.</summary>
    /// <param name="left">Two raws: the interval's start and its width before folding.</param>
    /// <param name="right">Two raws: the width's fold and the fraction placing an interior sample.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedIntervalCircularEnclosesTheSeries(long[] left, long[] right) {
        // Starts across the carrier's turn reduction, widths from a raw to several turns.
        var start = (left[0] >> ((int)(unchecked((ulong)right[0]) % 40UL)));
        var width = (unchecked((long)(((ulong)left[1]) >> 1)) >> ((int)(40UL + (unchecked((((ulong)right[0]) >> 8)) % 24UL))));
        var end = ((long)Math.Min(val1: ((decimal)long.MaxValue), val2: (((decimal)start) + width)));
        var angle = IntervalOf(first: start, second: end);

        foreach (var cosine in ((bool[])[false, true])) {
            var actual = (cosine
                ? FixedInterval.Cos(angle: angle)
                : FixedInterval.Sin(angle: angle));
            var label = $"{(cosine ? "cos" : "sin")} {angle} = {actual}";

            // A carrier extreme is an unbounded end, which reaches every crest and trough.
            if (angle.IsUnbounded) {
                if (actual != UnitBox) { return $"{label}: an unbounded angle interval is not [−1, 1]"; }

                continue;
            }
            var (crest, trough) = Oracles.ProvablyReachesExtrema(lower: angle.Lower.Value, offset: (cosine ? 0 : 1), upper: angle.Upper.Value);
            var supremum = (crest ? (BigInteger.One << (16 + Oracles.GuardBitCount)) : (BigInteger.MinusOne << 100));
            var infimum = (trough ? -(BigInteger.One << (16 + Oracles.GuardBitCount)) : (BigInteger.One << 100));

            if (crest && (actual.Upper.Value != (1L << 16))) { return $"{label}: a crest lies inside but the upper endpoint is not one"; }
            if (trough && (actual.Lower.Value != -(1L << 16))) { return $"{label}: a trough lies inside but the lower endpoint is not minus one"; }

            long[] samples = [angle.Lower.Value, angle.Upper.Value, PointInside(fraction: right[1], interval: angle).Value];

            for (var index = 0; (index < samples.Length); ++index) {
                var sample = samples[index];
                var enclosure = Circular(enclosures: Oracles.EncloseSinCos(guardBitCount: Oracles.GuardBitCount, raw: sample), cosine: cosine);
                var point = (cosine
                    ? FixedQ4816.Cos(angle: FixedQ4816.FromRawBits(value: sample))
                    : FixedQ4816.Sin(angle: FixedQ4816.FromRawBits(value: sample)));

                if (EnclosureOutside(enclosure: enclosure, interval: actual) is { } outside) { return $"{label}: at {sample} {outside}"; }
                if (!actual.Contains(value: point)) { return $"{label}: the shipped value {point} at {sample} lies outside"; }

                // The endpoints carry the extremes wherever no crest or trough lies inside.
                if (index < 2) {
                    supremum = BigInteger.Max(left: supremum, right: enclosure.High);
                    infimum = BigInteger.Min(left: infimum, right: enclosure.Low);
                }
            }

            // Between its extrema a circular function is monotone, so with none inside, the endpoints carry the
            // exact extremes; two raws of excursion covers the endpoint kernel's raw and a crest counted from just
            // outside the interval.
            var slack = (BigInteger.One << (Oracles.GuardBitCount + 1));

            if ((((BigInteger)actual.Upper.Value) << Oracles.GuardBitCount) > (supremum + slack)) { return $"{label}: the upper endpoint exceeds the exact supremum by more than two raws"; }
            if ((((BigInteger)actual.Lower.Value) << Oracles.GuardBitCount) < (infimum - slack)) { return $"{label}: the lower endpoint falls below the exact infimum by more than two raws"; }
        }

        return null;
    }
    /// <summary>The interval arctangent holds the series enclosure and the shipped value at every corner and an
    /// interior point of the box, and away from the origin and the cut it sits within two raws of the exact corner
    /// extremes.</summary>
    /// <param name="left">Four raws: the ordinate interval's start and width, then the abscissa's.</param>
    /// <param name="right">Two raws: the width folds and the fraction placing an interior sample.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedIntervalAtan2EnclosesTheSeries(long[] left, long[] right) {
        var shift = ((int)(unchecked((ulong)right[0]) % 44UL));
        var y = IntervalOf(first: (left[0] >> 20), second: ((left[0] >> 20) + (left[1] >> (20 + shift))));
        var x = IntervalOf(first: (left[2] >> 20), second: ((left[2] >> 20) + (left[3] >> (20 + shift))));
        var actual = FixedInterval.Atan2(x: x, y: y);
        var label = $"atan2({y}, {x}) = {actual}";
        var low = (BigInteger.One << 100);
        var high = -(BigInteger.One << 100);

        foreach (var (ordinate, abscissa) in ((ReadOnlySpan<(long, long)>)[(y.Lower.Value, x.Lower.Value), (y.Lower.Value, x.Upper.Value), (y.Upper.Value, x.Lower.Value), (y.Upper.Value, x.Upper.Value), (PointInside(fraction: right[1], interval: y).Value, PointInside(fraction: unchecked((right[1] * 31L)), interval: x).Value)])) {
            var enclosure = Oracles.EncloseAtan2(guardBitCount: Oracles.GuardBitCount, xRaw: abscissa, yRaw: ordinate);
            var point = FixedQ4816.Atan2(y: FixedQ4816.FromRawBits(value: ordinate), x: FixedQ4816.FromRawBits(value: abscissa));

            if (EnclosureOutside(enclosure: enclosure, interval: actual) is { } outside) { return $"{label}: at ({ordinate}, {abscissa}) {outside}"; }
            if (!actual.Contains(value: point)) { return $"{label}: the shipped value {point} at ({ordinate}, {abscissa}) lies outside"; }

            low = BigInteger.Min(left: low, right: enclosure.Low);
            high = BigInteger.Max(left: high, right: enclosure.High);
        }

        var holdsOrigin = (y.Contains(value: FixedQ4816.Zero) && x.Contains(value: FixedQ4816.Zero));
        var crossesCut = ((x.Lower.Value < 0L) && (y.Lower.Value < 0L) && (y.Upper.Value >= 0L));

        if (!holdsOrigin && !crossesCut) {
            var slack = (BigInteger.One << (Oracles.GuardBitCount + 1));

            if ((((BigInteger)actual.Upper.Value) << Oracles.GuardBitCount) > (high + slack)) { return $"{label}: the upper endpoint exceeds the greatest corner angle by more than two raws"; }
            if ((((BigInteger)actual.Lower.Value) << Oracles.GuardBitCount) < (low - slack)) { return $"{label}: the lower endpoint falls below the least corner angle by more than two raws"; }
        }

        return null;
    }
    /// <summary>The interval arcsine and arccosine hold the series enclosure and sit within three raws of the exact
    /// endpoint angles, the operand clamped to <c>[−1, 1]</c>: a raw for the Q16 root bracket (the angle moves at most
    /// one raw per raw of root), one for the shipped arctangent, and the widening raw.</summary>
    /// <param name="left">Two raws: the operand interval's endpoints, folded to about ±1.2.</param>
    /// <param name="right">One raw: the fraction placing an interior sample.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedIntervalArcFunctionsEncloseTheSeries(long[] left, long[] right) {
        var value = IntervalOf(first: ((left[0] >> 46) + ((left[0] >> 50) >> 2)), second: ((left[1] >> 46) + ((left[1] >> 50) >> 2)));
        var clampedLower = Math.Clamp(value: value.Lower.Value, min: -(1L << 16), max: (1L << 16));
        var clampedUpper = Math.Clamp(value: value.Upper.Value, min: -(1L << 16), max: (1L << 16));
        var arcSine = FixedInterval.Asin(value: value);
        var arcCosine = FixedInterval.Acos(value: value);
        var interior = Math.Clamp(value: PointInside(fraction: right[0], interval: value).Value, min: -(1L << 16), max: (1L << 16));
        var slack = ((BigInteger.One << Oracles.GuardBitCount) * 3);

        foreach (var sample in ((long[])[clampedLower, clampedUpper, interior])) {
            if (EnclosureOutside(enclosure: Oracles.EncloseArcSine(guardBitCount: Oracles.GuardBitCount, raw: sample), interval: arcSine) is { } sine) { return $"asin {value} = {arcSine}: at {sample} {sine}"; }
            if (EnclosureOutside(enclosure: Oracles.EncloseArcCosine(guardBitCount: Oracles.GuardBitCount, raw: sample), interval: arcCosine) is { } cosine) { return $"acos {value} = {arcCosine}: at {sample} {cosine}"; }
        }

        // Both are monotone, so the clamped endpoints carry the exact extremes.
        if ((((BigInteger)arcSine.Upper.Value) << Oracles.GuardBitCount) > (Oracles.EncloseArcSine(guardBitCount: Oracles.GuardBitCount, raw: clampedUpper).High + slack)) { return $"asin {value} = {arcSine}: the upper endpoint is more than three raws above the exact angle"; }
        if ((((BigInteger)arcSine.Lower.Value) << Oracles.GuardBitCount) < (Oracles.EncloseArcSine(guardBitCount: Oracles.GuardBitCount, raw: clampedLower).Low - slack)) { return $"asin {value} = {arcSine}: the lower endpoint is more than three raws below the exact angle"; }
        if ((((BigInteger)arcCosine.Upper.Value) << Oracles.GuardBitCount) > (Oracles.EncloseArcCosine(guardBitCount: Oracles.GuardBitCount, raw: clampedLower).High + slack)) { return $"acos {value} = {arcCosine}: the upper endpoint is more than three raws above the exact angle"; }
        if ((((BigInteger)arcCosine.Lower.Value) << Oracles.GuardBitCount) < (Oracles.EncloseArcCosine(guardBitCount: Oracles.GuardBitCount, raw: clampedUpper).Low - slack)) { return $"acos {value} = {arcCosine}: the lower endpoint is more than three raws below the exact angle"; }

        return null;
    }
    /// <summary>The fixed points of the interval type: the circular functions at the exact crest and trough raws, the
    /// arctangent's whole circle at the origin and across the cut, the unbounded sentinels, and the refusal of a
    /// reversed interval.</summary>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedIntervalEdges() {
        var quarter = FixedQ4816.FromRawBits(value: 102944L);

        if (FixedInterval.Sin(angle: new(lower: FixedQ4816.FromRawBits(value: 102943L), upper: quarter)).Upper != FixedQ4816.One) { return "sin over the raws either side of π/2 does not reach one"; }
        if (FixedInterval.Cos(angle: FixedInterval.FromPoint(value: FixedQ4816.Zero)).Upper != FixedQ4816.One) { return "cos at zero does not reach one"; }
        if (FixedInterval.Sin(angle: new(lower: FixedQ4816.MinValue, upper: FixedQ4816.MaxValue)) != UnitBox) { return "sin over the whole carrier is not [−1, 1]"; }
        if (FixedInterval.Atan2(x: UnitBox, y: UnitBox).Upper.Value != 205888L) { return "atan2 over a box holding the origin is not the whole circle"; }
        if (FixedInterval.Atan2(y: UnitBox, x: new(lower: FixedQ4816.FromRawBits(value: -(2L << 16)), upper: FixedQ4816.FromRawBits(value: -(1L << 16)))).Lower.Value != -205888L) { return "atan2 across the cut is not the whole circle"; }
        if ((FixedInterval.Entire + FixedInterval.FromPoint(value: FixedQ4816.One)) != FixedInterval.Entire) { return "an unbounded sum became bounded"; }
        // The top absorbs even where the bounded rule would answer a point, so an overflow is never multiplied away.
        if ((FixedInterval.Entire * FixedInterval.FromPoint(value: FixedQ4816.Zero)) != FixedInterval.Entire) { return "an unbounded product with zero is bounded"; }
        if (!FixedInterval.Clamp(value: FixedInterval.Entire, minimum: FixedQ4816.Zero, maximum: FixedQ4816.One).IsUnbounded) { return "a clamp hid an overflow"; }
        if (!FixedInterval.Sin(angle: FixedInterval.Entire).IsUnbounded) { return "a sine hid an overflow"; }
        // Overflow is the top: one raw past either end of the carrier, a negated minimum, a squared maximum and a
        // rounding past the maximum all leave it.
        if (!(FixedInterval.FromPoint(value: FixedQ4816.MaxValue) + FixedInterval.FromPoint(value: FixedQ4816.Epsilon)).IsUnbounded) { return "the maximum plus a raw stayed bounded"; }
        if (!(FixedInterval.FromPoint(value: FixedQ4816.MinValue) - FixedInterval.FromPoint(value: FixedQ4816.Epsilon)).IsUnbounded) { return "the minimum less a raw stayed bounded"; }
        if (!(-FixedInterval.FromPoint(value: FixedQ4816.MinValue)).IsUnbounded) { return "the negated minimum stayed bounded"; }
        if (!FixedInterval.Square(value: FixedInterval.FromPoint(value: FixedQ4816.MaxValue)).IsUnbounded) { return "the squared maximum stayed bounded"; }
        if (!FixedInterval.Round(value: FixedInterval.FromPoint(value: FixedQ4816.MaxValue)).IsUnbounded) { return "the maximum rounded up stayed bounded"; }
        // The carrier's extremes are ordinary values: a bounded interval may reach them, and an exact sum that returns
        // inside the carrier stays bounded.
        if ((FixedInterval.FromPoint(value: FixedQ4816.MaxValue) - FixedInterval.FromPoint(value: FixedQ4816.One)).IsUnbounded) { return "the maximum less one became unbounded"; }
        if (!new FixedInterval(lower: FixedQ4816.MinValue, upper: FixedQ4816.MaxValue).Contains(other: FixedInterval.FromPoint(value: FixedQ4816.MaxValue))) { return "the bounded whole carrier does not hold its maximum"; }
        if (new FixedInterval(lower: FixedQ4816.MinValue, upper: FixedQ4816.MaxValue).Contains(other: FixedInterval.Entire)) { return "a bounded interval holds the unbounded one"; }
        if ((FixedInterval.FromPoint(value: FixedQ4816.One) / UnitBox) != FixedInterval.Entire) { return "a quotient by an interval holding zero is not the whole line"; }

        try {
            _ = new FixedInterval(lower: FixedQ4816.One, upper: FixedQ4816.Zero);
            return "a reversed interval was accepted";
        } catch (ArgumentException) {
        }

        return null;
    }

    private static BigInteger RoundRationalTiesToEvenOracle(long raw) =>
        Oracles.RoundRationalTiesToEven(denominator: (BigInteger.One << 16), numerator: raw);
    private static int FoldShift(long bits) =>
        (((int)(unchecked((ulong)bits) % 64UL)) & 0x3F) switch {
            var shift when (shift < 16) => 0,
            var shift => (shift - 16),
        };
    private static FixedInterval IntervalOf(long first, long second) =>
        FixedInterval.Hull(first: FixedQ4816.FromRawBits(value: first), second: FixedQ4816.FromRawBits(value: second));
    // The point at the fraction bits/2⁶⁴ of the way across the interval, endpoints included.
    private static FixedQ4816 PointInside(long fraction, FixedInterval interval) {
        var span = (((BigInteger)interval.Upper.Value) - interval.Lower.Value);

        return FixedQ4816.FromRawBits(value: ((long)(interval.Lower.Value + ((span * unchecked((ulong)fraction)) >> 64))));
    }
    private static bool Fits(BigInteger value) =>
        ((value >= long.MinValue) && (value <= long.MaxValue));
    // The subject against the expected hull: null expects the unbounded interval, the hull having left the carrier.
    private static string? Mismatch(string name, FixedInterval actual, (long Lower, long Upper)? expected) =>
        ((expected is { } hull)
            ? ((!actual.IsUnbounded && (actual.Lower.Value == hull.Lower) && (actual.Upper.Value == hull.Upper))
                ? null
                : $"{name} is {actual}, the directed hull is [{hull.Lower}, {hull.Upper}] in raws")
            : (actual.IsUnbounded
                ? null
                : $"{name} is {actual}, but its exact hull leaves the carrier, so it must be unbounded"));
    // The chosen enclosure, clipped to [−1, 1]: the series' own width can reach a guard unit past a value of exactly ±1,
    // where the true value cannot go.
    private static Oracles.Enclosure Circular((Oracles.Enclosure Sin, Oracles.Enclosure Cos) enclosures, bool cosine) {
        var unit = (BigInteger.One << (16 + Oracles.GuardBitCount));
        var chosen = (cosine
            ? enclosures.Cos
            : enclosures.Sin);

        return new(
            Low: BigInteger.Max(left: chosen.Low, right: -unit),
            High: BigInteger.Min(left: chosen.High, right: unit)
        );
    }
    private static string? EnclosureOutside(Oracles.Enclosure enclosure, FixedInterval interval) {
        if ((((BigInteger)interval.Lower.Value) << Oracles.GuardBitCount) > enclosure.Low) { return $"the exact value's enclosure [{enclosure.Low}, {enclosure.High}] reaches below the lower endpoint"; }
        if ((((BigInteger)interval.Upper.Value) << Oracles.GuardBitCount) < enclosure.High) { return $"the exact value's enclosure [{enclosure.Low}, {enclosure.High}] reaches above the upper endpoint"; }

        return null;
    }
}
