namespace Puck.Cli.Landing;

/// <summary>
/// The two-diff comparison behind <see cref="LandingCommand"/>: which lines a revision range deletes, per file, and
/// the multiset difference between two such sets.
/// </summary>
/// <remarks>Deleted lines are compared as a MULTISET, not a set. A file that deletes three identical
/// <c>}</c> lines relative to the tip and two relative to the base has dropped one line that came from somewhere
/// else, and set semantics would report nothing. The comparison is on the line's exact text: whitespace-only
/// differences are real differences here, because the question is whether a byte of somebody's landing is
/// disappearing, not whether the code means the same thing.</remarks>
internal static class LandingDiff {
    /// <summary>Every line <paramref name="to"/> deletes relative to <paramref name="from"/>, keyed by the path the
    /// deletion happened in.</summary>
    /// <param name="from">The revision to compare from.</param>
    /// <param name="to">The revision to compare to.</param>
    public static Dictionary<string, List<string>> DeletedLines(string from, string to) {
        // -U0: no context lines, so every '-' line in the output is a genuine deletion rather than shared context.
        // --no-renames: a rename detected as such would hide the deletion side; this check wants the raw removal.
        // --no-color and -M0 keep the stream machine-readable and stable across a user's git config.
        var output = Git.Capture("diff", "--no-color", "--no-renames", "-U0", $"{from}..{to}");
        var deletions = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);
        var path = string.Empty;

        foreach (var line in output.Split(separator: '\n')) {
            var text = line.TrimEnd(trimChar: '\r');

            if (text.StartsWith(value: "+++ b/", comparisonType: StringComparison.Ordinal)) {
                path = text[6..];

                continue;
            }

            // A whole-file deletion writes "+++ /dev/null", so the path has to come from the '---' side instead.
            if (text.StartsWith(value: "+++ /dev/null", comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (text.StartsWith(value: "--- a/", comparisonType: StringComparison.Ordinal)) {
                path = text[6..];

                continue;
            }

            // '---' and '-' are both prefixed with '-', so the header check above must run first.
            if ((path.Length == 0) || !text.StartsWith(value: "-", comparisonType: StringComparison.Ordinal) || text.StartsWith(value: "---", comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (!deletions.TryGetValue(key: path, value: out var lines)) {
                lines = [];
                deletions.Add(key: path, value: lines);
            }

            lines.Add(item: text[1..]);
        }

        return deletions;
    }

    /// <summary>The multiset difference <paramref name="left"/> minus <paramref name="right"/>, per path — the
    /// deletions a landing performs that its own change set does not account for.</summary>
    /// <param name="left">The deletions the push would perform (relative to the landing tip).</param>
    /// <param name="right">The deletions the author's own work performs (relative to their base).</param>
    public static Dictionary<string, List<string>> Subtract(IReadOnlyDictionary<string, List<string>> left, IReadOnlyDictionary<string, List<string>> right) {
        var unaccounted = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);

        foreach (var (path, lines) in left) {
            var remaining = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

            if (right.TryGetValue(key: path, value: out var accountedLines)) {
                foreach (var line in accountedLines) {
                    remaining[line] = (remaining.TryGetValue(key: line, value: out var count) ? (count + 1) : 1);
                }
            }

            var surplus = new List<string>();

            foreach (var line in lines) {
                if (remaining.TryGetValue(key: line, value: out var count) && (count > 0)) {
                    remaining[line] = (count - 1);

                    continue;
                }

                // A blank or whitespace-only line carries no evidence about whose landing it came from, and a
                // reformatting that shifts blank lines around would otherwise fill the report with noise that
                // teaches a reader to skim it. Real content is what this verb is protecting.
                if (line.Trim().Length > 0) {
                    surplus.Add(item: line);
                }
            }

            if (surplus.Count > 0) {
                unaccounted.Add(key: path, value: surplus);
            }
        }

        return unaccounted;
    }

    /// <summary>The commits that touched <paramref name="path"/> between the author's base and the landing tip —
    /// exactly the landings the author never worked from, and therefore whose content an unaccounted deletion is
    /// dropping.</summary>
    /// <param name="from">The author's base.</param>
    /// <param name="to">The landing tip.</param>
    /// <param name="path">The repository-relative path.</param>
    public static IReadOnlyList<string> CommitsBetween(string from, string to, string path) {
        var output = Git.Capture("log", "--no-color", "--oneline", "--no-decorate", $"{from}..{to}", "--", path);
        var commits = new List<string>();

        foreach (var line in output.Split(separator: '\n')) {
            var text = line.TrimEnd(trimChar: '\r');

            if (text.Length > 0) {
                commits.Add(item: text);
            }
        }

        return commits;
    }
}
