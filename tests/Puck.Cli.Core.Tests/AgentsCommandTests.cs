using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class AgentsCommandTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: ("doctrine agents ' " + Guid.NewGuid().ToString(format: "N")));

    [Fact]
    public async Task LinkCreationIsIdempotentAndNeverReplacesConflictingContent() {
        var source = Path.Combine(path1: m_root, path2: ".claude/skills");
        var destination = Path.Combine(path1: m_root, path2: ".agents/skills");
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(expected: 1, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: true, root: m_root));
        Directory.CreateDirectory(path: source);
        Assert.Equal(expected: 1, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: true, root: m_root));
        Assert.False(condition: Directory.Exists(path: Path.GetDirectoryName(path: destination)));
        Assert.Equal(expected: 0, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: false, root: m_root));
        var target = new DirectoryInfo(path: destination).LinkTarget;

        Assert.NotNull(@object: target);
        File.WriteAllText(path: Path.Combine(path1: source, path2: "marker"), contents: "kept");
        Assert.Equal(expected: 0, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: true, root: m_root));
        Assert.Equal(expected: 0, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: false, root: m_root));
        Assert.Equal(expected: target, actual: new DirectoryInfo(path: destination).LinkTarget);
        Directory.Delete(path: destination);
        Directory.CreateDirectory(path: destination);
        Assert.Equal(expected: 2, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: false, root: m_root));
        Directory.Delete(path: destination);
        File.WriteAllText(contents: "ordinary file", path: destination);
        Assert.Equal(expected: 2, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: false, root: m_root));
        Assert.Equal(expected: "ordinary file", actual: File.ReadAllText(path: destination));
        File.Delete(path: destination);

        // Move a legitimate link into another checkout: its absolute target remains in the first.
        Assert.Equal(expected: 0, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: false, root: m_root));
        var other = Path.Combine(path1: m_root, path2: "other");

        Directory.CreateDirectory(path: Path.Combine(path1: other, path2: ".claude/skills"));
        Directory.CreateDirectory(path: Path.Combine(path1: other, path2: ".agents"));
        var moved = Path.Combine(path1: other, path2: ".agents/skills");

        Directory.Move(destDirName: moved, sourceDirName: destination);
        Assert.Equal(expected: 1, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: true, root: other));
        Assert.Equal(expected: 2, actual: await AgentsCommand.LinkAsync(cancellationToken: token, check: false, root: other));
        Assert.Equal(expected: target, actual: new DirectoryInfo(path: moved).LinkTarget);
        Directory.Delete(path: moved);
        Assert.Equal(expected: "kept", actual: File.ReadAllText(path: Path.Combine(path1: source, path2: "marker")));
    }
    public void Dispose() {
        if (Directory.Exists(path: m_root)) { Directory.Delete(path: m_root, recursive: true); }
    }
}
