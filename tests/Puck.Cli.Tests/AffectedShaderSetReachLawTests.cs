using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for placing a shader set's files: a set a canary's world names in <c>render.extensions</c> is placed through that
/// canary's documents, so its files choose the canaries whose worlds list it and not every canary that loads a manifest;
/// a set no document names falls back to the manifest's owner, the C# declaring the model a manifest is read into.
/// </summary>
public sealed class AffectedShaderSetReachLawTests {
    private const string Owner = "src/Engine/Sets.cs";

    private static readonly AffectedCanary GrainCanary = new(Directory: "tests/Canaries/grain", Files: ["tests/Canaries/grain/fixture.world.json"], Id: "grain", RequiresGpu: true);
    private static readonly AffectedCanary PostCanary = new(Directory: "tests/Canaries/post", Files: ["tests/Canaries/post/fixture.world.json"], Id: "post", RequiresGpu: true);
    private static readonly AffectedProject Engine = new(Directory: "src/Engine", IsSuite: false, Name: "Engine", References: []);

    private static string Manifest(string name) => $$"""
        {
          "$schema": "puck.shader.manifest.v1",
          "name": "{{name}}",
          "stages": { "vertex": "fullscreen.vert", "fragment": "{{name}}.frag" },
          "bindings": [ { "kind": "SampledImage", "name": "source" } ]
        }
        """;
    private static string World(string id, string extensions) => $$"""
        {
          "schema": "puck.world.definition.v1",
          "documentId": "{{id}}",
          "render": { "extensions": [{{extensions}}] }
        }
        """;

    // Two shader sets, grain and haze; the grain canary's world lists grain, the post canary's world lists nothing, and
    // both ran the manifest's owner when coverage was recorded, as every canary that loads a set does.
    private static readonly AffectedMemoryTree Tree = new(files: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["src/Engine/Engine.csproj"] = """<Project><ItemGroup><VertexShaderSource Include="Assets/**/*.vert.hlsl" /><FragmentShaderSource Include="Assets/**/*.frag.hlsl" /></ItemGroup></Project>""",
        ["src/Engine/Assets/fullscreen.vert.hlsl"] = "float4 main() : SV_Position { return 0; }",
        ["src/Engine/Assets/grain.puck.shader.json"] = Manifest(name: "grain"),
        ["src/Engine/Assets/grain.frag.hlsl"] = "#include \"grain.interface.hlsli\"\nfloat4 main() : SV_Target { return 0; }",
        ["src/Engine/Assets/grain.interface.hlsli"] = "static const uint Grain = 1;",
        ["src/Engine/Assets/haze.puck.shader.json"] = Manifest(name: "haze"),
        ["src/Engine/Assets/haze.frag.hlsl"] = "float4 main() : SV_Target { return 0; }",
        [Owner] = "public sealed record ShaderSetManifest(string Name);",
        ["tests/Canaries/grain/fixture.world.json"] = World(extensions: """{ "id": "grain" }""", id: "grain"),
        ["tests/Canaries/post/fixture.world.json"] = World(extensions: "", id: "post"),
    });

    private static AffectedPlan Select(string changed) {
        AffectedCanary[] canaries = [GrainCanary, PostCanary];
        var coverage = new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal) {
            [Owner] = new HashSet<string>(collection: ["grain", "post"]),
        };
        var shaders = new AffectedShaders(projects: [Engine], tree: Tree);
        var reachedBy = AffectedDocuments.ReachedBy(canaries: canaries, setFiles: shaders.FilesOf, tree: Tree);

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
                documented: manifest => reachedBy.ContainsKey(key: manifest),
                indexed: [.. coverage.Keys],
                projects: [Engine],
                shaders: shaders,
                tree: Tree
            ),
            worldClosure: new HashSet<string>(collection: ["Engine"], comparer: StringComparer.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void ANamedSetsFilesChooseTheCanariesWhoseWorldsListIt() {
        foreach (var path in ((string[])["grain.puck.shader.json", "grain.frag.hlsl", "grain.interface.hlsli"])) {
            var plan = Select(changed: $"src/Engine/Assets/{path}");

            Assert.Equal(actual: plan.Canaries, expected: ["grain"]);
            Assert.Empty(collection: plan.Unmapped);
        }
    }
    [Fact]
    public void AnUnnamedSetFallsBackToTheManifestsOwner() {
        foreach (var path in ((string[])["haze.puck.shader.json", "haze.frag.hlsl"])) {
            var plan = Select(changed: $"src/Engine/Assets/{path}");

            Assert.Equal(actual: plan.Canaries, expected: ["grain", "post"]);
            Assert.Empty(collection: plan.Unmapped);
        }
    }
}
