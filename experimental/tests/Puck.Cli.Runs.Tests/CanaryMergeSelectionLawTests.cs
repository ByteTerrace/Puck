using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Proves that <c>puck canary --merge</c> selects exactly the automatic set (headless, no requirements) together with
/// every proof requiring <c>gpu</c>, each once and in authored order, and nothing else.
/// </summary>
public sealed class CanaryMergeSelectionLawTests {
    // The oracle, written from the two sets' own definitions rather than through the runner's filters.
    private static string[] Union(IReadOnlyList<CanaryManifest> manifests) => [.. manifests
        .Where(predicate: static manifest => (((manifest.BootShape == CanaryBootShape.Headless) && (manifest.Requirements.Count == 0)) || manifest.Requirements.Contains(value: "gpu")))
        .Select(selector: static manifest => manifest.Id)];

    [Fact]
    public void MergeOverTheShippedManifestsIsTheUnionOfTheAutomaticAndGpuSets() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(condition: CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: repositoryRoot,
            strict: true
        ), userMessage: error);

        var merge = CanaryCommand.SelectMerge(manifests: manifests).Select(selector: static manifest => manifest.Id).ToArray();

        Assert.Equal(
            expected: Union(manifests: manifests),
            actual: merge
        );
        Assert.Contains(
            collection: manifests,
            filter: static manifest => manifest.IsAutomatic
        );
        Assert.Contains(
            collection: manifests,
            filter: static manifest => manifest.Requirements.Contains(value: "gpu")
        );
    }
}
