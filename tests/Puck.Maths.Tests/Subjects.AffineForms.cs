using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="FixedAffineForm"/> and <see cref="FixedAffineForms"/> to the law
/// suite.</summary>
internal static partial class Subjects {
    // The symbol point each check samples at: εᵢ = kᵢ / AffineSymbolDenominator, so membership is decided exactly in
    // BigInteger at a scale of the denominator.
    private const long AffineSymbolDenominator = 1024L;
    private const int AffineDraws = 3072;

    /// <summary>Every affine operation holds the shipped point operation's answer at the same symbols: at a drawn point
    /// of the symbol cube, operands realized as raws their forms hold there give point answers (the carrier's own
    /// operators) that the result form holds there too, decided exactly; a bounded result never stands beside a point
    /// answer past the carrier.</summary>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedAffineFormsHoldThePointOperations() {
        var generator = Pcg32XshRr.Create(state: 0xAFF1_0E5UL, stream: 47UL);

        for (var draw = 0; (draw < AffineDraws); ++draw) {
            var bits = (4 + ((int)(generator.NextUInt32() % 36U)));
            var a = DrawAffine(bits: bits, generator: ref generator);
            var b = DrawAffine(bits: bits, generator: ref generator);
            var c = DrawAffine(bits: bits, generator: ref generator);
            var t = DrawAffine(bits: Math.Min(val1: bits, val2: 17), generator: ref generator);
            var k = (DrawSymbol(generator: ref generator), DrawSymbol(generator: ref generator), DrawSymbol(generator: ref generator));
            var constant = FixedQ4816.FromRawBits(value: DrawSigned(bits: Math.Min(val1: bits, val2: 30), generator: ref generator));
            var divisor = FixedQ4816.FromRawBits(value: (DrawSigned(bits: Math.Min(val1: bits, val2: 30), generator: ref generator) | 1L));

            if (!TryAffinePoint(form: a, generator: ref generator, symbols: k, value: out var x) ||
                !TryAffinePoint(form: b, generator: ref generator, symbols: k, value: out var y) ||
                !TryAffinePoint(form: c, generator: ref generator, symbols: k, value: out var z) ||
                !TryAffinePoint(form: t, generator: ref generator, symbols: k, value: out var u)) {
                continue;
            }

            var (low, high) = ((constant <= divisor) ? (constant, divisor) : (divisor, constant));
            var vector = new FixedAffineForm3(X: a, Y: b, Z: c);
            var point = new FixedVector3(X: x, Y: y, Z: z);
            var weights = new FixedVector3(X: constant, Y: divisor, Z: FixedQ4816.FromRawBits(value: DrawSigned(bits: 20, generator: ref generator)));
            var rotation = new FixedQuaternion(
                X: FixedQ4816.FromRawBits(value: DrawSigned(bits: 16, generator: ref generator)),
                Y: FixedQ4816.FromRawBits(value: DrawSigned(bits: 16, generator: ref generator)),
                Z: FixedQ4816.FromRawBits(value: DrawSigned(bits: 16, generator: ref generator)),
                W: FixedQ4816.FromRawBits(value: (DrawSigned(bits: 16, generator: ref generator) | 1L))
            ).Normalize();
            (string Name, FixedAffineForm Form, FixedQ4816? Point)[] checks = [
                ("a + b", FixedAffineForms.Add(a: a, b: b), AffinePointOf(exact: (((BigInteger)x.Value) + y.Value))),
                ("a − b", FixedAffineForms.Subtract(a: a, b: b), AffinePointOf(exact: (((BigInteger)x.Value) - y.Value))),
                ("−a", FixedAffineForms.Negate(a: a), AffinePointOf(exact: -((BigInteger)x.Value))),
                ("a × b", FixedAffineForms.Multiply(a: a, b: b), AffineProduct(x: x, y: y)),
                ("a × k", FixedAffineForms.Multiply(a: a, b: constant), AffineProduct(x: x, y: constant)),
                ("a ÷ d", FixedAffineForms.Divide(a: a, b: divisor), AffineQuotient(x: x, y: divisor)),
                ("a ÷ b", FixedAffineForms.Divide(a: a, b: b), ((y.Value == 0L) ? null : AffineQuotient(x: x, y: y))),
                ("a·k/d", FixedAffineForms.MultiplyDivide(divisor: divisor, multiplier: constant, value: a), AffineMultiplyDivide(divisor: divisor, multiplier: constant, value: x)),
                ("|a|", FixedAffineForms.Abs(a: a), ((x == FixedQ4816.MinValue) ? null : FixedQ4816.Abs(value: x))),
                ("min(a, b)", FixedAffineForms.Min(a: a, b: b), FixedQ4816.Min(x: x, y: y)),
                ("max(a, b)", FixedAffineForms.Max(a: a, b: b), FixedQ4816.Max(x: x, y: y)),
                ("clamp(a, k, d)", FixedAffineForms.Clamp(a: a, high: high, low: low), FixedQ4816.Clamp(maximum: high, minimum: low, value: x)),
                ("√a", FixedAffineForms.Sqrt(a: a), FixedQ4816.Sqrt(value: x)),
                ("lerp(a, b, t)", FixedAffineForms.Lerp(a: a, b: b, t: t), AffineLerp(from: x, to: y, amount: u)),
                ("|(a, b, c)|", FixedAffineForms.Length(a: vector), (point.TryLength(length: out var length3) ? length3 : null)),
                ("|(a, b)|", FixedAffineForms.Length(a: new FixedAffineForm2(X: a, Y: b)), (new FixedVector2(X: x, Y: y).TryLength(length: out var length2) ? length2 : null)),
                ("(a, b, c)·w", FixedAffineForms.Dot(a: vector, b: weights), AffineDot(left: point, right: weights)),
                ("(a, b, c)·(a, b, c)", FixedAffineForms.Dot(a: vector, b: vector), AffineDot(left: point, right: point)),
                ("segment((a, b, c), w)", FixedAffineForms.SegmentParameter(p: vector, segment: weights), FixedVector3.SegmentParameter(point: point, segment: weights)),
                ("segment((a, b, c), (c, t, a))", FixedAffineForms.SegmentParameter(p: vector, segment: new FixedAffineForm3(X: c, Y: t, Z: a)), FixedVector3.SegmentParameter(point: point, segment: new FixedVector3(X: z, Y: u, Z: x))),
                ("segment((a, b), (t, c))", FixedAffineForms.SegmentParameter(p: new FixedAffineForm2(X: a, Y: b), segment: new FixedAffineForm2(X: t, Y: c)), FixedVector2.SegmentParameter(point: new FixedVector2(X: x, Y: y), segment: new FixedVector2(X: u, Y: z))),
                ("round a", FixedAffineForms.Round(a: a), (RoundStaysInCarrier(value: x) ? FixedQ4816.Round(value: x) : null)),
                ("floor a", FixedAffineForms.Floor(a: a), FixedQ4816.Floor(value: x)),
                ("smoothstep(k, d, a)", FixedAffineForms.Smoothstep(edge0: constant, edge1: divisor, x: a), FixedQ4816.Smoothstep(edge0: constant, edge1: divisor, value: x)),
                ("select(a < b, a, b)", FixedAffineForms.Select(a: a, b: b, condition: FixedAffineForms.Less(a: a, b: b)), ((x < y) ? x : y)),
                ("join(a, b) at a", FixedAffineForms.Join(a: a, b: b), x),
                ("join(a, b) at b", FixedAffineForms.Join(a: a, b: b), y),
            ];

            foreach (var (name, form, answer) in checks) {
                if (AffineMiss(answer: answer, form: form, name: name, symbols: k) is { } miss) {
                    return $"{miss}, with a = {a} at {x}, b = {b} at {y}, c = {c} at {z}, t = {t} at {u}, k = {constant}, d = {divisor}, ε = {k}/{AffineSymbolDenominator}";
                }
            }

            var turned = FixedAffineForms.RotateInverse(p: vector, rotation: rotation);
            var turnedPoint = rotation.RotateInverse(vector: point);

            foreach (var (name, form, answer) in ((string, FixedAffineForm, FixedQ4816?)[])[("turn x", turned.X, turnedPoint.X), ("turn y", turned.Y, turnedPoint.Y), ("turn z", turned.Z, turnedPoint.Z)]) {
                if (AffineMiss(answer: answer, form: form, name: name, symbols: k) is { } miss) {
                    return $"{miss}, turning ({x}, {y}, {z}) back through {rotation} as {vector}, ε = {k}/{AffineSymbolDenominator}";
                }
            }

            (string Name, FixedAffineTruth Truth, bool Point)[] truths = [
                ("a < b", FixedAffineForms.Less(a: a, b: b), (x < y)),
                ("a ≤ b", FixedAffineForms.LessEqual(a: a, b: b), (x <= y)),
                ("a > b", FixedAffineForms.Greater(a: a, b: b), (x > y)),
                ("a ≥ k", FixedAffineForms.GreaterEqual(a: a, b: constant), (x >= constant)),
                ("a = b", FixedAffineForms.Equal(a: a, b: b), (x == y)),
            ];

            foreach (var (name, truth, holds) in truths) {
                if (((truth == FixedAffineTruth.True) && !holds) || ((truth == FixedAffineTruth.False) && holds)) {
                    return $"{name} answers {truth} but the point comparison of {x} and {y} (k = {constant}) answers {holds}";
                }
            }
        }

