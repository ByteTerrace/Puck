using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves that <c>puck canary --merge</c> selects exactly the automatic set (headless, no requirements) together with
/// every proof requiring <c>gpu</c>, each once and in authored order, and nothing else.
/// </summary>
public sealed class CanaryMergeSelectionLawTests {
    private static CanaryManifest Manifest(string id, CanaryBootShape shape, params string[] requirements) {
        var leg = new CanaryLeg(
            Assertions: [],
            Authorities: [],
            AuthorityWorldPath: null,
            Commands: [],
            Connect: false,
            Name: "positive",
            ScriptPath: "positive.script.txt",
            WorldPath: "world.json"
        );

        return new CanaryManifest(
            Backends: [],
            Binding: id,
            BootShape: shape,
            DirectoryPath: id,
            Discriminating: (leg with { Name = "discriminating" }),
            Fixtures: [],
            Id: id,
            Positive: leg,
            Requirements: requirements,
            TimeoutSeconds: 10,
            Title: id
        );
    }
    // The oracle, written from the two sets' own definitions rather than through the runner's filters.
    private static string[] Union(IReadOnlyList<CanaryManifest> manifests) => [.. manifests
        .Where(predicate: static manifest => (((manifest.BootShape == CanaryBootShape.Headless) && (manifest.Requirements.Count == 0)) || manifest.Requirements.Contains(value: "gpu")))
        .Select(selector: static manifest => manifest.Id)];

    [Fact]
    public void MergeSelectsTheAutomaticSetAndEveryGpuProofAndNothingElse() {
        CanaryManifest[] manifests = [
            Manifest(id: "headless-free", shape: CanaryBootShape.Headless),
            Manifest(id: "offscreen-gpu", shape: CanaryBootShape.Offscreen, "gpu"),
            Manifest(id: "windowed-audio", shape: CanaryBootShape.Windowed, "audio-output"),
            Manifest(id: "windowed-gpu", shape: CanaryBootShape.Windowed, "gpu"),
            Manifest(id: "headless-pad", shape: CanaryBootShape.Headless, "input:dualsense"),
            Manifest(id: "stub-free", shape: CanaryBootShape.Stub),
            Manifest(id: "windowed-free", shape: CanaryBootShape.Windowed),
            Manifest(id: "offscreen-gpu-audio", shape: CanaryBootShape.Offscreen, "gpu", "audio-output"),
        ];

        Assert.Equal(
            expected: ["headless-free", "offscreen-gpu", "windowed-gpu", "offscreen-gpu-audio"],
            actual: CanaryCommand.SelectMerge(manifests: manifests).Select(selector: static manifest => manifest.Id)
        );
    }
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
