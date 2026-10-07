using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Display paths are relative only within their base; glob paths retain their matching form.</summary>
public sealed class CliPathsLawTests {
    [Fact]
    public void ATemporaryLogOutsideTheWorkingDirectoryIsAbsolute() {
        var log = Path.GetFullPath(path: Path.Combine(path1: Path.GetTempPath(), path2: "l138-codex/build.log"));
        var expected = log.Replace(newChar: '/', oldChar: '\\');

        Assert.Equal(expected: expected, actual: CliPaths.ToDisplay(fullPath: log));
        Assert.Equal(expected: expected, actual: CliPaths.ToDisplay(relativeTo: Directory.GetCurrentDirectory(), fullPath: log));
        Assert.Equal(
            expected: Path.GetRelativePath(relativeTo: Directory.GetCurrentDirectory(), path: log).Replace(newChar: '/', oldChar: '\\'),
            actual: CliPaths.RelForGlob(fullPath: log)
        );

        if (OperatingSystem.IsWindows()) {
            var otherDrive = ((char.ToUpperInvariant(c: Path.GetPathRoot(path: log)![0]) == 'C') ? "D:/build.log" : "C:/build.log");

            Assert.Equal(expected: otherDrive, actual: CliPaths.ToDisplay(relativeTo: Path.GetTempPath(), fullPath: otherDrive));
        }
    }
    [Fact]
    public void PathsWithinTheWorkingDirectoryStayRelative() {
        var workingDirectory = Directory.GetCurrentDirectory();
        var child = Path.Combine(path1: workingDirectory, path2: "nested/file.txt");

        Assert.Equal(expected: "nested/file.txt", actual: CliPaths.ToDisplay(fullPath: child));
        Assert.Equal(expected: "nested/file.txt", actual: CliPaths.ToDisplay(fullPath: child, relativeTo: workingDirectory));
        Assert.Equal(expected: ".", actual: CliPaths.ToDisplay(fullPath: workingDirectory));
        Assert.Equal(expected: ".", actual: CliPaths.ToDisplay(fullPath: workingDirectory, relativeTo: (workingDirectory + Path.DirectorySeparatorChar)));
        Assert.Equal(expected: "file.txt", actual: CliPaths.ToDisplay(fullPath: "nested/child/../file.txt", relativeTo: "nested/./base/.."));
        Assert.Equal(expected: "..child/file.txt", actual: CliPaths.ToDisplay(relativeTo: workingDirectory, fullPath: Path.Combine(path1: workingDirectory, path2: "..child/file.txt")));

        var root = Path.GetPathRoot(path: workingDirectory)!;

        Assert.Equal(expected: ".", actual: CliPaths.ToDisplay(fullPath: root, relativeTo: root));
        Assert.Equal(expected: "file.txt", actual: CliPaths.ToDisplay(relativeTo: root, fullPath: Path.Combine(path1: root, path2: "file.txt")));
    }
    [Fact]
    public void ASiblingWhoseNameExtendsTheWorkingDirectoryIsOutside() {
        var workingDirectory = Path.TrimEndingDirectorySeparator(path: Directory.GetCurrentDirectory());
        var sibling = (workingDirectory + "-sibling/file.txt");
        var expected = Path.GetFullPath(path: sibling).Replace(newChar: '/', oldChar: '\\');

        Assert.Equal(expected: expected, actual: CliPaths.ToDisplay(fullPath: sibling));
        Assert.Equal(expected: expected, actual: CliPaths.ToDisplay(fullPath: sibling, relativeTo: workingDirectory));
        Assert.Equal(expected: expected, actual: CliPaths.ToDisplay(fullPath: sibling, relativeTo: (workingDirectory + "/nested/..")));

        var differentlyCased = Path.Combine(path1: workingDirectory, path2: "CASE/file.txt");

        Assert.Equal(
            expected: (OperatingSystem.IsWindows() ? "file.txt" : differentlyCased.Replace(newChar: '/', oldChar: '\\')),
            actual: CliPaths.ToDisplay(relativeTo: Path.Combine(path1: workingDirectory, path2: "case"), fullPath: differentlyCased)
        );
    }
}
