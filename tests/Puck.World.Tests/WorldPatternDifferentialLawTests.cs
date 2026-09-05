using System.Text;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the pattern machine against two independent oracles over random node trees: a brute-force set-of-words
/// semantics, and the Re# engine (the reference derivative engine) over the same languages spelled as text.</summary>
public sealed class WorldPatternDifferentialLawTests {
    private const int Letters = 4;
    private const int LongestWord = 5;
    private const int Trees = 300;

    private static readonly PatternSymbol[] s_symbols = [
        new(CellName.Parse("a"), 1, 1),
        new(CellName.Parse("b"), 2, 2),
        new(CellName.Parse("c"), 3, 3),
    ];

    [Fact]
    public void TheMachineAgreesWithBruteForceAndWithResharpOnRandomTrees() {
        var random = new Xorshift(0x9E3779B97F4A7C15UL);
        var words = AllWords();
        var compiled = 0;
        var refused = 0;
        Span<long> values = stackalloc long[LongestWord];

        for (var tree = 0; tree < Trees; tree++) {
            var node = Random(random, depth: 0);
            var row = new PatternRow(CellName.Parse("t"), CellKind.Int, s_symbols, node, MaxStates: PatternCapacity.MaxStates);

            if (!CompiledPattern.TryCompile(row, out var machine, out var reason)) {
                Assert.Contains("states", reason);
                refused++;
                continue;
            }
            compiled++;

            var expected = Language(node, words);
            var regex = new Resharp.Regex("^(?:" + Text(node) + ")$");

            foreach (var word in words) {
                for (var index = 0; index < word.Length; index++) {
                    values[index] = word[index] - '0';
                }

                var ours = machine!.Match(values[..word.Length]) == 1L;
                Assert.True(expected.Contains(word) == ours, $"tree {tree} word '{word}': brute force {expected.Contains(word)}, machine {ours}: {Text(node)}");
                Assert.True(regex.IsMatch(Spell(word)) == ours, $"tree {tree} word '{word}': Re# {regex.IsMatch(Spell(word))}, machine {ours}: {Text(node)}");
            }
        }

        Assert.True(compiled >= Trees * 9 / 10, $"{compiled} compiled, {refused} refused");
    }

    // Letters 0..3: 0 is the unnamed remainder, 1..3 are a, b, c.
    private static List<string> AllWords() {
        var words = new List<string> { string.Empty };
        for (var length = 1; length <= LongestWord; length++) {
            var count = words.Count;
            for (var index = 0; index < count; index++) {
                if (words[index].Length != length - 1) { continue; }
                for (var letter = 0; letter < Letters; letter++) {
                    words.Add(words[index] + (char)('0' + letter));
                }
            }
        }
        return words;
    }

    private static string Spell(string word) {
        var builder = new StringBuilder(word.Length);
        foreach (var letter in word) { builder.Append((char)('w' + (letter - '0'))); }
        return builder.ToString();
    }

    private static string Class(bool remainder, bool a, bool b, bool c) {
        var builder = new StringBuilder("[");
        if (remainder) { builder.Append('w'); }
        if (a) { builder.Append('x'); }
        if (b) { builder.Append('y'); }
        if (c) { builder.Append('z'); }
        return builder.Append(']').ToString();
    }

    private static string Text(PatternNode node) => node switch {
        PatternNode.Symbol s => Class(false, s.Name == "a", s.Name == "b", s.Name == "c"),
        PatternNode.Except e => Class(true, e.Name != "a", e.Name != "b", e.Name != "c"),
        PatternNode.AnySymbol => "[wxyz]",
        PatternNode.Nothing => "(?:)",
        PatternNode.None => "~(_*)",
        PatternNode.Sequence q => "(?:" + string.Concat(q.Items.Select(Text)) + ")",
        PatternNode.Choice ch => "(?:" + string.Join("|", ch.Items.Select(Text)) + ")",
        PatternNode.Both both => "(?:" + string.Join("&", both.Items.Select(item => "(?:" + Text(item) + ")")) + ")",
        PatternNode.Complement n => "~(" + Text(n.Item) + ")",
        PatternNode.Optional o => "(?:" + Text(o.Item) + ")?",
        PatternNode.Star s => "(?:" + Text(s.Item) + ")*",
        PatternNode.Plus p => "(?:" + Text(p.Item) + ")+",
        PatternNode.Repeat r => "(?:" + Text(r.Item) + "){" + r.Min + "," + r.Max + "}",
        _ => throw new InvalidOperationException(),
    };

