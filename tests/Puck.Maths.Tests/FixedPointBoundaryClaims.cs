using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static class FixedPointBoundaryClaims {
    private static readonly int[] InvalidInverseShifts = [-1, -2, -31, int.MinValue, int.MaxValue];
    private static readonly int[] NegativeShifts = [-1, int.MinValue];
    private static readonly BigInteger[] RationalNumerators = [BigInteger.Zero, BigInteger.One, -BigInteger.One];
    private static readonly decimal[] Decimals = [0m, 1m, -1m, decimal.MaxValue];
    private static readonly long[] SingleMantissas = [1L << 23, (1L << 23) + 1, (1L << 24) - 1];
    private static readonly long[] Deltas = [-1, 0, 1];
    private static readonly long[] Signs = [-1, 1];
    private static readonly long[] SingleEdges = [0, 1, -1, 65536, -65536, (1L << 24) - 1, long.MinValue, long.MaxValue];
    private static readonly int[] Scales = [16, 32];
    private static readonly long[] CarrierEnds = [long.MinValue, long.MaxValue];
    private static readonly ulong[] UnsignedPeers = [0, 1, 65535, 65536, (1UL << 47) - 1, 1UL << 47, 1UL << 63, ulong.MaxValue];
    private static readonly long[] SignedPeers = [long.MinValue, -98305, -98304, -98303, -32769, -32768, -1,
        0, 1, 32767, 32768, 32769, 98303, 98304, 98305, long.MaxValue];

    private static class ConvertTo<T> where T : INumberBase<T> {
        public static T Checked<TSource>(TSource value) where TSource : INumberBase<TSource> => T.CreateChecked(value);
        public static T Saturating<TSource>(TSource value) where TSource : INumberBase<TSource> => T.CreateSaturating(value);
        public static T Truncating<TSource>(TSource value) where TSource : INumberBase<TSource> => T.CreateTruncating(value);
    }

    public static string? InverseShiftsRefuse() {
        foreach (var shift in InvalidInverseShifts) {
            Assert.False(FixedSymmetricSolve.TryInvertSymmetric2(1, 0, 1, shift, out var a, out var b, out var d));
            Assert.Equal((0L, 0L, 0L), (a, b, d));
            Assert.False(FixedSymmetricSolve.TryInvertSymmetric3(1, 0, 0, 1, 0, 1, shift,
                out a, out b, out var c, out d, out var e, out var f));
            Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L), (a, b, c, d, e, f));
        }
        Assert.True(FixedSymmetricSolve.TryInvertSymmetric2(1, 0, 1, 0, out var x, out _, out _));
        Assert.Equal(1L, x);
        return null;
    }

    public static string? RationalArgumentsRefuse() {
        foreach (var numerator in RationalNumerators) {
            Assert.Throws<DivideByZeroException>(() => FixedPointRounding.RoundRational(numerator, BigInteger.Zero, 0));
            foreach (var shift in NegativeShifts) {
                Assert.Throws<ArgumentOutOfRangeException>("fractionBitCount",
                    () => FixedPointRounding.RoundRational(numerator, BigInteger.One, shift));
            }
        }
        return null;
    }

    public static string? DecimalShiftRefuses() {
        foreach (var value in Decimals) {
            foreach (var shift in NegativeShifts) {
                Assert.Throws<ArgumentOutOfRangeException>("fractionBitCount",
                    () => FixedPointConvert.ScaleDecimalWide(value, shift));
            }
        }
        return null;
    }

    public static string? SingleRoundsOnce() {
        // Every representable raw binade, even/odd midpoint parity and both sides of each midpoint, including
        // the 53-bit double precision boundary where the old intermediate manufactures an exact float tie.
        for (var bit = 24; bit < 63; ++bit) {
            foreach (var mantissa in SingleMantissas) {
                var midpoint = (mantissa << (bit - 23)) + (1L << (bit - 24));
                foreach (var delta in Deltas) {
                    foreach (var sign in Signs) {
                        var raw = (midpoint + delta) * sign;
                        var value = FixedQ4816.FromRawBits(raw);
                        var expected = Oracles.NearestBinaryPresentationBits(raw, 16, 23, 8);
                        EqualBits(expected, BitConverter.SingleToUInt32Bits((float)value), raw, "float explicit");
                        EqualBits(expected, BitConverter.SingleToUInt32Bits(float.CreateChecked(value)), raw, "float checked");
                        EqualBits(expected, BitConverter.SingleToUInt32Bits(float.CreateSaturating(value)), raw, "float saturating");
                        EqualBits(expected, BitConverter.SingleToUInt32Bits(float.CreateTruncating(value)), raw, "float truncating");
                    }
                }
            }
        }
        foreach (var raw in SingleEdges) {
            Assert.Equal(Oracles.NearestBinaryPresentationBits(raw, 16, 23, 8),
                BitConverter.SingleToUInt32Bits((float)FixedQ4816.FromRawBits(raw)));
        }
        return null;
    }

    public static string? HalfRoundsOnce() {
        CheckHalfRaw(0, 16);
        CheckHalfRaw(0, 32);
        // Every binary16 midpoint, including the subnormal/normal, underflow and finite/infinity seams.
        for (var exponent = 0; exponent <= 30; ++exponent) {
            for (var fraction = 0; fraction < 1024; ++fraction) {
                var units = exponent == 0 ? fraction : (BigInteger)(1024 + fraction) << (exponent - 1);
                var nextUnits = units + (BigInteger.One << Math.Max(0, exponent - 1));
                foreach (var scale in Scales) {
                    // The midpoint has denominator 2^25. Integer division gives its raw neighbourhood even
                    // where Q16 cannot represent the midpoint itself.
                    var center = ((units + nextUnits) << scale) >> 25;
                    for (var delta = -1; delta <= 1; ++delta) {
                        CheckHalfRaw(center + delta, scale);
                        CheckHalfRaw(-(center + delta), scale);
                    }
                }
            }
        }
        foreach (var raw in CarrierEnds) {
            CheckHalfRaw(raw, 16);
            CheckHalfRaw(raw, 32);
        }
        CheckHalf(UFixedQ4816.MaxValue, ulong.MaxValue, 16);
        return null;
    }

    private static void CheckHalfRaw(BigInteger raw, int scale) {
        if (scale == 16) {
            CheckHalf(FixedQ4816.FromRawBits((long)raw), raw, scale);
            if (raw.Sign >= 0) { CheckHalf(UFixedQ4816.FromRawBits((ulong)raw), raw, scale); }
        } else {
            CheckHalf(FixedQ3232.FromRawBits((long)raw), raw, scale);
        }
    }

    private static void CheckHalf<T>(T value, BigInteger raw, int scale) where T : INumberBase<T> {
        var expected = (ushort)Oracles.NearestBinaryPresentationBits(raw, scale, 10, 5);
        EqualBits(expected, BitConverter.HalfToUInt16Bits(Half.CreateChecked(value)), raw, $"{typeof(T).Name} Half checked");
        EqualBits(expected, BitConverter.HalfToUInt16Bits(Half.CreateSaturating(value)), raw, $"{typeof(T).Name} Half saturating");
        EqualBits(expected, BitConverter.HalfToUInt16Bits(Half.CreateTruncating(value)), raw, $"{typeof(T).Name} Half truncating");
    }

    private static void EqualBits(uint expected, uint actual, BigInteger raw, string conversion) =>
        Assert.True(expected == actual, $"{conversion} at raw {raw}: expected 0x{expected:X}, got 0x{actual:X}");

    public static string? UnsignedPeersRoundAndApplyRangePolicy() {
        foreach (var raw in UnsignedPeers) {
            var source = UFixedQ4816.FromRawBits(raw);
            var exact = (BigInteger)raw << 16;
            var saturated = (long)BigInteger.Min(exact, long.MaxValue);
            Assert.Equal(saturated, ConvertTo<FixedQ3232>.Saturating(source).Value);
            Assert.Equal(Oracles.WrapToRaw(exact), ConvertTo<FixedQ3232>.Truncating(source).Value);
            if (exact <= long.MaxValue) {
                var converted = ConvertTo<FixedQ3232>.Checked(source);
                Assert.Equal((long)exact, converted.Value);
                Assert.Equal(source, ConvertTo<UFixedQ4816>.Checked(converted));
                Assert.Equal(source, ConvertTo<UFixedQ4816>.Saturating(converted));
                Assert.Equal(source, ConvertTo<UFixedQ4816>.Truncating(converted));
            } else {
                Assert.Throws<OverflowException>(() => ConvertTo<FixedQ3232>.Checked(source));
            }
        }
        foreach (var raw in SignedPeers) {
            var source = FixedQ3232.FromRawBits(raw);
            var rounded = Oracles.RoundRationalTiesToEven(raw, BigInteger.One << 16);
            Assert.Equal((ulong)BigInteger.Max(rounded, 0), ConvertTo<UFixedQ4816>.Saturating(source).Value);
            Assert.Equal(Oracles.WrapToUnsignedRaw(rounded), ConvertTo<UFixedQ4816>.Truncating(source).Value);
            if (rounded.Sign < 0) {
                Assert.Throws<OverflowException>(() => ConvertTo<UFixedQ4816>.Checked(source));
            } else {
                Assert.Equal((ulong)rounded, ConvertTo<UFixedQ4816>.Checked(source).Value);
            }
        }
        return null;
    }
}
