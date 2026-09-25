using Puck.Cli.Affected;
using Puck.Cli.Architecture;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <see cref="AffectedDocuments"/>: a graph document reaches the pass sources it declares, resolved beside it as
/// the loader resolves them, and nothing it does not name; a canary reaches the graph documents and shader sources its
/// worlds and fixtures name, so on the real tree a graph pass source chooses the canary that runs it; and the Direct3D 11
/// camera kernel, compiled through its own item, reaches the C# that loads it.
/// </summary>
public sealed class AffectedDocumentsLawTests {
    [Fact]
    public void AGraphDocumentReachesThePassSourcesItNames() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-affected-graph-");

        try {
            var passes = Directory.CreateDirectory(path: Path.Combine(path1: directory.FullName, path2: "passes"));
            var named = Path.Combine(path1: passes.FullName, path2: "fill.hlsl");
            var unnamed = Path.Combine(path1: passes.FullName, path2: "other.hlsl");
            var graph = Path.Combine(path1: directory.FullName, path2: "fill.graph.json");

            File.WriteAllText(contents: "[numthreads(8, 8, 1)] void main() {}", path: named);
            File.WriteAllText(contents: "[numthreads(8, 8, 1)] void main() {}", path: unnamed);
            File.WriteAllText(
                contents: """
                    {
                      "$schema": "puck.render.graph.v1",
                      "name": "fill",
                      "resources": [ { "name": "out", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "relative", "width": 1, "height": 1 } } ],
                      "passes": [ { "name": "fill", "kind": "compute", "source": "passes/fill.hlsl", "entryPoint": "main", "outputs": [ { "name": "out" } ] } ],
                      "outputs": [ "out" ]
                    }
                    """,
                path: graph
            );

            Assert.Equal(actual: AffectedDocuments.PassSources(graphPath: graph), expected: [Path.GetFullPath(path: named)]);
            Assert.Equal(
                actual: AffectedDocuments.Reach(path: graph).Order(comparer: StringComparer.OrdinalIgnoreCase),
                expected: [Path.GetFullPath(path: graph), Path.GetFullPath(path: named)]
            );
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void AFileACanarysDocumentsReachChoosesItAndIsMapped() {
        var plan = AffectedSelection.Select(
            canaries: [new AffectedCanary(Directory: "tests/Canaries/ink", Files: ["tests/Canaries/ink/fixture.world.json"], Id: "ink", RequiresGpu: true)],
            canariesReaching: static path => ((path == "src/World/Assets/ink.hlsl") ? new HashSet<string>(collection: ["ink"]) : new HashSet<string>()),
            catalogInputs: static (_, _) => false,
            changed: ["src/World/Assets/ink.hlsl"],
            consumersOf: static _ => [],
            coverage: new Dictionary<string, IReadOnlySet<string>>(),
            declaresTests: static _ => false,
            projects: [new AffectedProject(Directory: "src/World", IsSuite: false, Name: "World", References: [])],
            standInsFor: static _ => [],
            worldClosure: new HashSet<string>(collection: ["World"], comparer: StringComparer.OrdinalIgnoreCase)
        );

        Assert.Equal(actual: plan.Canaries, expected: ["ink"]);
        Assert.Empty(collection: plan.Unmapped);
    }
    /// <summary>On the real tree: the resample kernel reaches the canary whose fixture graph names it, each ink pass
    /// source reaches the canary whose world names the ink graph, and the camera conversion kernel reaches its
    /// loader.</summary>
    [Fact]
    public void OnTheTreeAGraphPassSourceReachesTheCanaryThatRunsIt() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(condition: AffectedCommand.TryCanaries(canaries: out var canaries, error: out var error, repositoryRoot: repositoryRoot), userMessage: error);

        var reachedBy = AffectedDocuments.ReachedBy(canaries: canaries, repositoryRoot: repositoryRoot);

        Assert.Contains(collection: reachedBy["src/Puck.Shaders/Assets/Shaders/Graph/resample.hlsl"], expected: "resample-reconstruction");

        foreach (var ink in ((string[])["ink-finish", "ink-simulation", "ink-visualize"])) {
            Assert.Contains(collection: reachedBy[$"src/Puck.World/Assets/pipelines/{ink}.hlsl"], expected: "pipeline-ink");
        }

        var kernels = AffectedStandIns.Kernels(
            projects: AffectedCommand.Projects(model: ArchitectureModel.Load(repositoryRoot: repositoryRoot), repositoryRoot: repositoryRoot),
            repositoryRoot: repositoryRoot
        );

        Assert.Contains(collection: kernels, filter: static kernel => (kernel.Path == "src/Puck.Platform.Windows/Assets/Shaders/camera-conversion.hlsl"));
        Assert.Equal(
            actual: AffectedStandIns.ShaderLoaders(
                indexed: ["src/Puck.Platform.Windows/Win32D3D11CameraFrameConverter.cs"],
                kernels: kernels,
                path: "src/Puck.Platform.Windows/Assets/Shaders/camera-conversion.hlsl",
                read: path => File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: path))
            ),
            expected: ["src/Puck.Platform.Windows/Win32D3D11CameraFrameConverter.cs"]
        );
    }
}
