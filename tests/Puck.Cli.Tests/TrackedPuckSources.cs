using Xunit;

namespace Puck.Cli.Tests;

/// <summary>The repository's tracked <c>.puck</c> sources outside the quarantined <c>experimental</c> trees, read from
/// git so another worktree or scratch output under the checkout is never one of them.</summary>
internal static class TrackedPuckSources {
    // Quarantined: read, never built or run.
    private static readonly string[] Excluded = ["experimental"];

    /// <summary>Returns the tracked sources under any of <paramref name="roots"/>, or every tracked source when none
    /// is named.</summary>
    /// <param name="roots">The forward-slashed directories, relative to the repository root, to keep sources under.</param>
    /// <returns>The sources' forward-slashed paths relative to the repository root, in ordinal order.</returns>
    public static TheoryData<string> Under(params string[] roots) {
        var root = RepositoryPaths.RequireRoot();
        var found = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var listing = CliGit.Run(
            arguments: ["ls-files", "-z", "--", "*.puck"],
            repository: root
        );

        Assert.True(
            condition: (listing.ExitCode == 0),
            userMessage: $"git ls-files failed: {listing.Stderr}"
        );

        foreach (var entry in listing.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        )) {
            var relative = entry.Trim();

            if (
                (relative.Length == 0) ||
                relative.Split('/').Any(predicate: segment => Excluded.Contains(value: segment, comparer: StringComparer.OrdinalIgnoreCase)) ||
                ((roots.Length > 0) && !roots.Any(predicate: directory => relative.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{directory}/")))
            ) {
                continue;
            }
            found.Add(item: relative);
        }

        Assert.NotEmpty(collection: found);

        return new TheoryData<string>(values: found);
    }
}
