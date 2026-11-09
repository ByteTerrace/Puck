using System.Globalization;

namespace Puck.Cli;

/// <summary>
/// The lines a branch adds, per file, read from git: the one parser of a unified diff's hunk headers, so a verb that asks
/// "which lines did this branch write" (the scan's duplicate check) never reads <c>git diff</c> text itself.
/// </summary>
public static class CliGitDiff {
    /// <summary>The 1-based, inclusive line range an added run covers in the new file.</summary>
    /// <param name="Start">The first added line.</param>
    /// <param name="End">The last added line.</param>
    public readonly record struct LineRange(int Start, int End);

    /// <summary>Reads the lines a branch adds to files matching a pathspec, as the branch's own change set: the diff from
    /// the merge base of <paramref name="baseRevision"/> and the head to the head, so work that landed on the base
    /// since the branch left it is not counted.</summary>
    /// <param name="repository">The directory git runs against.</param>
    /// <param name="baseRevision">The revision the branch is measured against.</param>
    /// <param name="head">The revision holding the branch's work, or <see langword="null"/> for the working tree, whose
    /// untracked, unignored files count as wholly added.</param>
    /// <param name="pathspec">The pathspec limiting the diff, such as <c>*.cs</c>.</param>
    /// <param name="added">The added line ranges by repository-relative path with forward slashes, in file order; a pure
    /// deletion contributes none, and a file with no added line is absent.</param>
    /// <param name="reason">Why git could not answer, or empty.</param>
    /// <returns><see langword="true"/> when git answered.</returns>
    public static bool TryReadAdded(string repository, string baseRevision, string? head, string pathspec, out Dictionary<string, List<LineRange>> added, out string reason) {
        added = new Dictionary<string, List<LineRange>>(comparer: StringComparer.Ordinal);
        reason = string.Empty;

        var tip = (head ?? "HEAD");

        if (!CliGit.TryMergeBase(first: baseRevision, mergeBase: out var mergeBase, repository: repository, second: tip)) {
            reason = $"{baseRevision} and {tip} share no merge base.";

            return false;
        }

        // The working-tree form compares the merge base with the files on disk (tracked edits included); the revision
        // form compares two commits. --no-renames keeps a moved file's lines added at the new path, where they are read.
        string[] arguments = ((head is null)
            ? ["diff", "--no-color", "--no-ext-diff", "--no-renames", "--unified=0", mergeBase, "--", pathspec]
            : ["diff", "--no-color", "--no-ext-diff", "--no-renames", "--unified=0", mergeBase, head, "--", pathspec]);
        var diff = CliGit.Run(repository, arguments);

        if (diff.ExitCode != 0) {
            reason = diff.Stderr.Trim();

            return false;
        }

        Parse(
            added: added,
            diff: diff.Stdout
        );

        if (head is null) {
            var untracked = CliGit.Run(repository, "ls-files", "-z", "--others", "--exclude-standard", "--", pathspec);

            if (untracked.ExitCode != 0) {
                reason = untracked.Stderr.Trim();

                return false;
            }

            foreach (var path in untracked.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0')) {
                added[path] = [new LineRange(Start: 1, End: int.MaxValue)];
            }
        }

        return true;
    }
    // Reads each file's `+++ b/<path>` header and the `+<start>[,<count>]` side of every hunk header; a zero count is a
    // pure deletion and adds nothing.
    private static void Parse(Dictionary<string, List<LineRange>> added, string diff) {
        List<LineRange>? current = null;

        foreach (var raw in diff.Split(separator: '\n')) {
            var line = raw.TrimEnd(trimChar: '\r');

            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "+++ ")) {
                current = null;

                if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "+++ b/")) {
                    current = [];
                    added[line[6..]] = current;
                }

                continue;
            }

            if ((current is null) || !line.StartsWith(comparisonType: StringComparison.Ordinal, value: "@@ ")) {
                continue;
            }

            var plus = line.IndexOf(value: '+');
            var end = line.IndexOf(startIndex: plus, value: ' ');
            var span = line[(plus + 1)..end].Split(separator: ',');
            var start = int.Parse(provider: CultureInfo.InvariantCulture, s: span[0]);
            var count = ((span.Length > 1)
                ? int.Parse(provider: CultureInfo.InvariantCulture, s: span[1])
                : 1);

            if (count > 0) {
                current.Add(item: new LineRange(Start: start, End: ((start + count) - 1)));
            }
        }

        foreach (var path in added.Where(predicate: static pair => (pair.Value.Count == 0)).Select(selector: static pair => pair.Key).ToList()) {
            _ = added.Remove(key: path);
        }
    }
}