    // The set of accepted words no longer than LongestWord, by the language equations themselves.
    private static HashSet<string> Language(PatternNode node, List<string> universe) {
        switch (node) {
            case PatternNode.Symbol s: return [((char)('0' + Ordinal(s.Name))).ToString()];
            case PatternNode.Except e: return [.. Enumerable.Range(0, Letters).Where(l => l != Ordinal(e.Name)).Select(l => ((char)('0' + l)).ToString())];
            case PatternNode.AnySymbol: return [.. Enumerable.Range(0, Letters).Select(l => ((char)('0' + l)).ToString())];
            case PatternNode.Nothing: return [string.Empty];
            case PatternNode.None: return [];
            case PatternNode.Sequence q: {
                var set = new HashSet<string> { string.Empty };
                foreach (var item in q.Items) { set = Concat(set, Language(item, universe)); }
                return set;
            }
            case PatternNode.Choice ch: {
                var set = new HashSet<string>();
                foreach (var item in ch.Items) { set.UnionWith(Language(item, universe)); }
                return set;
            }
            case PatternNode.Both both: {
                HashSet<string>? set = null;
                foreach (var item in both.Items) {
                    var language = Language(item, universe);
                    if (set is null) { set = language; } else { set.IntersectWith(language); }
                }
                return set!;
            }
            case PatternNode.Complement n: {
                var set = new HashSet<string>(universe);
                set.ExceptWith(Language(n.Item, universe));
                return set;
            }
            case PatternNode.Optional o: { var set = Language(o.Item, universe); set.Add(string.Empty); return set; }
            case PatternNode.Star s: return Closure(Language(s.Item, universe));
            case PatternNode.Plus p: { var unit = Language(p.Item, universe); return Concat(unit, Closure(unit)); }
            case PatternNode.Repeat r: {
                var unit = Language(r.Item, universe);
                var power = new HashSet<string> { string.Empty };
                var set = new HashSet<string>();
                for (var count = 0; count <= r.Max; count++) {
                    if (count >= r.Min) { set.UnionWith(power); }
                    power = Concat(power, unit);
                }
                return set;
            }
            default: throw new InvalidOperationException();
        }
    }

    private static int Ordinal(string name) => name switch { "a" => 1, "b" => 2, "c" => 3, _ => throw new InvalidOperationException() };

    private static HashSet<string> Concat(HashSet<string> left, HashSet<string> right) {
        var set = new HashSet<string>();
        foreach (var l in left) {
            foreach (var r in right) {
                if (l.Length + r.Length <= LongestWord) { set.Add(l + r); }
            }
        }
        return set;
    }

    private static HashSet<string> Closure(HashSet<string> unit) {
        var set = new HashSet<string> { string.Empty };
        var frontier = new HashSet<string> { string.Empty };
        while (frontier.Count > 0) {
            var next = Concat(frontier, unit);
            next.ExceptWith(set);
            set.UnionWith(next);
            frontier = next;
        }
        return set;
    }

    private static PatternNode Random(Xorshift random, int depth) {
        var leaf = depth >= 3 || random.Next(4) == 0;
        var pick = random.Next(leaf ? 5 : 14);
        string Name() => random.Next(3) switch { 0 => "a", 1 => "b", _ => "c" };
        return pick switch {
            0 => new PatternNode.Symbol(Name()),
            1 => new PatternNode.Except(Name()),
            2 => new PatternNode.AnySymbol(),
            3 => new PatternNode.Nothing(),
            4 => new PatternNode.None(),
            5 or 6 => new PatternNode.Sequence([Random(random, depth + 1), Random(random, depth + 1)]),
            7 => new PatternNode.Choice([Random(random, depth + 1), Random(random, depth + 1)]),
            8 => new PatternNode.Both([Random(random, depth + 1), Random(random, depth + 1)]),
            9 => new PatternNode.Complement(Random(random, depth + 1)),
            10 => new PatternNode.Optional(Random(random, depth + 1)),
            11 => new PatternNode.Star(Random(random, depth + 1)),
            12 => new PatternNode.Plus(Random(random, depth + 1)),
            _ => Repeat(random, depth),
        };
    }

    private static PatternNode Repeat(Xorshift random, int depth) {
        var min = random.Next(3);
        return new PatternNode.Repeat(Random(random, depth + 1), min, min + random.Next(3));
    }

    private sealed class Xorshift(ulong state) {
        private ulong m_state = state;
        public int Next(int bound) {
            m_state ^= m_state << 13;
            m_state ^= m_state >> 7;
            m_state ^= m_state << 17;
            return (int)(m_state % (ulong)bound);
        }
    }
}
