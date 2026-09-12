using Puck.State;

namespace Puck.Transpiler;

/// <summary>Ties the DSL's operator and keyword spellings to the <c>Puck.State</c> enums they stand for. The parser,
/// the emitter, the decompiler and the language server all read this table rather than each carrying a hand-typed
/// list of member names. A comparison or kind spelling that is not exactly an enum member's own name is refused
/// here, never folded onto a neighbouring member.</summary>
public static class PuckDslVocabulary {
    private static readonly (string Symbol, ActionStateComparison Comparison)[] s_comparisons = [
        // Longest-first, so a symbol scan never reads "<=" as "<".
        ("==", ActionStateComparison.Equal),
        ("!=", ActionStateComparison.NotEqual),
        ("<=", ActionStateComparison.LessOrEqual),
        (">=", ActionStateComparison.GreaterOrEqual),
        ("<", ActionStateComparison.Less),
        (">", ActionStateComparison.Greater),
    ];

    private static readonly CellKind[] s_comparisonKinds = [CellKind.Int, CellKind.Fixed];

    /// <summary>The cell kinds a <c>: Kind</c>/<c>as Kind</c> annotation and a <c>bind</c> declaration admit, spelled
    /// by their own enum member names. <c>CompareValue.Kind</c> and <c>RuleBinding.Kind</c> are both
    /// <see cref="CellKind"/>, but only the numeric arms are meaningful for a comparison or a binding.</summary>
    public static IReadOnlyList<string> ComparisonKindNames { get; } = [.. s_comparisonKinds.Select(static kind => Enum.GetName(kind)!)];

    /// <summary>Maps a DSL comparison operator symbol to its <see cref="ActionStateComparison"/> member.</summary>
    /// <param name="symbol">The operator symbol (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>).</param>
    /// <param name="comparison">The matching comparison member.</param>
    /// <returns><see langword="true"/> when the symbol names a comparison.</returns>
    public static bool TryParseComparator(string symbol, out ActionStateComparison comparison) {
        foreach (var (candidate, value) in s_comparisons) {
            if (string.Equals(candidate, symbol, StringComparison.Ordinal)) {
                comparison = value;
                return true;
            }
        }
        comparison = default;
        return false;
    }

    /// <summary>The DSL operator symbol that spells <paramref name="comparison"/>.</summary>
    /// <param name="comparison">The comparison member.</param>
    /// <returns>The operator symbol.</returns>
    public static string SymbolFor(ActionStateComparison comparison) {
        foreach (var (symbol, value) in s_comparisons) {
            if (value == comparison) {
                return symbol;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "no DSL operator spells this comparison");
    }

    /// <summary>The wire spelling of <paramref name="comparison"/> — its own enum member name.</summary>
    /// <param name="comparison">The comparison member.</param>
    /// <returns>The canonical member name.</returns>
    public static string NameOf(ActionStateComparison comparison) => Enum.GetName(comparison)!;

    /// <summary>Reads a wire <c>comparison</c> spelling, accepting only the exact canonical member name. A document
    /// spelling it in any other casing deserializes one way in the engine and must never be folded to a different
    /// operator here, so it is refused rather than guessed at.</summary>
    /// <param name="name">The wire spelling.</param>
    /// <param name="comparison">The matching comparison member.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is exactly a member name.</returns>
    public static bool TryParseCanonicalComparisonName(string? name, out ActionStateComparison comparison) {
        comparison = default;
        return (name is not null)
            && Enum.TryParse(name, ignoreCase: false, out comparison)
            && Enum.IsDefined(comparison);
    }

    /// <summary>Reads a <c>: Kind</c>/<c>as Kind</c>/<c>bind</c> kind word, accepting only a member name this DSL
    /// admits (<see cref="ComparisonKindNames"/>).</summary>
    /// <param name="word">The kind word.</param>
    /// <param name="kind">The matching cell kind.</param>
    /// <returns><see langword="true"/> when <paramref name="word"/> is exactly an admitted member name.</returns>
    public static bool TryParseComparisonKind(string? word, out CellKind kind) {
        kind = default;
        if (word is null) {
            return false;
        }
        foreach (var candidate in s_comparisonKinds) {
            if (string.Equals(Enum.GetName(candidate), word, StringComparison.Ordinal)) {
                kind = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Swaps a comparison's direction (never its equality) — <c>a &lt; b</c> read as <c>b &gt; a</c>.</summary>
    /// <param name="comparison">The comparison to flip.</param>
    /// <returns>The flipped comparison.</returns>
    public static ActionStateComparison Flip(ActionStateComparison comparison) => comparison switch {
        ActionStateComparison.Less => ActionStateComparison.Greater,
        ActionStateComparison.LessOrEqual => ActionStateComparison.GreaterOrEqual,
        ActionStateComparison.Greater => ActionStateComparison.Less,
        ActionStateComparison.GreaterOrEqual => ActionStateComparison.LessOrEqual,
        _ => comparison,
    };
}
