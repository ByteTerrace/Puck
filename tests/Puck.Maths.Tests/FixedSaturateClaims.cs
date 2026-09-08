using System.Numerics;

namespace Puck.Maths.Tests;

internal static class FixedSaturateClaims {
    // The oracle is exact integer arithmetic clamped to the long range; it shares no comparison with the subject,
    // which decides on Int128 against the long extremes.
    private static long Clamp(BigInteger value) => (
        (value < long.MinValue)
            ? long.MinValue
            : ((value > long.MaxValue)
                ? long.MaxValue
                : ((long)value))
    );

    public static string? ClampsToTheExtremes(long[] left, long[] right) {
        for (var lane = 0; (lane < left.Length); ++lane) {
            var sum = FixedSaturate.Add(left: FixedQ4816.FromRawBits(value: left[lane]), right: FixedQ4816.FromRawBits(value: right[lane])).Value;
            var expectedSum = Clamp(value: (((BigInteger)left[lane]) + right[lane]));

            if (sum != expectedSum) {
                return $"Add({left[lane]}, {right[lane]}) raw is {sum}, expected the clamped exact sum {expectedSum}";
            }

            // A widened accumulator two lanes wide: the left raw shifted past the long range plus the right raw, so
            // the narrowing sees both a far-out magnitude and an in-range value on every pair.
            var wide = ((((Int128)left[lane]) << 1) + right[lane]);
            var narrowed = FixedSaturate.ToInt64(value: wide);
            var expectedNarrow = Clamp(value: ((((BigInteger)left[lane]) << 1) + right[lane]));

            if (narrowed != expectedNarrow) {
                return $"ToInt64({wide}) is {narrowed}, expected {expectedNarrow}";
            }
        }

        return NarrowsTheSeams();
    }

    // The Int128 extremes and the long extremes' neighbours: the exact seams the clamp decides on.
    private static string? NarrowsTheSeams() {
        (Int128 Value, long Expected)[] seams = [
            (Int128.MinValue, long.MinValue),
            (Int128.MaxValue, long.MaxValue),
            (((Int128)long.MinValue) - 1, long.MinValue),
            ((Int128)long.MinValue, long.MinValue),
            (((Int128)long.MinValue) + 1, (long.MinValue + 1)),
            (((Int128)long.MaxValue) - 1, (long.MaxValue - 1)),
            ((Int128)long.MaxValue, long.MaxValue),
            (((Int128)long.MaxValue) + 1, long.MaxValue),
            (Int128.Zero, 0L),
        ];

        foreach (var (value, expected) in seams) {
            var narrowed = FixedSaturate.ToInt64(value: value);

            if (narrowed != expected) {
                return $"ToInt64({value}) is {narrowed}, expected {expected}";
            }
        }

        var saturatedHigh = FixedSaturate.Add(left: FixedQ4816.MaxValue, right: FixedQ4816.Epsilon).Value;
        var saturatedLow = FixedSaturate.Add(left: FixedQ4816.MinValue, right: FixedQ4816.FromRawBits(value: -1L)).Value;

        return ((saturatedHigh != long.MaxValue)
            ? $"Add(MaxValue, Epsilon) raw is {saturatedHigh}, expected long.MaxValue"
            : ((saturatedLow != long.MinValue)
                ? $"Add(MinValue, -1 raw) raw is {saturatedLow}, expected long.MinValue"
                : null));
    }
}
