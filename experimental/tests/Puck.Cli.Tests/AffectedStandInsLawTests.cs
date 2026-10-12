using Puck.Cli.Affected;
using Puck.Cli.Architecture;
using Puck.Cli.Schema;
using Puck.Shaders;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <see cref="AffectedStandIns"/>: a project's own build inputs stand for its indexed sources; a shader source
/// or include stands for the indexed C#, in the kernel's project or a project its build references, that names by an
/// exact string literal a kernel whose include closure reaches it; a post-process package's stage sources and its
/// frame interface stand for the C# declaring <see cref="PostProcessPackage"/>; a file <c>puck schema</c> writes stands for the
/// indexed sources declaring the type it is generated from; and on the real tree every non-C# source checkpoint 5's
/// record could not place now has a stand-in the index knows.
/// </summary>
public sealed class AffectedStandInsLawTests {
    private static readonly Dictionary<string, string> Texts = new(comparer: StringComparer.Ordinal) {
        ["src/Engine/Pipelines.cs"] = "Spec(name: \"sdf-beam\"); Spec(name: \"sdf-views-core\");",
        ["src/Engine/Views.cs"] = "/// <c>sdf-views.comp</c> renders the views.",
        ["src/Engine/Loader.cs"] = "Load(\"sdf-views.comp\");",
        ["src/Other/Blit.cs"] = "Load(\"sdf-beam\");",
        ["src/Engine/Model.cs"] = "public sealed record FrameDocument(int Width); internal readonly record struct FramePart(int X);",
    };
    private static readonly AffectedKernel[] Kernels = [
        new(Closure: ["src/Engine/Assets/sdf-beam.comp.hlsl", "src/Engine/Assets/sdf-common.hlsli"], Path: "src/Engine/Assets/sdf-beam.comp.hlsl", Projects: ["src/Engine"]),
        new(Closure: ["src/Engine/Assets/sdf-views.comp.hlsl", "src/Engine/Assets/sdf-common.hlsli", "src/Engine/Assets/sdf-visibility.hlsli"], Path: "src/Engine/Assets/sdf-views.comp.hlsl", Projects: ["src/Engine"]),
    ];

    private static string Read(string path) => Texts[path];

    /// <summary>On the real tree, each non-C# source kind checkpoint 5's coverage record left unplaced reaches indexed
    /// stand-ins: the loader an include reaches, the declaration a schema is generated from, and a project's
    /// sources.</summary>
    [Fact]
    public void EveryUnplacedSourceKindOnTheTreeHasAnIndexedStandIn() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var indexed = AffectedCoverage.Read(repositoryRoot: repositoryRoot).Keys.ToArray();
        var standInsFor = AffectedStandIns.Create(
            indexed: indexed,
            schemaSourceTypes: PuckRootCommand.Gate.SchemaSourceTypes,
            projects: AffectedCommand.Projects(
                model: ArchitectureModel.Load(repositoryRoot: repositoryRoot),
                repositoryRoot: repositoryRoot
            ),
            tree: new AffectedWorkingTree(root: repositoryRoot)
        );

        Assert.Contains(collection: standInsFor(arg: "src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-visibility.hlsli"), expected: "src/Puck.SdfVm/SdfKernelSet.cs");
        Assert.Contains(collection: standInsFor(arg: "src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json"), expected: "src/Puck.Shaders/Graph/RenderGraphModel.cs");
        Assert.Contains(collection: standInsFor(arg: "src/Puck.World/Assets/worlds/puck.world.projection.v1.schema.json"), expected: "src/Puck.World.Schema/WorldProjection.cs");

        // A post-process package's stage source and interface stand for the package's owner, and a conversion kernel for
        // the constant that names it in a project the kernel's project references.
        foreach (var path in ((string[])["sdf-film-grain.frag.hlsl", "sdf-film-grain.interface.hlsli"])) {
            Assert.Contains(collection: standInsFor(arg: $"src/Puck.SdfVm/Assets/Shaders/Sdf/passes/{path}"), expected: "src/Puck.Shaders/Graph/PostProcessPackage.cs");
        }
        foreach (var path in ((string[])["source-rgba.comp.hlsl", "source-transfer.comp.hlsl"])) {
            Assert.Contains(collection: standInsFor(arg: $"src/Puck.Shaders/Assets/Shaders/Sources/{path}"), expected: "src/Puck.Abstractions/Sources/ImageSourceConversion.cs");
        }

        foreach (var path in ((string[])[
            "src/Puck.DirectX/NativeMethods.txt",
            "src/Puck.Overlays/Puck.Overlays.csproj",
            "src/Puck.World/Assets/worlds/schema/prototypes.schema.json",
        ])) {
            var standIns = standInsFor(arg: path);

            Assert.NotEmpty(collection: standIns);
            Assert.All(collection: standIns, action: standIn => Assert.Contains(collection: indexed, expected: standIn));
        }
    }

    // A type the declaration law names.
    private sealed record FrameDocument(int Width);
}
