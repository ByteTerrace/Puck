namespace Puck.Assets.Documents;

/// <summary>The one reading of "which declared names did the author probably mean": the declared names spelled within
/// a third of the written name's length (at least two edits) of it, ignoring letter case, closest first. Every refusal
/// of an unknown document member or name takes its suggestion from here, so no two refusals disagree about what is
/// near.</summary>
public static class DocumentNearestNames {
    /// <summary>The most names <see cref="Among"/> returns.</summary>
    public const int MaxNearNames = 3;

    /// <summary>Finds the declared names nearest a written one.</summary>
    /// <param name="name">The name the author wrote.</param>
    /// <param name="candidates">The names the document declares.</param>
    /// <returns>Up to <see cref="MaxNearNames"/> candidates within the edit limit, closest first and ties in ordinal
    /// order; empty when none is near.</returns>
    public static string[] Among(string name, IEnumerable<string> candidates) {
        var limit = Math.Max(val1: 2, val2: (name.Length / 3));
        var written = name.ToLowerInvariant();

        return [.. candidates
            .Select(selector: candidate => (Candidate: candidate, Distance: EditDistance(a: written, b: candidate.ToLowerInvariant())))
            .Where(predicate: pair => (pair.Distance <= limit))
            .OrderBy(keySelector: static pair => pair.Distance)
            .ThenBy(keySelector: static pair => pair.Candidate, comparer: StringComparer.Ordinal)
            .Take(count: MaxNearNames)
            .Select(selector: static pair => pair.Candidate)];
    }
    /// <summary>Spells the suggestion a refusal appends, or nothing when no candidate is near.</summary>
    /// <param name="name">The name the author wrote.</param>
    /// <param name="candidates">The names the document declares.</param>
    /// <returns>For example <c>; nearest: 'size', 'sides'</c>; empty when none is near.</returns>
    public static string Suggestion(string name, IEnumerable<string> candidates) {
        var near = Among(candidates: candidates, name: name);

        return ((near.Length == 0) ? string.Empty : $"; nearest: {string.Join(separator: ", ", values: near.Select(selector: static candidate => $"'{candidate}'"))}");
    }

    // The Levenshtein distance between two names.
    private static int EditDistance(string a, string b) {
        var previous = new int[(b.Length + 1)];
        var current = new int[(b.Length + 1)];

        for (var j = 0; (j <= b.Length); j++) {
            previous[j] = j;
        }
        for (var i = 1; (i <= a.Length); i++) {
            current[0] = i;
            for (var j = 1; (j <= b.Length); j++) {
                current[j] = Math.Min(
                    val1: Math.Min(val1: (previous[j] + 1), val2: (current[(j - 1)] + 1)),
                    val2: (previous[(j - 1)] + ((a[(i - 1)] == b[(j - 1)]) ? 0 : 1))
                );
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
