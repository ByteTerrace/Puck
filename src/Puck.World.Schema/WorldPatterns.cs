using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>One symbol of a pattern's alphabet: the cell values in <paramref name="Min"/>..<paramref name="Max"/>
/// (inclusive, in the pattern's kind) read as this letter. Symbols may overlap; the refined alphabet splits them.</summary>
/// <param name="Name">The symbol name a pattern node references.</param>
/// <param name="Min">The least value the symbol accepts.</param>
/// <param name="Max">The greatest value the symbol accepts.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPatternSymbol(WorldCellName Name, decimal Min, decimal Max);

/// <summary>The closed pattern vocabulary over a row's cell values, matched against the whole word. Complement and
/// intersection are first-class, so "no two adjacent kings" and "holds a 2 and a 5" are single patterns rather than
/// rule arithmetic.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldPatternNode.Symbol), "symbol")]
[JsonDerivedType(typeof(WorldPatternNode.AnySymbol), "any")]
[JsonDerivedType(typeof(WorldPatternNode.Except), "except")]
[JsonDerivedType(typeof(WorldPatternNode.Nothing), "empty")]
[JsonDerivedType(typeof(WorldPatternNode.Sequence), "sequence")]
[JsonDerivedType(typeof(WorldPatternNode.Choice), "choice")]
[JsonDerivedType(typeof(WorldPatternNode.Both), "all")]
[JsonDerivedType(typeof(WorldPatternNode.Complement), "not")]
[JsonDerivedType(typeof(WorldPatternNode.Optional), "optional")]
[JsonDerivedType(typeof(WorldPatternNode.Star), "star")]
[JsonDerivedType(typeof(WorldPatternNode.Plus), "plus")]
[JsonDerivedType(typeof(WorldPatternNode.Repeat), "repeat")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record WorldPatternNode {
    /// <summary>One token whose value falls in the named symbol.</summary>
    public sealed record Symbol(string Name) : WorldPatternNode;
    /// <summary>One token of any value, named symbols and the unnamed remainder alike.</summary>
    public sealed record AnySymbol : WorldPatternNode;
    /// <summary>One token whose value falls outside the named symbol.</summary>
    public sealed record Except(string Name) : WorldPatternNode;
    /// <summary>The empty word.</summary>
    public sealed record Nothing : WorldPatternNode;
    /// <summary>The items matched one after another.</summary>
    public sealed record Sequence(IReadOnlyList<WorldPatternNode> Items) : WorldPatternNode;
    /// <summary>Any one of the items.</summary>
    public sealed record Choice(IReadOnlyList<WorldPatternNode> Items) : WorldPatternNode;
    /// <summary>Every item at once: the word is in each item's language.</summary>
    public sealed record Both(IReadOnlyList<WorldPatternNode> Items) : WorldPatternNode;
    /// <summary>Every word the item does not match.</summary>
    public sealed record Complement(WorldPatternNode Item) : WorldPatternNode;
    /// <summary>The item or nothing.</summary>
    public sealed record Optional(WorldPatternNode Item) : WorldPatternNode;
    /// <summary>The item zero or more times.</summary>
    public sealed record Star(WorldPatternNode Item) : WorldPatternNode;
    /// <summary>The item one or more times.</summary>
    public sealed record Plus(WorldPatternNode Item) : WorldPatternNode;
    /// <summary>The item between <paramref name="Min"/> and <paramref name="Max"/> times.</summary>
    public sealed record Repeat(WorldPatternNode Item, int Min, int Max) : WorldPatternNode;
}

/// <summary>One row of the <c>patterns</c> section: a regular language over cell values, compiled once to a
/// deterministic table the <c>$match:</c> operand runs allocation-free, one indexed step per token.</summary>
/// <param name="Name">The pattern name a rule references.</param>
/// <param name="Kind">The numeric kind of the values the word is read from: Int or Fixed.</param>
/// <param name="Symbols">The alphabet, 1..32 named value ranges.</param>
/// <param name="Pattern">The language.</param>
/// <param name="Attribute">For a zone source, the keyed row (over the zone's token domain) whose cell values form the
/// word, in pile order; null reads the source row's own cell values.</param>
/// <param name="MaxStates">The machine-state budget the compile refuses past, 1..256.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPatternRow(
    WorldCellName Name,
    CellKind Kind,
    IReadOnlyList<WorldPatternSymbol> Symbols,
    WorldPatternNode Pattern,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Attribute = null,
    int MaxStates = WorldPatternCapacity.DefaultStates
);

