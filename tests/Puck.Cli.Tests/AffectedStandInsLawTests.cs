using Puck.Cli.Affected;
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

    [Fact]
    public void AProjectsBuildInputsStandForItsIndexedSources() {
        Assert.True(condition: AffectedStandIns.IsProjectInput(path: "src/Engine/Engine.csproj"));
        Assert.False(condition: AffectedStandIns.IsProjectInput(path: "src/Engine/packages.lock.json"));
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
        Assert.Equal(actual: SchemaCommand.SourceTypesOf(relativePath: "src/Puck.World.Schema/Schemas/puck.world.projection.v1.schema.json"), expected: [typeof(WorldProjectionDocument)]);
        Assert.Equal(actual: SchemaCommand.SourceTypesOf(relativePath: "src/Puck.World.Schema/Schemas/schema/prototypes.schema.json"), expected: [typeof(WorldPrototype)]);
        Assert.Empty(collection: SchemaCommand.SourceTypesOf(relativePath: "src/Puck.World/Assets/worlds/puck.world.json"));
    }
    /// <summary>A kernel is named by a constant in a project its project's build references: the conversion pass's
    /// loader. A project the kernel's project does not reference names nothing, even with the same literal.</summary>
    [Fact]
    public void AKernelStandsForTheConstantThatNamesItInAReferencedProject() {
        var tree = new AffectedMemoryTree(files: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Contracts/Contracts.csproj"] = "<Project />",
            ["src/Contracts/Passes.cs"] = "public const string Rgba = \"source-rgba\";",
            ["src/Engine/Engine.csproj"] = """<Project><ItemGroup><ComputeShaderSource Include="Assets/**/*.comp.hlsl" /></ItemGroup></Project>""",
            ["src/Engine/Assets/source-rgba.comp.hlsl"] = "[numthreads(8, 8, 1)] void main() {}",
            ["src/Other/Other.csproj"] = "<Project />",
            ["src/Other/Echo.cs"] = "Load(\"source-rgba\");",
        });
        AffectedProject[] projects = [
            new(Directory: "src/Contracts", IsSuite: false, Name: "Contracts", References: []),
            new(Directory: "src/Engine", IsSuite: false, Name: "Engine", References: ["Contracts"]),
            new(Directory: "src/Other", IsSuite: false, Name: "Other", References: []),
        ];

        Assert.Equal(actual: Assert.Single(collection: AffectedStandIns.Kernels(projects: projects, tree: tree)).Projects, expected: ["src/Contracts", "src/Engine"]);
        Assert.Equal(
            actual: AffectedStandIns.Create(indexed: ["src/Contracts/Passes.cs", "src/Other/Echo.cs"], projects: projects, schemaSourceTypes: PuckRootCommand.Gate.SchemaSourceTypes, tree: tree)(arg: "src/Engine/Assets/source-rgba.comp.hlsl"),
            expected: ["src/Contracts/Passes.cs"]
        );
    }
    /// <summary>A post-process package's stage sources with their includes, and the frame interface generated for it,
    /// stand for the package's owner, the C# declaring <see cref="PostProcessPackage"/>; a stage source no package names
    /// does not.</summary>
    [Fact]
    public void APostPackagesSourcesAndInterfaceStandForItsOwner() {
        var tree = new AffectedMemoryTree(files: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Engine/Engine.csproj"] = """<Project><ItemGroup><VertexShaderSource Include="Assets/**/*.vert.hlsl" /><FragmentShaderSource Include="Assets/**/*.frag.hlsl" /></ItemGroup></Project>""",
            ["src/Engine/Assets/fullscreen.vert.hlsl"] = "float4 main() : SV_Position { return 0; }",
            ["src/Engine/Assets/grain.frag.hlsl"] = "#include \"grain.common.hlsli\"\nfloat4 main() : SV_Target { return 0; }",
            ["src/Engine/Assets/grain.common.hlsli"] = "static const uint Grain = 1;",
            ["src/Engine/Assets/other.frag.hlsl"] = "float4 main() : SV_Target { return 0; }",
            ["src/Engine/Post.cs"] = "public sealed class PostProcessPackage { }",
        });
        AffectedProject[] projects = [new(Directory: "src/Engine", IsSuite: false, Name: "Engine", References: [])];
        var packages = new RenderGraphPackageCatalog(packages: [new RenderGraphPackage(
            Id: "grain",
            Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.FragmentSampled)],
            Members: [ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: "source", type: ShaderValueType.Float4)],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ColorAttachmentWrite)],
            Stages: new RenderGraphPackageStages(
                Directory: "Assets",
                Fragment: "grain.frag",
                Vertex: "fullscreen.vert"
            ),
            Summary: "grain"
        )]);
        var standInsFor = AffectedStandIns.Create(
            indexed: ["src/Engine/Post.cs"],
            projects: projects,
            schemaSourceTypes: PuckRootCommand.Gate.SchemaSourceTypes,
            shaders: new AffectedShaders(packages: packages, projects: projects, tree: tree),
            tree: tree
        );

        foreach (var path in ((string[])["grain.frag.hlsl", "grain.common.hlsli", "fullscreen.vert.hlsl", "grain.interface.hlsli"])) {
            Assert.Equal(actual: standInsFor(arg: $"src/Engine/Assets/{path}"), expected: ["src/Engine/Post.cs"]);
        }

        Assert.Empty(collection: standInsFor(arg: "src/Engine/Assets/other.frag.hlsl"));
    }

    // A type the declaration law names.
    private sealed record FrameDocument(int Width);
}
