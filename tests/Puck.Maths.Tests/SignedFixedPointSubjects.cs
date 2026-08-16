using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private interface ISignedFixedPointAdapter<T>
        where T : struct, INumber<T>, ISignedNumber<T>, IMinMaxValue<T> {
        static abstract int FractionBitCount { get; }
        static abstract int IntegerBitCount { get; }
        static abstract int TotalBitCount { get; }
        // Epsilon and the record's primary constructor are the two pieces of the grid statement the generic-math
        // interfaces do not name. Construct must stay distinct from FromRaw: the grid law walks both construction
        // routes, and an adapter that forwarded them to one member would prove that member twice.
        static abstract T Epsilon { get; }

        static abstract T Construct(long value);
        static abstract T FromRaw(long value);
        static abstract T FromInteger(long value);
        static abstract T FromDouble(double value);
        static abstract double ToDouble(T value);
        static abstract long Raw(T value);
        static abstract T Ceiling(T value);
        static abstract T Floor(T value);
        static abstract T Fractional(T value);
        static abstract T Round(T value);
        static abstract T Truncate(T value);
        static abstract T Lerp(T from, T to, T amount);
    }
    private readonly struct Q4816Adapter : ISignedFixedPointAdapter<FixedQ4816> {
        public static int FractionBitCount => FixedQ4816.FractionBitCount;
        public static int IntegerBitCount => FixedQ4816.IntegerBitCount;
        public static int TotalBitCount => FixedQ4816.TotalBitCount;
        public static FixedQ4816 Epsilon => FixedQ4816.Epsilon;

        public static FixedQ4816 Construct(long value) => new(Value: value);
        public static FixedQ4816 FromRaw(long value) => FixedQ4816.FromRawBits(value: value);
        public static FixedQ4816 FromInteger(long value) => FixedQ4816.FromInteger(value: value);
        public static FixedQ4816 FromDouble(double value) => FixedQ4816.FromDouble(value: value);
        public static double ToDouble(FixedQ4816 value) => ((double)value);
        public static long Raw(FixedQ4816 value) => value.Value;
        public static FixedQ4816 Ceiling(FixedQ4816 value) => FixedQ4816.Ceiling(value: value);
        public static FixedQ4816 Floor(FixedQ4816 value) => FixedQ4816.Floor(value: value);
        public static FixedQ4816 Fractional(FixedQ4816 value) => FixedQ4816.Fractional(value: value);
        public static FixedQ4816 Round(FixedQ4816 value) => FixedQ4816.Round(value: value);
        public static FixedQ4816 Truncate(FixedQ4816 value) => FixedQ4816.Truncate(value: value);
        public static FixedQ4816 Lerp(FixedQ4816 from, FixedQ4816 to, FixedQ4816 amount) =>
            FixedQ4816.Lerp(amount: amount, from: from, to: to);
    }
    private readonly struct Q1648Adapter : ISignedFixedPointAdapter<FixedQ1648> {
        public static int FractionBitCount => FixedQ1648.FractionBitCount;
        public static int IntegerBitCount => FixedQ1648.IntegerBitCount;
        public static int TotalBitCount => FixedQ1648.TotalBitCount;
        public static FixedQ1648 Epsilon => FixedQ1648.Epsilon;

        public static FixedQ1648 Construct(long value) => new(Value: value);
        public static FixedQ1648 FromRaw(long value) => FixedQ1648.FromRawBits(value: value);
        public static FixedQ1648 FromInteger(long value) => FixedQ1648.FromInteger(value: value);
        public static FixedQ1648 FromDouble(double value) => FixedQ1648.FromDouble(value: value);
        public static double ToDouble(FixedQ1648 value) => ((double)value);
        public static long Raw(FixedQ1648 value) => value.Value;
        public static FixedQ1648 Ceiling(FixedQ1648 value) => FixedQ1648.Ceiling(value: value);
        public static FixedQ1648 Floor(FixedQ1648 value) => FixedQ1648.Floor(value: value);
        public static FixedQ1648 Fractional(FixedQ1648 value) => FixedQ1648.Fractional(value: value);
        public static FixedQ1648 Round(FixedQ1648 value) => FixedQ1648.Round(value: value);
        public static FixedQ1648 Truncate(FixedQ1648 value) => FixedQ1648.Truncate(value: value);
        public static FixedQ1648 Lerp(FixedQ1648 from, FixedQ1648 to, FixedQ1648 amount) =>
            FixedQ1648.Lerp(amount: amount, from: from, to: to);
    }
    private readonly struct Q3232Adapter : ISignedFixedPointAdapter<FixedQ3232> {
        public static int FractionBitCount => FixedQ3232.FractionBitCount;
        public static int IntegerBitCount => FixedQ3232.IntegerBitCount;
        public static int TotalBitCount => FixedQ3232.TotalBitCount;
        public static FixedQ3232 Epsilon => FixedQ3232.Epsilon;

        public static FixedQ3232 Construct(long value) => new(Value: value);
        public static FixedQ3232 FromRaw(long value) => FixedQ3232.FromRawBits(value: value);
        public static FixedQ3232 FromInteger(long value) => FixedQ3232.FromInteger(value: value);
        public static FixedQ3232 FromDouble(double value) => FixedQ3232.FromDouble(value: value);
        public static double ToDouble(FixedQ3232 value) => ((double)value);
        public static long Raw(FixedQ3232 value) => value.Value;
        public static FixedQ3232 Ceiling(FixedQ3232 value) => FixedQ3232.Ceiling(value: value);
        public static FixedQ3232 Floor(FixedQ3232 value) => FixedQ3232.Floor(value: value);
        public static FixedQ3232 Fractional(FixedQ3232 value) => FixedQ3232.Fractional(value: value);
        public static FixedQ3232 Round(FixedQ3232 value) => FixedQ3232.Round(value: value);
        public static FixedQ3232 Truncate(FixedQ3232 value) => FixedQ3232.Truncate(value: value);
        public static FixedQ3232 Lerp(FixedQ3232 from, FixedQ3232 to, FixedQ3232 amount) =>
            FixedQ3232.Lerp(amount: amount, from: from, to: to);
    }
    private static class SignedFixedPointSubject<T, TAdapter>
        where T : struct, INumber<T>, ISignedNumber<T>, IMinMaxValue<T>
        where TAdapter : struct, ISignedFixedPointAdapter<T> {
        private static readonly BigInteger OneRaw = (BigInteger.One << TAdapter.FractionBitCount);

        // The grid statement, shared across the widths and given its evidence by the caller: the expected fraction bit
        // count and every ladder are hand-derived literals the calling width states for itself, so this body holds the
        // shape of the claim and none of its expectations. No expectation is read back off the type being checked.
        public static string? GridAndConstruction(
            int fractionBitCount,
            ReadOnlySpan<long> rawLadder,
            ReadOnlySpan<long> integerLadder,
            ReadOnlySpan<long> refusedIntegers,
            ReadOnlySpan<(double Value, long Expected)> doubleLadder,
            bool includeDoubleProjection
        ) =>
            (DeclaredConstants(fractionBitCount: fractionBitCount) ??
            (ConstructionRoutes(rawLadder: rawLadder) ??
            (WholeNumberSeam(integerLadder: integerLadder, refusedIntegers: refusedIntegers) ??
            (DoubleSeam(doubleLadder: doubleLadder) ??
            (includeDoubleProjection ? DoubleProjection(rawLadder: rawLadder) : null)))));
        public static string? AdditiveOpsExact(long[] left, long[] right) {
            var rawA = left[0];
            var rawB = right[0];
            var a = TAdapter.FromRaw(value: rawA);
            var b = TAdapter.FromRaw(value: rawB);
            var exactA = new BigInteger(value: rawA);
            var exactB = new BigInteger(value: rawB);

            if (TAdapter.Raw(value: (a - b)) != Oracles.WrapToRaw(value: (exactA - exactB))) { return $"the difference of {rawA} and {rawB} is wrong"; }
            if (TAdapter.Raw(value: (-a)) != Oracles.WrapToRaw(value: -exactA)) { return $"the negation of {rawA} is wrong"; }
            if ((+a) != a) { return $"unary plus moved the raw {rawA}"; }
            if (-(-a) != a) { return $"negation is not an involution at {rawA}"; }

            var incremented = a;
            var decremented = a;

            ++incremented;
            --decremented;

            if (TAdapter.Raw(value: incremented) != Oracles.WrapToRaw(value: (exactA + OneRaw))) { return $"the increment of {rawA} is wrong"; }
            if (TAdapter.Raw(value: decremented) != Oracles.WrapToRaw(value: (exactA - OneRaw))) { return $"the decrement of {rawA} is wrong"; }
            if (incremented != (a + T.One)) { return $"the increment of {rawA} differs from adding one"; }
            if (decremented != (a - T.One)) { return $"the decrement of {rawA} differs from subtracting one"; }

            var restored = incremented;

            --restored;

            return ((restored != a)
                ? $"increment and decrement are not mutually inverse at {rawA}"
                : null);
        }
        public static string? CheckedOpsRefuse(long[] left, long[] right) {
            var rawA = left[0];
            var rawB = NonZeroDivisor(b: right[0]);
            var a = TAdapter.FromRaw(value: rawA);
            var b = TAdapter.FromRaw(value: rawB);
            var exactA = new BigInteger(value: rawA);
            var exactB = new BigInteger(value: rawB);

            if (CheckedOperator(checkedCall: () => checked((a + b)), exact: (exactA + exactB), name: "addition", rawA: rawA, rawB: rawB, uncheckedCall: () => (a + b)) is { } addition) { return addition; }
            if (CheckedOperator(checkedCall: () => checked((a - b)), exact: (exactA - exactB), name: "subtraction", rawA: rawA, rawB: rawB, uncheckedCall: () => (a - b)) is { } subtraction) { return subtraction; }
            if (CheckedOperator(checkedCall: () => checked(-a), exact: -exactA, name: "negation", rawA: rawA, rawB: rawB, uncheckedCall: () => (-a)) is { } negation) { return negation; }
            if (CheckedOperator(name: "increment", rawA: rawA, rawB: rawB, exact: (exactA + OneRaw), checkedCall: () => { var value = a; return checked(++value); }, uncheckedCall: () => { var value = a; return ++value; }) is { } increment) { return increment; }
            if (CheckedOperator(name: "decrement", rawA: rawA, rawB: rawB, exact: (exactA - OneRaw), checkedCall: () => { var value = a; return checked(--value); }, uncheckedCall: () => { var value = a; return --value; }) is { } decrement) { return decrement; }
            if (CheckedOperator(name: "multiplication", rawA: rawA, rawB: rawB, exact: Oracles.RoundRationalTiesToEven(denominator: OneRaw, numerator: (exactA * exactB)), checkedCall: () => checked((a * b)), uncheckedCall: () => (a * b)) is { } multiplication) { return multiplication; }
            if (CheckedOperator(name: "division", rawA: rawA, rawB: rawB, exact: Oracles.RoundRationalTiesToEven(denominator: exactB, numerator: (exactA * OneRaw)), checkedCall: () => checked((a / b)), uncheckedCall: () => (a / b)) is { } division) { return division; }

            if (!Throws<DivideByZeroException>(action: () => _ = (a / T.Zero))) { return "the unchecked division answered a zero divisor"; }
            if (!Throws<DivideByZeroException>(action: () => _ = checked((a / T.Zero)))) { return "the checked division answered a zero divisor"; }
            if (!Throws<OverflowException>(action: () => _ = checked((T.MinValue / T.NegativeOne)))) { return "the checked division answered MinValue over negative one"; }
            if ((T.MinValue / T.NegativeOne) != T.MinValue) { return "the unchecked division of MinValue over negative one did not wrap to MinValue"; }

            return null;
        }
        public static string? ModulusExact(long[] left, long[] right) {
            var rawA = left[0];
            var rawB = NonZeroDivisor(b: right[0]);
            var actual = TAdapter.Raw(value: (TAdapter.FromRaw(value: rawA) % TAdapter.FromRaw(value: rawB)));
            var exactA = new BigInteger(value: rawA);
            var exactB = new BigInteger(value: rawB);
            var expected = (exactA - (BigInteger.Divide(dividend: exactA, divisor: exactB) * exactB));

            if (actual != expected) { return $"the remainder of {rawA} over {rawB} is {actual}, expected {expected}"; }
            if (BigInteger.Abs(value: new BigInteger(value: actual)) >= BigInteger.Abs(value: exactB)) { return $"the remainder of {rawA} over {rawB} is not smaller than the divisor"; }
            if (!((exactA - actual) % exactB).IsZero) { return $"the division identity fails at ({rawA}, {rawB})"; }

            foreach (var divisor in ((ReadOnlySpan<long>)[1L, -1L])) {
                if ((TAdapter.FromRaw(value: rawA) % TAdapter.FromRaw(value: divisor)) != T.Zero) { return $"the remainder of {rawA} over the raw {divisor} is not zero"; }
            }

            if ((T.MinValue % TAdapter.FromRaw(value: -1L)) != T.Zero) { return "MinValue over the negative epsilon did not return zero"; }
            if (!Throws<DivideByZeroException>(action: () => _ = (TAdapter.FromRaw(value: rawA) % T.Zero))) { return "the remainder answered a zero divisor"; }

            return null;
        }
        public static string? OrderExact(long[] left, long[] right) {
            var rawA = left[0];
            var rawB = right[0];
            var a = TAdapter.FromRaw(value: rawA);
            var b = TAdapter.FromRaw(value: rawB);
            var exactA = new BigInteger(value: rawA);
            var exactB = new BigInteger(value: rawB);
            var order = BigInteger.Compare(left: exactA, right: exactB);

            if (Math.Sign(value: a.CompareTo(other: b)) != order) { return $"the comparison of {rawA} and {rawB} reports the wrong order"; }
            if ((a < b) != (order < 0)) { return $"the less-than operator disagrees at ({rawA}, {rawB})"; }
            if ((a <= b) != (order <= 0)) { return $"the less-or-equal operator disagrees at ({rawA}, {rawB})"; }
            if ((a > b) != (order > 0)) { return $"the greater-than operator disagrees at ({rawA}, {rawB})"; }
            if ((a >= b) != (order >= 0)) { return $"the greater-or-equal operator disagrees at ({rawA}, {rawB})"; }
            if (TAdapter.Raw(value: T.Max(x: a, y: b)) != BigInteger.Max(left: exactA, right: exactB)) { return $"the maximum of {rawA} and {rawB} is wrong"; }
            if (TAdapter.Raw(value: T.Min(x: a, y: b)) != BigInteger.Min(left: exactA, right: exactB)) { return $"the minimum of {rawA} and {rawB} is wrong"; }
            if (T.Sign(value: a) != exactA.Sign) { return $"the sign of {rawA} is wrong"; }
            if ((0 == order) && (T.Max(x: a, y: b) != T.Min(x: a, y: b))) { return $"the two selections disagree at the equal pair {rawA}"; }

            var bound = new BigInteger(value: left[1]);
            var lower = BigInteger.Min(left: exactB, right: bound);
            var upper = BigInteger.Max(left: exactB, right: bound);
            var clamped = T.Clamp(value: a, min: TAdapter.FromRaw(value: ((long)lower)), max: TAdapter.FromRaw(value: ((long)upper)));

            if (TAdapter.Raw(value: clamped) != BigInteger.Min(left: BigInteger.Max(left: exactA, right: lower), right: upper)) { return $"the clamp of {rawA} into [{lower}, {upper}] is wrong"; }
            if (T.Clamp(max: a, min: a, value: a) != a) { return $"the clamp is not the identity inside its own bounds at {rawA}"; }

            if (((IComparable)a).CompareTo(obj: null) != 1) { return "a null comparand does not sort first"; }
            if (((IComparable)a).CompareTo(obj: a) != 0) { return $"the boxed comparison of {rawA} against itself is not zero"; }
            if (!Throws<ArgumentException>(action: () => _ = ((IComparable)a).CompareTo(obj: "not a fixed-point value"), paramName: "obj")) { return "the boxed comparison accepted a foreign type"; }
            if (!Throws<ArgumentException>(action: () => _ = T.Clamp(value: a, min: T.One, max: T.Zero))) { return "the clamp accepted an inverted range"; }

            return null;
        }
        public static string? MagnitudeSelectionExact(long[] left, long[] right) {
            var rawA = left[0];
            var rawB = right[0];
            var a = TAdapter.FromRaw(value: rawA);
            var b = TAdapter.FromRaw(value: rawB);
            var exactA = new BigInteger(value: rawA);
            var exactB = new BigInteger(value: rawB);
            var magnitudeA = BigInteger.Abs(value: exactA);
            var magnitudeB = BigInteger.Abs(value: exactB);
            var expectedMaximum = (((magnitudeA > magnitudeB) || ((magnitudeA == magnitudeB) && (exactA >= exactB))) ? a : b);
            var expectedMinimum = (((magnitudeA < magnitudeB) || ((magnitudeA == magnitudeB) && (exactA <= exactB))) ? a : b);

            if (T.MaxMagnitude(x: a, y: b) != expectedMaximum) { return $"the greater magnitude of ({rawA}, {rawB}) is wrong"; }
            if (T.MinMagnitude(x: a, y: b) != expectedMinimum) { return $"the lesser magnitude of ({rawA}, {rawB}) is wrong"; }
            if (T.MaxMagnitudeNumber(x: a, y: b) != T.MaxMagnitude(x: a, y: b)) { return $"MaxMagnitudeNumber diverges from MaxMagnitude at ({rawA}, {rawB})"; }
            if (T.MinMagnitudeNumber(x: a, y: b) != T.MinMagnitude(x: a, y: b)) { return $"MinMagnitudeNumber diverges from MinMagnitude at ({rawA}, {rawB})"; }
            if (unchecked((TAdapter.Raw(value: T.MaxMagnitude(x: a, y: b)) + TAdapter.Raw(value: T.MinMagnitude(x: a, y: b)))) != unchecked((rawA + rawB))) { return $"the two selections do not partition the pair ({rawA}, {rawB})"; }

            if (long.MinValue == rawA) {
                if (!Throws<OverflowException>(action: () => _ = T.Abs(value: a))) { return "the absolute value of MinValue was answered"; }
            } else if (TAdapter.Raw(value: T.Abs(value: a)) != magnitudeA) {
                return $"the absolute value of {rawA} is wrong";
            }

            foreach (var sign in ((ReadOnlySpan<long>)[rawB, -1L, 0L, 1L])) {
                var target = TAdapter.FromRaw(value: sign);

                if ((long.MinValue == rawA) && (sign >= 0L)) {
                    if (!Throws<OverflowException>(action: () => _ = T.CopySign(sign: target, value: a))) { return $"the positive magnitude of MinValue was answered at sign {sign}"; }

                    continue;
                }

                var expected = ((sign < 0L) ? -magnitudeA : magnitudeA);

                if (TAdapter.Raw(value: T.CopySign(sign: target, value: a)) != Oracles.WrapToRaw(value: expected)) { return $"the sign transplant of {rawA} onto {sign} is wrong"; }
            }

            return null;
        }
        public static string? IntegralPartsExact(long[] left, ReadOnlySpan<long> fixedRaws) {
            if (IntegralPartsAt(raw: left[0]) is { } sampled) { return sampled; }

            foreach (var raw in fixedRaws) {
                if (IntegralPartsAt(raw: raw) is { } fixedRung) { return fixedRung; }
            }

            return null;
        }
        public static string? PredicatesClassify(long[] left) {
            if (PredicatesAt(raw: left[0]) is { } sampled) { return sampled; }

            foreach (var raw in ((ReadOnlySpan<long>)[long.MinValue, long.MaxValue, 0L])) {
                if (PredicatesAt(raw: raw) is { } extreme) { return extreme; }
            }

            return null;
        }
        public static string? LerpEndpointsAndOracle(long[] left, long[] right) {
            var fromRaw = left[0];
            var toRaw = right[0];
            var amountRaw = left[1];
            var from = TAdapter.FromRaw(value: fromRaw);
            var to = TAdapter.FromRaw(value: toRaw);
            var amount = TAdapter.FromRaw(value: amountRaw);
            var exact = ((new BigInteger(value: fromRaw) << TAdapter.FractionBitCount) + ((new BigInteger(value: toRaw) - fromRaw) * amountRaw));
            var expected = Oracles.RoundDyadic(exact: exact, shift: TAdapter.FractionBitCount);

            if (TAdapter.Raw(value: TAdapter.Lerp(amount: amount, from: from, to: to)) != expected) { return $"the interpolation of ({fromRaw}, {toRaw}) at {amountRaw} is wrong"; }
            if (TAdapter.Lerp(from: from, to: to, amount: T.Zero) != from) { return $"the zero endpoint of ({fromRaw}, {toRaw}) is not exact"; }
            if (TAdapter.Lerp(from: from, to: to, amount: T.One) != to) { return $"the unit endpoint of ({fromRaw}, {toRaw}) is not exact"; }
            if (TAdapter.Lerp(amount: amount, from: from, to: from) != from) { return $"the degenerate segment at {fromRaw} moved under the amount {amountRaw}"; }

            return null;
        }
        public static string? TextRoundTrip(long[] left, int formatBufferLength, NumberStyles parseStyle, bool includeStyledOverloads) {
            var raw = left[0];
            var value = TAdapter.FromRaw(value: raw);
            var rendered = value.ToString();
            var reference = Oracles.ExactDyadicDecimalSigned(value: new BigInteger(value: raw), shift: TAdapter.FractionBitCount);

            if (rendered != reference) { return $"the raw {raw} rendered as '{rendered}', expected '{reference}'"; }

            Span<char> destination = stackalloc char[rendered.Length];

            if (!value.TryFormat(charsWritten: out var written, destination: destination, format: "G", provider: null) ||
                (written != rendered.Length) ||
                !destination[..written].SequenceEqual(other: rendered)) { return $"the span format did not fill an exact destination at raw {raw}"; }

            destination[..^1].Fill(value: '#');

            if (value.TryFormat(destination: destination[..^1], charsWritten: out var refused, format: default, provider: null) || (0 != refused)) { return $"the span format claimed a short destination at raw {raw}"; }
            if (destination[..^1].ContainsAnyExcept(value: '#')) { return $"the failed span format left a partial rendering behind at raw {raw}"; }
            if (!Throws<FormatException>(action: () => { Span<char> local = stackalloc char[formatBufferLength]; _ = value.TryFormat(charsWritten: out _, destination: local, format: "N2", provider: null); })) { return $"the span format accepted an unsupported specifier at raw {raw}"; }

            var point = rendered.IndexOf(value: '.');
            var digits = ((point < 0) ? rendered : string.Concat(str0: rendered.AsSpan(length: point, start: 0), str1: rendered.AsSpan(start: (point + 1))));
            var fractionDigitCount = ((point < 0) ? 0 : ((rendered.Length - point) - 1));

            var (inRange, quantized) = Oracles.DecimalToRaw(
                numerator: BigInteger.Parse(value: digits, provider: CultureInfo.InvariantCulture),
                decimalExponent: fractionDigitCount,
                shift: TAdapter.FractionBitCount
            );

            if (!inRange || (quantized != raw)) { return $"the oracle re-derived '{rendered}' as {quantized} (in range: {inRange}), not {raw}"; }

            return ParseAll(
                text: rendered,
                provider: CultureInfo.InvariantCulture,
                expected: raw,
                parseStyle: parseStyle,
                includeStyledOverloads: includeStyledOverloads
            );
        }
        public static string? ParseAll(string text, IFormatProvider provider, long expected, NumberStyles parseStyle, bool includeStyledOverloads) {
            if (TAdapter.Raw(value: T.Parse(provider: provider, s: text)) != expected) { return $"the string parse of '{text}' did not return {expected}"; }
            if (TAdapter.Raw(value: T.Parse(s: text.AsSpan(), provider: provider)) != expected) { return $"the span parse of '{text}' did not return {expected}"; }
            if (!T.TryParse(provider: provider, result: out var fromString, s: text) || (TAdapter.Raw(value: fromString) != expected)) { return $"the string try-parse of '{text}' did not return {expected}"; }
            if (!T.TryParse(s: text.AsSpan(), provider: provider, result: out var fromSpan) || (TAdapter.Raw(value: fromSpan) != expected)) { return $"the span try-parse of '{text}' did not return {expected}"; }

            if (!includeStyledOverloads) {
                return null;
            }

            if (TAdapter.Raw(value: T.Parse(provider: provider, s: text, style: parseStyle)) != expected) { return $"the styled string parse of '{text}' did not return {expected}"; }
            if (TAdapter.Raw(value: T.Parse(s: text.AsSpan(), style: parseStyle, provider: provider)) != expected) { return $"the styled span parse of '{text}' did not return {expected}"; }
            if (!T.TryParse(provider: provider, result: out var styledString, s: text, style: parseStyle) || (TAdapter.Raw(value: styledString) != expected)) { return $"the styled string try-parse of '{text}' did not return {expected}"; }
            if (!T.TryParse(s: text.AsSpan(), style: parseStyle, provider: provider, result: out var styledSpan) || (TAdapter.Raw(value: styledSpan) != expected)) { return $"the styled span try-parse of '{text}' did not return {expected}"; }

            return null;
        }

        // Every declared constant as one consistent fact. The expected fraction bit count arrives from the caller, so
        // the first comparison is against an independent statement of the format rather than the type agreeing with
        // itself; once it holds, the checks below are free to read the type's own count.
        private static string? DeclaredConstants(int fractionBitCount) {
            // Read the three declared bit counts into locals so the grid statement is a comparison the run makes rather
            // than one the compiler folds away; a folded comparison would make the counterexample unreachable.
            var fractionBits = TAdapter.FractionBitCount;
            var integerBits = TAdapter.IntegerBitCount;
            var totalBits = TAdapter.TotalBitCount;

            if (fractionBits != fractionBitCount) { return $"FractionBitCount is {fractionBits}"; }
            if (integerBits != (totalBits - fractionBits)) { return "the integer and fraction bit counts do not partition the carrier"; }
            if (totalBits != 64) { return $"TotalBitCount is {totalBits}"; }
            if (T.Radix != 2) { return $"Radix is {T.Radix}"; }
            if (TAdapter.Raw(value: T.One) != (1L << fractionBits)) { return $"one has raw {TAdapter.Raw(value: T.One)}"; }
            if (TAdapter.Raw(value: TAdapter.Epsilon) != 1L) { return $"epsilon has raw {TAdapter.Raw(value: TAdapter.Epsilon)}"; }
            if (TAdapter.Raw(value: T.NegativeOne) != -TAdapter.Raw(value: T.One)) { return $"negative one has raw {TAdapter.Raw(value: T.NegativeOne)}"; }
            if (TAdapter.Raw(value: T.Zero) != 0L) { return $"zero has raw {TAdapter.Raw(value: T.Zero)}"; }
            if (default(T) != T.Zero) { return "the default value is not zero"; }
            if (TAdapter.Raw(value: T.MaxValue) != long.MaxValue) { return $"MaxValue has raw {TAdapter.Raw(value: T.MaxValue)}"; }
            if (TAdapter.Raw(value: T.MinValue) != long.MinValue) { return $"MinValue has raw {TAdapter.Raw(value: T.MinValue)}"; }
            if (T.AdditiveIdentity != T.Zero) { return "the additive identity is not zero"; }
            if (T.MultiplicativeIdentity != T.One) { return "the multiplicative identity is not one"; }

            return null;
        }
        private static string? ConstructionRoutes(ReadOnlySpan<long> rawLadder) {
            foreach (var raw in rawLadder) {
                if (TAdapter.Raw(value: TAdapter.Construct(value: raw)) != raw) { return $"the constructor moved the ladder raw {raw}"; }
                if (TAdapter.Raw(value: TAdapter.FromRaw(value: raw)) != raw) { return $"FromRawBits moved the ladder raw {raw}"; }
            }

            return null;
        }
        private static string? WholeNumberSeam(ReadOnlySpan<long> integerLadder, ReadOnlySpan<long> refusedIntegers) {
            foreach (var value in integerLadder) {
                var expected = (new BigInteger(value: value) << TAdapter.FractionBitCount);

                if (TAdapter.Raw(value: TAdapter.FromInteger(value: value)) != expected) { return $"the whole number {value} did not scale to {expected}"; }
            }

            foreach (var value in refusedIntegers) {
                if (!Throws<ArgumentOutOfRangeException>(action: () => _ = TAdapter.FromInteger(value: value), paramName: "value")) {
                    return $"the unrepresentable whole number {value} was accepted";
                }
            }

            return null;
        }
        private static string? DoubleSeam(ReadOnlySpan<(double Value, long Expected)> doubleLadder) {
            foreach (var (value, expected) in doubleLadder) {
                var converted = TAdapter.FromDouble(value: value);

                if (TAdapter.Raw(value: converted) != expected) { return $"the double {value} converted to raw {TAdapter.Raw(value: converted)}, expected {expected}"; }
            }

            return null;
        }
        // The outward double seam (op_Explicit): one round-to-nearest-ties-to-even of the signed raw followed by an
        // exact scale by 2^-fractionBitCount, read as an exact bit pattern against an oracle that assembles the
        // IEEE-754 encoding from the format in BigInteger.
        private static string? DoubleProjection(ReadOnlySpan<long> rawLadder) {
            foreach (var raw in rawLadder) {
                var projected = TAdapter.ToDouble(value: TAdapter.FromRaw(value: raw));
                var bits = BitConverter.DoubleToUInt64Bits(value: projected);
                var expected = Oracles.NearestBinary64Bits(value: new BigInteger(value: raw), shift: TAdapter.FractionBitCount);

                if (bits != expected) { return $"the raw {raw} projected to {bits:X16}, expected {expected:X16}"; }
            }

            return null;
        }
        private static string? CheckedOperator(string name, long rawA, long rawB, BigInteger exact, Func<T> checkedCall, Func<T> uncheckedCall) {
            if ((exact < long.MinValue) || (exact > long.MaxValue)) {
                return (Throws<OverflowException>(action: () => _ = checkedCall())
                    ? null
                    : $"the checked {name} of ({rawA}, {rawB}) answered {exact}, which is outside the carrier, instead of refusing it");
            }

            var landed = checkedCall();

            if (TAdapter.Raw(value: landed) != exact) { return $"the checked {name} of ({rawA}, {rawB}) is {TAdapter.Raw(value: landed)}, expected {exact}"; }
            if (landed != uncheckedCall()) { return $"the checked and unchecked {name} of ({rawA}, {rawB}) disagree inside the carrier"; }

            return null;
        }
        private static string? IntegralPartsAt(long raw) {
            var value = TAdapter.FromRaw(value: raw);
            var exact = new BigInteger(value: raw);
            var half = (OneRaw >> 1);
            var floorUnits = Oracles.FloorQuotient(denominator: OneRaw, numerator: exact);
            var floor = (floorUnits * OneRaw);
            var fraction = (exact - floor);
            var truncated = (BigInteger.Divide(dividend: exact, divisor: OneRaw) * OneRaw);
            var ceiling = (-(Oracles.FloorQuotient(denominator: OneRaw, numerator: -exact)) * OneRaw);
            var roundsUp = ((fraction > half) || ((fraction == half) && !((floorUnits & BigInteger.One).IsZero)));
            var rounded = (roundsUp ? (floor + OneRaw) : floor);

            if (TAdapter.Raw(value: TAdapter.Floor(value: value)) != floor) { return $"the floor of {raw} is wrong"; }
            if (TAdapter.Raw(value: TAdapter.Truncate(value: value)) != truncated) { return $"the truncation of {raw} is wrong"; }
            if (TAdapter.Raw(value: TAdapter.Fractional(value: value)) != fraction) { return $"the fractional part of {raw} is wrong"; }

            if (ceiling > long.MaxValue) {
                if (!Throws<OverflowException>(action: () => _ = TAdapter.Ceiling(value: value))) { return $"the ceiling of {raw} was answered past the carrier"; }
            } else if (TAdapter.Raw(value: TAdapter.Ceiling(value: value)) != ceiling) {
                return $"the ceiling of {raw} is wrong";
            }

            if (rounded > long.MaxValue) {
                if (!Throws<OverflowException>(action: () => _ = TAdapter.Round(value: value))) { return $"the rounding of {raw} was answered past the carrier"; }
            } else if (TAdapter.Raw(value: TAdapter.Round(value: value)) != rounded) {
                return $"the rounding of {raw} is wrong";
            }

            if ((floor + fraction) != exact) { return $"the floor and the fraction do not reconstruct {raw}"; }
            if ((fraction.Sign < 0) || (fraction >= OneRaw)) { return $"the fractional part of {raw} left the unit interval"; }
            if ((ceiling - floor) != (fraction.IsZero ? BigInteger.Zero : OneRaw)) { return $"the ceiling is not the floor plus one whole unit at {raw}"; }
            if (truncated != ((raw < 0L) ? ceiling : floor)) { return $"the truncation of {raw} does not round toward zero"; }
            if ((rounded != floor) && (rounded != ceiling)) { return $"the rounding of {raw} landed off the integer grid"; }

            return null;
        }
        private static string? PredicatesAt(long raw) {
            var value = TAdapter.FromRaw(value: raw);
            var exact = new BigInteger(value: raw);
            var isInteger = (exact % OneRaw).IsZero;
            var wholeUnits = Oracles.FloorQuotient(denominator: OneRaw, numerator: exact);
            var isEven = (isInteger && (wholeUnits % 2).IsZero);
            var isOdd = (isInteger && !(wholeUnits % 2).IsZero);

            if (T.IsInteger(value: value) != isInteger) { return $"the integrality of {raw} is wrong"; }
            if (T.IsEvenInteger(value: value) != isEven) { return $"the even-integer classification of {raw} is wrong"; }
            if (T.IsOddInteger(value: value) != isOdd) { return $"the odd-integer classification of {raw} is wrong"; }
            if (T.IsZero(value: value) != exact.IsZero) { return $"the zero classification of {raw} is wrong"; }
            if (T.IsNegative(value: value) != (exact.Sign < 0)) { return $"the negative classification of {raw} is wrong"; }
            if (T.IsPositive(value: value) != (exact.Sign >= 0)) { return $"the positive classification of {raw} is wrong"; }
            if (T.IsNormal(value: value) != !exact.IsZero) { return $"the normal classification of {raw} is wrong"; }

            if (!T.IsCanonical(value: value)) { return $"{raw} is not canonical"; }
            if (!T.IsFinite(value: value)) { return $"{raw} is not finite"; }
            if (!T.IsRealNumber(value: value)) { return $"{raw} is not a real number"; }
            if (T.IsComplexNumber(value: value)) { return $"{raw} claims to be a complex number"; }
            if (T.IsImaginaryNumber(value: value)) { return $"{raw} claims to be an imaginary number"; }
            if (T.IsInfinity(value: value)) { return $"{raw} claims to be infinite"; }
            if (T.IsNegativeInfinity(value: value)) { return $"{raw} claims to be negative infinity"; }
            if (T.IsPositiveInfinity(value: value)) { return $"{raw} claims to be positive infinity"; }
            if (T.IsNaN(value: value)) { return $"{raw} claims not to be a number"; }
            if (T.IsSubnormal(value: value)) { return $"{raw} claims to be subnormal"; }

            if (T.IsPositive(value: value) == T.IsNegative(value: value)) { return $"the two sign classifiers agree at {raw}"; }
            if (T.IsEvenInteger(value: value) && T.IsOddInteger(value: value)) { return $"{raw} is both even and odd"; }
            if ((T.IsEvenInteger(value: value) || T.IsOddInteger(value: value)) != T.IsInteger(value: value)) { return $"the parities do not cover integrality at {raw}"; }
            if (T.IsZero(value: value) == T.IsNormal(value: value)) { return $"the zero and normal classifiers agree at {raw}"; }
            if (T.IsZero(value: value) != (value == T.AdditiveIdentity)) { return $"the zero classifier disagrees with the additive identity at {raw}"; }

            return null;
        }
    }
}
