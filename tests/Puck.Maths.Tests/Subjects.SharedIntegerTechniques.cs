using System.Numerics;

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

    public static string? SplitMix64ReferenceStream() {
        ulong[] expected = [6457827717110365317UL, 3203168211198807973UL, 9817491932198370423UL, 4593380528125082431UL, 16408922859458223821UL];
        var stream = new SplitMix64(seed: 1234567UL);

        for (var index = 0; (index < expected.Length); index++) {
            var actual = stream.NextUInt64();

            if (actual != expected[index]) { return $"word {index}: {actual} != {expected[index]}"; }
        }

        var restored = new SplitMix64(seed: stream.State);
        var next = stream.NextUInt64();

        return ((restored.NextUInt64() == next) ? null : "a stream built from State drew a different next word");
    }
    public static string? PartitionPointVsScan() {
        int[][] runs = [[], [3], [1, 1, 1], [1, 2, 2, 2, 5, 5, 9], [-4, -4, 0, 7, 7, 7, 7, 8]];

        foreach (var run in runs) {
            for (var key = -6; (key <= 10); key++) {
                var below = 0;
                var notAbove = 0;

                foreach (var value in run) {
                    if (value < key) { below++; }
                    if (value <= key) { notAbove++; }
                }

                var lower = PartitionPoint.Of(span: run, state: key, isBefore: static (limit, value) => (value < limit));
                var upper = PartitionPoint.Of(span: run, state: key, isBefore: static (limit, value) => (value <= limit));

                if (lower != below) { return $"[{string.Join(",", run)}] key {key}: lower bound {lower} != {below}"; }
                if (upper != notAbove) { return $"[{string.Join(",", run)}] key {key}: upper bound {upper} != {notAbove}"; }
            }
        }

        return null;
    }
    public static string? SequenceEqualityKeys() {
        var comparer = SequenceEqualityComparer<string>.Instance;
        string[] array = ["a", "b"];
        IReadOnlyList<string> list = new List<string> { "a", "b" };
        IReadOnlyList<string> other = new List<string> { "a", "c" };

        if (!comparer.Equals(x: array, y: ["a", "b"])) { return "equal arrays compared unequal"; }
        if (!comparer.Equals(x: list, y: array)) { return "an equal list and array compared unequal"; }
        if (comparer.Equals(x: list, y: other) || comparer.Equals(x: array, y: ["a"]) || comparer.Equals(x: array, y: ["b", "a"])) { return "a differing sequence compared equal"; }
        if (comparer.Equals(x: array, y: null) || comparer.Equals(x: null, y: array) || !comparer.Equals(x: (string[]?)null, y: null)) { return "null compared wrongly"; }
        if ((comparer.GetHashCode(obj: array) != comparer.GetHashCode(obj: list)) || (comparer.GetHashCode(obj: array) != comparer.GetHashCode(alternate: array.AsSpan()))) { return "equal sequences hashed differently"; }
        if (!comparer.Equals(alternate: array.AsSpan(), other: comparer.Create(alternate: array.AsSpan()))) { return "a created key differs from its span"; }

        var table = new Dictionary<string[], int>(comparer: comparer) { [array] = 7 };

        return (table.GetAlternateLookup<ReadOnlySpan<string>>().TryGetValue(key: ["a", "b"], value: out var found) && (found == 7)) ? null : "an alternate lookup missed an equal key";
    }
    public static string? Leb128RoundTrip() {
        var spelling = new byte[Leb128.MaxByteCount];

        for (var bits = 0; (bits <= 64); bits++) {
            var edge = ((bits == 0) ? 0UL : ((bits == 64) ? ulong.MaxValue : ((1UL << bits) - 1UL)));

            foreach (var value in new ulong[] { edge, (edge >> 1), (edge - 1UL) }) {
                var length = Leb128.Write(destination: spelling, value: value);
                var needed = Math.Max(1, ((((64 - BitOperations.LeadingZeroCount(value: value)) + 6) / 7)));

                if (length != needed) { return $"{value}: wrote {length} bytes, minimal is {needed}"; }

                var offset = 0;

                if (!Leb128.TryRead(data: spelling.AsSpan(start: 0, length: length), maxBits: 64, offset: ref offset, requireMinimal: true, value: out var read) || (read != value) || (offset != length)) {
                    return $"{value}: read back {read} at {offset}";
                }

                var valueBits = (64 - BitOperations.LeadingZeroCount(value: value));
                var narrow = Math.Max(1, valueBits);

                offset = 0;
                if (!Leb128.TryRead(data: spelling.AsSpan(start: 0, length: length), maxBits: narrow, offset: ref offset, requireMinimal: true, value: out _)) { return $"{value}: refused at its own {narrow} bits"; }
                if (narrow > 1) {
                    offset = 0;
                    if (Leb128.TryRead(data: spelling.AsSpan(start: 0, length: length), maxBits: (narrow - 1), offset: ref offset, requireMinimal: false, value: out _)) { return $"{value}: accepted past {narrow - 1} bits"; }
                }
                if (length > 1) {
                    offset = 0;
                    if (Leb128.TryRead(data: spelling.AsSpan(start: 0, length: (length - 1)), maxBits: 64, offset: ref offset, requireMinimal: false, value: out _)) { return $"{value}: truncated spelling accepted"; }
                }
            }
        }

        var padded = new byte[] { 0x81, 0x00 };
        var at = 0;

        if (Leb128.TryRead(data: padded, maxBits: 32, offset: ref at, requireMinimal: true, value: out _)) { return "a padded spelling was accepted when minimal was required"; }
        at = 0;
        if (!Leb128.TryRead(data: padded, maxBits: 32, offset: ref at, requireMinimal: false, value: out var one) || (one != 1UL)) { return "a padded spelling was refused when padding is allowed"; }
        at = 0;

        return (Leb128.TryRead(data: new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 }, maxBits: 32, offset: ref at, requireMinimal: false, value: out _) ? "an over-long spelling was accepted" : null);
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
