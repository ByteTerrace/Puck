using Puck.Cli.Affected;
using Puck.Cli.Architecture;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <see cref="AffectedDocuments"/>: a graph document reaches the pass sources it declares, resolved beside it as
/// the loader resolves them, and the includes they reach, and nothing it does not name; a canary reaches the graph
/// documents and shader sources its worlds and fixtures name, so on the real tree a graph pass source chooses the canary
/// that runs it, whether the documents are read from the disk or from a revision through git; and the Direct3D 11 camera
/// kernel, compiled through its own item, reaches the C# that loads it.
/// </summary>
public sealed class AffectedDocumentsLawTests {
    [Fact]
    public void AGraphDocumentReachesThePassSourcesItNamesAndTheirIncludes() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-affected-graph-");

        try {
            var passes = Directory.CreateDirectory(path: Path.Combine(path1: directory.FullName, path2: "passes"));
            var named = Path.Combine(path1: passes.FullName, path2: "fill.hlsl");
            var unnamed = Path.Combine(path1: passes.FullName, path2: "other.hlsl");
            var include = Path.Combine(path1: passes.FullName, path2: "common.hlsli");
            var unincluded = Path.Combine(path1: passes.FullName, path2: "unused.hlsli");
            var graph = Path.Combine(path1: directory.FullName, path2: "fill.graph.json");

            File.WriteAllText(contents: "#include \"common.hlsli\"\n[numthreads(8, 8, 1)] void main() {}", path: named);
            File.WriteAllText(contents: "static const uint Fill = 1;", path: include);
            File.WriteAllText(contents: "static const uint Unused = 1;", path: unincluded);
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

            var tree = new AffectedWorkingTree(root: directory.FullName);

            Assert.Equal(actual: AffectedDocuments.PassSources(graph: "fill.graph.json", tree: tree), expected: ["passes/fill.hlsl"]);
            Assert.Equal(
                actual: AffectedDocuments.Reach(path: "fill.graph.json", tree: tree).Order(comparer: StringComparer.Ordinal),
                expected: ["fill.graph.json", "passes/common.hlsli", "passes/fill.hlsl"]
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
    /// source reaches the canary whose world names the ink graph, the include the source-conversion passes share
    /// reaches that canary, and the camera conversion kernel reaches its loader.</summary>
    [Fact]
    public void OnTheTreeAGraphPassSourceReachesTheCanaryThatRunsIt() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(condition: AffectedCommand.TryCanaries(canaries: out var canaries, error: out var error, repositoryRoot: repositoryRoot), userMessage: error);

        var workingTree = new AffectedWorkingTree(root: repositoryRoot);
        var reachedBy = AffectedDocuments.ReachedBy(
            canaries: canaries,
            setFiles: new AffectedShaders(
                projects: AffectedCommand.Projects(model: ArchitectureModel.Load(repositoryRoot: repositoryRoot), repositoryRoot: repositoryRoot),
                tree: workingTree
            ).FilesOf,
            tree: workingTree
        );

        // The film-grain set is reached only through the world that lists it in render.extensions.
        Assert.Equal(actual: reachedBy["src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-film-grain.frag.hlsl"], expected: ["post-pass"]);

        Assert.Contains(collection: reachedBy["src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl"], expected: "resample-reconstruction");

        foreach (var ink in ((string[])["ink-finish", "ink-simulation", "ink-visualize"])) {
            Assert.Contains(collection: reachedBy[$"src/Puck.World/Assets/pipelines/{ink}.hlsl"], expected: "pipeline-ink");
        }

        Assert.Contains(collection: reachedBy["src/Puck.Shaders/Assets/Shaders/Sources/image-source.hlsli"], expected: "source-conversion");

        // The same reach read from a revision's tree through git rather than the disk: the committed documents of two
        // real canaries reach the same pass sources.
        var recorded = AffectedDocuments.ReachedBy(
            canaries: [.. canaries.Where(predicate: static canary => (canary.Id is "resample-reconstruction" or "pipeline-ink"))],
            tree: new AffectedRevisionTree(revision: "HEAD", root: repositoryRoot)
        );

        Assert.Contains(collection: recorded["src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl"], expected: "resample-reconstruction");
        Assert.Contains(collection: recorded["src/Puck.World/Assets/pipelines/ink-finish.hlsl"], expected: "pipeline-ink");

        var kernels = AffectedStandIns.Kernels(
            projects: AffectedCommand.Projects(model: ArchitectureModel.Load(repositoryRoot: repositoryRoot), repositoryRoot: repositoryRoot),
            tree: new AffectedWorkingTree(root: repositoryRoot)
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
