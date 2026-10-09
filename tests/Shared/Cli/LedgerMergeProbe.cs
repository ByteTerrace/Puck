using Puck.Testing;
using Xunit;

namespace Puck.Cli.Testing;

/// <summary>Merges three versions of a ledger the way git does when it merges two branches, so a law can show a
/// concurrent change collide on paper without a repository.</summary>
internal static class LedgerMergeProbe {
    /// <summary>Three-way merges <paramref name="ours"/> and <paramref name="theirs"/> over <paramref name="baseText"/>
    /// with <c>git merge-file</c>.</summary>
    /// <param name="baseText">The common ancestor's text.</param>
    /// <param name="ours">One branch's text.</param>
    /// <param name="theirs">The other branch's text.</param>
    /// <returns>The number of conflicts git reports, and the merged text with its conflict markers.</returns>
    public static (int Conflicts, string Merged) Merge(string baseText, string ours, string theirs) {
        using var directory = new TemporaryDirectory(prefix: "puck-ledger-merge-");

        File.WriteAllText(
            contents: baseText,
            path: Path.Combine(
                path1: directory.RootPath,
                path2: "base.json"
            )
        );
        File.WriteAllText(
            contents: ours,
            path: Path.Combine(
                path1: directory.RootPath,
                path2: "ours.json"
            )
        );
        File.WriteAllText(
            contents: theirs,
            path: Path.Combine(
                path1: directory.RootPath,
                path2: "theirs.json"
            )
        );

        var result = CliGit.Run(
            directory.RootPath,
            "merge-file", "-p", "ours.json", "base.json", "theirs.json"
        );

        Assert.True(
            condition: (result.ExitCode >= 0),
            userMessage: $"git merge-file failed: {result.Stderr}"
        );

        return (result.ExitCode, result.Stdout);
    }
}
