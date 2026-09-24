using System.Text;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the pattern machine against two independent oracles over random node trees: a brute-force set-of-words
/// semantics, and the Re# engine (the reference derivative engine) over the same languages spelled as text.</summary>
public sealed class WorldPatternDifferentialLawTests {
    private const int Letters = 4;
    private const int LongestWord = 5;
    private const int Trees = 300;

    private static readonly PatternSymbol[] Symbols = [
        new(
            CellName.Parse(candidate: "a"),
            1,
            1
        ),
        new(
            CellName.Parse(candidate: "b"),
            2,
            2
        ),
        new(
            CellName.Parse(candidate: "c"),
            3,
            3
        ),
    ];

    // Letters 0..3: 0 is the unnamed remainder, 1..3 are a, b, c.
    private static List<string> AllWords() {
        var words = new List<string> { string.Empty };

        for (var length = 1; (length <= LongestWord); length++) {
            var count = words.Count;

            for (var index = 0; (index < count); index++) {
                if (words[index].Length != (length - 1)) { continue; }
                for (var letter = 0; (letter < Letters); letter++) {
                    words.Add(item: (words[index] + ((char)('0' + letter))));
                }
            }
        }
        return words;
    }
    private static string Class(bool remainder, bool a, bool b, bool c) {
        var builder = new StringBuilder(value: "[");

        if (remainder) { builder.Append(value: 'w'); }
        if (a) { builder.Append(value: 'x'); }
        if (b) { builder.Append(value: 'y'); }
        if (c) { builder.Append(value: 'z'); }
        return builder.Append(value: ']').ToString();
    }
    private static HashSet<string> Closure(HashSet<string> unit) {
        var set = new HashSet<string> { string.Empty };
        var frontier = new HashSet<string> { string.Empty };

        while (frontier.Count > 0) {
            var next = Concat(
                left: frontier,
                right: unit
            );

            next.ExceptWith(other: set);
            set.UnionWith(other: next);
            frontier = next;
        }
        return set;
    }
    private static HashSet<string> Concat(HashSet<string> left, HashSet<string> right) {
        var set = new HashSet<string>();

        foreach (var l in left) {
            foreach (var r in right) {
                if ((l.Length + r.Length) <= LongestWord) { set.Add(item: (l + r)); }
            }
        }
        return set;
    }
    // The set of accepted words no longer than LongestWord, by the language equations themselves.
    private static HashSet<string> Language(PatternNode node, List<string> universe) {
        switch (node) {
            case PatternNode.Symbol s: return [((char)('0' + Ordinal(name: s.Name))).ToString()];
            case PatternNode.Except e:
                return [.. Enumerable.Range(
                    count: Letters,
                    start: 0
                ).Where(predicate: l => (l != Ordinal(name: e.Name))).Select(selector: l => ((char)('0' + l)).ToString())];
            case PatternNode.AnySymbol:
                return [.. Enumerable.Range(
                    count: Letters,
                    start: 0
                ).Select(selector: l => ((char)('0' + l)).ToString())];
            case PatternNode.Nothing: return [string.Empty];
            case PatternNode.None: return [];
            case PatternNode.Sequence q: {
                    var set = new HashSet<string> { string.Empty };

                    foreach (var item in q.Items) {
                        set = Concat(
                        left: set,
                        right: Language(
                            node: item,
                            universe: universe
                        )
                    );
                    }
                    return set;
                }
            case PatternNode.Choice ch: {
                    var set = new HashSet<string>();

                    foreach (var item in ch.Items) {
                        set.UnionWith(other: Language(
                        node: item,
                        universe: universe
                    ));
                    }
                    return set;
                }
            case PatternNode.Both both: {
                    HashSet<string>? set = null;

                    foreach (var item in both.Items) {
                        var language = Language(
                            node: item,
                            universe: universe
                        );

                        if (set is null) { set = language; } else { set.IntersectWith(other: language); }
                    }
                    return set!;
                }
            case PatternNode.Complement n: {
                    var set = new HashSet<string>(collection: universe);

                    set.ExceptWith(other: Language(
                        node: n.Item,
                        universe: universe
                    ));
                    return set;
                }
            case PatternNode.Optional o: {
                    var set = Language(
                node: o.Item,
                universe: universe
            ); set.Add(item: string.Empty); return set;
                }
            case PatternNode.Star s:
                return Closure(unit: Language(
                node: s.Item,
                universe: universe
            ));
            case PatternNode.Plus p: {
                    var unit = Language(
                node: p.Item,
                universe: universe
            ); return Concat(
                left: unit,
                right: Closure(unit: unit)
            );
                }
            case PatternNode.Repeat r: {
                    var unit = Language(
                        node: r.Item,
                        universe: universe
                    );
                    var power = new HashSet<string> { string.Empty };
                    var set = new HashSet<string>();

                    for (var count = 0; (count <= r.Max); count++) {
                        if (count >= r.Min) { set.UnionWith(other: power); }
                        power = Concat(
                            left: power,
                            right: unit
                        );
                    }
                    return set;
                }
            default: throw new InvalidOperationException();
        }
    }
    private static int Ordinal(string name) => name switch { "a" => 1, "b" => 2, "c" => 3, _ => throw new InvalidOperationException() };
    private static PatternNode Random(Xorshift random, int depth) {
        var leaf = ((depth >= 3) || (random.Next(bound: 4) == 0));
        var pick = random.Next(bound: (leaf
            ? 5
            : 14));

        string Name() => random.Next(bound: 3) switch { 0 => "a", 1 => "b", _ => "c" };
        return pick switch {
            0 => new PatternNode.Symbol(Name: Name()),
            1 => new PatternNode.Except(Name: Name()),
            2 => new PatternNode.AnySymbol(),
            3 => new PatternNode.Nothing(),
            4 => new PatternNode.None(),
            5 or 6 => new PatternNode.Sequence(Items: [Random(
                depth: (depth + 1),
                random: random
            ), Random(
                depth: (depth + 1),
                random: random
            )]),
            7 => new PatternNode.Choice(Items: [Random(
                depth: (depth + 1),
                random: random
            ), Random(
                depth: (depth + 1),
                random: random
            )]),
            8 => new PatternNode.Both(Items: [Random(
                depth: (depth + 1),
                random: random
            ), Random(
                depth: (depth + 1),
                random: random
            )]),
            9 => new PatternNode.Complement(Item: Random(
            depth: (depth + 1),
            random: random
        )),
            10 => new PatternNode.Optional(Item: Random(
            depth: (depth + 1),
            random: random
        )),
            11 => new PatternNode.Star(Item: Random(
            depth: (depth + 1),
            random: random
        )),
            12 => new PatternNode.Plus(Item: Random(
            depth: (depth + 1),
            random: random
        )),
            _ => Repeat(
            depth: depth,
            random: random
        ),
        };
    }
    private static PatternNode Repeat(Xorshift random, int depth) {
        var min = random.Next(bound: 3);

        return new PatternNode.Repeat(
            Random(
                depth: (depth + 1),
                random: random
            ),
            min,
            (min + random.Next(bound: 3))
        );
    }
    private static string Spell(string word) {
        var builder = new StringBuilder(capacity: word.Length);

        foreach (var letter in word) { builder.Append(value: ((char)('w' + (letter - '0')))); }
        return builder.ToString();
    }
    private static string Text(PatternNode node) => node switch {
        PatternNode.Symbol s => Class(
        false,
        (s.Name == "a"),
        (s.Name == "b"),
        (s.Name == "c")
    ),
        PatternNode.Except e => Class(
        true,
        (e.Name != "a"),
        (e.Name != "b"),
        (e.Name != "c")
    ),
        PatternNode.AnySymbol => "[wxyz]",
        PatternNode.Nothing => "(?:)",
        PatternNode.None => "~(_*)",
        PatternNode.Sequence q => (("(?:" + string.Concat(values: q.Items.Select(selector: Text))) + ")"),
        PatternNode.Choice ch => (("(?:" + string.Join(
        separator: "|",
        values: ch.Items.Select(selector: Text)
    )) + ")"),
        PatternNode.Both both => (("(?:" + string.Join(
        separator: "&",
        values: both.Items.Select(selector: item => (("(?:" + Text(node: item)) + ")"))
    )) + ")"),
        PatternNode.Complement n => (("~(" + Text(node: n.Item)) + ")"),
        PatternNode.Optional o => (("(?:" + Text(node: o.Item)) + ")?"),
        PatternNode.Star s => (("(?:" + Text(node: s.Item)) + ")*"),
        PatternNode.Plus p => (("(?:" + Text(node: p.Item)) + ")+"),
        PatternNode.Repeat r => (((((("(?:" + Text(node: r.Item)) + "){") + r.Min) + ",") + r.Max) + "}"),
        _ => throw new InvalidOperationException(),
    };

