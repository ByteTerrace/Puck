namespace Puck.Testing;

/// <summary>The names a spelling law sweeps: the edges of the one identifier rule
/// (<c>Puck.State.IdentifierSpelling</c>) and a seeded, deterministic fuzz over an alphabet that straddles it. The
/// fuzz draws from SplitMix64 rather than <see cref="Random"/>, so the corpus is the same on every runtime.</summary>
internal static class IdentifierCorpus {
    // Weighted toward identifier characters so a useful share of the fuzz is bare, and carrying every character a
    // copy of the rule has disagreed about: the sigil, a dot, a colon, the quote marks, a backslash, and non-ASCII
    // letters, digits and marks.
    private const string Alphabet = "abcxyzABQZ___0189$$-. :`\"\\éΩа٣\u0301";

    /// <summary>Gets the hand-picked edges: the empty name, each class boundary, the sigil in every position, the
    /// generated-name spellings of both joiners, characters just outside ASCII, look-alikes, and words a context
    /// reserves.</summary>
    public static IReadOnlyList<string> Edges { get; } = [
        "", "a", "Z", "_", "__", "_a", "a_", "a1", "A1_b2", "row2", "pieceCell",
        "1", "1a", "9_", "-a", "a-b", "seat-1", "a b", " a", "a ", "a.b", ".a", "a.", "a..b", "a:b", ":a",
        "$", "$a", "$_", "$1", "$a1", "a$", "a$b", "$$a", "$a$b", "$a.b", "$a:b", "$a:$b", "$a:-1", "$$",
        "turn$east", "expect$1$fixed", "$pool$a$live", "~", "a~b", "~a", "a~", "rulepush~push-block",
        "`a`", "a`b", "\"a\"", "a\"b", "a\\b", "\ta", "a\nb",
        "é", "café", "cafe\u0301", "naïve", "Ωmega", "\u0430", "a\u0430", "a\u0663", "\U0001D465", "ǅ",
        "any", "empty", "except", "none", "count", "sum", "all", "dot", "vector", "embed", "squareRoot",
        "true", "false", "null", "auto", "and", "or", "not", "as", "in", "of",
        "let", "rule", "when", "for", "if", "import", "export", "template", "schema", "basis", "table", "local",
        "select", "SELECT", "text", "row", "int", "value", "tick", "readers", "exists", "join",
    ];

    /// <summary>Returns <see cref="Edges"/> followed by <paramref name="count"/> fuzzed names, without repeats.</summary>
    /// <param name="count">The number of fuzzed names drawn.</param>
    /// <param name="seed">The SplitMix64 seed.</param>
    /// <returns>The corpus, in a fixed order.</returns>
    public static IReadOnlyList<string> Names(int count, ulong seed = 0x5EED_01D5UL) {
        var names = new List<string>(collection: Edges);
        var seen = new HashSet<string>(collection: Edges, comparer: StringComparer.Ordinal);
        var state = seed;

        while (names.Count < (Edges.Count + count)) {
            var length = (1 + ((int)(Next(state: ref state) % 8UL)));
            var characters = new char[length];

            for (var index = 0; (index < length); index++) {
                characters[index] = Alphabet[((int)(Next(state: ref state) % ((ulong)Alphabet.Length)))];
            }

            var name = new string(value: characters);

            if (seen.Add(item: name)) {
                names.Add(item: name);
            }
        }

        return names;
    }

    private static ulong Next(ref ulong state) {
        var z = (state += 0x9E3779B97F4A7C15UL);

        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EBUL);

        return z ^ (z >> 31);
    }
}
