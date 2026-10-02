using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Every tracked MSBuild project, props and targets file outside <c>experimental/</c> spells its paths with forward
/// slashes (docs/development/contributing.md, File paths). Puck's MSBuild readers read a backslash as a separator the
/// way MSBuild does, so this law, not any reader, keeps the one spelling. No backslash appears in these files at all,
/// comments included, so the law is a plain character check.
/// </summary>
public sealed class MsBuildPathSpellingLawTests {
    // Every 1-based line of an MSBuild file's text that holds a backslash.
    private static IEnumerable<int> BackslashLines(string text) => text.Split(separator: '\n')
        .Select(selector: static (line, index) => (Line: line, Number: (index + 1)))
        .Where(predicate: static entry => entry.Line.Contains(value: '\\'))
        .Select(selector: static entry => entry.Number);

    [Fact]
    public void EveryTrackedMsBuildFileSpellsItsPathsWithForwardSlashes() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var listing = CliGit.Run(repositoryRoot, "ls-files", "-z", "--", "*.csproj", "*.props", "*.targets");

        Assert.True(
            condition: (listing.ExitCode == 0),
            userMessage: $"git ls-files failed: {listing.Stderr}"
        );

        var files = listing.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        ).Where(predicate: static file => !file.StartsWith(comparisonType: StringComparison.Ordinal, value: "experimental/")).ToArray();
        var offending = files.SelectMany(selector: file => BackslashLines(text: File.ReadAllText(path: Path.Combine(
            path1: repositoryRoot,
            path2: file
        ))).Select(selector: line => $"{file}:{line}")).ToArray();

        Assert.NotEmpty(collection: files);
        Assert.True(
            condition: (offending.Length == 0),
            userMessage: $"MSBuild paths use forward slashes; these lines hold a backslash:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: offending)}"
        );
    }
    [Fact]
    public void TheCheckFindsABackslashOnTheLineThatHoldsIt() {
        Assert.Equal(
            actual: BackslashLines(text: "<Project>\n    <Import Project=\"../../build/WorldAssets.targets\" />\n    <Import Project=\"..\\..\\build\\Shaders.targets\" />\n</Project>\n"),
            expected: [3]
        );
    }
}
