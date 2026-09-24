using Puck.State;

namespace Puck.Transpiler;

/// <summary>Ties the DSL's kind keyword spellings to the <c>Puck.State</c> enum they stand for. The parser, the
/// emitter, the decompiler and the language server all read this table rather than each carrying a hand-typed list of
/// member names. A kind spelling that is not exactly an enum member's own name is refused here, never folded onto a
/// neighbouring member. A comparison's operator and document spellings belong to
/// <see cref="ExpressionComparisons"/>.</summary>
public static class PuckDslVocabulary {
    private static readonly CellKind[] ComparisonKinds = [CellKind.Int, CellKind.Fixed];

    /// <summary>The cell kinds a <c>: Kind</c>/<c>as Kind</c> annotation and a <c>local</c> declaration admit, spelled
    /// by their own enum member names. <c>CompareValue.Kind</c> and <c>RuleLocal.Kind</c> are both
    /// <see cref="CellKind"/>, but only the numeric arms are meaningful for a comparison or a local.</summary>
    public static IReadOnlyList<string> ComparisonKindNames { get; } = [.. ComparisonKinds.Select(selector: static kind => Enum.GetName(value: kind)!)];

    /// <summary>Reads a <c>: Kind</c>/<c>as Kind</c>/<c>local</c> kind word, accepting only a member name this DSL
    /// admits (<see cref="ComparisonKindNames"/>).</summary>
    /// <param name="word">The kind word.</param>
    /// <param name="kind">The matching cell kind.</param>
    /// <returns><see langword="true"/> when <paramref name="word"/> is exactly an admitted member name.</returns>
    public static bool TryParseComparisonKind(string? word, out CellKind kind) {
        kind = default;
        if (word is null) {
            return false;
        }
        foreach (var candidate in ComparisonKinds) {
            if (string.Equals(
                a: Enum.GetName(value: candidate),
                b: word,
                comparisonType: StringComparison.Ordinal
            )) {
                kind = candidate;
                return true;
            }
        }
        return false;
    }
}