/// <summary>Representation ceilings for the pattern section.</summary>
public static class WorldPatternCapacity {
    /// <summary>The most pattern rows a document declares.</summary>
    public const int MaxRows = 64;
    /// <summary>The longest word one read walks: every source row fits, so a read is always decided.</summary>
    public const int MaxWord = WorldTopologyCompilation.MaxCells;
    /// <summary>The most named symbols in one alphabet.</summary>
    public const int MaxSymbols = 32;
    /// <summary>The most times a <c>repeat</c> node may unroll its item.</summary>
    public const int MaxRepeat = 64;
    /// <summary>The state ceiling any row may budget: 256 states over 64 letters is a 64 KiB table.</summary>
    public const int MaxStates = 256;
    /// <summary>The state budget a row that declares none gets.</summary>
    public const int DefaultStates = 64;
}

/// <summary>A compiled pattern: the refined alphabet that numbered its letters and the deterministic machine over
/// them, built from the pattern's Brzozowski derivatives.</summary>
/// <remarks>Each machine state IS a derivative of the pattern — the language still expected after the tokens read so
/// far — kept canonical by hash-consing with the classical similarity rules (flattened, sorted, deduplicated unions and
/// intersections; absorbed empties; merged letter sets). Similarity keeps the state count finite for every pattern,
/// complement and intersection included, and the row's <c>maxStates</c> bounds it by name at validation.</remarks>
public sealed class CompiledWorldPattern {
    private readonly RangeAlphabet m_alphabet;
    private readonly int[] m_transitions;
    private readonly bool[] m_accepting;

    private CompiledWorldPattern(WorldPatternRow source, RangeAlphabet alphabet, int[] transitions, bool[] accepting) {
        Source = source;
        m_alphabet = alphabet;
        m_transitions = transitions;
        m_accepting = accepting;
    }

    /// <summary>Gets the authored row.</summary>
    public WorldPatternRow Source { get; }
    /// <summary>Gets the number of letters after refinement, the unnamed remainder included.</summary>
    public int LetterCount => m_alphabet.LetterCount;
    /// <summary>Gets the number of states in the compiled machine.</summary>
    public int StateCount => m_accepting.Length;

    /// <summary>Runs a word of raw cell values through the machine.</summary>
    /// <param name="values">The word, raw in the pattern's kind.</param>
    /// <returns>1 when the whole word is in the language, 0 when it is not.</returns>
    public long Match(ReadOnlySpan<long> values) {
        var letters = m_alphabet.LetterCount;
        var state = 0;

        for (var index = 0; index < values.Length; index++) {
            state = m_transitions[(state * letters) + m_alphabet.LetterOf(value: values[index])];
        }

        return m_accepting[state] ? 1L : 0L;
    }

    /// <summary>Compiles one authored row: refines its symbols into letters, lowers the node tree to a canonical term,
    /// and explores the term's derivatives breadth-first into a table inside the row's state budget.</summary>
    /// <param name="row">The authored row.</param>
    /// <param name="compiled">The machine, on success.</param>
    /// <param name="reason">Why the row refused, on failure.</param>
    /// <returns><see langword="true"/> when the row compiled.</returns>
    public static bool TryCompile(WorldPatternRow row, out CompiledWorldPattern? compiled, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);

        compiled = null;
        var symbols = row.Symbols ?? [];

        if (row.Kind is not (CellKind.Int or CellKind.Fixed)) {
            reason = $"pattern '{row.Name}' kind must be int or fixed";
            return false;
        }
        if (row.MaxStates is < 1 or > WorldPatternCapacity.MaxStates) {
            reason = $"pattern '{row.Name}' maxStates must be 1..{WorldPatternCapacity.MaxStates}";
            return false;
        }
        if (symbols.Count is < 1 or > WorldPatternCapacity.MaxSymbols) {
            reason = $"pattern '{row.Name}' declares {symbols.Count} symbols; 1..{WorldPatternCapacity.MaxSymbols} are admitted";
            return false;
        }

        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var ranges = new (long Low, long High)[symbols.Count];

