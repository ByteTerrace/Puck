using Puck.Cli.Transpiler;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>What <c>puck fmt</c> does with a source it cannot print: it says which file and why, leaves the file
/// alone, and exits non-zero — in directory mode as well as on one file.</summary>
public sealed class PuckFmtCommandTests {
    private const string Broken = "host {\n  when\n";
    private const string Unformatted = "host {\n    authority: \"a\"\n}\n";

    private static string Directory() {
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-fmt-law-{Guid.NewGuid():N}"
        );

        System.IO.Directory.CreateDirectory(path: path);

        return path;
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ASourceThatDoesNotParseIsRefusedAndLeftAlone(bool check) {
        var directory = Directory();
        var file = Path.Combine(
            path1: directory,
            path2: "broken.puck"
        );

        try {
            File.WriteAllText(
                contents: Broken,
                path: file
            );

            Assert.Equal(
                actual: PuckFmtCommand.Execute(
                    check: check,
                    path: file
                ),
                expected: 2
            );
            Assert.Equal(
                actual: File.ReadAllText(path: file),
                expected: Broken
            );
        } finally {
            System.IO.Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // A sweep that formatted one file and could not read another has not swept the tree, whatever mode it ran in.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ADirectoryWithAFailedFileExitsNonZero(bool check) {
        var directory = Directory();

        try {
            File.WriteAllText(
                contents: Broken,
                path: Path.Combine(
                    path1: directory,
                    path2: "broken.puck"
                )
            );
            File.WriteAllText(
                contents: Unformatted,
                path: Path.Combine(
                    path1: directory,
                    path2: "ragged.puck"
                )
            );

            Assert.Equal(
                actual: PuckFmtCommand.Execute(
                    check: check,
                    path: directory
                ),
                expected: 2
            );
        } finally {
            System.IO.Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void ADirectoryOfFormattableSourcesSucceeds() {
        var directory = Directory();
        var file = Path.Combine(
            path1: directory,
            path2: "ragged.puck"
        );

        try {
            File.WriteAllText(
                contents: Unformatted,
                path: file
            );

            Assert.Equal(
                actual: PuckFmtCommand.Execute(
                    check: true,
                    path: directory
                ),
                expected: 1
            );
            Assert.Equal(
                actual: PuckFmtCommand.Execute(
                    check: false,
                    path: directory
                ),
                expected: 0
            );
            Assert.Equal(
                actual: File.ReadAllText(path: file),
                expected: "host {\n  authority: \"a\"\n}\n"
            );
        } finally {
            System.IO.Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