        return null;
    }
    /// <summary>The edges of the affine forms: the top absorbs through every operation, a box seeds its own symbols and
    /// the shared parts of a difference cancel exactly, a truth's conjunction is Kleene's, and the constructor refuses
    /// a negative error and a range past the carrier.</summary>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedAffineFormEdges() {
        var entire = FixedAffineForm.Entire;
        var one = FixedAffineForm.FromPoint(value: FixedQ4816.One);
        var box = FixedAffineForm3.FromBox(lower: new FixedVector3(X: -FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 2L)), upper: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.FromRawBits(value: 3L), Z: FixedQ4816.FromInteger(value: 5L)));

        if (!FixedAffineForms.Add(a: entire, b: one).IsUnbounded) { return "an unbounded sum became bounded"; }
        if (!FixedAffineForms.Multiply(a: entire, b: FixedQ4816.Zero).IsUnbounded) { return "an unbounded product with zero is bounded"; }
        if (!FixedAffineForms.Min(a: entire, b: one).IsUnbounded) { return "a minimum hid an overflow"; }
        if (!FixedAffineForms.Abs(a: entire).IsUnbounded) { return "an absolute value hid an overflow"; }
        if (!FixedAffineForms.Clamp(a: entire, high: FixedQ4816.One, low: FixedQ4816.Zero).IsUnbounded) { return "a clamp hid an overflow"; }
        if (!FixedAffineForms.Length(a: new FixedAffineForm3(X: entire, Y: one, Z: one)).IsUnbounded) { return "a length hid an overflow"; }
        if (FixedAffineForms.Less(a: entire, b: one) != FixedAffineTruth.Unknown) { return "a comparison with the unbounded form decided"; }
        if (!FixedAffineForms.Add(a: FixedAffineForm.FromPoint(value: FixedQ4816.MaxValue), b: FixedQ4816.Epsilon).IsUnbounded) { return "the maximum plus a raw stayed bounded"; }
        if (box.X.Range != new FixedInterval(lower: -FixedQ4816.One, upper: FixedQ4816.One)) { return $"a box's X axis holds {box.X.Range}, not [−1, 1]"; }
        if ((box.Y.Range.Lower.Value > 0L) || (box.Y.Range.Upper.Value < 3L)) { return $"a box's odd-width Y axis holds {box.Y.Range}, not all of [0, 3 raws]"; }
        if ((box.Z.Symbol2 == FixedQ4816.Zero) || (box.Z.Symbol0 != FixedQ4816.Zero) || (box.Z.Symbol1 != FixedQ4816.Zero) || (box.Z.Error != FixedQ4816.Zero)) { return "a box's Z axis rides other than its own symbol alone"; }
        if (FixedAffineForms.Subtract(a: box.X, b: box.X).Range != FixedInterval.FromPoint(value: FixedQ4816.Zero)) { return "the shared parts of x − x did not cancel"; }
        if (FixedAffineForms.And(a: FixedAffineTruth.True, b: FixedAffineTruth.Unknown) != FixedAffineTruth.Unknown) { return "true and unknown is not unknown"; }
        if (FixedAffineForms.And(a: FixedAffineTruth.False, b: FixedAffineTruth.Unknown) != FixedAffineTruth.False) { return "false and unknown is not false"; }
        if (FixedAffineForms.And(a: FixedAffineTruth.True, b: FixedAffineTruth.True) != FixedAffineTruth.True) { return "true and true is not true"; }
        if (FixedAffineForms.Select(a: one, b: entire, condition: FixedAffineTruth.True) != one) { return "a true selection did not take its first arm alone"; }
        if (FixedAffineForm.FromInterval(interval: FixedInterval.Entire) != entire) { return "the unbounded interval lifted to a bounded form"; }

        try {
            _ = new FixedAffineForm(center: FixedQ4816.Zero, error: -FixedQ4816.Epsilon, symbol0: FixedQ4816.Zero, symbol1: FixedQ4816.Zero, symbol2: FixedQ4816.Zero);
            return "a negative error was accepted";
        } catch (ArgumentOutOfRangeException) {
        }

        try {
            _ = new FixedAffineForm(center: FixedQ4816.MaxValue, error: FixedQ4816.Epsilon, symbol0: FixedQ4816.Zero, symbol1: FixedQ4816.Zero, symbol2: FixedQ4816.Zero);
            return "a range past the carrier was accepted";
        } catch (ArgumentException) {
        }

        return null;
    }

    // A form whose coefficients reach about 2^bits raws, with a symbol or the error zero now and then.
    private static FixedAffineForm DrawAffine(ref Pcg32XshRr generator, int bits) {
        var shape = generator.NextUInt32();
        var center = DrawSigned(bits: bits, generator: ref generator);
        var symbol0 = (((shape & 1U) == 0U) ? 0L : DrawSigned(bits: Math.Max(val1: 0, val2: (bits - ((int)((shape >> 4) % 8U)))), generator: ref generator));
        var symbol1 = (((shape & 2U) == 0U) ? 0L : DrawSigned(bits: Math.Max(val1: 0, val2: (bits - ((int)((shape >> 8) % 8U)))), generator: ref generator));
        var symbol2 = (((shape & 4U) == 0U) ? 0L : DrawSigned(bits: Math.Max(val1: 0, val2: (bits - ((int)((shape >> 12) % 8U)))), generator: ref generator));
        var error = (((shape & 8U) == 0U) ? 0L : Math.Abs(value: DrawSigned(bits: Math.Max(val1: 0, val2: (bits - 4)), generator: ref generator)));

        return new FixedAffineForm(
            center: FixedQ4816.FromRawBits(value: center),
            error: FixedQ4816.FromRawBits(value: error),
            symbol0: FixedQ4816.FromRawBits(value: symbol0),
            symbol1: FixedQ4816.FromRawBits(value: symbol1),
            symbol2: FixedQ4816.FromRawBits(value: symbol2)
        );
    }
    // A raw in [−2^bits, 2^bits).
    private static long DrawSigned(ref Pcg32XshRr generator, int bits) {
        var raw = unchecked((long)((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32()));

        return (raw >> (63 - Math.Clamp(value: bits, min: 0, max: 62)));
    }
    private static long DrawSymbol(ref Pcg32XshRr generator) =>
        (((long)(generator.NextUInt32() % ((2U * ((uint)AffineSymbolDenominator)) + 1U))) - AffineSymbolDenominator);
    // A raw the form holds at the symbols, or false where it holds none there (no noise and a centre off the grid).
    private static bool TryAffinePoint(FixedAffineForm form, (long, long, long) symbols, ref Pcg32XshRr generator, out FixedQ4816 value) {
        var linear = AffineLinear(form: form, symbols: symbols);
        // The floor of (2·linear + D) / 2D: BigInteger's division truncates, so a negative remainder steps it down.
        var nearest = BigInteger.DivRem(dividend: ((linear * 2) + AffineSymbolDenominator), divisor: (2 * AffineSymbolDenominator), remainder: out var remainder);
        var error = form.Error.Value;
        var offset = ((error == 0L) ? 0L : (((long)(((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32()) % ((((ulong)error) * 2UL) + 1UL))) - error));

        if (remainder < 0) {
            nearest -= 1;
        }

        foreach (var candidate in new[] { (nearest + offset), nearest }) {
            if ((candidate >= long.MinValue) && (candidate <= long.MaxValue) && (BigInteger.Abs(value: ((candidate * AffineSymbolDenominator) - linear)) <= (((BigInteger)error) * AffineSymbolDenominator))) {
                value = FixedQ4816.FromRawBits(value: ((long)candidate));
                return true;
            }
        }

        value = FixedQ4816.Zero;
        return false;
    }
    // The form's linear part at the symbols, at the denominator's scale.
    private static BigInteger AffineLinear(FixedAffineForm form, (long K0, long K1, long K2) symbols) =>
        ((((((BigInteger)form.Center.Value) * AffineSymbolDenominator) + (((BigInteger)form.Symbol0.Value) * symbols.K0)) + (((BigInteger)form.Symbol1.Value) * symbols.K1)) + (((BigInteger)form.Symbol2.Value) * symbols.K2));
    // Whether a bounded form misses its point answer at the symbols, or stands bounded beside an answer past the carrier.
    private static string? AffineMiss(string name, FixedAffineForm form, FixedQ4816? answer, (long, long, long) symbols) {
        if (form.IsUnbounded) {
            return null;
        }

        if (answer is not { } value) {
            return $"{name} is bounded ({form}) where the point answer leaves the carrier";
        }

        return ((BigInteger.Abs(value: ((((BigInteger)value.Value) * AffineSymbolDenominator) - AffineLinear(form: form, symbols: symbols))) > (((BigInteger)form.Error.Value) * AffineSymbolDenominator))
            ? $"{name} = {form} does not hold the point answer {value}"
            : null);
    }
    private static FixedQ4816? AffinePointOf(BigInteger exact) =>
        (((exact >= long.MinValue) && (exact <= long.MaxValue)) ? FixedQ4816.FromRawBits(value: ((long)exact)) : null);
    // The carrier's rounded product, or null where the exact product leaves the carrier (the carrier wraps there).
    private static FixedQ4816? AffineProduct(FixedQ4816 x, FixedQ4816 y) =>
        ((AffinePointOf(exact: ((((BigInteger)x.Value) * y.Value) >> 16)) is null) ? null : (x * y));
    private static FixedQ4816? AffineQuotient(FixedQ4816 x, FixedQ4816 y) =>
        ((AffinePointOf(exact: ((((BigInteger)x.Value) << 16) / y.Value)) is null) ? null : (x / y));
    private static FixedQ4816? AffineMultiplyDivide(FixedQ4816 value, FixedQ4816 multiplier, FixedQ4816 divisor) =>
        ((AffinePointOf(exact: ((((BigInteger)value.Value) * multiplier.Value) / divisor.Value)) is null) ? null : FusedArithmetic.MultiplyDivide(divisor: divisor, multiplicand: value, multiplier: multiplier));
    private static FixedQ4816? AffineLerp(FixedQ4816 from, FixedQ4816 to, FixedQ4816 amount) =>
        ((AffinePointOf(exact: (((BigInteger)from.Value) + (((((BigInteger)to.Value) - from.Value) * amount.Value) >> 16))) is null) ? null : FixedQ4816.Lerp(amount: amount, from: from, to: to));
    private static FixedQ4816? AffineDot(FixedVector3 left, FixedVector3 right) =>
        ((AffinePointOf(exact: ((((((BigInteger)left.X.Value) * right.X.Value) + (((BigInteger)left.Y.Value) * right.Y.Value)) + (((BigInteger)left.Z.Value) * right.Z.Value)) >> 16)) is null) ? null : FixedVector3.Dot(left: left, right: right));
    // FixedQ4816.Round saturates rather than leaving the carrier past its greatest integer.
    private static bool RoundStaysInCarrier(FixedQ4816 value) => (value.Value <= (long.MaxValue - (1L << 16)));
}