        for (var index = 0; index < symbols.Count; index++) {
            var symbol = symbols[index];

            if (symbol is null || !names.TryAdd(symbol.Name.Value, index)) {
                reason = $"pattern '{row.Name}' symbol {index} is null or repeats a name";
                return false;
            }
            if (!TryLower(row.Kind, symbol.Min, out var low) || !TryLower(row.Kind, symbol.Max, out var high) || low > high) {
                reason = $"pattern '{row.Name}' symbol '{symbol.Name}' range is not min <= max inside its kind";
                return false;
            }

            ranges[index] = (low, high);
        }

        var alphabet = RangeAlphabet.Create(ranges: ranges, masks: out var masks);
        var terms = new PatternTerms(letterCount: alphabet.LetterCount);

        if (!terms.TryLower(row.Pattern, masks, names, out var root, out reason)) {
            reason = $"pattern '{row.Name}' {reason}";
            return false;
        }
        if (!terms.TryExplore(root: root, stateLimit: row.MaxStates, transitions: out var transitions, accepting: out var accepting)) {
            reason = $"pattern '{row.Name}' needs more than {row.MaxStates} states";
            return false;
        }

        compiled = new(source: row, alphabet: alphabet, transitions: transitions, accepting: accepting);
        reason = string.Empty;
        return true;
    }

    private static bool TryLower(CellKind kind, decimal literal, out long raw) {
        try {
            raw = (kind == CellKind.Fixed)
                ? WorldStateNumericLiteral.ToFixed(value: literal).Value
                : checked((long)decimal.Round(d: literal, decimals: 0, mode: MidpointRounding.ToEven));
            return true;
        } catch (OverflowException) {
            raw = 0;
            return false;
        }
    }

    // The letters are the distinct symbol memberships the authored ranges cut the value line into: every maximal run
    // of values inside the same set of ranges is one letter, and every run inside no range shares the remainder
    // letter. At most 32 ranges cut 63 interior runs, so the alphabet always fits the 64-bit letter masks.
    private sealed class RangeAlphabet {
        private readonly long[] m_starts;
        private readonly int[] m_letters;

        private RangeAlphabet(long[] starts, int[] letters, int letterCount) {
            m_starts = starts;
            m_letters = letters;
            LetterCount = letterCount;
        }

        public int LetterCount { get; }

        // The run holding a value is the last start at or below it; values below the first start are run 0.
        public int LetterOf(long value) {
            var found = Array.BinarySearch(m_starts, value);
            var run = (found >= 0) ? found : (~found - 1);
            return m_letters[run + 1];
        }

        public static RangeAlphabet Create((long Low, long High)[] ranges, out ulong[] masks) {
            var cuts = new SortedSet<long>();
            foreach (var (low, high) in ranges) {
                cuts.Add(low);
                if (high < long.MaxValue) { cuts.Add(high + 1); }
            }
            var starts = cuts.ToArray();
            // Run 0 lies below every start; run r + 1 begins at starts[r].
            var letters = new int[starts.Length + 1];
            var membership = new ulong[starts.Length + 1];
            var letterCount = 1;
            for (var run = 0; run <= starts.Length; run++) {
                var probe = (run == 0) ? long.MinValue : starts[run - 1];
                for (var symbol = 0; symbol < ranges.Length; symbol++) {
                    if (ranges[symbol].Low <= probe && probe <= ranges[symbol].High) { membership[run] |= 1UL << symbol; }
                }
                letters[run] = (membership[run] == 0UL) ? 0 : letterCount++;
            }
            masks = new ulong[ranges.Length];
            for (var run = 0; run <= starts.Length; run++) {
                for (var symbol = 0; symbol < ranges.Length; symbol++) {
                    if ((membership[run] & (1UL << symbol)) != 0UL) { masks[symbol] |= 1UL << letters[run]; }
                }
            }
            return new(starts, letters, letterCount);
        }
    }

    // The compile-time term store: every term is hash-consed, so two similar derivatives share one identity and the
    // machine's states are the distinct similarity classes reached from the root.
    private sealed class PatternTerms {
        private enum Kind : byte { Empty, Epsilon, Letters, Concat, Or, And, Star, Not }

        private readonly record struct Term(Kind Kind, ulong Mask, int Left, int Right, int[] Items);
        private readonly List<Term> m_terms = [];
        private readonly Dictionary<Term, int> m_identities = new(TermComparer.Instance);
        private readonly Dictionary<(int Term, int Letter), int> m_derivatives = [];
        private readonly int m_letterCount;
        private readonly ulong m_all;

        public PatternTerms(int letterCount) {
            m_letterCount = letterCount;
            m_all = (letterCount == 64) ? ulong.MaxValue : ((1UL << letterCount) - 1UL);
            Empty = Intern(new(Kind.Empty, 0, -1, -1, []));
            Epsilon = Intern(new(Kind.Epsilon, 0, -1, -1, []));
            Universe = Intern(new(Kind.Not, 0, Empty, -1, []));
        }

        public int Empty { get; }
        public int Epsilon { get; }
        public int Universe { get; }

        public bool TryLower(WorldPatternNode? node, ulong[] masks, Dictionary<string, int> names, out int term, out string reason) {
            term = Empty;
            reason = string.Empty;

            switch (node) {
                case WorldPatternNode.Symbol symbol:
                    if (!names.TryGetValue(symbol.Name ?? string.Empty, out var ordinal)) { reason = $"names no symbol '{symbol.Name}'"; return false; }
                    term = Letters(masks[ordinal]);
                    return true;
                case WorldPatternNode.Except except:
                    if (!names.TryGetValue(except.Name ?? string.Empty, out var excluded)) { reason = $"names no symbol '{except.Name}'"; return false; }
                    term = Letters(m_all & ~masks[excluded]);
                    return true;
                case WorldPatternNode.AnySymbol:
                    term = Letters(m_all);
                    return true;
                case WorldPatternNode.Nothing:
                    term = Epsilon;
                    return true;
                case WorldPatternNode.Sequence sequence: {
                    var items = sequence.Items ?? [];
                    term = Epsilon;
                    for (var index = items.Count - 1; index >= 0; index--) {
                        if (!TryLower(items[index], masks, names, out var part, out reason)) { return false; }
                        term = Concat(part, term);
                    }
                    return true;
                }
                case WorldPatternNode.Choice choice: {
                    if (choice.Items is not { Count: > 0 }) { reason = "choice needs at least one item"; return false; }
                    var parts = new int[choice.Items.Count];
                    for (var index = 0; index < parts.Length; index++) {
                        if (!TryLower(choice.Items[index], masks, names, out parts[index], out reason)) { return false; }
                    }
                    term = Or(parts);
                    return true;
                }
                case WorldPatternNode.Both both: {
                    if (both.Items is not { Count: > 0 }) { reason = "all needs at least one item"; return false; }
                    var parts = new int[both.Items.Count];
                    for (var index = 0; index < parts.Length; index++) {
                        if (!TryLower(both.Items[index], masks, names, out parts[index], out reason)) { return false; }
                    }
                    term = And(parts);
                    return true;
                }
                case WorldPatternNode.Complement complement:
                    if (!TryLower(complement.Item, masks, names, out var negated, out reason)) { return false; }
                    term = Not(negated);
                    return true;
                case WorldPatternNode.Optional optional:
                    if (!TryLower(optional.Item, masks, names, out var maybe, out reason)) { return false; }
                    term = Or([Epsilon, maybe]);
                    return true;
                case WorldPatternNode.Star star:
                    if (!TryLower(star.Item, masks, names, out var starred, out reason)) { return false; }
                    term = Star(starred);
                    return true;
                case WorldPatternNode.Plus plus:
                    if (!TryLower(plus.Item, masks, names, out var repeated, out reason)) { return false; }
                    term = Concat(repeated, Star(repeated));
                    return true;
                case WorldPatternNode.Repeat repeat: {
                    if (repeat.Min < 0 || repeat.Max < repeat.Min || repeat.Max > WorldPatternCapacity.MaxRepeat) { reason = $"repeat needs 0 <= min <= max <= {WorldPatternCapacity.MaxRepeat}"; return false; }
                    if (!TryLower(repeat.Item, masks, names, out var unit, out reason)) { return false; }
                    var optionalUnit = Or([Epsilon, unit]);
                    term = Epsilon;
                    for (var count = 0; count < repeat.Max; count++) {
                        term = Concat((count < repeat.Min) ? unit : optionalUnit, term);
                    }
                    return true;
                }
                default:
                    reason = "contains a null or unknown node";
                    return false;
            }
        }

        // Breadth-first over derivatives: state 0 is the root, every letter of every discovered state is followed once,
        // and the walk refuses the moment the budget would be exceeded.
        public bool TryExplore(int root, int stateLimit, out int[] transitions, out bool[] accepting) {
            var states = new List<int> { root };
            var indices = new Dictionary<int, int> { [root] = 0 };
            var table = new List<int>();

            for (var state = 0; state < states.Count; state++) {
                for (var letter = 0; letter < m_letterCount; letter++) {
                    var next = Derivative(states[state], letter);

                    if (!indices.TryGetValue(next, out var index)) {
                        if (states.Count == stateLimit) {
                            transitions = [];
                            accepting = [];
                            return false;
                        }

                        index = states.Count;
                        indices[next] = index;
                        states.Add(next);
                    }

                    table.Add(index);
                }
            }

            transitions = [.. table];
            accepting = new bool[states.Count];

            for (var state = 0; state < states.Count; state++) {
                accepting[state] = Nullable(states[state]);
            }

            return true;
        }

        private bool Nullable(int term) {
            var node = m_terms[term];

            return node.Kind switch {
                Kind.Epsilon or Kind.Star => true,
                Kind.Empty or Kind.Letters => false,
                Kind.Concat => Nullable(node.Left) && Nullable(node.Right),
                Kind.Or => node.Items.Any(Nullable),
                Kind.And => node.Items.All(Nullable),
                Kind.Not => !Nullable(node.Left),
                _ => false,
            };
        }

        private int Derivative(int term, int letter) {
            if (m_derivatives.TryGetValue((term, letter), out var known)) {
                return known;
            }

            var node = m_terms[term];
            var result = node.Kind switch {
                Kind.Empty or Kind.Epsilon => Empty,
                Kind.Letters => (((node.Mask >> letter) & 1UL) != 0UL) ? Epsilon : Empty,
                Kind.Concat => Nullable(node.Left)
                    ? Or([Concat(Derivative(node.Left, letter), node.Right), Derivative(node.Right, letter)])
                    : Concat(Derivative(node.Left, letter), node.Right),
                Kind.Or => Or(Derivatives(node.Items, letter)),
                Kind.And => And(Derivatives(node.Items, letter)),
                Kind.Star => Concat(Derivative(node.Left, letter), term),
                Kind.Not => Not(Derivative(node.Left, letter)),
                _ => Empty,
            };

            m_derivatives[(term, letter)] = result;
            return result;
        }

        private int[] Derivatives(int[] items, int letter) {
            var result = new int[items.Length];
            for (var index = 0; index < items.Length; index++) {
                result[index] = Derivative(items[index], letter);
            }
            return result;
        }

        private int Letters(ulong mask) => (mask == 0UL) ? Empty : Intern(new(Kind.Letters, mask, -1, -1, []));

        private int Concat(int left, int right) {
            if (left == Empty || right == Empty) { return Empty; }
            if (left == Epsilon) { return right; }
            if (right == Epsilon) { return left; }
            if (m_terms[left].Kind == Kind.Concat) { return Concat(m_terms[left].Left, Concat(m_terms[left].Right, right)); }
            return Intern(new(Kind.Concat, 0, left, right, []));
        }

        private int Or(int[] parts) {
            var items = new SortedSet<int>();
            var mask = 0UL;

            foreach (var part in Flatten(parts, Kind.Or)) {
                if (part == Universe) { return Universe; }
                if (part == Empty) { continue; }
                if (m_terms[part].Kind == Kind.Letters) { mask |= m_terms[part].Mask; continue; }
                items.Add(part);
            }
            if (mask != 0UL) { items.Add(Letters(mask)); }

            return items.Count switch {
                0 => Empty,
                1 => items.Min,
                _ => Intern(new(Kind.Or, 0, -1, -1, [.. items])),
            };
        }

        private int And(int[] parts) {
            var items = new SortedSet<int>();
            var mask = m_all;
            var sawLetters = false;

            foreach (var part in Flatten(parts, Kind.And)) {
                if (part == Empty) { return Empty; }
                if (part == Universe) { continue; }
                if (m_terms[part].Kind == Kind.Letters) { mask &= m_terms[part].Mask; sawLetters = true; continue; }
                items.Add(part);
            }
            if (sawLetters) {
                if (mask == 0UL) { return Empty; }
                items.Add(Letters(mask));
            }

            return items.Count switch {
                0 => Universe,
                1 => items.Min,
                _ => Intern(new(Kind.And, 0, -1, -1, [.. items])),
            };
        }

        private int Star(int item) {
            if (item == Empty || item == Epsilon) { return Epsilon; }
            if (m_terms[item].Kind == Kind.Star) { return item; }
            return Intern(new(Kind.Star, 0, item, -1, []));
        }

        private int Not(int item) => (m_terms[item].Kind == Kind.Not) ? m_terms[item].Left : Intern(new(Kind.Not, 0, item, -1, []));

        private IEnumerable<int> Flatten(int[] parts, Kind kind) {
            foreach (var part in parts) {
                if (m_terms[part].Kind == kind) {
                    foreach (var nested in m_terms[part].Items) { yield return nested; }
                } else {
                    yield return part;
                }
            }
        }

        private int Intern(Term term) {
            if (!m_identities.TryGetValue(term, out var identity)) {
                identity = m_terms.Count;
                m_terms.Add(term);
                m_identities[term] = identity;
            }

            return identity;
        }

        // Structural identity: two terms are one term when every field and every item agrees.
        private sealed class TermComparer : IEqualityComparer<Term> {
            public static TermComparer Instance { get; } = new();

            public bool Equals(Term x, Term y) => x.Kind == y.Kind && x.Mask == y.Mask && x.Left == y.Left && x.Right == y.Right && x.Items.AsSpan().SequenceEqual(y.Items);

            public int GetHashCode(Term term) {
                var hash = new HashCode();
                hash.Add(term.Kind);
                hash.Add(term.Mask);
                hash.Add(term.Left);
                hash.Add(term.Right);
                foreach (var item in term.Items) { hash.Add(item); }
                return hash.ToHashCode();
            }
        }
    }
}

