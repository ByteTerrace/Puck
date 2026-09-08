namespace Puck.State;

/// <summary>A compiled pattern: the refined alphabet that numbered its letters and the deterministic machine over
/// them, built from the pattern's Brzozowski derivatives.</summary>
/// <remarks>Each machine state IS a derivative of the pattern — the language still expected after the tokens read so
/// far — kept canonical by hash-consing with the classical similarity rules (flattened, sorted, deduplicated unions and
/// intersections; absorbed empties; merged letter sets). Similarity keeps the state count finite for every pattern,
/// complement and intersection included, and the row's <c>maxStates</c> bounds it by name at validation.</remarks>
public sealed class CompiledPattern {
    private readonly RangeAlphabet m_alphabet;
    private readonly ulong[] m_masks;
    private readonly int[] m_transitions;
    private readonly bool[] m_accepting;

    private CompiledPattern(PatternRow source, RangeAlphabet alphabet, ulong[] masks, int[] transitions, bool[] accepting) {
        Source = source;
        m_alphabet = alphabet;
        m_masks = masks;
        m_transitions = transitions;
        m_accepting = accepting;
    }

    /// <summary>Gets the authored row.</summary>
    public PatternRow Source { get; }
    /// <summary>Gets the number of letters after refinement, the unnamed remainder included.</summary>
    public int LetterCount => m_alphabet.LetterCount;
    /// <summary>Gets the number of states in the compiled machine.</summary>
    public int StateCount => m_accepting.Length;

    /// <summary>Runs a word of raw cell values through the machine.</summary>
    /// <param name="values">The word, raw in the pattern's kind.</param>
    /// <returns>1 when the whole word is in the language, 0 when it is not.</returns>
    public long Match(ReadOnlySpan<long> values) {
        var state = 0;

        for (var index = 0; index < values.Length; index++) {
            state = Step(state: state, letter: LetterOf(value: values[index]));
        }

        return m_accepting[state] ? 1L : 0L;
    }

    /// <summary>Finds the longest prefix of a word that is in the language.</summary>
    /// <param name="values">The word, raw in the pattern's kind.</param>
    /// <returns>The length of the longest accepted prefix, 0 when only the empty word is accepted, or -1 when no
    /// prefix is.</returns>
    public long LongestAcceptedPrefix(ReadOnlySpan<long> values) {
        var state = 0;
        var longest = (m_accepting[0] ? 0L : -1L);

        for (var index = 0; index < values.Length; index++) {
            state = Step(state: state, letter: LetterOf(value: values[index]));

            if (m_accepting[state]) {
                longest = index + 1;
            }
        }

        return longest;
    }

    /// <summary>Gets the letter a raw value reads as.</summary>
    /// <param name="value">The raw value in the pattern's kind.</param>
    public int LetterOf(long value) => m_alphabet.LetterOf(value: value);
    /// <summary>Follows one transition of the machine.</summary>
    /// <param name="state">The current state; 0 is the start.</param>
    /// <param name="letter">The letter read.</param>
    /// <returns>The next state.</returns>
    public int Step(int state, int letter) => m_transitions[(state * m_alphabet.LetterCount) + letter];
    /// <summary>Gets a value indicating whether a state accepts.</summary>
    /// <param name="state">The state.</param>
    public bool Accepts(int state) => m_accepting[state];
    /// <summary>Names a letter by the symbols it belongs to.</summary>
    /// <param name="letter">The letter.</param>
    /// <returns>The symbol names joined by <c>|</c>, or <c>remainder</c> for the unnamed letter.</returns>
    public string DescribeLetter(int letter) {
        var names = new List<string>();

        for (var symbol = 0; symbol < m_masks.Length; symbol++) {
            if (((m_masks[symbol] >> letter) & 1UL) != 0UL) {
                names.Add(Source.Symbols[symbol].Name.Value);
            }
        }

        return (names.Count == 0) ? "remainder" : string.Join('|', names);
    }

    /// <summary>Compiles one authored row: refines its symbols into letters, lowers the node tree to a canonical term,
    /// and explores the term's derivatives breadth-first into a table inside the row's state budget.</summary>
    /// <param name="row">The authored row.</param>
    /// <param name="compiled">The machine, on success.</param>
    /// <param name="reason">Why the row refused, on failure.</param>
    /// <returns><see langword="true"/> when the row compiled.</returns>
    public static bool TryCompile(PatternRow row, out CompiledPattern? compiled, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);

        compiled = null;
        var symbols = row.Symbols ?? [];

        if (row.Kind is not (CellKind.Int or CellKind.Fixed)) {
            reason = $"pattern '{row.Name}' kind must be int or fixed";
            return false;
        }
        if (row.MaxStates is < 1 or > PatternCapacity.MaxStates) {
            reason = $"pattern '{row.Name}' maxStates must be 1..{PatternCapacity.MaxStates}";
            return false;
        }
        if (symbols.Count is < 1 or > PatternCapacity.MaxSymbols) {
            reason = $"pattern '{row.Name}' declares {symbols.Count} symbols; 1..{PatternCapacity.MaxSymbols} are admitted";
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

        compiled = new(source: row, alphabet: alphabet, masks: masks, transitions: transitions, accepting: accepting);
        reason = string.Empty;
        return true;
    }

