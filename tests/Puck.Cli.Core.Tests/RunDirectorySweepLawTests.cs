using Puck.Confinement;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class RunDirectorySweepLawTests : IDisposable {
    // Past the depth at which a recursive delete overflows the stack.
    private const int Depth = 5000;
    private const string Kind = "sweeplaw";
    private const int SmallStackBytes = (256 * 1024);

    private readonly TemporaryDirectory m_root = new(prefix: "puck-sweeproot-");

    // A finished run's directory: this process's id with a creation time that makes the process a later reuse of the id.
    private string StaleDirectory(string token) {
        var path = m_root.PathOf(name: $"puck-{Kind}-{Environment.ProcessId}-{token}");

        _ = Directory.CreateDirectory(path: path);
        Directory.SetCreationTimeUtc(
            creationTimeUtc: (DateTime.UtcNow - TimeSpan.FromDays(value: 2)),
            path: path
        );

        return path;
    }

    [Fact]
    public void AStaleDirectoryOfAnyDepthIsSweptWithoutOverflowingTheStack() {
        var stale = StaleDirectory(token: "deep");

        using (var anchor = ConfinedDirectory.OpenAnchor(create: false, path: stale)) {
            var current = anchor!;

            for (var level = 0; (level < Depth); level++) {
                var next = current.Child(create: true, name: "d")!;

                if (level > 0) {
                    current.Dispose();
                }

                current = next;
            }

            using (current.OpenFile(create: true, name: "leaf.txt")) { }
            current.Dispose();
        }

        var report = new StringWriter();
        var removed = -1;

        // A recursive removal of this depth needs far more than the stack this thread has; an iterative one needs none.
        var sweeper = new Thread(
            maxStackSize: SmallStackBytes,
            start: () => removed = RunDirectory.Sweep(age: TimeSpan.FromHours(value: 1), prefix: Kind, report: report, root: m_root.RootPath)
        );

        sweeper.Start();
        sweeper.Join();
        Assert.Equal(expected: 1, actual: removed);
        Assert.False(condition: Directory.Exists(path: stale));
        Assert.Equal(expected: "", actual: report.ToString());
    }
    [Fact]
    public void AStaleDirectoryHoldingALinkOutOfTheRootIsLeftWithTheTargetUntouchedAndNamed() {
        var outside = m_root.PathOf(name: "outside");
        var stale = StaleDirectory(token: "linked");

        _ = Directory.CreateDirectory(path: outside);
        File.WriteAllText(contents: "kept", path: Path.Combine(path1: outside, path2: "evidence.txt"));
        File.WriteAllText(contents: "gone", path: Path.Combine(path1: stale, path2: "loose.txt"));
        DirectoryLinks.Create(link: Path.Combine(path1: stale, path2: "escape"), target: outside);

        var report = new StringWriter();

        Assert.Equal(expected: 0, actual: RunDirectory.Sweep(age: TimeSpan.FromHours(value: 1), prefix: Kind, report: report, root: m_root.RootPath));
        Assert.True(condition: Directory.Exists(path: stale));
        Assert.Equal(expected: "kept", actual: File.ReadAllText(path: Path.Combine(path1: outside, path2: "evidence.txt")));
        Assert.Contains(expectedSubstring: "run directory not removed", actualString: report.ToString());
        Assert.Contains(expectedSubstring: stale, actualString: report.ToString());

        // The sweep names what it refused; it does not stop the next directory being swept.
        var next = StaleDirectory(token: "plain");

        File.WriteAllText(contents: "gone", path: Path.Combine(path1: next, path2: "loose.txt"));
        Assert.Equal(expected: 1, actual: RunDirectory.Sweep(age: TimeSpan.FromHours(value: 1), prefix: Kind, report: new StringWriter(), root: m_root.RootPath));
        Assert.False(condition: Directory.Exists(path: next));
        DirectoryLinks.Remove(link: Path.Combine(path1: stale, path2: "escape"));
    }

    public void Dispose() => m_root.Dispose();
}
