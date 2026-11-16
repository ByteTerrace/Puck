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
    public void LinkedAncestorInspectsBelowTheRootNeverTheRootItself() {
        using var directory = new TemporaryDirectory();
        var real = Directory.CreateDirectory(path: directory.PathOf(name: "real")).FullName;
        var linkedRoot = directory.PathOf(name: "linked-root");

        _ = Directory.CreateDirectory(path: Path.Combine(path1: real, path2: "inner/deeper"));
        DirectoryLinks.Create(link: linkedRoot, target: real);

        try {
            // The checkout root reached through a link is still the root the caller chose.
            Assert.Null(@object: CliTreeFiles.LinkedAncestor(path: Path.Combine(path1: linkedRoot, path2: "inner/deeper"), root: linkedRoot));
            Assert.Null(@object: CliTreeFiles.LinkedAncestor(path: linkedRoot, root: linkedRoot));
            Assert.Equal(expected: Path.GetFullPath(path: linkedRoot), actual: CliTreeFiles.LinkedAncestor(path: Path.Combine(path1: linkedRoot, path2: "inner")));

            var below = Path.GetFullPath(path: directory.PathOf(name: "real/inner/alias"));

            DirectoryLinks.Create(link: below, target: Path.Combine(path1: real, path2: "inner/deeper"));
            try {
                Assert.Equal(expected: below, actual: CliTreeFiles.LinkedAncestor(path: Path.Combine(path1: below, path2: "x"), root: real));
            } finally { DirectoryLinks.Remove(link: below); }
        } finally { DirectoryLinks.Remove(link: linkedRoot); }
    }
    [Fact]
    public void ConfinedContainmentFollowsFileIdentityNotSpelling() {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) { return; }
        using var directory = new TemporaryDirectory();
        var real = Directory.CreateDirectory(path: directory.PathOf(name: "real")).FullName;
        var alias = directory.PathOf(name: "alias");
        var source = directory.WriteText(name: "real/source.puck", text: "x");

        DirectoryLinks.Create(link: alias, target: real);
        try {
            Assert.False(condition: PuckPaths.IsWithin(path: source, root: alias));
            Assert.True(condition: Puck.Confinement.ConfinedDirectory.Contains(root: alias, path: source));
            Assert.True(condition: Puck.Confinement.ConfinedDirectory.Contains(root: real, path: Path.Combine(path1: real, path2: "not-yet/child")));
            Assert.False(condition: Puck.Confinement.ConfinedDirectory.Contains(root: Path.Combine(path1: real, path2: "absent"), path: source));
            Assert.False(condition: Puck.Confinement.ConfinedDirectory.Contains(root: Directory.CreateDirectory(path: directory.PathOf(name: "sibling")).FullName, path: source));
        } finally { DirectoryLinks.Remove(link: alias); }
    }
    // A destination directory that is a link out of the destination refuses the copy at the link: the file outside is
    // neither deleted nor replaced, whether the whole tree or a selection is copied.
    [Fact]
    public void ATreeCopyNeverDeletesOrWritesThroughADestinationLink() {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) { return; }
        using var directory = new TemporaryDirectory();
        var source = directory.PathOf(name: "source");
        var destination = Directory.CreateDirectory(path: directory.PathOf(name: "destination")).FullName;
        var outside = directory.WriteText(name: "outside/victim.txt", text: "outside");
        var linked = Path.Combine(path1: destination, path2: "sub");

        _ = directory.WriteText(name: "source/sub/victim.txt", text: "source");
        DirectoryLinks.Create(link: linked, target: Path.GetDirectoryName(path: outside)!);
        try {
            _ = Assert.ThrowsAny<IOException>(testCode: () => CliFiles.CopyDirectory(destination: destination, source: source));
            Assert.Equal(expected: "outside", actual: File.ReadAllText(path: outside));
            _ = Assert.ThrowsAny<IOException>(testCode: () => CliFiles.CopyDirectory(destination: destination, files: [Path.Combine(path1: source, path2: "sub", path3: "victim.txt")], source: source));
            Assert.Equal(expected: "outside", actual: File.ReadAllText(path: outside));
        } finally { DirectoryLinks.Remove(link: linked); }
    }
    // A directory link in the source tree is never followed: the copy refuses it rather than copying what lies outside.
    [Fact]
    public void ATreeCopyNeverFollowsASourceLink() {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) { return; }
        using var directory = new TemporaryDirectory();
        var source = Directory.CreateDirectory(path: directory.PathOf(name: "source")).FullName;
        var destination = directory.PathOf(name: "destination");
        var outside = directory.WriteText(name: "outside/secret.txt", text: "secret");
        var linked = Path.Combine(path1: source, path2: "linked");

        DirectoryLinks.Create(link: linked, target: Path.GetDirectoryName(path: outside)!);
        try {
            _ = Assert.ThrowsAny<IOException>(testCode: () => CliFiles.CopyDirectory(destination: destination, source: source));
            Assert.False(condition: File.Exists(path: Path.Combine(path1: destination, path2: "linked", path3: "secret.txt")));
            Assert.Equal(expected: "secret", actual: File.ReadAllText(path: outside));
        } finally { DirectoryLinks.Remove(link: linked); }
    }
    // An ordinary copy replaces what the destination holds and keeps each file's last write time; a clean copy refuses
    // a destination that holds anything, leaving it as it was.
    [Fact]
    public void ACleanCopyRefusesAnOccupiedDestinationAndAnOrdinaryCopyReplaces() {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) { return; }
        using var directory = new TemporaryDirectory();
        var source = directory.PathOf(name: "package");
        var written = directory.WriteText(name: "package/lib/app.dll", text: "new package");
        var stamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        _ = Directory.CreateDirectory(path: directory.PathOf(name: "package/empty"));
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: stamp, path: written);
        var stale = directory.WriteText(name: "install/lib/app.dll", text: "old package");
        var extra = directory.WriteText(name: "install/old.dll", text: "old");
        var install = directory.PathOf(name: "install");

        _ = Assert.Throws<IOException>(testCode: () => CliFiles.CopyDirectory(destination: install, intoEmpty: true, source: source));
        Assert.Equal(expected: "old package", actual: File.ReadAllText(path: stale));
        Assert.True(condition: File.Exists(path: extra));

        Assert.Equal(expected: 1, actual: CliFiles.CopyDirectory(destination: install, source: source));
        Assert.Equal(expected: "new package", actual: File.ReadAllText(path: stale));
        Assert.Equal(expected: stamp, actual: File.GetLastWriteTimeUtc(path: stale));
        Assert.True(condition: Directory.Exists(path: directory.PathOf(name: "install/empty")));

        var clean = directory.PathOf(name: "clean");

        Assert.Equal(expected: 1, actual: CliFiles.CopyDirectory(destination: clean, intoEmpty: true, source: source));
        Assert.Equal(expected: "new package", actual: File.ReadAllText(path: Path.Combine(path1: clean, path2: "lib", path3: "app.dll")));
    }
    [Fact]
    public void OwningProjectDirectoryKeepsTheNearestDirectoryWhenItHoldsSeveralProjects() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "outer.csproj", text: "<Project />");
        directory.WriteText(name: "child/a.csproj", text: "<Project />");
        directory.WriteText(name: "child/b.csproj", text: "<Project />");
        var nested = Directory.CreateDirectory(path: directory.PathOf(name: "child/nested")).FullName;

        Assert.Equal(expected: Path.GetFullPath(path: directory.PathOf(name: "child")), actual: CliProjects.FindOwningProjectDirectory(start: nested));
        Assert.Null(@object: CliProjects.FindOwningProject(start: nested));
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
