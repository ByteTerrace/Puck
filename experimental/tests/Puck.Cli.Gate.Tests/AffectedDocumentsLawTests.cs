using Puck.Cli.Affected;
using Puck.Cli.Architecture;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

/// <summary>
/// Laws for <see cref="AffectedDocuments"/>: a graph document reaches the pass sources it declares, resolved beside it as
/// the loader resolves them, and the includes they reach, and nothing it does not name; a canary reaches the graph
/// documents and shader sources its worlds and fixtures name, so on the real tree a graph pass source chooses the canary
/// that runs it, whether the documents are read from the disk or from a revision through git; and the Direct3D 11 camera
/// kernel, compiled through its own item, reaches the C# that loads it.
/// </summary>
public sealed class AffectedDocumentsLawTests {
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
            packageFiles: new AffectedShaders(
                projects: AffectedCommand.Projects(model: ArchitectureModel.Load(repositoryRoot: repositoryRoot), repositoryRoot: repositoryRoot),
                tree: workingTree
            ).FilesOf,
            tree: workingTree
        );

        // The film-grain package is reached only through the worlds that run it in views.post.
        Assert.Equal(actual: reachedBy["src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-film-grain.frag.hlsl"], expected: ["kernel-counters", "post-pass"]);

        Assert.Contains(collection: reachedBy["src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl"], expected: "resample-reconstruction");

        foreach (var ink in ((string[])["ink-finish", "ink-simulation", "ink-visualize"])) {
            Assert.Contains(collection: reachedBy[$"src/Puck.World/Assets/pipelines/{ink}.hlsl"], expected: "pipeline-ink");
        }

        Assert.Contains(collection: reachedBy["src/Puck.Shaders/Assets/Shaders/Sources/image-source.hlsli"], expected: "source-conversion");

        // The same reach read from a revision's tree through git rather than the disk: the committed documents of two
        // real canaries reach the same pass sources, a world authored as .puck included (the revision exports once and composes it).
        using var head = new AffectedRevisionTree(documentTrees: AffectedRevisionExport.DocumentTrees, revision: "HEAD", root: repositoryRoot);
        var recorded = AffectedDocuments.ReachedBy(
            canaries: [.. canaries.Where(predicate: static canary => (canary.Id is "resample-reconstruction" or "pipeline-ink"))],
            tree: head
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

    /// <summary>A module authored only as a <c>.puck</c> source (no <c>.world.json</c> beside it) is a layer the importing
    /// world composes from, so a world authored as a source reaches it, in the working tree and in a revision.</summary>
    private static void WriteModuleTree(string root) {
        var modules = Directory.CreateDirectory(path: Path.Combine(path1: root, path2: "mods"));

        File.Copy(
            destFileName: Path.Combine(path1: modules.FullName, path2: "ttt.puck"),
            sourceFileName: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/games/tictactoe.puck")
        );
        File.WriteAllText(
            contents: "schema: \"puck.world.definition.v1\"\n\nimport \"mods/ttt\" as a\n",
            path: Path.Combine(path1: root, path2: "host.puck")
        );
    }

    [Fact]
    public void AModuleAuthoredOnlyAsASourceIsReachedThroughTheImportThatNamesIt() {
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-module-");

        WriteModuleTree(root: scratch.RootPath);

        var reached = AffectedDocuments.Reach(path: "host.puck", tree: new AffectedWorkingTree(root: scratch.RootPath));

        Assert.Contains(collection: reached, expected: "host.puck");
        Assert.Contains(collection: reached, expected: "mods/ttt.puck");
    }
    [Fact]
    public void AModuleAuthoredOnlyAsASourceIsReachedFromTheRevisionThatRecordedIt() {
        using var checkout = new GitScratchCheckout();

        WriteModuleTree(root: checkout.Root);
        _ = checkout.Commit(message: "the tree");

        // The working tree no longer holds the files: only the revision does, and it composes them from an export.
        File.Delete(path: Path.Combine(path1: checkout.Root, path2: "mods", path3: "ttt.puck"));

        using var revision = new AffectedRevisionTree(documentTrees: ["host.puck", "mods"], revision: "HEAD", root: checkout.Root);
        var reached = AffectedDocuments.Reach(path: "host.puck", tree: revision);

        Assert.Contains(collection: reached, expected: "mods/ttt.puck");
    }
}
