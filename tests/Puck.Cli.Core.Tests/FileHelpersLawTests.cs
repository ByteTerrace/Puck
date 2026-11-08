using Puck.Abstractions;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class FileHelpersLawTests {
    [InlineData("", new string[0])]
    [InlineData("a\r\n\r\nb\n", new[] { "a", "", "b" })]
    [InlineData("\n", new[] { "" })]
    [InlineData("a\rb", new[] { "a", "b" })]
    [Theory]
    public void MaterializedAndSpanLinesAgree(string text, string[] expected) {
        Assert.Equal(expected: expected, actual: CliText.AllLines(text: text));
        var actual = new List<string>();

        foreach (var line in CliText.EnumerateLines(text: text.AsSpan())) { actual.Add(item: line.ToString()); }
        Assert.Equal(actual: actual, expected: expected);
        Assert.Equal(expected: expected.Where(predicate: static line => (line.Length > 0)), actual: CliText.Lines(text: text));
        Assert.Equal(expected: new[] { "" }, actual: CliText.AllLines(emptyIsLine: true, text: ""));
    }
    [Fact]
    public void WorktreeRecordsKeepFlagsAndUnterminatedFinalRecord() {
        var root = Path.GetFullPath(path: "worktree with spaces");
        var listing = $"worktree {root}\0HEAD abc\0branch refs/heads/lane\0locked\0prunable reason\0\0worktree {root}-detached\0HEAD def";
        var records = CliWorktree.Parse(listing: listing);

        Assert.Equal(expected: 2, actual: records.Count);
        Assert.Equal(expected: string.Empty, actual: records[0].Locked);
        Assert.Equal(expected: "reason", actual: records[0].Prunable);
        Assert.Equal(expected: "lane", actual: records[0].Branch);
        Assert.Null(@object: records[1].Branch);
        Assert.Null(@object: records[1].Locked);
        Assert.Equal(expected: "def", actual: records[1].Head);
        Assert.Equal(expected: PuckPaths.Normalize(path: (root + "-detached")), actual: records[1].Path);
    }
    [Fact]
    public void ContainmentUsesBoundariesAndHostCaseRules() {
        var root = Path.GetFullPath(path: "tree");

        Assert.True(condition: PuckPaths.IsWithin(path: root, root: (root + Path.DirectorySeparatorChar)));
        Assert.True(condition: PuckPaths.IsWithin(root: root, path: Path.Combine(path1: root, path2: "..hidden/file")));
        Assert.False(condition: PuckPaths.IsWithin(path: (root + "-other/file"), root: root));
        Assert.False(condition: PuckPaths.IsWithin(root: root, path: Path.Combine(path1: root, path2: "../outside")));
        Assert.True(condition: PuckPaths.IsWithin(root: Path.GetPathRoot(path: root)!, path: root));
        if (OperatingSystem.IsWindows()) { Assert.True(condition: PuckPaths.IsWithin(root: root.ToUpperInvariant(), path: root.ToLowerInvariant())); }
    }
    [Fact]
    public void OwningProjectStopsAtBoundaryAndAtAmbiguousNearestDirectory() {
        using var directory = new TemporaryDirectory();
        var project = directory.WriteText(name: "parent.csproj", text: "<Project />");
        var child = Directory.CreateDirectory(path: directory.PathOf(name: "child/nested")).FullName;

        Assert.Equal(expected: project, actual: CliProjects.FindOwningProject(start: child, root: directory.RootPath));
        Assert.Null(@object: CliProjects.FindOwningProject(start: child, root: directory.PathOf(name: "child")));
        directory.WriteText(name: "child/a.csproj", text: "<Project />");
        directory.WriteText(name: "child/b.csproj", text: "<Project />");
        Assert.Null(@object: CliProjects.FindOwningProject(start: child));
    }
}
