using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The low <paramref name="width"/> bits of a word, as a 128-bit bit string.</summary>
    public static UInt128 WordMask(int width) =>
        ((width >= 128)
            ? UInt128.MaxValue
            : ((UInt128.One << width) - UInt128.One));
    /// <summary>Walks every word position from the bottom and copies the next unread source bit into each position the mask selects.</summary>
    public static UInt128 DepositBits(UInt128 value, UInt128 mask, int width) {
        var result = UInt128.Zero;
        var source = 0;

        for (var position = 0; (position < width); ++position) {
            if (((mask >> position) & UInt128.One) == UInt128.Zero) { continue; }
            if (((value >> source) & UInt128.One) != UInt128.Zero) { result |= (UInt128.One << position); }

            ++source;
        }

        return result;
    }
    /// <summary>Walks every word position from the bottom and appends the value's bit at each position the mask selects.</summary>
    public static UInt128 ExtractBits(UInt128 value, UInt128 mask, int width) {
        var result = UInt128.Zero;
        var destination = 0;

        for (var position = 0; (position < width); ++position) {
            if (((mask >> position) & UInt128.One) == UInt128.Zero) { continue; }
            if (((value >> position) & UInt128.One) != UInt128.Zero) { result |= (UInt128.One << destination); }

            ++destination;
        }

        return result;
    }
    /// <summary>Places bit <c>i</c> of operand <c>a</c> at position <c>ways * i + a</c>, dropping every position at or above <paramref name="resultWidth"/>.</summary>
    public static UInt128 Interleave(UInt128[] operands, int inputWidth, int resultWidth) {
        var ways = operands.Length;
        var result = UInt128.Zero;

        for (var lane = 0; (lane < ways); ++lane) {
            for (var bit = 0; (bit < inputWidth); ++bit) {
                var position = ((ways * bit) + lane);

                if (
                    (position < resultWidth) &&
                    (((operands[lane] >> bit) & UInt128.One) != UInt128.Zero)
                ) { result |= (UInt128.One << position); }
            }
        }

        return result;
    }
    /// <summary>Reads component <paramref name="lane"/> of a <paramref name="ways"/>-way interleave: its bit <c>i</c> is the input bit at <c>ways * i + lane</c>, kept below <paramref name="resultWidth"/>.</summary>
    public static UInt128 Deinterleave(UInt128 value, int ways, int lane, int inputWidth, int resultWidth) {
        var result = UInt128.Zero;

        for (var bit = 0; (bit < resultWidth); ++bit) {
            var position = ((ways * bit) + lane);

            if (position >= inputWidth) { break; }
            if (((value >> position) & UInt128.One) != UInt128.Zero) { result |= (UInt128.One << bit); }
        }

        return result;
    }
    /// <summary>Every bit from the highest set bit of a <paramref name="width"/>-bit word down to bit zero, found by scanning down from the top.</summary>
    public static UInt128 SmearBelowHighestSetBit(UInt128 bits, int width) {
        for (var position = (width - 1); (position >= 0); --position) {
            if (((bits >> position) & UInt128.One) != UInt128.Zero) { return WordMask(width: (position + 1)); }
        }

        return UInt128.Zero;
    }
    /// <summary>Rounds a mathematical integer to a multiple of the alignment by exact floored or ceiling division, then reduces it into a <paramref name="width"/>-bit two's-complement word.</summary>
    public static BigInteger Align(BigInteger value, BigInteger alignment, bool up, int width) {
        var quotient = BigInteger.DivRem(
            dividend: value,
            divisor: alignment,
            remainder: out var remainder
        );

        if (!remainder.IsZero) {
            if (up && (remainder.Sign > 0)) { ++quotient; }
            if (!up && (remainder.Sign < 0)) { --quotient; }
        }

        var modulus = (BigInteger.One << width);
        var wrapped = ((((quotient * alignment) % modulus) + modulus) % modulus);

        return wrapped;
    }
}