    [Fact]
    public void TheMachineAgreesWithBruteForceAndWithResharpOnRandomTrees() {
        var random = new Xorshift(state: 0x9E3779B97F4A7C15UL);
        var words = AllWords();
        var compiled = 0;
        var refused = 0;
        Span<long> values = stackalloc long[LongestWord];

        for (var tree = 0; (tree < Trees); tree++) {
            var node = Random(
                random,
                depth: 0
            );
            var row = new PatternRow(
                CellName.Parse(candidate: "t"),
                CellKind.Int,
                Symbols,
                node,
                MaxStates: PatternCapacity.MaxStates
            );

            if (!CompiledPattern.TryCompile(
                compiled: out var machine,
                reason: out var reason,
                row: row
            )) {
                Assert.Contains(
                    actualString: reason,
                    expectedSubstring: "states"
                );
                refused++;
                continue;
            }
            compiled++;

            var expected = Language(
                node: node,
                universe: words
            );
            var regex = new Resharp.Regex((("^(?:" + Text(node: node)) + ")$"));

            foreach (var word in words) {
                for (var index = 0; (index < word.Length); index++) {
                    values[index] = (word[index] - '0');
                }

                var ours = (machine!.Match(values: values[..word.Length]) == 1L);

                Assert.True(
                    condition: (expected.Contains(item: word) == ours),
                    userMessage: $"tree {tree} word '{word}': brute force {expected.Contains(item: word)}, machine {ours}: {Text(node: node)}"
                );
                Assert.True(
                    condition: (regex.IsMatch(input: Spell(word: word)) == ours),
                    userMessage: $"tree {tree} word '{word}': Re# {regex.IsMatch(input: Spell(word: word))}, machine {ours}: {Text(node: node)}"
                );
            }
        }

        Assert.True(
            condition: (compiled >= ((Trees * 9) / 10)),
            userMessage: $"{compiled} compiled, {refused} refused"
        );
    }

    private sealed class Xorshift(ulong state) {
        private ulong m_state = state;

        public int Next(int bound) {
            m_state ^= (m_state << 13);
            m_state ^= (m_state >> 7);
            m_state ^= (m_state << 17);
            return ((int)(m_state % ((ulong)bound)));
        }
    }
}
