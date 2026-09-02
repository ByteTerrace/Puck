using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims for two members the ratchet found classified nowhere: <see cref="FixedQ4816.AngularFrequency"/>, the shared
/// <c>ω = 2πf</c> rational every second-order and soft-constraint derivation in the library and in
/// <c>Puck.Physics</c> now forms from, and <see cref="Rational"/>, the exact-rational primitive those derivations
/// carry their intermediates in. <see cref="LawRegistry"/> invokes each claim below as a Default-tier law.
/// </summary>
internal static class AngularFrequencyAndRationalClaims {
    private static readonly long[] FrequencyRaws = [
        0L, 65536L, -65536L, 131072L, 1L, -1L, 4325376L, -4325376L, long.MaxValue, long.MinValue,
    ];

    /// <summary>Proves <see cref="FixedQ4816.AngularFrequency"/> against its own contract (a fixed denominator, and a
    /// numerator that is exactly <c>2·PiQ61·frequencyHz.Value</c>), against a scale-invariance identity independent
    /// of the formula's shape, and — the independent leg — against a <see cref="double"/> reconstruction of
    /// <c>2π·frequencyHz</c> that shares no constant or arithmetic path with the fixed-point one.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? AngularFrequencySurface() {
        var expectedDenominator = (BigInteger.One << (FixedQ4816.PiQ61FractionBitCount + FixedQ4816.FractionBitCount));

        foreach (var raw in FrequencyRaws) {
            var frequency = FixedQ4816.FromRawBits(value: raw);

            var (numerator, denominator) = FixedQ4816.AngularFrequency(frequencyHz: frequency);

            if (denominator != expectedDenominator) {
                return $"AngularFrequency({frequency}) returned denominator {denominator}, expected {expectedDenominator}";
            }

            var expectedNumerator = ((2 * ((BigInteger)FixedQ4816.PiQ61)) * raw);

            if (numerator != expectedNumerator) {
                return $"AngularFrequency({frequency}) returned numerator {numerator}, expected {expectedNumerator}";
            }

            var expectedSign = Math.Sign(value: raw);

            if (numerator.Sign != expectedSign) {
                return $"AngularFrequency({frequency}) returned a numerator whose sign disagrees with the frequency's own";
            }

            // Scale invariance, checked by cross-multiplication rather than by re-deriving the formula: doubling the
            // authored frequency must double the exact rational, independent of how the numerator/denominator pair
            // is formed.
            if ((raw > (long.MinValue / 2)) && (raw < (long.MaxValue / 2))) {
                var (doubledNumerator, doubledDenominator) = FixedQ4816.AngularFrequency(frequencyHz: FixedQ4816.FromRawBits(value: (2 * raw)));

                if ((doubledNumerator * denominator) != ((2 * numerator) * doubledDenominator)) {
                    return $"AngularFrequency(2·{frequency}) is not twice AngularFrequency({frequency}) under cross-multiplication";
                }
            }

            // The independent leg: a double reconstruction of 2π·f that shares neither PiQ61 nor the fixed-point
            // shift with the member under test.
            var reference = ((2.0 * Math.PI) * ((double)frequency));
            var reconstructed = (((double)numerator) / ((double)denominator));
            var drift = Math.Abs(value: (reference - reconstructed));
            var tolerance = (1e-9 * (1.0 + Math.Abs(value: reference)));

            if (drift > tolerance) {
                return $"AngularFrequency({frequency}) = {reconstructed} disagrees with the double reconstruction {reference} by {drift}, past tolerance {tolerance}";
            }
        }

