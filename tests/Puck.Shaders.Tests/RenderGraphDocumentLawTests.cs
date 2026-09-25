using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for the <c>puck.render.graph.v1</c> document: a graph of shader and package passes validates and plans through
/// the one pipeline planner, each package refusal is named, a planner refusal passes through with its own code, a view
/// reading its own output goes through the planner's history resource, a package pass, which only a graph's packages
/// member declares, plans as its own kind over its compute shape, and every checked-in graph document plans alike through a
/// pipeline host, which offers no package, and through the engine's catalog.
/// </summary>
public sealed class RenderGraphDocumentLawTests {
    private const string Graph = """
        {
          "$schema": "puck.render.graph.v1",
          "name": "security-monitor",
          "resources": [
            { "name": "screen", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 }, "initialization": "External" },
            { "name": "scene", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } },
            { "name": "graded", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } },
            { "name": "final", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } }
          ],
          "passes": [
            { "name": "grade", "source": "grade.hlsl", "entryPoint": "main", "kind": "Compute", "inputs": [{ "name": "scene" }, { "name": "screen" }], "outputs": [{ "name": "graded" }] }
          ],
          "packages": [
            { "name": "hud", "package": "overlay", "inputs": [{ "name": "graded" }], "outputs": [{ "name": "final" }] },
            { "name": "world", "package": "sdf.world", "outputs": [{ "name": "scene" }] }
          ],
          "outputs": ["final"]
        }
        """;

