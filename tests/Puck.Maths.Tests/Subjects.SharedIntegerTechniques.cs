namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? ReciprocalDivisionBoundaries() {
        try {
            _ = new ReciprocalDivisor32(divisor: 0U);
            return "zero divisor was accepted";
        } catch (ArgumentOutOfRangeException) { }

        foreach (var value in new uint[] { 0U, 1U, int.MaxValue, (1U << 31), uint.MaxValue }) {
            if (default(ReciprocalDivisor32).Divide(value: value) != value) { return $"default divisor changed {value}"; }
        }
        // Every divisor the prime counter uses, then powers of two and their neighbours through the full uint range.
        for (var divisor = 1U; divisor <= 65_535U; divisor++) {
            if (ReciprocalDivisionAt(divisor: divisor, value: uint.MaxValue) is { } failure) { return failure; }
        }
        for (var bit = 16; bit < 32; bit++) {
            var power = (1U << bit);
            foreach (var divisor in new uint[] { (power - 1U), power, (power + 1U), (uint.MaxValue - (power - 1U)) }) {
                if (ReciprocalDivisionAt(divisor: divisor, value: uint.MaxValue) is { } failure) { return failure; }
            }
        }
        return ReciprocalDivisionAt(divisor: uint.MaxValue, value: uint.MaxValue);
    }

    public static string? ReciprocalDivisionSample(long[] left, long[] right) {
        var divisor = unchecked((uint)right[0]);
        return ReciprocalDivisionAt(divisor: ((divisor == 0U) ? 1U : divisor), value: unchecked((uint)left[0]));
    }

    private static string? ReciprocalDivisionAt(uint divisor, uint value) {
        var by = new ReciprocalDivisor32(divisor: divisor);
        var lastMultiple = ((uint.MaxValue / divisor) * divisor);
        // Probe both ends of the quotient interval, including the last representable multiple's lower neighbour.
        foreach (var dividend in new uint[] { 0U, 1U, value, (divisor - 1U), divisor,
            ((divisor == uint.MaxValue) ? divisor : (divisor + 1U)), (lastMultiple - 1U), lastMultiple, uint.MaxValue }) {
            var actual = by.Divide(value: dividend);
            var expected = Oracles.ReciprocalDivision(value: dividend, divisor: divisor);
            if (actual != expected) { return $"{dividend}/{divisor}: {actual} != {expected}"; }
        }
        return null;
    }

    public static string? PackedPairHash(long[] left, long[] right) {
        var comparer = PackedIntPairComparer.Instance;
        var x = left[0];
        var y = right[0];
        if (!comparer.Equals(x: x, y: x) || (comparer.Equals(x: x, y: y) != (x == y))) { return "packed equality changed"; }
        var actual = comparer.GetHashCode(obj: x);
        var expected = Oracles.PackedPairHash(value: x);
        return ((actual == expected) ? null : $"pair={x}: hash {actual} != {expected}");
    }
}