        return null;
    }

    private static bool CrossEqual(Rational left, Rational right) =>
        ((left.Numerator * right.Denominator) == (right.Numerator * left.Denominator));
    private static double ToDouble(Rational value) =>
        (((double)value.Numerator) / ((double)value.Denominator));

    /// <summary>Proves <see cref="Rational"/> is a valid rational representation before proving its field-axiom
    /// identities: <see langword="default"/>(<see cref="Rational"/>) reads back as the canonical <c>0/1</c> — never
    /// the invalid <c>0/0</c> its own zero-initialized storage would otherwise carry — and that reading survives
    /// <see cref="FixedPointRounding.TryRoundRational"/>; the constructor refuses an explicit zero denominator by
    /// exception type AND parameter name; and <c>/</c> refuses a zero-numerator divisor, both an explicit one and
    /// the defaulted one, by exception type. Only then are the field-axiom identities checked EXACTLY, by
    /// cross-multiplication rather than by reducing either side (the type never reduces, so <c>==</c> on the record's
    /// own generated equality is not a valid rational-equality test) — commutativity and associativity of
    /// <c>+</c>/<c>*</c>, distributivity, additive and multiplicative identity and inverse, and that <c>-</c>/<c>/</c>
    /// agree with their defining identities <c>a − b = a + (−b)</c> and <c>(a / b)·b = a</c>. The independent leg
    /// reconstructs every operator result as a <see cref="double"/> from the operands' own numerator/denominator
    /// pairs and compares against the exact result converted the same way, so a defect in an operator's arithmetic
    /// (not merely its cross-multiplied bookkeeping) has a second path to be caught on.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RationalAlgebraSurface() {
        // default(Rational) never ran the constructor, so its raw storage is Numerator=0 and a zero denominator
        // backing — the invariant every OTHER Rational holds only because the ctor refuses a zero denominator. The
        // canonical reading is 0/1, not the invalid 0/0.
        var defaulted = default(Rational);

        if (!defaulted.Numerator.IsZero || (defaulted.Denominator != BigInteger.One)) {
            return $"default(Rational) read back as {defaulted.Numerator}/{defaulted.Denominator} rather than the canonical 0/1";
        }

        if (!FixedPointRounding.TryRoundRational(numerator: defaulted.Numerator, denominator: defaulted.Denominator, fractionBitCount: 16, result: out var defaultedRaw) || (0L != defaultedRaw)) {
            return $"TryRoundRational on default(Rational)'s own Numerator/Denominator did not round cleanly to raw zero";
        }

        // A direct zero-denominator construction is refused by the ctor itself, unlike the default's own
        // zero-initialized storage, which bypasses every constructor.
        try {
            _ = new Rational(Numerator: BigInteger.One, Denominator: BigInteger.Zero);

            return "new Rational(1, 0) did not refuse a zero denominator";
        } catch (ArgumentOutOfRangeException refusal) {
            if ("Denominator" != refusal.ParamName) {
                return $"new Rational(1, 0)'s refusal names '{refusal.ParamName}' rather than 'Denominator'";
            }
        }

        // Division by the rational zero (a zero numerator) would otherwise manufacture a zero denominator; the
        // operator refuses it instead, for both an explicitly zero-numerator divisor and the defaulted one.
        try {
            _ = (Rational.One / new Rational(Numerator: BigInteger.Zero, Denominator: 5));

            return "Rational.One / (0/5) did not throw DivideByZeroException";
        } catch (DivideByZeroException) { }

        try {
            _ = (Rational.One / defaulted);

            return "Rational.One / default(Rational) did not throw DivideByZeroException";
        } catch (DivideByZeroException) { }

        var zero = new Rational(Numerator: BigInteger.Zero, Denominator: BigInteger.One);
        var samples = new Rational[] {
            new(Denominator: 1, Numerator: 1),
            new(Denominator: 1, Numerator: -1),
            new(Denominator: 7, Numerator: 3),
            new(Denominator: 7, Numerator: -3),
            new(Denominator: 5, Numerator: 22),
            new(Denominator: 3, Numerator: 0),
            new(Numerator: (BigInteger.One << 96), Denominator: (BigInteger.One << 40)),
            new(Numerator: -(BigInteger.One << 96), Denominator: (BigInteger.One << 40)),
        };

        // The ctor and Numerator/Denominator readback: a constructed value reports back exactly what it was given.
        foreach (var sample in samples) {
            var reconstructed = new Rational(Numerator: sample.Numerator, Denominator: sample.Denominator);

            if ((reconstructed.Numerator != sample.Numerator) || (reconstructed.Denominator != sample.Denominator)) {
                return $"Rational({sample.Numerator}, {sample.Denominator}) did not read back its own operands";
            }
        }

        if (!CrossEqual(left: Rational.Two, right: (Rational.One + Rational.One))) {
            return "Rational.Two is not Rational.One + Rational.One";
        }

        foreach (var a in samples) {
            if (!CrossEqual(left: (a + zero), right: a)) { return $"{a} + 0 is not {a}"; }
            if (!CrossEqual(left: (a * Rational.One), right: a)) { return $"{a} * 1 is not {a}"; }
            if (!CrossEqual(left: (Rational.One * a), right: a)) { return $"1 * {a} is not {a}"; }
            if (!CrossEqual(left: (a + (-a)), right: zero)) { return $"{a} + (-{a}) is not 0"; }
            if (!CrossEqual(left: (a - a), right: zero)) { return $"{a} - {a} is not 0"; }

            var negatedTwice = (-(-a));

            if (!CrossEqual(left: negatedTwice, right: a)) { return $"-(-{a}) is not {a}"; }

            foreach (var b in samples) {
                if (!CrossEqual(left: (a + b), right: (b + a))) { return $"{a} + {b} is not commutative"; }
                if (!CrossEqual(left: (a * b), right: (b * a))) { return $"{a} * {b} is not commutative"; }
                if (!CrossEqual(left: (a - b), right: (a + (-b)))) { return $"{a} - {b} disagrees with {a} + (-{b})"; }

                var directDouble = (ToDouble(value: a) + ToDouble(value: b));
                var exactDouble = ToDouble(value: (a + b));
                var tolerance = (1e-6 * (1.0 + Math.Abs(value: directDouble)));

                if (Math.Abs(value: (directDouble - exactDouble)) > tolerance) {
                    return $"{a} + {b} = {exactDouble} disagrees with the double reconstruction {directDouble}";
                }

                if (b.Numerator.IsZero) { continue; }

                var quotient = (a / b);

                if (!CrossEqual(left: (quotient * b), right: a)) { return $"({a} / {b}) * {b} is not {a}"; }

                foreach (var c in samples) {
                    if (!CrossEqual(left: ((a + b) + c), right: (a + (b + c)))) { return $"({a} + {b}) + {c} is not associative"; }
                    if (!CrossEqual(left: ((a * b) * c), right: (a * (b * c)))) { return $"({a} * {b}) * {c} is not associative"; }
                    if (!CrossEqual(left: (a * (b + c)), right: ((a * b) + (a * c)))) { return $"{a} * ({b} + {c}) does not distribute"; }
                }
            }
        }

        return null;
    }
}
