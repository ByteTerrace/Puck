using Puck.Cli.Docs;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Laws for the fragment half of <c>puck docs links</c>: a link's <c>#fragment</c> must name a heading anchor
/// of the markdown file it points at, slugged as GitHub renders headings.</summary>
public sealed class DocsLinksCommandTests {
    private const string Target = """
        # Target

        ## Good heading

        ## Repeat

        ## Repeat

        ### `puck scan`—source sweep
        """;

    private static (int ExitCode, string Output) Check(string document) {
        using var directory = new TemporaryDirectory();

        directory.WriteText(
            name: "docs/target.md",
            text: Target
        );
        directory.WriteText(
            name: "docs/document.md",
            text: document
        );

        return ConsoleCapture.Run(run: () => DocsLinksCommand.Run(
            documents: ["docs/document.md"],
            repositoryRoot: directory.RootPath
        ));
    }

    [Fact]
    public void AGoodAnchorResolves() {
        var (exitCode, output) = Check(document: """
            See [the heading](target.md#good-heading) and [the sweep](target.md#puck-scansource-sweep).
            """);

        Assert.True(
            condition: (exitCode == 0),
            userMessage: output
        );
    }
    [Fact]
    public void ABrokenAnchorFailsTheRun() {
        var (exitCode, output) = Check(document: """
            See [the heading](target.md#missing-heading).
            """);

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "FAIL: docs/document.md:1: anchor 'target.md#missing-heading' does not resolve"
        );
    }
    [Fact]
    public void ADuplicateHeadingTakesANumberedSuffix() {
        var (resolvedExit, resolvedOutput) = Check(document: """
            See [the first](target.md#repeat) and [the second](target.md#repeat-1).
            """);

        Assert.True(
            condition: (resolvedExit == 0),
            userMessage: resolvedOutput
        );

        var (brokenExit, brokenOutput) = Check(document: """
            See [a third](target.md#repeat-2).
            """);

        Assert.Equal(
            actual: brokenExit,
            expected: 1
        );
        Assert.Contains(
            actualString: brokenOutput,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "anchor 'target.md#repeat-2' does not resolve"
        );
    }
    [Fact]
    public void ASameFileAnchorResolvesAgainstTheDocumentItself() {
        var (resolvedExit, resolvedOutput) = Check(document: """
            # Local section

            See [above](#local-section).
            """);

        Assert.True(
            condition: (resolvedExit == 0),
            userMessage: resolvedOutput
        );

        var (brokenExit, brokenOutput) = Check(document: """
            # Local section

            ```sh
            # not a heading
            ```

            See [the fence comment](#not-a-heading).
            """);

        Assert.Equal(
            actual: brokenExit,
            expected: 1
        );
        Assert.Contains(
            actualString: brokenOutput,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "FAIL: docs/document.md:7: anchor '#not-a-heading' does not resolve"
        );
    }
}
