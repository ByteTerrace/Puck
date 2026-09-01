using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims for <see cref="FixedCosineTransform"/> and <see cref="FixedCosineTransformPlan"/>. Every statement is
/// either an EXACT identity (the constant input, whose route touches only twiddles that are exactly <c>1</c>; the
/// impulse, whose spectrum is the plan's own cosine table) or a MEASURED bound: the post-twiddle and every butterfly
/// beneath it round, so round trip, linearity and Parseval hold only within a ceiling this file measures and freezes.
/// <see cref="LawRegistry"/> invokes each claim below as a Default-tier law; the <c>*Deep</c> siblings mirror the
/// same statements at strictly stronger operands (longer lengths) as Deep-tier laws.
/// </summary>
internal static class DctClaims {
    // Operand magnitude every sweep draws within: raw in [-2^20, 2^20], about +/-16.0 — the same envelope the fft.*
    // family measures at, so the two families' bounds read against each other.
    private const long AmplitudeRaw = (1L << 20);

    private static readonly int[] DefaultLengths = [1, 2, 4, 8, 16, 32, 64, 128, 256];
    private static readonly int[] DeepLengths = [512, 1024, 2048, 4096];
    // Small lengths only: the direct sum is O(N^2) SinCos calls.
    private static readonly int[] DirectSumLengths = [1, 2, 4, 8, 16, 32];

    // Measured maxima at the envelope above, with margin.
    private const long RoundTripBoundDefault = 64L;
    private const long RoundTripBoundDeep = 96L;
    private const long LinearityBoundDefault = 24L;
    private const long LinearityBoundDeep = 96L;
    private const long DirectSumBound = 128L;

    private static readonly BigInteger ParsevalBoundDefault = 400_000_000_000L;
    private static readonly BigInteger ParsevalBoundDeep = 60_000_000_000_000L;

    private static FixedQ4816[] Sequence(int length, ulong stream) {
        var rng = Pcg32XshRr.Create(state: 0x4443_5432_2D46_4958UL, stream: stream);
        var values = new FixedQ4816[length];

        for (var i = 0; (i < length); ++i) {
            var raw = (((long)rng.NextUInt32(maximum: ((uint)((2 * AmplitudeRaw) + 1)), minimum: 0U)) - AmplitudeRaw);

            values[i] = FixedQ4816.FromRawBits(value: raw);
        }

        return values;
    }
    // The direct O(N^2) DCT-II sum, built from the SAME FixedQ4816.SinCos kernel the plan's twiddles use and the
    // one-rounding FixedQ4816 multiply, but with no even/odd fold, no Fourier network and no post-twiddle: bin k
    // accumulates x[n] * Cos(pi * (2n + 1) * k / (2N)) over all n. A different route over the identical kernel, so
    // agreement pins the fold, the Fourier route and the post-twiddle rather than the kernel.
    private static FixedQ4816[] DirectDct(ReadOnlySpan<FixedQ4816> values) {
        var n = values.Length;
        var result = new FixedQ4816[n];

        for (var k = 0; (k < n); ++k) {
            var sum = FixedQ4816.Zero;

            for (var index = 0; (index < n); ++index) {
                var angle = FixedQ4816.FromDouble(value: ((Math.PI * ((2 * index) + 1) * k) / (2.0 * n)));

                sum += (values[index] * FixedQ4816.Cos(angle: angle));
            }

            result[k] = sum;
        }

        return result;
    }
    private static string? Refuses(Action action, Type type, string? parameterName, string what) {
        try {
            action();
        } catch (Exception thrown) when (type.IsInstanceOfType(o: thrown)) {
            if (parameterName is null) { return null; }

            return (((thrown is ArgumentException argument) && (argument.ParamName == parameterName))
                ? null
                : $"{what} threw {thrown.GetType().Name} naming '{(thrown as ArgumentException)?.ParamName}' rather than '{parameterName}'");
        } catch (Exception thrown) {
            return $"{what} threw {thrown.GetType().Name} rather than {type.Name}";
        }

        return $"{what} did not throw at all";
    }

    /// <summary>Proves a constant input's spectrum is EXACTLY <c>N * value</c> at bin zero and EXACTLY zero
    /// elsewhere, that <see cref="FixedCosineTransform.Inverse"/> restores the constant EXACTLY at every sample, and
    /// that an impulse at sample zero produces EXACTLY the plan's cosine table — <c>X[k] == Cos(-pi*k/(2N))</c> as
    /// <see cref="FixedQ4816.SinCos"/> quantizes it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ConstantAndImpulseExact() {
        foreach (var length in DefaultLengths) {
            var plan = FixedCosineTransformPlan.Create(length: length);
            var scratch = new FixedComplex[length];
            var constant = new FixedQ4816[length];
            var constantValue = FixedQ4816.FromInteger(value: 3);

            for (var i = 0; (i < length); ++i) { constant[i] = constantValue; }

            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: constant);