    private static RenderGraphPlan Plan(string json) => new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: RenderGraphDefinition.Parse(json: json));
    private static IReadOnlyList<string> Codes(string json) => [.. Assert.Throws<ShaderPipelineCompilationException>(testCode: () => Plan(json: json)).Diagnostics.Select(selector: static diagnostic => diagnostic.Code)];
    private static string Edit(Action<JsonObject> change) {
        var document = JsonNode.Parse(json: Graph)!.AsObject();

        change(obj: document);

        return document.ToJsonString();
    }
    private static JsonObject Ref(string name) => new() { ["name"] = name };
    private static JsonArray Refs(params string[] names) => [.. names.Select(selector: static name => ((JsonNode)Ref(name: name)))];
    private static JsonObject Package(JsonObject document, string name) => document["packages"]!.AsArray().Select(selector: static node => node!.AsObject()).Single(predicate: node => (((string?)node["name"]) == name));

    [Fact]
    public void AGraphOfShaderAndPackagePassesPlansThroughThePipelinePlanner() {
        var plan = Plan(json: Graph);

        Assert.Equal(
            expected: ["world", "grade", "hud"],
            actual: plan.Steps.Select(selector: static step => step.Name)
        );
        Assert.Equal(expected: RenderGraphPackageCatalog.SdfWorld, actual: plan.Steps[0].Package?.Id);
        Assert.Null(@object: plan.Steps[1].Package);
        Assert.Equal(expected: RenderGraphPackageCatalog.Overlay, actual: plan.Steps[2].Package?.Id);
        Assert.Equal(expected: ["screen"], actual: plan.Inputs);
        Assert.Equal(expected: ["final"], actual: plan.Outputs);
        Assert.Equal(
            expected: ShaderPipelinePassKind.Package,
            actual: plan.Pipeline.Passes[2].Kind
        );
        Assert.Equal(expected: RenderGraphPackageCatalog.Overlay, actual: plan.Pipeline.Passes[2].Declaration.Source);
        Assert.Equal(expected: ShaderPipelineDocumentPassKind.Compute, actual: plan.Pipeline.Passes[2].Declaration.Kind);
        Assert.Equal(
            expected: ShaderPipelinePassKind.Compute,
            actual: plan.Pipeline.Passes[1].Kind
        );
        Assert.Equal(expected: "grade", actual: plan.Pipeline.Passes[1].Declaration.Name);
        Assert.Equal(
            expected: ["grade"],
            actual: plan.Pipeline.Definition.ShaderPasses.Select(selector: static pass => pass.Name)
        );
        Assert.Equal(expected: [0], actual: plan.Pipeline.Passes[1].Dependencies);
        Assert.Equal(expected: [1], actual: plan.Pipeline.Passes[2].Dependencies);
        Assert.All(
            action: static step => Assert.NotEmpty(collection: step.Planned.Accesses),
            collection: plan.Steps
        );
    }
    [Fact]
    public void EveryPackageRefusalIsNamed() {
        Assert.Equal(
            expected: ["RENDERGRAPH_PACKAGE_UNKNOWN"],
            actual: Codes(json: Edit(change: static document => Package(document: document, name: "world")["package"] = "sdf.moon"))
        );
        Assert.Equal(
            expected: ["RENDERGRAPH_PACKAGE_PORTS"],
            actual: Codes(json: Edit(change: static document => Package(document: document, name: "hud")["inputs"] = Refs("graded", "scene")))
        );
        Assert.Equal(
            expected: ["RENDERGRAPH_PACKAGE_BINDING"],
            actual: Codes(json: Edit(change: static document => Package(document: document, name: "hud")["inputs"] = new JsonArray(new JsonObject { ["binding"] = 3, ["name"] = "graded" })))
        );
        Assert.Equal(
            expected: ["RENDERGRAPH_SCHEMA"],
            actual: Codes(json: Edit(change: static document => document["$schema"] = "puck.render.graph.v2"))
        );
        Assert.Equal(
            expected: ["RENDERGRAPH_PACKAGE_OUTPUT"],
            actual: Codes(json: Edit(change: static document => {
                document["resources"]!.AsArray().Add(item: new JsonObject { ["name"] = "counts", ["kind"] = "Buffer", ["sizeBytes"] = 16 });
                Package(document: document, name: "world")["outputs"] = Refs("counts");
            }))
        );
    }
    [Fact]
    public void APlannerRefusalPassesThroughWithItsOwnCode() {
        Assert.Contains(
            collection: Codes(json: Edit(change: static document => Package(document: document, name: "hud")["outputs"] = Refs("graded"))),
            expected: "SHADERPIPE_SINGLE_WRITER"
        );
        Assert.Contains(
            collection: Codes(json: Edit(change: static document => Package(document: document, name: "world")["inputs"] = Refs("scene"))),
            expected: "RENDERGRAPH_PACKAGE_PORTS"
        );
    }
    [Fact]
    public void ADocumentCannotNameThePackageKind() {
        // A shader pass's kind has no Package member, so the graph reader and the pipeline loader that reads through it
        // refuse the name at the member that spells it, before any planner sees the document.
        var graph = Assert.Throws<JsonException>(testCode: static () => RenderGraphDefinition.Parse(json: Edit(change: static document => document["passes"]![0]!["kind"] = "Package")));
        var pipeline = Assert.Throws<JsonException>(testCode: static () => ShaderPipelineLoader.ParseDefinition(
            name: "stray",
            path: "stray.graph.json",
            text: """
                {
                  "$schema": "puck.render.graph.v1",
                  "name": "stray",
                  "resources": [],
                  "passes": [{ "name": "stray", "source": "stray.hlsl", "entryPoint": "main", "kind": "Package" }],
                  "outputs": []
                }
                """
        ));

        Assert.Contains(
            actualString: graph.Message,
            expectedSubstring: "$.passes[0].kind"
        );
        Assert.Contains(
            actualString: pipeline.Message,
            expectedSubstring: "$.passes[0].kind"
        );
    }
    [Fact]
    public void AViewReadingItsOwnOutputGoesThroughTheHistoryResource() {
        var mirror = Edit(change: static document => {
            var final = document["resources"]!.AsArray().Select(selector: static node => node!.AsObject()).Single(predicate: static node => (((string?)node["name"]) == "final"));

            final["history"] = true;
            final["initialization"] = "Zero";
            document["passes"]![0]!["inputs"] = new JsonArray(Ref(name: "scene"), Ref(name: "screen"), new JsonObject { ["name"] = "final", ["previousFrame"] = true });
        });
        var plan = Plan(json: mirror);
        var storage = plan.Pipeline.Storages.Single(predicate: static storage => (storage.Name == "final"));

        Assert.True(condition: storage.History);
        Assert.Contains(
            collection: plan.Pipeline.Passes[1].Accesses,
            filter: static access => ((access.Version == "final") && access.PreviousFrame)
        );
        Assert.Contains(
            collection: Codes(json: Edit(change: static document => document["passes"]![0]!["inputs"] = Refs("scene", "screen", "final"))),
            expected: "SHADERPIPE_CYCLE"
        );
    }
    [Fact]
    public void EveryCheckedInGraphDocumentPlansAlikeInAPipelineAndInTheEngine() {
        var root = RepositoryPaths.RequireRoot();
        var documents = new[] { "src", "tests" }
            .SelectMany(selector: directory => Directory.EnumerateFiles(
                path: Path.Combine(
                    path1: root,
                    path2: directory
                ),
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.graph.json"
            ))
            .Where(predicate: static path => !path.Replace(newChar: '/', oldChar: '\\').Split(separator: '/').Any(predicate: static segment => (segment is "bin" or "obj")))
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(collection: documents);

        foreach (var path in documents) {
            var text = File.ReadAllText(path: path);
            var pipeline = ShaderPipelineLoader.ParseDefinition(
                name: Path.GetFileNameWithoutExtension(path: path),
                path: path,
                text: text
            );
            var graph = RenderGraphDefinition.Parse(json: text);
            var pipelinePlanned = RenderGraphCompiler.ShaderPasses.TryCompile(
                definition: pipeline,
                diagnostics: out var pipelineDiagnostics,
                plan: out var pipelinePlan
            );
            var graphPlanned = new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).TryCompile(
                definition: graph,
                diagnostics: out var graphDiagnostics,
                plan: out var graphPlan
            );

            Assert.True(
                condition: (pipelinePlanned == graphPlanned),
                userMessage: path
            );
            Assert.Equal(
                expected: pipelineDiagnostics.Select(selector: static diagnostic => diagnostic.Code),
                actual: graphDiagnostics.Select(selector: static diagnostic => diagnostic.Code)
            );

            if (!pipelinePlanned) {
                continue;
            }

            var planned = pipelinePlan!.Pipeline;

            Assert.Equal(expected: planned.PassOrder, actual: graphPlan!.Pipeline.PassOrder);
            Assert.Equal(expected: planned.Outputs, actual: graphPlan.Outputs);
            Assert.Equal(
                expected: planned.Storages.Select(selector: static storage => (storage.Name, storage.History, storage.Clear, string.Join(separator: ",", values: storage.Versions))),
                actual: graphPlan.Pipeline.Storages.Select(selector: static storage => (storage.Name, storage.History, storage.Clear, string.Join(separator: ",", values: storage.Versions)))
            );

            for (var index = 0; (index < planned.Passes.Count); index++) {
                Assert.Equal(
                    expected: planned.Passes[index].Accesses,
                    actual: graphPlan.Pipeline.Passes[index].Accesses
                );
                Assert.Null(@object: graphPlan.Steps[index].Package);
            }
        }
    }
    [Fact]
    public void TheShippedCatalogOffersEveryShippedPostProcessSetAsAPackage() {
        var catalog = RenderGraphPackageCatalog.WithPostProcess(postProcess: ShaderSetCatalog.Scan(rootDirectory: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders"
        )));

        Assert.True(condition: catalog.TryGet(
            id: "post.sdf-film-grain",
            package: out var grain
        ));
        Assert.Equal(expected: RenderGraphPackagePort.Image, actual: Assert.Single(collection: grain.Inputs));
        Assert.Equal(expected: RenderGraphPackagePort.Image, actual: Assert.Single(collection: grain.Outputs));
        Assert.True(condition: catalog.TryGet(
            id: RenderGraphPackageCatalog.SdfWorld,
            package: out _
        ));
    }
}
