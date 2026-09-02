using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims for <see cref="WalshHadamardTransform"/>. Every statement is an EXACT integer identity or exact agreement
/// with <see cref="Oracles.WalshHadamardNatural"/>'s definition-form <see cref="BigInteger"/> sum — nothing here is a
/// bound, because the subject is plain integer addition and subtraction inside the envelope the sweep respects.
/// <see cref="LawRegistry"/> invokes each claim below as a Default-tier law.
/// </summary>
internal static class WhtClaims {
    // The lengths every sweep runs at: the two degenerate transforms, a handful of small powers of two, and one past
    // any single machine word's worth of butterflies.
    private static readonly int[] Lengths = [1, 2, 4, 8, 16, 64, 256, 1024];

    // Operand envelope for the long carrier: |x| < 2^40, so N * max|x| < 2^50 at the longest length and no butterfly
    // ever wraps. The int carrier sweep uses |x| < 2^20 for the same reason (N * 2^20 < 2^31 at 1024).
    private const long LongAmplitudeRaw = (1L << 40);
    private const int IntAmplitudeRaw = (1 << 20);

    private static long[] LongSequence(int length, ulong stream) {
        var rng = Pcg32XshRr.Create(state: 0x5748_542D_4E41_5455UL, stream: stream);
        var values = new long[length];

        for (var i = 0; (i < length); ++i) {
            var raw = ((((ulong)rng.NextUInt32()) << 32) | rng.NextUInt32());

            values[i] = (((long)(raw & ((1UL << 41) - 1UL))) - LongAmplitudeRaw);
        }

        return values;
    }
    private static int[] IntSequence(int length, ulong stream) {
        var rng = Pcg32XshRr.Create(state: 0x5748_542D_494E_5433UL, stream: stream);
        var values = new int[length];

        for (var i = 0; (i < length); ++i) {
            values[i] = (((int)(rng.NextUInt32() & ((1U << 21) - 1U))) - IntAmplitudeRaw);
        }

        return values;
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

    /// <summary>Proves <see cref="WalshHadamardTransform.Inverse{T}"/> undoes <see cref="WalshHadamardTransform.Forward{T}"/>
    /// EXACTLY — bit-for-bit — over both the <see cref="long"/> and the <see cref="int"/> carrier at every swept
    /// length, so the generic network is pinned at two widths rather than one.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RoundTripExact() {
        foreach (var length in Lengths) {
            var original = LongSequence(length: length, stream: ((ulong)length));
            var working = ((long[])original.Clone());

            WalshHadamardTransform.Forward<long>(values: working);
            WalshHadamardTransform.Inverse<long>(values: working);

            for (var i = 0; (i < length); ++i) {
                if (working[i] != original[i]) {
                    return $"long carrier, length {length}, index {i}: round trip gave {working[i]}, expected {original[i]}";
                }
            }

            var originalInt = IntSequence(length: length, stream: ((ulong)length));
            var workingInt = ((int[])originalInt.Clone());

            WalshHadamardTransform.Forward<int>(values: workingInt);
            WalshHadamardTransform.Inverse<int>(values: workingInt);

            for (var i = 0; (i < length); ++i) {
                if (workingInt[i] != originalInt[i]) {
                    return $"int carrier, length {length}, index {i}: round trip gave {workingInt[i]}, expected {originalInt[i]}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="WalshHadamardTransform.Forward{T}"/> is EXACTLY linear —
    /// <c>Forward(a) + Forward(b) == Forward(a + b)</c> pointwise — at every swept length.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LinearityExact() {
        foreach (var length in Lengths) {
            var a = LongSequence(length: length, stream: (100UL + ((ulong)length)));
            var b = LongSequence(length: length, stream: (200UL + ((ulong)length)));
            var sum = new long[length];

            for (var i = 0; (i < length); ++i) { sum[i] = (a[i] + b[i]); }

            WalshHadamardTransform.Forward<long>(values: a);
            WalshHadamardTransform.Forward<long>(values: b);
            WalshHadamardTransform.Forward<long>(values: sum);

            for (var k = 0; (k < length); ++k) {
                var expected = (a[k] + b[k]);

                if (sum[k] != expected) {
                    return $"length {length}, bin {k}: Forward(a+b) = {sum[k]}, Forward(a)+Forward(b) = {expected}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="WalshHadamardTransform.Forward{T}"/> matches <see cref="Oracles.WalshHadamardNatural"/>'s
    /// O(N^2) definition-form sum EXACTLY at every swept length — which also pins the ordering as Sylvester (natural),
    /// since a sequency-ordered network would agree at bin zero and disagree elsewhere.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ForwardVsOracleExact() {
        foreach (var length in Lengths) {
            var input = LongSequence(length: length, stream: (300UL + ((ulong)length)));
            var expected = Oracles.WalshHadamardNatural(values: input);
            var actual = ((long[])input.Clone());

            WalshHadamardTransform.Forward<long>(values: actual);

            for (var k = 0; (k < length); ++k) {
                if (actual[k] != expected[k]) {
                    return $"length {length}, bin {k}: Forward gave {actual[k]}, O(N^2) oracle gives {expected[k]}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves Parseval's identity for the unscaled transform — <c>sum X[k]^2 == N * sum x[n]^2</c> —
    /// holds EXACTLY in <see cref="BigInteger"/> at every swept length.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ParsevalExact() {
        foreach (var length in Lengths) {
            var original = LongSequence(length: length, stream: (400UL + ((ulong)length)));
            var timeEnergy = BigInteger.Zero;

            foreach (var v in original) { timeEnergy += (((BigInteger)v) * v); }

            var spectrum = ((long[])original.Clone());

            WalshHadamardTransform.Forward<long>(values: spectrum);

            var frequencyEnergy = BigInteger.Zero;

            foreach (var v in spectrum) { frequencyEnergy += (((BigInteger)v) * v); }

            var expected = (((BigInteger)length) * timeEnergy);

            if (frequencyEnergy != expected) {
                return $"length {length}: sum X^2 = {frequencyEnergy}, N * sum x^2 = {expected}";
            }
        }

        return null;
    }
    /// <summary>Proves every documented refusal: a <see cref="WalshHadamardTransform.Forward{T}"/> or
    /// <see cref="WalshHadamardTransform.Inverse{T}"/> span whose length is zero or not a power of two.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LengthRefusals() {
        return (Refuses(action: () => WalshHadamardTransform.Forward<long>(values: new long[0]), type: typeof(ArgumentException), parameterName: "values", what: "Forward on an empty span") ??
               (Refuses(action: () => WalshHadamardTransform.Forward<long>(values: new long[3]), type: typeof(ArgumentException), parameterName: "values", what: "Forward at length 3 (not a power of two)") ??
               (Refuses(action: () => WalshHadamardTransform.Forward<int>(values: new int[6]), type: typeof(ArgumentException), parameterName: "values", what: "Forward at length 6 (not a power of two)") ??
               (Refuses(action: () => WalshHadamardTransform.Inverse<long>(values: new long[0]), type: typeof(ArgumentException), parameterName: "values", what: "Inverse on an empty span") ??
               Refuses(action: () => WalshHadamardTransform.Inverse<long>(values: new long[5]), type: typeof(ArgumentException), parameterName: "values", what: "Inverse at length 5 (not a power of two)")))));
    }
}