            var expectedDc = (constantValue * FixedQ4816.FromInteger(value: length));

            if (constant[0] != expectedDc) {
                return $"length {length}: constant input's bin 0 is {constant[0]}, expected exactly {expectedDc}";
            }

            for (var k = 1; (k < length); ++k) {
                if (constant[k] != FixedQ4816.Zero) {
                    return $"length {length}: constant input's bin {k} is {constant[k]}, expected exactly zero";
                }
            }

            FixedCosineTransform.Inverse(plan: plan, scratch: scratch, values: constant);

            for (var i = 0; (i < length); ++i) {
                if (constant[i] != constantValue) {
                    return $"length {length}: constant round trip gave {constant[i]} at sample {i}, expected exactly {constantValue}";
                }
            }

            var impulse = new FixedQ4816[length];

            impulse[0] = FixedQ4816.One;
            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: impulse);

            for (var k = 0; (k < length); ++k) {
                var expected = FixedQ4816.Cos(angle: FixedQ4816.FromDouble(value: ((-Math.PI * k) / (2.0 * length))));

                if (impulse[k] != expected) {
                    return $"length {length}: impulse bin {k} is {impulse[k]}, expected exactly the cosine table entry {expected}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedCosineTransform.Inverse"/> recovers <see cref="FixedCosineTransform.Forward"/>'s
    /// input within a measured raw-Q16 ULP bound, over <see cref="DefaultLengths"/> at the module's amplitude
    /// envelope.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RoundTripBound() =>
        RoundTripBoundCore(bound: RoundTripBoundDefault, lengths: DefaultLengths, saltBase: 1_000UL);
    /// <summary>MIRROR of <see cref="RoundTripBound"/> at <see cref="DeepLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RoundTripBoundDeepMirror() =>
        RoundTripBoundCore(bound: RoundTripBoundDeep, lengths: DeepLengths, saltBase: 2_000UL);

    private static string? RoundTripBoundCore(int[] lengths, long bound, ulong saltBase) {
        foreach (var length in lengths) {
            var plan = FixedCosineTransformPlan.Create(length: length);
            var original = Sequence(length: length, stream: (saltBase + ((ulong)length)));
            var working = ((FixedQ4816[])original.Clone());
            var scratch = new FixedComplex[length];

            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: working);
            FixedCosineTransform.Inverse(plan: plan, scratch: scratch, values: working);

            for (var i = 0; (i < length); ++i) {
                var error = Math.Abs(value: (working[i].Value - original[i].Value));

                if (error > bound) {
                    return $"length {length}, index {i}: round trip error {error} raw ULPs exceeds the bound {bound}";
                }
            }
        }

        return null;
    }

    /// <summary>Proves <see cref="FixedCosineTransform.Forward"/> is linear within a measured raw-Q16 ULP bound —
    /// <c>Forward(a) + Forward(b)</c> against <c>Forward(a + b)</c> — over <see cref="DefaultLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LinearityBound() =>
        LinearityBoundCore(bound: LinearityBoundDefault, lengths: DefaultLengths, saltBase: 3_000UL);
    /// <summary>MIRROR of <see cref="LinearityBound"/> at <see cref="DeepLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LinearityBoundDeepMirror() =>
        LinearityBoundCore(bound: LinearityBoundDeep, lengths: DeepLengths, saltBase: 4_000UL);

    private static string? LinearityBoundCore(int[] lengths, long bound, ulong saltBase) {
        foreach (var length in lengths) {
            var plan = FixedCosineTransformPlan.Create(length: length);
            var a = Sequence(length: length, stream: (saltBase + ((ulong)length)));
            var b = Sequence(length: length, stream: ((saltBase + 500_000UL) + ((ulong)length)));
            var sum = new FixedQ4816[length];
            var scratch = new FixedComplex[length];

            for (var i = 0; (i < length); ++i) { sum[i] = (a[i] + b[i]); }

            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: a);
            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: b);
            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: sum);

            for (var k = 0; (k < length); ++k) {
                var error = Math.Abs(value: (sum[k].Value - (a[k] + b[k]).Value));

                if (error > bound) {
                    return $"length {length}, bin {k}: linearity error {error} raw ULPs exceeds the bound {bound}";
                }
            }
        }

        return null;
    }

    /// <summary>Proves Parseval's identity for the unscaled DCT-II — <c>N * sum x[n]^2 == X[0]^2 + 2 * sum over k &gt;= 1 of X[k]^2</c>
    /// — holds within a measured raw Q32-unit bound, computed EXACTLY in <see cref="BigInteger"/> from the integer
    /// raw components, over <see cref="DefaultLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ParsevalBound() =>
        ParsevalBoundCore(bound: ParsevalBoundDefault, lengths: DefaultLengths, saltBase: 5_000UL);
    /// <summary>MIRROR of <see cref="ParsevalBound"/> at <see cref="DeepLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ParsevalBoundDeepMirror() =>
        ParsevalBoundCore(bound: ParsevalBoundDeep, lengths: DeepLengths, saltBase: 6_000UL);

    private static string? ParsevalBoundCore(int[] lengths, BigInteger bound, ulong saltBase) {
        foreach (var length in lengths) {
            var plan = FixedCosineTransformPlan.Create(length: length);
            var original = Sequence(length: length, stream: (saltBase + ((ulong)length)));
            var timeEnergy = BigInteger.Zero;

            foreach (var v in original) { timeEnergy += (((BigInteger)v.Value) * v.Value); }

            var spectrum = ((FixedQ4816[])original.Clone());
            var scratch = new FixedComplex[length];

            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: spectrum);

            var frequencyEnergy = (((BigInteger)spectrum[0].Value) * spectrum[0].Value);

            for (var k = 1; (k < length); ++k) {
                frequencyEnergy += (2 * (((BigInteger)spectrum[k].Value) * spectrum[k].Value));
            }

            var error = BigInteger.Abs(value: (frequencyEnergy - (((BigInteger)length) * timeEnergy)));

            if (error > bound) {
                return $"length {length}: Parseval error {error} raw Q32 units exceeds the bound {bound}";
            }
        }

        return null;
    }

    /// <summary>Proves the Fourier route — even/odd fold, one <see cref="FixedFourierTransform"/>, one post-twiddle
    /// per bin — agrees with the direct O(N^2) DCT-II sum built from the SAME <see cref="FixedQ4816.SinCos"/> kernel
    /// but a different route, within a measured bound; this pins the fold, the route and the post-twiddle rather
    /// than the kernel.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ForwardVsDirectSum() {
        foreach (var length in DirectSumLengths) {
            var plan = FixedCosineTransformPlan.Create(length: length);
            var input = Sequence(length: length, stream: (8_000UL + ((ulong)length)));
            var viaPlan = ((FixedQ4816[])input.Clone());
            var scratch = new FixedComplex[length];

            FixedCosineTransform.Forward(plan: plan, scratch: scratch, values: viaPlan);

            var direct = DirectDct(values: input);

            for (var k = 0; (k < length); ++k) {
                var error = Math.Abs(value: (viaPlan[k].Value - direct[k].Value));

                if (error > DirectSumBound) {
                    return $"length {length}, bin {k}: Fourier route gave {viaPlan[k]}, direct sum gives {direct[k]}, error {error} exceeds the bound {DirectSumBound}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves every documented refusal: a non-power-of-two or non-positive
    /// <see cref="FixedCosineTransformPlan.Create"/> length, and a mis-sized values or scratch span to
    /// <see cref="FixedCosineTransform.Forward"/> or <see cref="FixedCosineTransform.Inverse"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LengthRefusals() {
        var plan = FixedCosineTransformPlan.Create(length: 8);

        return (Refuses(action: () => FixedCosineTransformPlan.Create(length: 0), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "FixedCosineTransformPlan.Create(0)") ??
               (Refuses(action: () => FixedCosineTransformPlan.Create(length: -8), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "FixedCosineTransformPlan.Create(-8)") ??
               (Refuses(action: () => FixedCosineTransformPlan.Create(length: 12), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "FixedCosineTransformPlan.Create(12) (not a power of two)") ??
               (Refuses(action: () => FixedCosineTransform.Forward(plan: plan, scratch: new FixedComplex[8], values: new FixedQ4816[4]), type: typeof(ArgumentException), parameterName: "values", what: "Forward with a mis-sized values span") ??
               (Refuses(action: () => FixedCosineTransform.Forward(plan: plan, scratch: new FixedComplex[4], values: new FixedQ4816[8]), type: typeof(ArgumentException), parameterName: "scratch", what: "Forward with a mis-sized scratch span") ??
               (Refuses(action: () => FixedCosineTransform.Inverse(plan: plan, scratch: new FixedComplex[8], values: new FixedQ4816[16]), type: typeof(ArgumentException), parameterName: "values", what: "Inverse with a mis-sized values span") ??
               Refuses(action: () => FixedCosineTransform.Inverse(plan: plan, scratch: new FixedComplex[16], values: new FixedQ4816[8]), type: typeof(ArgumentException), parameterName: "scratch", what: "Inverse with a mis-sized scratch span")))))));
    }
}
