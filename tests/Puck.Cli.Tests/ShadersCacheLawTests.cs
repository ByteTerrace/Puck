using Puck.Cli.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for the shader cache verbs CI relies on: <c>puck shaders cache prune</c> removes the entries unused for its bound,
/// keeps the rest and reports both, and refuses a bound under one minute; <c>puck shaders compare --build --cache</c>
/// compiles only into an absent or empty directory, so the cache CI saves after a passing comparison holds this host's
/// own compiles and nothing restored.
/// </summary>
public sealed class ShadersCacheLawTests {
    [Fact]
    public void PruneRemovesTheEntriesUnusedForItsBoundAndReportsWhatItKept() {
        using var root = new TemporaryDirectory(prefix: "puck-shaders-cache-");
        var now = new DateTime(day: 1, hour: 12, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2026);
        var stale = Path.Combine(path1: root.RootPath, path2: "stale.spv");
        var used = Path.Combine(path1: root.RootPath, path2: "used.dxil");

        File.WriteAllBytes(bytes: [1, 2, 3], path: stale);
        File.WriteAllBytes(bytes: [4, 5], path: used);
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: now.AddMinutes(value: -91), path: stale);
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: now.AddMinutes(value: -89), path: used);

        var (exitCode, output, error) = ConsoleCapture.RunSplit(run: () => CacheCommand.Run(
            cacheDirectory: root.RootPath,
            nowUtc: now,
            unusedMinutes: 90
        ));

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.Contains(actualString: output, expectedSubstring: "removed 1 file(s)");
        Assert.Contains(actualString: output, expectedSubstring: "kept 1");
        Assert.Empty(collection: error.Trim());
        Assert.False(condition: File.Exists(path: stale), userMessage: "An entry unused for longer than the bound survived the prune.");
        Assert.True(condition: File.Exists(path: used), userMessage: "An entry used within the bound was pruned.");
    }
    [Fact]
    public void PruneRefusesABoundUnderOneMinuteAndRemovesNothing() {
        using var root = new TemporaryDirectory(prefix: "puck-shaders-cache-");
        var entry = Path.Combine(path1: root.RootPath, path2: "entry.spv");

        File.WriteAllBytes(bytes: [1], path: entry);
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: DateTime.UtcNow.AddDays(value: -1), path: entry);

        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => CacheCommand.Run(
            cacheDirectory: root.RootPath,
            nowUtc: DateTime.UtcNow,
            unusedMinutes: 0
        ));

        Assert.Equal(actual: exitCode, expected: 2);
        Assert.Contains(actualString: error, expectedSubstring: "at least 1 minute");
        Assert.True(condition: File.Exists(path: entry), userMessage: "A refused prune removed an entry.");
    }
    [Fact]
    public void CompareBuildsOnlyIntoAnAbsentOrEmptyCache() {
        using var root = new TemporaryDirectory(prefix: "puck-shaders-cache-");
        var empty = Directory.CreateDirectory(path: Path.Combine(path1: root.RootPath, path2: "empty")).FullName;
        var filled = Directory.CreateDirectory(path: Path.Combine(path1: root.RootPath, path2: "filled")).FullName;
        var file = Path.Combine(path1: root.RootPath, path2: "file");

        File.WriteAllBytes(bytes: [1], path: Path.Combine(path1: filled, path2: "restored.spv"));
        File.WriteAllBytes(bytes: [1], path: file);

        Assert.Null(@object: CompareCommand.RefuseCache(cache: Path.Combine(path1: root.RootPath, path2: "absent")));
        Assert.Null(@object: CompareCommand.RefuseCache(cache: empty));
        Assert.Contains(actualString: CompareCommand.RefuseCache(cache: filled), expectedSubstring: "not empty");
        Assert.Contains(actualString: CompareCommand.RefuseCache(cache: file), expectedSubstring: "is a file");
    }
}
