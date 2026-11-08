using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>A worktree record from Git's NUL-delimited porcelain output. Null lock/prune reasons mean the flags
/// are absent; an empty reason means the flag is present without a reason.</summary>
public sealed record CliWorktree(string Path, string Head, string? Branch, string? Locked, string? Prunable) {
    /// <summary>Parses records without interpreting quoting, spaces or newlines inside a path.</summary>
    /// <param name="listing">Output of <c>git worktree list --porcelain -z</c>.</param>
    /// <returns>Every worktree, including a final record without a trailing separator.</returns>
    public static List<CliWorktree> Parse(string listing) {
        var worktrees = new List<CliWorktree>();
        string? path = null, branch = null, locked = null, prunable = null;
        var head = string.Empty;

        void Flush() {
            if (path is not null) { worktrees.Add(item: new CliWorktree(Branch: branch, Head: head, Locked: locked, Path: path, Prunable: prunable)); }
            path = branch = locked = prunable = null;
            head = string.Empty;
        }
        foreach (var field in listing.Split(separator: '\0')) {
            if (field.Length == 0) { Flush(); } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree ")) { path = PuckPaths.Normalize(path: field[9..]); } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "HEAD ")) { head = field[5..]; } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "branch refs/heads/")) { branch = field[18..]; } else if ((field == "locked") || field.StartsWith(comparisonType: StringComparison.Ordinal, value: "locked ")) { locked = ((field.Length == 6) ? string.Empty : field[7..]); } else if ((field == "prunable") || field.StartsWith(comparisonType: StringComparison.Ordinal, value: "prunable ")) { prunable = ((field.Length == 8) ? string.Empty : field[9..]); }
        }
        Flush();
        return worktrees;
    }
}
