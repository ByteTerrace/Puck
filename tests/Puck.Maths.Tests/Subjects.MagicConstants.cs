using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? FermatMaskBitOracle() =>
        AtEveryIntegerWidth<FermatMaskBitOracleWidths>();

    private static string? FermatMaskBitOracle<T>() where T : IBinaryInteger<T> {
        // The complete finite exponent domain, including the final half-word mask that ReverseBits never reads.
        var bits = (Unsafe.SizeOf<T>() * 8);
        var wordMask = ((BigInteger.One << bits) - 1);

        for (var exponent = 0; ((1 << exponent) < bits); ++exponent) {
            var block = (1 << exponent);
            var actual = BigInteger.CreateChecked(value: exponent.NthFermatMask<T>()) & wordMask;
            var expected = Oracles.RepeatPatternBits(
                pattern: ((BigInteger.One << block) - 1),
                blockWidth: (block * 2),
                bitWidth: bits
            );

            if (actual != expected) { return $"{typeof(T).Name} exponent={exponent} mask={actual}, expected={expected}"; }
        }

        return null;
    }

    public static string? ReplicationMaskBitOracle() =>
        AtEveryIntegerWidth<ReplicationMaskBitOracleWidths>();

    private static string? ReplicationMaskBitOracle<T>() where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);
        var wordMask = ((BigInteger.One << bits) - 1);

        // Every width, dividing or not: a block that does not divide the word still marks its truncated last copy.
        for (var width = 1; (width <= bits); ++width) {
            var actual = BigInteger.CreateChecked(value: width.ReplicationMask<T>()) & wordMask;
            var expected = Oracles.RepeatPatternBits(
                pattern: BigInteger.One,
                blockWidth: width,
                bitWidth: bits
            );

            if (actual != expected) { return $"{typeof(T).Name} width={width} mask={actual}, expected={expected}"; }
        }

        return null;
    }

    public static string? RepeatBitsBitOracle(long[] left, long[] right) {
        var raw = (((BigInteger)((ulong)left[0])) << 64) | ((ulong)left[1]);
        var selector = ((ulong)right[0]);

        return (RepeatBitsBitOracle<byte>(
            raw: raw,
            selector: selector
        ) ?? (RepeatBitsBitOracle<sbyte>(
            raw: raw,
            selector: selector
        ) ??
            (RepeatBitsBitOracle<ushort>(
            raw: raw,
            selector: selector
        ) ?? (RepeatBitsBitOracle<short>(
            raw: raw,
            selector: selector
        ) ??
            (RepeatBitsBitOracle<uint>(
            raw: raw,
            selector: selector
        ) ?? (RepeatBitsBitOracle<int>(
            raw: raw,
            selector: selector
        ) ??
            (RepeatBitsBitOracle<ulong>(
            raw: raw,
            selector: selector
        ) ?? (RepeatBitsBitOracle<long>(
            raw: raw,
            selector: selector
        ) ??
            (RepeatBitsBitOracle<UInt128>(
            raw: raw,
            selector: selector
        ) ?? (RepeatBitsBitOracle<Int128>(
            raw: raw,
            selector: selector
        ) ??
            (RepeatBitsBitOracle<nuint>(
            raw: raw,
            selector: selector
        ) ?? RepeatBitsBitOracle<nint>(
            raw: raw,
            selector: selector
        ))))))))))));
    }

    private static string? RepeatBitsBitOracle<T>(BigInteger raw, ulong selector) where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);
        var width = (1 + ((int)(selector % ((ulong)bits))));

        return CheckRepeatedPattern<T>(
            pattern: raw & ((BigInteger.One << width) - 1),
            width: width
        );
    }
    private static string? CheckRepeatedPattern<T>(BigInteger pattern, int width) where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);
        var input = T.CreateTruncating(value: pattern);
        var actual = BigInteger.CreateChecked(value: input.RepeatBits(blockWidth: width)) & ((BigInteger.One << bits) - 1);
        var expected = Oracles.RepeatPatternBits(
            bitWidth: bits,
            blockWidth: width,
            pattern: pattern
        );

        return ((actual != expected)
            ? $"{typeof(T).Name} width={width} pattern={pattern}: {actual}, expected={expected}"
            : null
        );
    }

    public static string? PeriodicMaskBoundaries() {
        var failure = AtEveryIntegerWidth<PeriodicMaskBoundariesWidths>();

        if (failure is not null) { return failure; }

        // Every valid byte pattern at every width, including the signed full-word patterns and truncated last copies.
        for (var width = 1; (width <= 8); ++width) {
            for (var pattern = 0; (pattern < (1 << width)); ++pattern) {
                failure = (CheckRepeatedPattern<byte>(
                    pattern: pattern,
                    width: width
                ) ?? CheckRepeatedPattern<sbyte>(
                    pattern: pattern,
                    width: width
                ));
                if (failure is not null) { return failure; }
            }
        }

        return (CheckRepeatedPattern<uint>(
            pattern: 0xAB,
            width: 8
        ) ??
            (MagicConstantRefusal<NotSupportedException>(() => 8.ReplicationMask<BigInteger>()) ??
            MagicConstantRefusal<NotSupportedException>(() => BigInteger.One.RepeatBits(blockWidth: 8))));
    }

    private static string? PeriodicMaskBoundaries<T>() where T : IBinaryInteger<T> {
        var bits = (Unsafe.SizeOf<T>() * 8);

        for (var width = -1; (width <= (bits + 1)); ++width) {
            if (
                (width <= 0) ||
                (width > bits)
            ) {
                var refusal = (MagicConstantRefusal<ArgumentOutOfRangeException>(
                    action: () => width.ReplicationMask<T>(),
                    parameter: "blockWidth"
                ) ??
                    MagicConstantRefusal<ArgumentOutOfRangeException>(
                    action: () => T.One.RepeatBits(blockWidth: width),
                    parameter: "blockWidth"
                ));

                if (refusal is not null) { return $"{typeof(T).Name} width={width}: {refusal}"; }
                continue;
            }

            var blockMask = ((BigInteger.One << width) - 1);
            var failure = (CheckRepeatedPattern<T>(
                pattern: BigInteger.Zero,
                width: width
            ) ?? (CheckRepeatedPattern<T>(
                pattern: BigInteger.One,
                width: width
            ) ??
                (CheckRepeatedPattern<T>(
                pattern: blockMask,
                width: width
            ) ?? CheckRepeatedPattern<T>(
                pattern: (BigInteger.One << (width - 1)),
                width: width
            ))));

            if (failure is not null) { return failure; }
            if (width == bits) { continue; }

            failure = (MagicConstantRefusal<ArgumentOutOfRangeException>(
                action: () => (T.One << width).RepeatBits(blockWidth: width),
                parameter: "value"
            ) ??
                MagicConstantRefusal<ArgumentOutOfRangeException>(
                action: () => T.AllBitsSet.RepeatBits(blockWidth: width),
                parameter: "value"
            ));
            if (failure is not null) { return $"{typeof(T).Name} width={width}: {failure}"; }
        }

        return (MagicConstantRefusal<ArgumentOutOfRangeException>(
            action: () => int.MinValue.ReplicationMask<T>(),
            parameter: "blockWidth"
        ) ??
            MagicConstantRefusal<ArgumentOutOfRangeException>(
            action: () => int.MaxValue.ReplicationMask<T>(),
            parameter: "blockWidth"
        ));
    }
    private static string? MagicConstantRefusal<TException>(Action action, string? parameter = null) where TException : Exception {
        try { action(); } catch (Exception exception) {
            if (exception.GetType() != typeof(TException)) { return $"expected {typeof(TException).Name}, got {exception.GetType().Name}"; }
            if (
                (parameter is not null) &&
                ((exception as ArgumentException)?.ParamName != parameter)
            ) { return $"expected exception parameter {parameter}"; }

            return null;
        }

        return $"expected {typeof(TException).Name}, but the call returned";
    }

    // A statement made once per binary-integer carrier: every signed and unsigned width from a byte to 128 bits, plus
    // the two native widths, first failure wins.
    private interface IIntegerWidthClaim {
        static abstract string? At<T>() where T : IBinaryInteger<T>;
    }
    private readonly struct FermatMaskBitOracleWidths : IIntegerWidthClaim {
        public static string? At<T>() where T : IBinaryInteger<T> => FermatMaskBitOracle<T>();
    }
    private readonly struct ReplicationMaskBitOracleWidths : IIntegerWidthClaim {
        public static string? At<T>() where T : IBinaryInteger<T> => ReplicationMaskBitOracle<T>();
    }
    private readonly struct PeriodicMaskBoundariesWidths : IIntegerWidthClaim {
        public static string? At<T>() where T : IBinaryInteger<T> => PeriodicMaskBoundaries<T>();
    }

    private static string? AtEveryIntegerWidth<TClaim>()
        where TClaim : IIntegerWidthClaim =>
        (TClaim.At<byte>() ?? (TClaim.At<sbyte>() ?? (TClaim.At<ushort>() ?? (TClaim.At<short>() ?? (TClaim.At<uint>() ?? (TClaim.At<int>() ??
            (TClaim.At<ulong>() ?? (TClaim.At<long>() ?? (TClaim.At<UInt128>() ?? (TClaim.At<Int128>() ?? (TClaim.At<nuint>() ?? TClaim.At<nint>())))))))))));
}
