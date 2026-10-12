using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>CONTRACT UNDER TEST: staging a composition source for a federated canary leg refuses a staging directory
/// that is the source's own directory or an ancestor of it, and leaves the source in place.</summary>
public sealed class CanaryFederatedCompositionLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void StagingRefusesToDeleteTheSourceOrItsAncestor(bool ancestor) {
        using var files = new TemporaryDirectory(prefix: "puck-composition-source-");
        var directory = Path.Combine(path1: files.RootPath, path2: "source");

        _ = Directory.CreateDirectory(path: directory);
        var source = Path.Combine(path1: directory, path2: "world.puck");

        File.WriteAllText(contents: "source remains present", path: source);

        Assert.False(condition: WorldStaging.TryStageComposition(
            catalog: CliWorldVocabulary.EnsureInstalled(),
            directory: (ancestor ? files.RootPath : directory),
            entry: null,
            entryName: out _,
            entryPath: out _,
            path: source,
            reason: out var reason,
            worlds: [new WorldCompiledWorld(Entry: true, Json: "{}"u8.ToArray(), Name: "entry")]
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "contain the composition source");
        Assert.Equal(actual: File.ReadAllText(path: source), expected: "source remains present");
    }
}
