using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static class FixedPointBoundaryClaims {
    private static readonly int[] InvalidInverseShifts = [-1, -2, -31, int.MinValue, 128, int.MaxValue];
    private static readonly int[] NegativeShifts = [-1, int.MinValue];
    private static readonly BigInteger[] RationalNumerators = [BigInteger.Zero, BigInteger.One, -BigInteger.One];
    private static readonly decimal[] Decimals = [0m, 1m, -1m, decimal.MaxValue];
    private static readonly long[] SingleMantissas = [(1L << 23), ((1L << 23) + 1), ((1L << 24) - 1)];
    private static readonly long[] Deltas = [-1, 0, 1];
    private static readonly long[] Signs = [-1, 1];
    private static readonly long[] SingleEdges = [0, 1, -1, 65536, -65536, ((1L << 24) - 1), long.MinValue, long.MaxValue];
    private static readonly int[] Scales = [16, 32];
    private static readonly long[] CarrierEnds = [long.MinValue, long.MaxValue];
    private static readonly ulong[] UnsignedPeers = [0, 1, 65535, 65536, ((1UL << 47) - 1), (1UL << 47), (1UL << 63), ulong.MaxValue];
    private static readonly long[] SignedPeers = [long.MinValue, (long.MinValue + 32767), (long.MinValue + 32768),
        (long.MinValue + 32769), -98305, -98304, -98303, -32769, -32768, -1,
        0, 1, 32767, 32768, 32769, 98303, 98304, 98305,
        (long.MaxValue - 32768), (long.MaxValue - 32767), (long.MaxValue - 32766), long.MaxValue];

    private static class ConvertTo<T> where T : INumberBase<T> {
        public static T Checked<TSource>(TSource value) where TSource : INumberBase<TSource> => T.CreateChecked(value: value);
        public static T Saturating<TSource>(TSource value) where TSource : INumberBase<TSource> => T.CreateSaturating(value: value);
        public static T Truncating<TSource>(TSource value) where TSource : INumberBase<TSource> => T.CreateTruncating(value: value);
    }

    public static string? InverseShiftsRefuse() {
        foreach (var shift in InvalidInverseShifts) {
            Assert.False(condition: FixedSymmetricSolve.TryInvertSymmetric2(a: 1, b: 0, d: 1, invA: out var a, invB: out var b, invD: out var d, outputFractionShift: shift));
            Assert.Equal(actual: (a, b, d), expected: (0L, 0L, 0L));
            Assert.False(condition: FixedSymmetricSolve.TryInvertSymmetric3(a: 1, b: 0, c: 0, d: 1, e: 0, f: 1, invA: out a,
                invB: out b, invC: out var c, invD: out d, invE: out var e, invF: out var f, outputFractionShift: shift));
            Assert.Equal(actual: (a, b, c, d, e, f), expected: (0L, 0L, 0L, 0L, 0L, 0L));
        }

        Assert.True(condition: FixedSymmetricSolve.TryInvertSymmetric2(a: 1, b: 0, d: 1, invA: out var x, invB: out _, invD: out _, outputFractionShift: 0));
        Assert.Equal(actual: x, expected: 1L);
        return null;
    }
    public static string? RationalArgumentsRefuse() {
        foreach (var numerator in RationalNumerators) {
            Assert.Throws<DivideByZeroException>(testCode: () => FixedPointRounding.RoundRational(numerator, BigInteger.Zero, 0));
            foreach (var shift in NegativeShifts) {
                Assert.Throws<ArgumentOutOfRangeException>(paramName: "fractionBitCount",
                    testCode: () => FixedPointRounding.RoundRational(numerator, BigInteger.One, shift));
            }
        }
        return null;
    }
    public static string? SolveShiftsRefuse() {
        foreach (var shift in NegativeShifts) {
            Assert.False(condition: FixedSymmetricSolve.TrySolveSymmetric2(a: 1, b: 0, d: 1, outputFractionShift: shift, rhsX: 1, rhsY: 1, x: out var x, y: out var y));
            Assert.Equal(actual: (x, y), expected: (0L, 0L));
            Assert.False(condition: FixedSymmetricSolve.TrySolveSymmetric3(a: 1, b: 0, c: 0, d: 1, e: 0, f: 1, outputFractionShift: shift, rhsX: 1, rhsY: 1, rhsZ: 1,
                x: out x, y: out y, z: out var z));
            Assert.Equal(actual: (x, y, z), expected: (0L, 0L, 0L));
        }

        return null;
    }
    public static string? DecimalShiftRefuses() {
        foreach (var value in Decimals) {
            foreach (var shift in NegativeShifts) {
                Assert.Throws<ArgumentOutOfRangeException>(paramName: "fractionBitCount",
                    testCode: () => FixedPointConvert.ScaleDecimalWide(fractionBitCount: shift, value: value));
            }
        }
        return null;
    }
    public static string? SingleRoundsOnce() {
        // Every representable raw binade, even/odd midpoint parity and both sides of each midpoint, including
        // the 53-bit double precision boundary where the old intermediate manufactures an exact float tie.
        for (var bit = 24; (bit < 63); ++bit) {
            foreach (var mantissa in SingleMantissas) {
                var midpoint = ((mantissa << (bit - 23)) + (1L << (bit - 24)));

                foreach (var delta in Deltas) {
                    foreach (var sign in Signs) {
                        var raw = ((midpoint + delta) * sign);

                        AssertSingleConversions(raw: raw);
                    }
                }

            }
        }

        foreach (var raw in SingleEdges) {
            AssertSingleConversions(raw: raw);
        }

        return null;
    }

    private static void AssertSingleConversions(long raw) {
        var value = FixedQ4816.FromRawBits(value: raw);
        var expected = Oracles.NearestBinaryPresentationBits(exponentBits: 8, fractionBits: 16, mantissaBits: 23, raw: raw);

        EqualBits(expected, BitConverter.SingleToUInt32Bits(value: ((float)value)), raw, "float explicit");
        EqualBits(expected, BitConverter.SingleToUInt32Bits(value: float.CreateChecked(value: value)), raw, "float checked");
        EqualBits(expected, BitConverter.SingleToUInt32Bits(value: float.CreateSaturating(value: value)), raw, "float saturating");
        EqualBits(expected, BitConverter.SingleToUInt32Bits(value: float.CreateTruncating(value: value)), raw, "float truncating");
    }

    public static string? DirectPeerHooksMatchExactScaling() {
        foreach (var raw in SignedPeers) {
            var source = FixedQ3232.FromRawBits(value: raw);
            var rounded = Oracles.RoundRationalTiesToEven(raw, (BigInteger.One << 16));

            CheckDirectHooks<FixedQ3232, UFixedQ4816>(source, rounded, true, static value => value.Value);
            CheckDirectHooks<FixedQ3232, FixedQ4816>(source, rounded, false, static value => value.Value);
        }

        foreach (var raw in UnsignedPeers) {
            CheckDirectHooks<UFixedQ4816, FixedQ3232>(UFixedQ4816.FromRawBits(value: raw), (((BigInteger)raw) << 16),
                false, static value => value.Value);
        }

        foreach (var raw in SingleEdges) {
            CheckDirectHooks<FixedQ4816, FixedQ3232>(FixedQ4816.FromRawBits(value: raw), (((BigInteger)raw) << 16),
                false, static value => value.Value);
        }

        return null;
    }

    private static void CheckDirectHooks<TSource, TTarget>(TSource source, BigInteger exact, bool unsigned,
        Func<TTarget, BigInteger> raw) where TSource : struct, INumberBase<TSource> where TTarget : struct, INumberBase<TTarget> {
        var minimum = (unsigned ? BigInteger.Zero : long.MinValue);
        var maximum = (unsigned ? (BigInteger)ulong.MaxValue : long.MaxValue);
        var saturated = BigInteger.Clamp(max: maximum, min: minimum, value: exact);
        var truncated = (unsigned ? (BigInteger)Oracles.WrapToUnsignedRaw(value: exact) : Oracles.WrapToRaw(value: exact));

        Assert.True(condition: IDirectNumberConversions<TTarget>.FromSaturating(result: out var from, value: source));
        Assert.True(condition: IDirectNumberConversions<TSource>.ToSaturating(result: out TTarget to, value: source));
        Assert.Equal(saturated, raw(from));
        Assert.Equal(saturated, raw(to));
        Assert.True(condition: IDirectNumberConversions<TTarget>.FromTruncating(result: out from, value: source));
        Assert.True(condition: IDirectNumberConversions<TSource>.ToTruncating(result: out to, value: source));
        Assert.Equal(truncated, raw(from));
        Assert.Equal(truncated, raw(to));
        if ((exact < minimum) || (exact > maximum)) {
            Assert.Throws<OverflowException>(testCode: () => IDirectNumberConversions<TTarget>.FromChecked(result: out _, value: source));
            Assert.Throws<OverflowException>(testCode: () => IDirectNumberConversions<TSource>.ToChecked(result: out TTarget _, value: source));
        } else {
            Assert.True(condition: IDirectNumberConversions<TTarget>.FromChecked(result: out from, value: source));
            Assert.True(condition: IDirectNumberConversions<TSource>.ToChecked(result: out to, value: source));
            Assert.Equal(exact, raw(from));
            Assert.Equal(exact, raw(to));
        }
    }

    public static string? HalfRoundsOnce() {
        CheckHalfRaw(raw: 0, scale: 16);
        CheckHalfRaw(raw: 0, scale: 32);
        // Every binary16 midpoint, including the subnormal/normal, underflow and finite/infinity seams.
        for (var exponent = 0; (exponent <= 30); ++exponent) {
            for (var fraction = 0; (fraction < 1024); ++fraction) {
                var units = ((exponent == 0) ? fraction : (((BigInteger)(1024 + fraction)) << (exponent - 1)));
                var nextUnits = (units + (BigInteger.One << Math.Max(val1: 0, val2: (exponent - 1))));

                foreach (var scale in Scales) {
                    // The midpoint has denominator 2^25. Integer division gives its raw neighbourhood even
                    // where Q16 cannot represent the midpoint itself.
                    var center = (((units + nextUnits) << scale) >> 25);

                    for (var delta = -1; (delta <= 1); ++delta) {
                        CheckHalfRaw(raw: (center + delta), scale: scale);
                        CheckHalfRaw(raw: -(center + delta), scale: scale);
                    }
                }

            }
        }

        foreach (var raw in CarrierEnds) {
            CheckHalfRaw(raw: raw, scale: 16);
            CheckHalfRaw(raw: raw, scale: 32);
        }

        CheckHalf(UFixedQ4816.MaxValue, ulong.MaxValue, 16);
        return null;
    }

    private static void CheckHalfRaw(BigInteger raw, int scale) {
        if (scale == 16) {
            CheckHalf(FixedQ4816.FromRawBits(value: ((long)raw)), raw, scale);
            if (raw.Sign >= 0) { CheckHalf(UFixedQ4816.FromRawBits(value: ((ulong)raw)), raw, scale); }
        } else {
            CheckHalf(FixedQ3232.FromRawBits(value: ((long)raw)), raw, scale);
        }
    }
    private static void CheckHalf<T>(T value, BigInteger raw, int scale) where T : INumberBase<T> {
        var expected = ((ushort)Oracles.NearestBinaryPresentationBits(exponentBits: 5, fractionBits: scale, mantissaBits: 10, raw: raw));

        EqualBits(expected, BitConverter.HalfToUInt16Bits(value: Half.CreateChecked(value: value)), raw, $"{typeof(T).Name} Half checked");
        EqualBits(expected, BitConverter.HalfToUInt16Bits(value: Half.CreateSaturating(value: value)), raw, $"{typeof(T).Name} Half saturating");
        EqualBits(expected, BitConverter.HalfToUInt16Bits(value: Half.CreateTruncating(value: value)), raw, $"{typeof(T).Name} Half truncating");
    }
    private static void EqualBits(uint expected, uint actual, BigInteger raw, string conversion) =>
        Assert.True(condition: (expected == actual), userMessage: $"{conversion} at raw {raw}: expected 0x{expected:X}, got 0x{actual:X}");

    public static string? UnsignedPeersRoundAndApplyRangePolicy() {
        foreach (var raw in UnsignedPeers) {
            var source = UFixedQ4816.FromRawBits(value: raw);
            var exact = (((BigInteger)raw) << 16);
            var saturated = ((long)BigInteger.Min(left: exact, right: long.MaxValue));

            Assert.Equal(saturated, ConvertTo<FixedQ3232>.Saturating(value: source).Value);
            Assert.Equal(Oracles.WrapToRaw(value: exact), ConvertTo<FixedQ3232>.Truncating(value: source).Value);
            if (exact <= long.MaxValue) {
                var converted = ConvertTo<FixedQ3232>.Checked(value: source);

                Assert.Equal(((long)exact), converted.Value);
                Assert.Equal(source, ConvertTo<UFixedQ4816>.Checked(value: converted));
                Assert.Equal(source, ConvertTo<UFixedQ4816>.Saturating(value: converted));
                Assert.Equal(source, ConvertTo<UFixedQ4816>.Truncating(value: converted));
            } else {
                Assert.Throws<OverflowException>(testCode: () => ConvertTo<FixedQ3232>.Checked(value: source));
            }
        }
        foreach (var raw in SignedPeers) {
            var source = FixedQ3232.FromRawBits(value: raw);
            var rounded = Oracles.RoundRationalTiesToEven(raw, (BigInteger.One << 16));

            Assert.Equal(((ulong)BigInteger.Max(left: rounded, right: 0)), ConvertTo<UFixedQ4816>.Saturating(value: source).Value);
            Assert.Equal(Oracles.WrapToUnsignedRaw(value: rounded), ConvertTo<UFixedQ4816>.Truncating(value: source).Value);
            if (rounded.Sign < 0) {
                Assert.Throws<OverflowException>(testCode: () => ConvertTo<UFixedQ4816>.Checked(value: source));
            } else {
                Assert.Equal(((ulong)rounded), ConvertTo<UFixedQ4816>.Checked(value: source).Value);
            }
        }
        return null;
    }
}
