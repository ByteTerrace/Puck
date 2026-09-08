using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? FermatMaskBitOracle() =>
        FermatMaskBitOracle<byte>() ?? FermatMaskBitOracle<sbyte>() ??
        FermatMaskBitOracle<ushort>() ?? FermatMaskBitOracle<short>() ??
        FermatMaskBitOracle<uint>() ?? FermatMaskBitOracle<int>() ??
        FermatMaskBitOracle<ulong>() ?? FermatMaskBitOracle<long>() ??
        FermatMaskBitOracle<UInt128>() ?? FermatMaskBitOracle<Int128>() ??
        FermatMaskBitOracle<nuint>() ?? FermatMaskBitOracle<nint>();

    private static string? FermatMaskBitOracle<T>() where T : IBinaryInteger<T> {
        // Resolve the internal kernel directly so its complete finite exponent domain is exercised, including
        // the final half-word mask that ReverseBits implements with a separate final swap.
        var subject = typeof(BinaryIntegerFunctions).GetMethod(name: "NthFermatMask", bindingAttr: BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeArguments: typeof(T)).CreateDelegate<Func<int, T>>();
        var bits = (Unsafe.SizeOf<T>() * 8);
        var wordMask = ((BigInteger.One << bits) - 1);

        for (var exponent = 0; (1 << exponent) < bits; ++exponent) {
            var block = (1 << exponent);
            var actual = (BigInteger.CreateChecked(value: subject(exponent)) & wordMask);
            var expected = Oracles.RepeatPatternBits(pattern: ((BigInteger.One << block) - 1), blockWidth: (block * 2), bitWidth: bits);

            if (actual != expected) { return $"{typeof(T).Name} exponent={exponent} mask={actual}, expected={expected}"; }
        }

        return null;
    }

    public static string? ReplicationMaskBitOracle() =>
        ReplicationMaskBitOracle<byte>() ?? ReplicationMaskBitOracle<sbyte>() ??
        ReplicationMaskBitOracle<ushort>() ?? ReplicationMaskBitOracle<short>() ??
        ReplicationMaskBitOracle<uint>() ?? ReplicationMaskBitOracle<int>() ??
        ReplicationMaskBitOracle<ulong>() ?? ReplicationMaskBitOracle<long>() ??
        ReplicationMaskBitOracle<UInt128>() ?? ReplicationMaskBitOracle<Int128>() ??
        ReplicationMaskBitOracle<nuint>() ?? ReplicationMaskBitOracle<nint>();

    private static string? ReplicationMaskBitOracle<T>() where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);
        var wordMask = ((BigInteger.One << bits) - 1);

        for (var width = 1; width <= bits; ++width) {
            if ((bits % width) != 0) { continue; }

            var actual = (BigInteger.CreateChecked(value: width.ReplicationMask<T>()) & wordMask);
            var expected = Oracles.RepeatPatternBits(pattern: BigInteger.One, blockWidth: width, bitWidth: bits);

            if (actual != expected) { return $"{typeof(T).Name} width={width} mask={actual}, expected={expected}"; }
        }

        return null;
    }

    public static string? RepeatBitsBitOracle(long[] left, long[] right) {
        var raw = (((BigInteger)(ulong)left[0] << 64) | (ulong)left[1]);
        var selector = (ulong)right[0];

        return RepeatBitsBitOracle<byte>(raw, selector) ?? RepeatBitsBitOracle<sbyte>(raw, selector) ??
            RepeatBitsBitOracle<ushort>(raw, selector) ?? RepeatBitsBitOracle<short>(raw, selector) ??
            RepeatBitsBitOracle<uint>(raw, selector) ?? RepeatBitsBitOracle<int>(raw, selector) ??
            RepeatBitsBitOracle<ulong>(raw, selector) ?? RepeatBitsBitOracle<long>(raw, selector) ??
            RepeatBitsBitOracle<UInt128>(raw, selector) ?? RepeatBitsBitOracle<Int128>(raw, selector) ??
            RepeatBitsBitOracle<nuint>(raw, selector) ?? RepeatBitsBitOracle<nint>(raw, selector);
    }

    private static string? RepeatBitsBitOracle<T>(BigInteger raw, ulong selector) where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);
        var width = (1 << (int)(selector % (ulong)(BitOperations.Log2(value: (uint)bits) + 1)));

        return CheckRepeatedPattern<T>(pattern: (raw & ((BigInteger.One << width) - 1)), width: width);
    }

    private static string? CheckRepeatedPattern<T>(BigInteger pattern, int width) where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);
        var input = T.CreateTruncating(value: pattern);
        var actual = (BigInteger.CreateChecked(value: input.RepeatBits(blockWidth: width)) & ((BigInteger.One << bits) - 1));
        var expected = Oracles.RepeatPatternBits(pattern: pattern, blockWidth: width, bitWidth: bits);

        return ((actual != expected) ? $"{typeof(T).Name} width={width} pattern={pattern}: {actual}, expected={expected}" : null);
    }

    public static string? PeriodicMaskBoundaries() {
        var failure = PeriodicMaskBoundaries<byte>() ?? PeriodicMaskBoundaries<sbyte>() ??
            PeriodicMaskBoundaries<ushort>() ?? PeriodicMaskBoundaries<short>() ??
            PeriodicMaskBoundaries<uint>() ?? PeriodicMaskBoundaries<int>() ??
            PeriodicMaskBoundaries<ulong>() ?? PeriodicMaskBoundaries<long>() ??
            PeriodicMaskBoundaries<UInt128>() ?? PeriodicMaskBoundaries<Int128>() ??
            PeriodicMaskBoundaries<nuint>() ?? PeriodicMaskBoundaries<nint>();

        if (failure is not null) { return failure; }

        // Every valid byte pattern, including the signed full-word patterns.
        for (var width = 1; width <= 8; width *= 2) {
            for (var pattern = 0; pattern < (1 << width); ++pattern) {
                failure = CheckRepeatedPattern<byte>(pattern, width) ?? CheckRepeatedPattern<sbyte>(pattern, width);
                if (failure is not null) { return failure; }
            }
        }

        return CheckRepeatedPattern<uint>(pattern: 0xAB, width: 8) ??
            MagicConstantRefusal<NotSupportedException>(() => 8.ReplicationMask<BigInteger>()) ??
            MagicConstantRefusal<NotSupportedException>(() => BigInteger.One.RepeatBits(blockWidth: 8));
    }

    private static string? PeriodicMaskBoundaries<T>() where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);

        for (var width = -1; width <= (bits + 1); ++width) {
            if ((width <= 0) || (width > bits) || ((bits % width) != 0)) {
                var refusal = MagicConstantRefusal<ArgumentOutOfRangeException>(() => width.ReplicationMask<T>(), "blockWidth") ??
                    MagicConstantRefusal<ArgumentOutOfRangeException>(() => T.One.RepeatBits(blockWidth: width), "blockWidth");
                if (refusal is not null) { return $"{typeof(T).Name} width={width}: {refusal}"; }
                continue;
            }

            var blockMask = ((BigInteger.One << width) - 1);
            var failure = CheckRepeatedPattern<T>(BigInteger.Zero, width) ?? CheckRepeatedPattern<T>(BigInteger.One, width) ??
                CheckRepeatedPattern<T>(blockMask, width) ?? CheckRepeatedPattern<T>(BigInteger.One << (width - 1), width);
            if (failure is not null) { return failure; }
            if (width == bits) { continue; }

            failure = MagicConstantRefusal<ArgumentOutOfRangeException>(
                () => (T.One << width).RepeatBits(blockWidth: width), "value") ??
                MagicConstantRefusal<ArgumentOutOfRangeException>(
                    () => T.AllBitsSet.RepeatBits(blockWidth: width), "value");
            if (failure is not null) { return $"{typeof(T).Name} width={width}: {failure}"; }
        }

        return MagicConstantRefusal<ArgumentOutOfRangeException>(() => int.MinValue.ReplicationMask<T>(), "blockWidth") ??
            MagicConstantRefusal<ArgumentOutOfRangeException>(() => int.MaxValue.ReplicationMask<T>(), "blockWidth");
    }

    private static string? MagicConstantRefusal<TException>(Action action, string? parameter = null) where TException : Exception {
        try { action(); }
        catch (Exception exception) {
            if (exception.GetType() != typeof(TException)) { return $"expected {typeof(TException).Name}, got {exception.GetType().Name}"; }
            if ((parameter is not null) && ((exception as ArgumentException)?.ParamName != parameter)) { return $"expected exception parameter {parameter}"; }

            return null;
        }

        return $"expected {typeof(TException).Name}, but the call returned";
    }
}
