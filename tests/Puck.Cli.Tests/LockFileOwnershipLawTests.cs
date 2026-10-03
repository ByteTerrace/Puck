using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: every tracked <c>packages.lock.json</c> sits beside the project whose restore writes
/// it. A lock no project owns is never refreshed by any restore, so it pins versions nothing builds against and drifts
/// silently.</summary>
public sealed class LockFileOwnershipLawTests {
    [Fact]
    public void EveryTrackedLockFileSitsBesideTheProjectThatRestoresIt() {
        var root = RepositoryPaths.RequireRoot();
        // experimental/ is quarantined out of the build (AGENTS.md), so no restore of the live tree owns its locks.
        var listing = CliGit.Run(root, "ls-files", "-z", "--", "*packages.lock.json", ":!:experimental/");

        Assert.Equal(expected: 0, actual: listing.ExitCode);

        var orphans = listing.Stdout
            .Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0')
            .Where(predicate: lockFile => !Directory.EnumerateFiles(path: Path.GetDirectoryName(path: Path.Combine(path1: root, path2: lockFile))!, searchPattern: "*.csproj").Any())
            .ToArray();

        Assert.True(condition: (orphans.Length == 0), userMessage: $"lock files no project restores: {string.Join(separator: ", ", values: orphans)}");
    }
}
