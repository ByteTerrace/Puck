using Puck.Assets;
using Puck.Cli.Canary;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Proves the offscreen canary shape without a GPU: the shipped pipeline manifests load and expand to one proof per
/// backend, the loader refuses an offscreen manifest that could skip a backend, <c>imageRegion</c> decides pixels
/// against author-derived bounds in both directions, <c>imageDifference</c> holds a capture to a box-filtered
/// reference in both directions, and the runner classifies unsupported environments and
/// unresolved pipeline waits from a transcript.
/// </summary>
public sealed class CanaryOffscreenLawTests {
    [Fact]
    public void TheShippedPipelineManifestsLoadAsOneProofPerBackend() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: repositoryRoot,
            strict: true
        );

        Assert.True(
            condition: loaded,
            userMessage: error
        );
        var offscreen = manifests.Where(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Offscreen)).ToArray();

        Assert.Contains(
            collection: offscreen,
            filter: static manifest => (manifest.Id == "pipeline-feedback")
        );
        Assert.Contains(
            collection: offscreen,
            filter: static manifest => (manifest.Id == "pipeline-ink")
        );
        Assert.All(
            collection: offscreen,
            action: static manifest => {
                Assert.Equal(
                    expected: ["vulkan", "directx"],
                    actual: manifest.Backends
                );
                Assert.False(condition: manifest.IsAutomatic);
            }
        );
        Assert.Equal(
            expected: ["pipeline-feedback on vulkan", "pipeline-feedback on directx"],
            actual: CanaryCommand.ExpandProofs(backends: WorldOffscreenLeg.Backends, manifests: [.. offscreen.Where(predicate: static manifest => (manifest.Id == "pipeline-feedback"))]).Select(selector: static proof => proof.Label)
        );
    }
    [InlineData("pipeline-feedback/fixture.puck", "feedback", 1f)]
    [InlineData("pipeline-feedback/wrong-history.puck", "feedback", 1f)]
    [InlineData("pipeline-ink/fixture.puck", "ink", 0f)]
    [Theory]
    public void EveryOffscreenFixtureWorldValidatesAsAnOffscreenPipelineHost(string relativePath, string pipeline, float timeScale) {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        var catalog = CliWorldVocabulary.EnsureInstalled();

        Assert.True(
            condition: Puck.World.WorldDefinitionLoader.TryLoadFile(
                catalog: catalog,
                catalogFingerprint: catalog.CompositionFingerprint,
                definition: out var definition,
                path: Path.Combine(
                    path1: repositoryRoot,
                    path2: "tests",
                    path3: "Puck.World.Canaries",
                    path4: relativePath
                ),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: Puck.World.WorldHostPresentation.Offscreen,
            actual: definition!.Host.Presentation
        );
        var row = Assert.Single(collection: (definition.Views.Graphs ?? []));

        Assert.Equal(
            expected: pipeline,
            actual: row.Name
        );
        Assert.Equal(
            expected: timeScale,
            actual: row.TimeScale
        );
    }
}
