using Puck.Testing;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>What <c>puck format</c> does with <c>.puck</c> sources: it prints each one back in the printer's one layout,
/// and a source it cannot print is named, left alone, and fails the run with exit 2 — for one file and for a tree,
/// with <c>--check</c> or without.</summary>
public sealed class FormatPuckSourceTests {
    private const string Broken = "host {\n  when\n";
    private const string Unformatted = "host {\n    authority: \"a\"\n}\n";

    private static int Format(string path, bool check) => ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: (check
        ? ["format", path, "--check"]
        : ["format", path]))).ExitCode;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ASourceThatDoesNotParseIsRefusedAndLeftAlone(bool check) {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(
            path1: directory.RootPath,
            path2: "broken.puck"
        );

        File.WriteAllText(
            contents: Broken,
            path: file
        );

        Assert.Equal(
            actual: Format(
                check: check,
                path: file
            ),
            expected: 2
        );
        Assert.Equal(
            actual: File.ReadAllText(path: file),
            expected: Broken
        );
    }
    // A sweep that formatted one file and could not print another has not swept the tree, whatever mode it ran in.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ATreeWithAnUnparseableSourceExitsTwo(bool check) {
        using var directory = new TemporaryDirectory();

        File.WriteAllText(
            contents: Broken,
            path: Path.Combine(
                path1: directory.RootPath,
                path2: "broken.puck"
            )
        );
        File.WriteAllText(
            contents: Unformatted,
            path: Path.Combine(
                path1: directory.RootPath,
                path2: "ragged.puck"
            )
        );

        Assert.Equal(
            actual: Format(
                check: check,
                path: directory.RootPath
            ),
            expected: 2
        );
    }
    [Fact]
    public void ATreeIsCheckedThenFormattedToTwoSpaceIndentation() {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(
            path1: directory.RootPath,
            path2: "ragged.puck"
        );

        File.WriteAllText(
            contents: Unformatted,
            path: file
        );

        Assert.Equal(
            actual: Format(
                check: true,
                path: directory.RootPath
            ),
            expected: 1
        );
        Assert.Equal(
            actual: File.ReadAllText(path: file),
            expected: Unformatted
        );
        Assert.Equal(
            actual: Format(
                check: false,
                path: directory.RootPath
            ),
            expected: 0
        );
        Assert.Equal(
            actual: File.ReadAllText(path: file),
            expected: "host {\n  authority: \"a\"\n}\n"
        );
        Assert.Equal(
            actual: Format(
                check: true,
                path: directory.RootPath
            ),
            expected: 0
        );
    }
    // A --file-list and its entries resolve against the working directory, and naming a root beside it is a usage
    // error rather than a second, silently ignored selection.
    [Fact]
    public void AFileListAndARootDoNotCombine() {
        using var directory = new TemporaryDirectory();
        var list = Path.Combine(
            path1: directory.RootPath,
            path2: "files.json"
        );

        File.WriteAllText(
            contents: "[]",
            path: list
        );

        Assert.Equal(
            actual: ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: ["format", directory.RootPath, "--file-list", list])).ExitCode,
            expected: 2
        );
    }
}