    private static bool TryLower(CellKind kind, decimal literal, out long raw) {
        try {
            raw = (kind == CellKind.Fixed)
                ? NumericLiteral.ToFixed(value: literal).Value
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

        // The n-ary lowering Choice and Both share: a non-empty item list, each item lowered in order, the first

        // refusal winning — the combinator (Or/And) is the caller's only difference.

        private bool TryLowerItems(IReadOnlyList<PatternNode>? items, string what, ulong[] masks, Dictionary<string, int> names, out int[] parts, out string reason) {

            if (items is not { Count: > 0 }) { parts = []; reason = $"{what} needs at least one item"; return false; }

            parts = new int[items.Count];

            for (var index = 0; index < parts.Length; index++) {

                if (!TryLower(items[index], masks, names, out parts[index], out reason)) { return false; }

            }

            reason = string.Empty;

            return true;

        }

        public bool TryLower(PatternNode? node, ulong[] masks, Dictionary<string, int> names, out int term, out string reason) {
            term = Empty;
            reason = string.Empty;

            switch (node) {
                case PatternNode.Symbol symbol:
                    if (!names.TryGetValue(symbol.Name ?? string.Empty, out var ordinal)) { reason = $"names no symbol '{symbol.Name}'"; return false; }
                    term = Letters(masks[ordinal]);
                    return true;
                case PatternNode.Except except:
                    if (!names.TryGetValue(except.Name ?? string.Empty, out var excluded)) { reason = $"names no symbol '{except.Name}'"; return false; }
                    term = Letters(m_all & ~masks[excluded]);
                    return true;
                case PatternNode.AnySymbol:
                    term = Letters(m_all);
                    return true;
                case PatternNode.Nothing:
                    term = Epsilon;
                    return true;
                case PatternNode.None:
                    term = Empty;
                    return true;
                case PatternNode.Sequence sequence: {
                    var items = sequence.Items ?? [];
                    term = Epsilon;
                    for (var index = items.Count - 1; index >= 0; index--) {
                        if (!TryLower(items[index], masks, names, out var part, out reason)) { return false; }
                        term = Concat(part, term);
                    }
                    return true;
                }
                case PatternNode.Choice choice: {
                    if (!TryLowerItems(choice.Items, "choice", masks, names, out var parts, out reason)) { return false; }
                    term = Or(parts);
                    return true;
                }
                case PatternNode.Both both: {
                    if (!TryLowerItems(both.Items, "all", masks, names, out var parts, out reason)) { return false; }
                    term = And(parts);
                    return true;
                }
                case PatternNode.Complement complement:
                    if (!TryLower(complement.Item, masks, names, out var negated, out reason)) { return false; }
                    term = Not(negated);
                    return true;
                case PatternNode.Optional optional:
                    if (!TryLower(optional.Item, masks, names, out var maybe, out reason)) { return false; }
                    term = Or([Epsilon, maybe]);
                    return true;
                case PatternNode.Star star:
                    if (!TryLower(star.Item, masks, names, out var starred, out reason)) { return false; }
                    term = Star(starred);
                    return true;
                case PatternNode.Plus plus:
                    if (!TryLower(plus.Item, masks, names, out var repeated, out reason)) { return false; }
                    term = Concat(repeated, Star(repeated));
                    return true;
                case PatternNode.Repeat repeat: {
                    if (repeat.Min < 0 || repeat.Max < repeat.Min || repeat.Max > PatternCapacity.MaxRepeat) { reason = $"repeat needs 0 <= min <= max <= {PatternCapacity.MaxRepeat}"; return false; }
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
public sealed class CompiledPatterns {
    private readonly Dictionary<string, CompiledPattern> m_patterns;

    private CompiledPatterns(Dictionary<string, CompiledPattern> patterns) {
        m_patterns = patterns;
    }

    /// <summary>The empty table.</summary>
    public static CompiledPatterns Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>Gets the compiled patterns in declaration order.</summary>
    public IEnumerable<CompiledPattern> All => m_patterns.Values;
    /// <summary>Gets the number of compiled patterns.</summary>
    public int Count => m_patterns.Count;

    /// <summary>Finds a compiled pattern by name.</summary>
    /// <param name="name">The pattern name.</param>
    /// <param name="pattern">The compiled pattern, when declared.</param>
    /// <returns><see langword="true"/> when the document declares it.</returns>
    public bool TryGet(string name, out CompiledPattern pattern) => m_patterns.TryGetValue(name, out pattern!);

    /// <summary>Compiles every row of a document's <c>patterns</c> section.</summary>
    /// <param name="rows">The pattern rows, or <see langword="null"/> for none.</param>
    /// <param name="patterns">The table, on success.</param>
    /// <param name="errors">Every refusal, by row.</param>
    /// <returns><see langword="true"/> when every row compiled.</returns>
    public static bool TryCompileAll(IReadOnlyList<PatternRow>? rows, out CompiledPatterns patterns, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: errors);

        var table = new Dictionary<string, CompiledPattern>(StringComparer.Ordinal);
        rows ??= [];

        if (rows.Count > PatternCapacity.MaxRows) {
            errors.Add(item: $"patterns declares {rows.Count} rows; the maximum is {PatternCapacity.MaxRows}.");
        }

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];

            if (row is null) {
                errors.Add(item: $"patterns[{index}] is null.");
                continue;
            }
            if (!CompiledPattern.TryCompile(row: row, compiled: out var compiled, reason: out var reason)) {
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
