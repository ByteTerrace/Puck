using System.Text.RegularExpressions;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Every indented JSON writer in the tracked <c>src/</c> tree outside <c>experimental/</c> names its newline as
/// <c>"\n"</c>. An indented <c>System.Text.Json</c> writer otherwise breaks lines with the platform's
/// <see cref="Environment.NewLine"/>, so the same document serializes to different bytes on Windows and Linux, and
/// any hash of those bytes (a canonical asset hash, a world definition's fingerprint or content hash) differs between
/// them. The law reads the source, not a run's output, because on Linux the output agrees either way.
/// </summary>
public sealed partial class JsonNewlineSpellingLawTests {
    [GeneratedRegex(pattern: @"WriteIndented\s*=\s*true")]
    private static partial Regex Indented();
    [GeneratedRegex(pattern: @"NewLine\s*=\s*""\\n""")]
    private static partial Regex LineFeed();
    // Every 1-based line of a C# source whose indented-writer setting has no line-feed newline in the same options:
    // the initializer or attribute argument list the setting sits in, from its opening brace or parenthesis.
    private static IEnumerable<int> UnpinnedLines(string text) {
        foreach (Match match in Indented().Matches(input: text)) {
            var opening = Math.Max(
                val1: text.LastIndexOf(
                    startIndex: match.Index,
                    value: '{'
                ),
                val2: text.LastIndexOf(
                    startIndex: match.Index,
                    value: '('
                )
            );
            var closing = text.IndexOfAny(
                anyOf: ['}', ')'],
                startIndex: match.Index
            );
            var options = text[Math.Max(val1: 0, val2: opening)..((closing < 0) ? text.Length : (closing + 1))];

            if (!LineFeed().IsMatch(input: options)) {
                yield return (text[..match.Index].Count(predicate: static character => (character == '\n')) + 1);
            }
        }
    }

    [Fact]
    public void EveryIndentedJsonWriterBreaksLinesWithALineFeed() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var listing = CliGit.Run(repositoryRoot, "ls-files", "-z", "--", "src/*.cs");

        Assert.True(
            condition: (listing.ExitCode == 0),
            userMessage: $"git ls-files failed: {listing.Stderr}"
        );

        var sources = listing.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        );
        var writers = 0;
        var unpinned = new List<string>();

        foreach (var source in sources) {
            var text = File.ReadAllText(path: Path.Combine(
                path1: repositoryRoot,
                path2: source
            ));

            writers += Indented().Count(input: text);
            unpinned.AddRange(collection: UnpinnedLines(text: text).Select(selector: line => $"{source}:{line}"));
        }

        Assert.True(condition: (writers > 0));
        Assert.True(
            condition: (unpinned.Count == 0),
            userMessage: $"An indented JSON writer names NewLine = \"\\n\" beside WriteIndented = true; these do not:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: unpinned)}"
        );
    }
    [Fact]
    public void TheCheckFindsAnIndentedWriterThatLeavesTheNewlineToThePlatform() {
        Assert.Equal(
            expected: [2],
            actual: UnpinnedLines(text: "var pinned = new JsonSerializerOptions { NewLine = \"\\n\", WriteIndented = true };\nvar platform = new JsonSerializerOptions { WriteIndented = true };\n")
        );
    }
}
