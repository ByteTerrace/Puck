namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // A random 128-bit mask and a sparse one built from it: each is read through the UInt128 form and through the same
    // bits as two 64-bit words, against a bit-by-bit test of the raw words that calls no BitWords member.
    public static string? BitWordsMembersBitOracle(long[] left, long[] right) {
        var low = (ulong)left[1];
        var high = (ulong)left[0];
        var shift = (int)(((ulong)right[0]) % 64UL);

        return (BitWordsAgree(high: high, low: low) ?? BitWordsAgree(high: (high & (high >> shift)), low: (low & (low << shift))));
    }
    public static string? BitWordsSetAndRefusals() {
        Span<ulong> words = stackalloc ulong[3];

        for (var index = 0; (index < 192); index += 7) {
            BitWords.Add(words: words, index: index);
        }
        for (var index = 0; (index < 192); ++index) {
            var expected = ((index % 7) == 0);

            if (BitWords.Contains(words: words, index: index) != expected) { return $"after adding every seventh member, member {index} reads {!expected}"; }
        }
        BitWords.Remove(words: words, index: 63);
        if (BitWords.Contains(words: words, index: 63) || (BitWords.Count(words: words) != 27)) { return "removing member 63 left it set or miscounted the rest"; }
        if (BitWords.Contains(words: words, index: 192) || BitWords.Contains(words: words, index: -1)) { return "a member past the span or below zero reads as set"; }
        foreach (var (members, expected) in ((int, int)[])[(0, 0), (1, 1), (64, 1), (65, 2), (128, 2), (129, 3)]) {
            if (BitWords.WordsFor(members: members) != expected) { return $"WordsFor({members}) = {BitWords.WordsFor(members: members)}, expected {expected}"; }
        }
        foreach (var refused in (Action[])[() => BitWords.Bit(index: 128), () => BitWords.Bit(index: -1), () => BitWords.Word32(mask: UInt128.One, word: 4), () => BitWords.WordsFor(members: -1)]) {
            try {
                refused();

                return "an argument outside its range was accepted";
            } catch (ArgumentOutOfRangeException) { }
        }
        return null;
    }

    // Random words of one to seven 64-bit words shifted every signed distance up to two words past their width either
    // way, every run extracted from them and every set width complemented, against a bit-by-bit copy that calls no
    // BitWords member.
    public static string? BitWordsSpanMovesBitOracle() {
        var random = new Random(Seed: 20261128);

        static bool Raw(ReadOnlySpan<ulong> words, int index) => ((index >= 0) && (index < (words.Length * 64)) && (((words[index / 64] >> (index % 64)) & 1UL) != 0UL));

        for (var length = 1; (length <= 7); length++) {
            var source = new ulong[length];

            for (var word = 0; (word < length); word++) {
                source[word] = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40);
            }
            for (var destinationLength = 1; (destinationLength <= (length + 2)); destinationLength++) {
                var destination = new ulong[destinationLength];

                for (var distance = (-64 * (length + 2)); (distance <= (64 * (length + 2))); distance++) {
                    BitWords.Shift(destination: destination, distance: distance, source: source);
                    for (var bit = 0; (bit < (destinationLength * 64)); bit++) {
                        if (Raw(words: destination, index: bit) != Raw(words: source, index: (bit - distance))) {
                            return $"{length} words shifted {distance} into {destinationLength}: bit {bit} reads unlike source bit {bit - distance}";
                        }
                    }
                }
            }
            for (var start = 0; (start <= ((length * 64) + 3)); start += 5) {
                for (var count = 0; (count <= ((length * 64) + 70)); count += 7) {
                    var destination = new ulong[(BitWords.WordsFor(members: count) + 1)];

                    destination.AsSpan().Fill(value: ulong.MaxValue);
                    BitWords.Extract(count: count, destination: destination, start: start, words: source);
                    for (var bit = 0; (bit < (destination.Length * 64)); bit++) {
                        var expected = ((bit < count) && Raw(words: source, index: (start + bit)));

                        if (Raw(words: destination, index: bit) != expected) {
                            return $"run {start}+{count} of {length} words: bit {bit} reads {!expected}";
                        }
                    }
                }
            }
        }
        for (var members = 0; (members <= 200); members++) {
            var words = new ulong[(BitWords.WordsFor(members: members) + 1)];

            for (var word = 0; (word < words.Length); word++) {
                words[word] = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40);
            }

            var before = (ulong[])words.Clone();

            BitWords.Complement(members: members, words: words);
            for (var bit = 0; (bit < (words.Length * 64)); bit++) {
                var expected = ((bit < members)
                    ? !Raw(words: before, index: bit)
                    : ((bit >= (BitWords.WordsFor(members: members) * 64)) && Raw(words: before, index: bit)));

                if (Raw(words: words, index: bit) != expected) {
                    return $"complement of {members} positions: bit {bit} reads {!expected}";
                }
            }
        }
        foreach (var refused in (Action[])[
            () => BitWords.Complement(members: -1, words: new ulong[1]),
            () => BitWords.Extract(count: 1, destination: new ulong[1], start: -1, words: new ulong[1]),
            () => BitWords.Extract(count: -1, destination: new ulong[1], start: 0, words: new ulong[1]),
        ]) {
            try {
                refused();

                return "a negative run start or length was accepted";
            } catch (ArgumentOutOfRangeException) { }
        }
        try {
            BitWords.Extract(count: 65, destination: new ulong[1], start: 0, words: new ulong[2]);

            return "a destination shorter than the run was accepted";
        } catch (ArgumentException) { }
        try {
            BitWords.Complement(members: 65, words: new ulong[1]);

            return "a complement over more positions than its words was accepted";
        } catch (ArgumentException) { }

        return null;
    }

    private static string? BitWordsAgree(ulong high, ulong low) {
        var mask = new UInt128(upper: high, lower: low);
        ReadOnlySpan<ulong> words = [low, high];
        var expected = new List<int>();

        for (var index = 0; (index < 128); ++index) {
            if ((((index < 64) ? (low >> index) : (high >> (index - 64))) & 1UL) != 0UL) { expected.Add(item: index); }
        }

        var fromMask = new List<int>();
        var fromWords = new List<int>();

        foreach (var member in BitWords.Members(mask: mask)) { fromMask.Add(item: member); }
        foreach (var member in BitWords.Members(words: words)) { fromWords.Add(item: member); }
        if (!fromMask.SequenceEqual(second: expected)) { return $"mask {high:X16}{low:X16}: members [{string.Join(separator: ",", values: fromMask)}], expected [{string.Join(separator: ",", values: expected)}]"; }
        if (!fromWords.SequenceEqual(second: expected)) { return $"words {high:X16}{low:X16}: members [{string.Join(separator: ",", values: fromWords)}], expected [{string.Join(separator: ",", values: expected)}]"; }
        if ((BitWords.Count(mask: mask) != expected.Count) || (BitWords.Count(words: words) != expected.Count)) { return $"mask {high:X16}{low:X16}: count {BitWords.Count(mask: mask)}/{BitWords.Count(words: words)}, expected {expected.Count}"; }

        var span = ((expected.Count == 0) ? 0 : (expected[^1] + 1));

        if (BitWords.Span(mask: mask) != span) { return $"mask {high:X16}{low:X16}: span {BitWords.Span(mask: mask)}, expected {span}"; }
        for (var index = -1; (index <= 128); ++index) {
            var set = expected.Contains(item: index);

            if ((BitWords.Contains(mask: mask, index: index) != set) || (BitWords.Contains(words: words, index: index) != set)) { return $"mask {high:X16}{low:X16}: member {index} reads unlike the raw bit"; }
            if ((index >= 0) && (index < 128) && (((mask & BitWords.Bit(index: index)) != UInt128.Zero) != set)) { return $"mask {high:X16}{low:X16}: Bit({index}) selects unlike the raw bit"; }
        }
        for (var word = 0; (word < 4); ++word) {
            var raw = (uint)(((word < 2) ? low : high) >> ((word % 2) * 32));

            if (BitWords.Word32(mask: mask, word: word) != raw) { return $"mask {high:X16}{low:X16}: word {word} reads {BitWords.Word32(mask: mask, word: word):X8}, expected {raw:X8}"; }
        }
        return null;
    }
}
