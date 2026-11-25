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
