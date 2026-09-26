using Puck.Cli.Affected;
using Puck.Shaders;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for placing a post-process package's files: a package a canary's world names in <c>views.post</c> is placed
/// through that canary's documents, so its files choose the canaries whose worlds run it and not every canary that draws
/// a post pass; a package no document names falls back to its owner, the C# declaring <see cref="PostProcessPackage"/>.
/// </summary>
public sealed class AffectedPostPackageReachLawTests {
    private const string Owner = "src/Engine/Post.cs";

    private static readonly AffectedCanary GrainCanary = new(Directory: "tests/Canaries/grain", Files: ["tests/Canaries/grain/fixture.world.json"], Id: "grain", RequiresGpu: true);
    private static readonly AffectedCanary PostCanary = new(Directory: "tests/Canaries/post", Files: ["tests/Canaries/post/fixture.world.json"], Id: "post", RequiresGpu: true);
    private static readonly AffectedProject Engine = new(Directory: "src/Engine", IsSuite: false, Name: "Engine", References: []);
    // Two post-process packages, grain and haze, each drawing its fragment stage under Assets.
    private static readonly RenderGraphPackageCatalog Packages = new(packages: [Package(id: "grain"), Package(id: "haze")]);

    private static RenderGraphPackage Package(string id) => new(
        Id: id,
        Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.FragmentSampled)],
        Members: [ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: "source", type: ShaderValueType.Float4)],
        Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ColorAttachmentWrite)],
        Stages: new RenderGraphPackageStages(
            Directory: "Assets",
            Fragment: $"{id}.frag",
            Vertex: "fullscreen.vert"
        ),
        Summary: id
    );
    private static string World(string id, string post) => $$"""
        {
          "schema": "puck.world.definition.v1",
          "documentId": "{{id}}",
          "views": { "post": [{{post}}] }
        }
        """;

    // The grain canary's world runs grain, the post canary's world runs nothing, and both ran the packages' owner when
    // coverage was recorded, as every canary that draws a post pass does.
    private static readonly AffectedMemoryTree Tree = new(files: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["src/Engine/Engine.csproj"] = """<Project><ItemGroup><VertexShaderSource Include="Assets/**/*.vert.hlsl" /><FragmentShaderSource Include="Assets/**/*.frag.hlsl" /></ItemGroup></Project>""",
        ["src/Engine/Assets/fullscreen.vert.hlsl"] = "float4 main() : SV_Position { return 0; }",
        ["src/Engine/Assets/grain.frag.hlsl"] = "#include \"grain.interface.hlsli\"\nfloat4 main() : SV_Target { return 0; }",
        ["src/Engine/Assets/grain.interface.hlsli"] = "static const uint Grain = 1;",
        ["src/Engine/Assets/haze.frag.hlsl"] = "float4 main() : SV_Target { return 0; }",
        [Owner] = "public sealed class PostProcessPackage { }",
        ["tests/Canaries/grain/fixture.world.json"] = World(id: "grain", post: """{ "name": "grain", "package": "grain" }"""),
        ["tests/Canaries/post/fixture.world.json"] = World(id: "post", post: ""),
    });

    private static AffectedPlan Select(string changed) {
        AffectedCanary[] canaries = [GrainCanary, PostCanary];
        var coverage = new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal) {
            [Owner] = new HashSet<string>(collection: ["grain", "post"]),
        };
        var shaders = new AffectedShaders(packages: Packages, projects: [Engine], tree: Tree);
        var reachedBy = AffectedDocuments.ReachedBy(canaries: canaries, packageFiles: shaders.FilesOf, tree: Tree);

        return AffectedSelection.Select(
            canaries: canaries,
            canariesReaching: path => (reachedBy.TryGetValue(key: path, value: out var reaching)
                ? reaching
                : new HashSet<string>()),
            catalogInputs: static (_, _) => false,
            changed: [changed],
            consumersOf: static _ => [],
            coverage: coverage,
            declaresTests: static _ => false,
            projects: [Engine],
            standInsFor: AffectedStandIns.Create(
                documented: source => reachedBy.ContainsKey(key: source),
                indexed: [.. coverage.Keys],
                projects: [Engine],
                shaders: shaders,
                tree: Tree
            ),
            worldClosure: new HashSet<string>(collection: ["Engine"], comparer: StringComparer.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void ANamedPackagesFilesChooseTheCanariesWhoseWorldsRunIt() {
        foreach (var path in ((string[])["grain.frag.hlsl", "grain.interface.hlsli"])) {
            var plan = Select(changed: $"src/Engine/Assets/{path}");

            Assert.Equal(actual: plan.Canaries, expected: ["grain"]);
            Assert.Empty(collection: plan.Unmapped);
        }
    }
    [Fact]
    public void AnUnnamedPackageFallsBackToItsOwner() {
        foreach (var path in ((string[])["haze.frag.hlsl", "haze.interface.hlsli"])) {
            var plan = Select(changed: $"src/Engine/Assets/{path}");

            Assert.Equal(actual: plan.Canaries, expected: ["grain", "post"]);
            Assert.Empty(collection: plan.Unmapped);
        }
    }
}
