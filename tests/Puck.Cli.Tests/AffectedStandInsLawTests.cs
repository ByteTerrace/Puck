using Puck.Cli.Affected;
using Puck.Cli.Architecture;
using Puck.Cli.Schema;
using Puck.Shaders;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <see cref="AffectedStandIns"/>: a project's own build inputs stand for its indexed sources; a shader source
/// or include stands for the indexed C# of the kernel's own project that names, by an exact string literal, a kernel
/// whose include closure reaches it; a file <c>puck schema</c> writes stands for the indexed sources declaring the type
/// it is generated from; and on the real tree every non-C# source checkpoint 5's record could not place now has a
/// stand-in the index knows.
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
        new(Closure: ["src/Engine/Assets/sdf-beam.comp.hlsl", "src/Engine/Assets/sdf-common.hlsli"], Path: "src/Engine/Assets/sdf-beam.comp.hlsl", Project: "src/Engine"),
        new(Closure: ["src/Engine/Assets/sdf-views.comp.hlsl", "src/Engine/Assets/sdf-common.hlsli", "src/Engine/Assets/sdf-visibility.hlsli"], Path: "src/Engine/Assets/sdf-views.comp.hlsl", Project: "src/Engine"),
    ];

    private static string Read(string path) => Texts[path];

    [Fact]
    public void AProjectsBuildInputsStandForItsIndexedSources() {
        Assert.True(condition: AffectedStandIns.IsProjectInput(path: "src/Engine/Engine.csproj"));
        Assert.True(condition: AffectedStandIns.IsProjectInput(path: "src/Engine/packages.lock.json"));
        Assert.True(condition: AffectedStandIns.IsProjectInput(path: "src/Engine/NativeMethods.txt"));
        Assert.False(condition: AffectedStandIns.IsProjectInput(path: "src/Engine/Model.cs"));
        Assert.Equal(
            actual: AffectedStandIns.ProjectSources(directory: "src/Engine", indexed: Texts.Keys),
            expected: ["src/Engine/Loader.cs", "src/Engine/Model.cs", "src/Engine/Pipelines.cs", "src/Engine/Views.cs"]
        );
    }
    [Fact]
    public void AKernelIsNamedByItsFileAndItsFileWithoutItsStage() {
        Assert.Equal(actual: AffectedStandIns.KernelNames(kernelPath: "src/Engine/Assets/sdf-beam.comp.hlsl"), expected: ["sdf-beam.comp", "sdf-beam"]);
        Assert.Equal(actual: AffectedStandIns.KernelNames(kernelPath: "src/World/Assets/ink.hlsl"), expected: ["ink"]);
    }
    /// <summary>An include reaches every kernel whose closure holds it, and each kernel reaches only the sources of its
    /// own project that name it by an exact literal: a prefix of another kernel's name and a name in prose are not a
    /// loader, and neither is a source in another project.</summary>
    [Fact]
    public void AShaderStandsForTheLoadersOfEveryKernelItsClosureReaches() {
        Assert.Equal(
            actual: AffectedStandIns.ShaderLoaders(indexed: Texts.Keys, kernels: Kernels, path: "src/Engine/Assets/sdf-common.hlsli", read: Read),
            expected: ["src/Engine/Loader.cs", "src/Engine/Pipelines.cs"]
        );
        Assert.Equal(
            actual: AffectedStandIns.ShaderLoaders(indexed: Texts.Keys, kernels: Kernels, path: "src/Engine/Assets/sdf-visibility.hlsli", read: Read),
            expected: ["src/Engine/Loader.cs"]
        );
        Assert.Empty(collection: AffectedStandIns.ShaderLoaders(indexed: Texts.Keys, kernels: Kernels, path: "src/Engine/Assets/unused.hlsli", read: Read));
    }
    [Fact]
    public void AGeneratedFileStandsForTheSourcesDeclaringItsGeneratorsTypes() {
        Assert.Equal(
            actual: AffectedStandIns.Declaring(indexed: Texts.Keys, read: Read, types: [typeof(FrameDocument)]),
            expected: ["src/Engine/Model.cs"]
        );
        Assert.Equal(actual: SchemaCommand.SourceTypesOf(relativePath: "src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json"), expected: [typeof(RenderGraphDefinition)]);
        Assert.Equal(actual: SchemaCommand.SourceTypesOf(relativePath: "src/Puck.World/Assets/worlds/puck.world.projection.v1.schema.json"), expected: [typeof(WorldProjectionDocument)]);
        Assert.Equal(actual: SchemaCommand.SourceTypesOf(relativePath: "src/Puck.World/Assets/worlds/schema/prototypes.schema.json"), expected: [typeof(WorldPrototype)]);
        Assert.Empty(collection: SchemaCommand.SourceTypesOf(relativePath: "src/Puck.World/Assets/worlds/puck.world.json"));
    }
    /// <summary>On the real tree, each non-C# source kind checkpoint 5's coverage record left unplaced reaches indexed
    /// stand-ins: the loader an include reaches, the declaration a schema is generated from, and a project's
    /// sources.</summary>
    [Fact]
    public void EveryUnplacedSourceKindOnTheTreeHasAnIndexedStandIn() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var indexed = AffectedCoverage.Read(repositoryRoot: repositoryRoot).Keys.ToArray();
        var standInsFor = AffectedStandIns.Create(
            indexed: indexed,
            projects: AffectedCommand.Projects(
                model: ArchitectureModel.Load(repositoryRoot: repositoryRoot),
                repositoryRoot: repositoryRoot
            ),
            repositoryRoot: repositoryRoot
        );

        Assert.Contains(collection: standInsFor(arg: "src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-visibility.hlsli"), expected: "src/Puck.SdfVm/SdfWorldEngine.Pipelines.cs");
        Assert.Contains(collection: standInsFor(arg: "src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json"), expected: "src/Puck.Shaders/Graph/RenderGraphModel.cs");
        Assert.Contains(collection: standInsFor(arg: "src/Puck.World/Assets/worlds/puck.world.projection.v1.schema.json"), expected: "src/Puck.World.Schema/WorldProjection.cs");

        foreach (var path in ((string[])[
            "src/Puck.DirectX/NativeMethods.txt",
            "src/Puck.Overlays/Puck.Overlays.csproj",
            "src/Puck.Overlays/packages.lock.json",
            "src/Puck.World.Client/packages.lock.json",
            "src/Puck.World/packages.lock.json",
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
