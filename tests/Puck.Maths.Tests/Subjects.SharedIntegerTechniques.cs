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
        for (var divisor = 1U; (divisor <= 65_535U); divisor++) {
            if (ReciprocalDivisionAt(divisor: divisor, value: uint.MaxValue) is { } failure) { return failure; }
        }
        for (var bit = 16; (bit < 32); bit++) {
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
            var expected = Oracles.ReciprocalDivision(divisor: divisor, value: dividend);

            if (actual != expected) { return $"{dividend}/{divisor}: {actual} != {expected}"; }
        }
        return null;
    }

    public static string? MultisetDigestBoundaries() {
        if (MultisetDigest.Empty.Value != 0UL) { return "the empty digest is not zero"; }
        ulong[] elements = [0UL, 1UL, 1UL, ulong.MaxValue, 0x9E3779B97F4A7C15UL, 42UL];
        var forward = MultisetDigest.Empty;
        var backward = MultisetDigest.Empty;

        for (var index = 0; (index < elements.Length); index++) {
            forward = forward.Add(element: elements[index]);
            backward = backward.Add(element: elements[elements.Length - 1 - index]);
        }
        if (forward != backward) { return "two orders of one multiset digest differently"; }
        if (forward.Remove(element: 42UL).Add(element: 42UL) != forward) { return "Remove does not undo Add"; }
        var first = MultisetDigest.Empty.Add(element: elements[0]).Add(element: elements[1]);
        var second = MultisetDigest.Empty.Add(element: elements[2]).Add(element: elements[3]).Add(element: elements[4]).Add(element: elements[5]);

        return ((first.Union(other: second) == forward) ? null : "Union differs from adding both multisets");
    }
    public static string? MultisetDigestSample(long[] left, long[] right) {
        var digest = MultisetDigest.Empty;

        foreach (var element in left) { digest = digest.Add(element: unchecked((ulong)element)); }
        foreach (var element in right) { digest = digest.Add(element: unchecked((ulong)element)); }
        foreach (var element in left) { digest = digest.Add(element: unchecked((ulong)element)); }
        var expected = Oracles.MultisetDigest(elements: [.. left, .. right, .. left]);

        if (digest.Value != expected) { return $"digest {digest.Value:x16} != {expected:x16}"; }
        foreach (var element in right) { digest = digest.Remove(element: unchecked((ulong)element)); }
        expected = Oracles.MultisetDigest(elements: [.. left, .. left]);
        return ((digest.Value == expected) ? null : $"after removal {digest.Value:x16} != {expected:x16}");
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