/// <summary>Every compiled pattern of one document, keyed by name.</summary>
public sealed class CompiledWorldPatterns {
    private readonly Dictionary<string, CompiledWorldPattern> m_patterns;

    private CompiledWorldPatterns(Dictionary<string, CompiledWorldPattern> patterns) {
        m_patterns = patterns;
    }

    /// <summary>The empty table.</summary>
    public static CompiledWorldPatterns Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>Gets the compiled patterns in declaration order.</summary>
    public IEnumerable<CompiledWorldPattern> All => m_patterns.Values;
    /// <summary>Gets the number of compiled patterns.</summary>
    public int Count => m_patterns.Count;

    /// <summary>Finds a compiled pattern by name.</summary>
    /// <param name="name">The pattern name.</param>
    /// <param name="pattern">The compiled pattern, when declared.</param>
    /// <returns><see langword="true"/> when the document declares it.</returns>
    public bool TryGet(string name, out CompiledWorldPattern pattern) => m_patterns.TryGetValue(name, out pattern!);

    /// <summary>Compiles every row of a document's <c>patterns</c> section.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="patterns">The table, on success.</param>
    /// <param name="errors">Every refusal, by row.</param>
    /// <returns><see langword="true"/> when every row compiled.</returns>
    public static bool TryCompileAll(WorldDefinition definition, out CompiledWorldPatterns patterns, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: errors);

        var table = new Dictionary<string, CompiledWorldPattern>(StringComparer.Ordinal);
        var rows = definition.Patterns;

        if (rows.Count > WorldPatternCapacity.MaxRows) {
            errors.Add(item: $"patterns declares {rows.Count} rows; the maximum is {WorldPatternCapacity.MaxRows}.");
        }

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];

            if (row is null) {
                errors.Add(item: $"patterns[{index}] is null.");
                continue;
            }
            if (!CompiledWorldPattern.TryCompile(row: row, compiled: out var compiled, reason: out var reason)) {
                errors.Add(item: $"patterns[{index}] {reason}.");
                continue;
            }
            if (!table.TryAdd(row.Name.Value, compiled!)) {
                errors.Add(item: $"patterns[{index}] name '{row.Name}' is duplicated.");
            }
        }

        patterns = new(table);
        return errors.Count == 0;
    }
}
