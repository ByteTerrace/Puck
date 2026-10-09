using Puck.Cli.Automation;
using Xunit;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>
/// <c>puck artifacts test-windows --shard i --shards n</c> runs one of n shards of the archived manifest on each CI runner.
/// The shards together must run every assembly exactly once, whatever the manifest, the recorded durations or the shard
/// count: an assembly no shard runs is a suite CI silently stops running, and one two shards run is a race on its reports.
/// They must also stay balanced, or the slowest runner sets the job's time.
/// </summary>
public sealed class ArtifactsShardLawTests {
    private static IReadOnlyDictionary<string, double> Committed() => ArtifactsCommand.ReadDurations(path: Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: ArtifactsCommand.DurationsTable
    ));
    // Every assembly the committed table names, plus ones it has never seen, as a manifest's repository paths.
    private static string[] Manifest(IReadOnlyDictionary<string, double> seconds, int unrecorded) => [
        .. seconds.Keys.Select(selector: name => $"tests/{name}/bin/Release/net10.0/{name}.dll"),
        .. Enumerable.Range(count: unrecorded, start: 0).Select(selector: index => $"tests/Puck.Unrecorded{index}.Tests/bin/Release/net10.0/Puck.Unrecorded{index}.Tests.dll"),
    ];

    [Fact]
    public void EveryAssemblyRunsInExactlyOneShard() {
        var seconds = Committed();

        foreach (var unrecorded in ((int[])[0, 1, 7])) {
            var manifest = Manifest(seconds: seconds, unrecorded: unrecorded);

            for (var count = 1; (count <= (manifest.Length + 2)); count++) {
                var shards = ArtifactsCommand.Partition(assemblies: manifest, count: count, seconds: seconds);
                var runs = shards.SelectMany(selector: shard => shard).ToArray();

                Assert.Equal(expected: count, actual: shards.Count);
                Assert.Equal(expected: manifest.Order(comparer: StringComparer.Ordinal), actual: runs.Order(comparer: StringComparer.Ordinal));
            }
        }
    }
    [Fact]
    public void NoShardRunsLongerThanAnEvenSplitAndItsLongestAssembly() {
        var seconds = Committed();
        var manifest = Manifest(seconds: seconds, unrecorded: 3);
        var median = seconds.Values.Order().ElementAt(index: (seconds.Count / 2));

        double Weight(string assembly) => seconds.GetValueOrDefault(key: Path.GetFileNameWithoutExtension(path: assembly), defaultValue: median);
        var total = manifest.Sum(selector: Weight);
        var longest = manifest.Max(selector: Weight);

        for (var count = 1; (count <= 8); count++) {
            foreach (var shard in ArtifactsCommand.Partition(assemblies: manifest, count: count, seconds: seconds)) {
                Assert.True(
                    condition: (shard.Sum(selector: Weight) <= ((total / count) + longest)),
                    userMessage: $"A shard of {count} records {shard.Sum(selector: Weight)} s against an even split of {(total / count)} s."
                );
                Assert.Equal(expected: shard.OrderByDescending(keySelector: Weight), actual: shard);
            }
        }
    }
    [Fact]
    public void ThePartitionIsTheSameOnEveryRunner() {
        var seconds = Committed();
        var manifest = Manifest(seconds: seconds, unrecorded: 2);
        var reversed = manifest.Reverse().ToArray();

        Assert.Equal(
            expected: ArtifactsCommand.Partition(assemblies: manifest, count: 4, seconds: seconds),
            actual: ArtifactsCommand.Partition(assemblies: reversed, count: 4, seconds: seconds)
        );
    }
    [Fact]
    public void ThePartitionRefusesNoShardsAndARepeatedAssembly() {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ArtifactsCommand.Partition(assemblies: ["a.dll"], count: 0, seconds: new Dictionary<string, double>()));
        Assert.Throws<ArgumentException>(testCode: () => ArtifactsCommand.Partition(assemblies: ["tests/A/a.dll", "tests/A/a.dll"], count: 2, seconds: new Dictionary<string, double>()));
    }
}
