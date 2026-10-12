using Puck.Cli.Automation;
using Xunit;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>
/// <c>puck artifacts test-windows --shard i --shards n</c> runs one of n shards of the archived manifest on each CI runner.
/// The shards together must run every assembly exactly once, whatever the manifest or the shard count: an assembly no
/// shard runs is a suite CI silently stops running, and one two shards run is a race on its reports.
/// </summary>
public sealed class ArtifactsShardLawTests {
    private static string[] Manifest(int count) => [
        .. Enumerable.Range(count: count, start: 0).Select(selector: index => $"tests/Puck.Suite{index}.Tests/bin/Release/net10.0/Puck.Suite{index}.Tests.dll"),
    ];

    [Fact]
    public void EveryAssemblyRunsInExactlyOneShard() {
        foreach (var size in ((int[])[0, 1, 7, 40])) {
            var manifest = Manifest(count: size);

            for (var count = 1; (count <= (manifest.Length + 2)); count++) {
                var shards = ArtifactsCommand.Partition(assemblies: manifest, count: count);
                var runs = shards.SelectMany(selector: shard => shard).ToArray();

                Assert.Equal(expected: count, actual: shards.Count);
                Assert.Equal(expected: manifest.Order(comparer: StringComparer.Ordinal), actual: runs.Order(comparer: StringComparer.Ordinal));
                Assert.True(condition: ((shards.Max(selector: shard => shard.Count) - shards.Min(selector: shard => shard.Count)) <= 1));
            }
        }
    }
    [Fact]
    public void ThePartitionIsTheSameOnEveryRunner() {
        var manifest = Manifest(count: 23);
        var reversed = manifest.Reverse().ToArray();

        Assert.Equal(
            expected: ArtifactsCommand.Partition(assemblies: manifest, count: 4),
            actual: ArtifactsCommand.Partition(assemblies: reversed, count: 4)
        );
    }
    [Fact]
    public void ThePartitionRefusesNoShardsAndARepeatedAssembly() {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ArtifactsCommand.Partition(assemblies: ["a.dll"], count: 0));
        Assert.Throws<ArgumentException>(testCode: () => ArtifactsCommand.Partition(assemblies: ["tests/A/a.dll", "tests/A/a.dll"], count: 2));
    }
}
