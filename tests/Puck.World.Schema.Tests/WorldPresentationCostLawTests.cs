using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for the cost report's presentation dimension and the frame-graph schema: every <c>views.graphs</c> row appears
/// with its extent ceiling, rate and passes, a document-only analysis names why its passes are unplanned, and the
/// generated <c>puck.render.graph.v1</c> schema admits a graph and every checked-in pipeline document retagged as one.
/// </summary>
public sealed class WorldPresentationCostLawTests {
    private static WorldDefinition Document(params WorldViewGraph[] graphs) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 30),
        ViewsRaw: new WorldViewDefaults(
            GraphBudget: new WorldViewGraphBudget(PassPixelsPerFrame: 1_000_000),
            Graphs: graphs
        )
    );
    private static Json.Schema.JsonSchema GraphSchema() => SchemaVerdicts.Build(schema: JsonNode.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json")))!);

    [Fact]
    public void EveryGraphRowAppearsWithItsRateAndADocumentOnlyAnalysisNamesItsPassesUnplanned() {
        var report = WorldCostReport.Generate(definition: Document(
            new WorldViewGraph(
                Name: "security",
                Refresh: new WorldViewGraphRefresh(Divisor: 2),
                Source: "graphs/camera.graph.json"
            ),
            new WorldViewGraph(
                Name: "lobby",
                Refresh: new WorldViewGraphRefresh(Hertz: 10),
                Source: "graphs/camera.graph.json"
            ),
            new WorldViewGraph(
                Name: "main",
                Source: "graphs/main.graph.json"
            )
        ));
        var presentation = report.Presentation;

        Assert.Equal(expected: ["security", "lobby", "main"], actual: presentation.Instances.Select(selector: static line => line.Name));
        Assert.Equal(expected: ["1/2 frames", "10 Hz", "every frame"], actual: presentation.Instances.Select(selector: static line => line.DescribeRate()));
        Assert.All(
            action: static line => Assert.Equal(expected: (null, WorldPresentationCost.UnplannedIssue), actual: (line.Passes, line.Issue)),
            collection: presentation.Instances
        );
        Assert.Equal(expected: 1_000_000, actual: presentation.PassPixelsPerFrame);
        Assert.Contains(expectedSubstring: "budget 1000000 pass-pixels/frame", actualString: presentation.Describe());

        var priced = WorldPresentationCost.Measure(
            definition: Document(new WorldViewGraph(
                Name: "security",
                Refresh: new WorldViewGraphRefresh(Divisor: 2),
                Source: "graphs/camera.graph.json"
            )),
            passes: static _ => (3, null)
        );

        Assert.Equal(expected: 1.5, actual: priced.Instances[0].PassDisplaysPerFrame);
    }
    [Fact]
    public void ADocumentWithoutGraphsHasNoPresentationLines() {
        var report = WorldCostReport.Generate(definition: new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 30)));

        Assert.Same(expected: WorldPresentationCost.None, actual: report.Presentation);
        Assert.Equal(expected: "graphs none", actual: report.Presentation.Describe());
    }
    [Fact]
    public void TheGraphSchemaAdmitsAGraphAndEveryCheckedInGraphDocument() {
        var schema = GraphSchema();
        var graph = JsonNode.Parse(json: """
            {
              "$schema": "puck.render.graph.v1",
              "name": "monitor",
              "resources": [
                { "name": "scene", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } },
                { "name": "final", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } }
              ],
              "packages": [
                { "name": "world", "package": "sdf.world", "outputs": [{ "name": "scene" }] },
                { "name": "hud", "package": "overlay", "inputs": [{ "name": "scene" }], "outputs": [{ "name": "final" }] }
              ],
              "outputs": ["final"]
            }
            """)!;

        Assert.True(condition: schema.Admits(instance: graph), userMessage: schema.Explain(instance: graph));

        var wrongTag = graph.DeepClone();

        wrongTag["$schema"] = "puck.render.graph.v2";
        Assert.False(condition: schema.Admits(instance: wrongTag));

        var unknownMember = graph.DeepClone();

        unknownMember["config"] = new JsonObject();
        Assert.False(condition: schema.Admits(instance: unknownMember));

        var root = RepositoryPaths.RequireRoot();
        var documents = new[] { "src", "tests" }
            .SelectMany(selector: directory => Directory.EnumerateFiles(
                path: Path.Combine(path1: root, path2: directory),
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.pipeline.json"
            ))
            .Where(predicate: static path => !path.Replace(newChar: '/', oldChar: '\\').Split(separator: '/').Any(predicate: static segment => (segment is "bin" or "obj")))
            .ToArray();

        Assert.NotEmpty(collection: documents);

        foreach (var path in documents) {
            var document = JsonNode.Parse(json: File.ReadAllText(path: path))!;

            Assert.True(condition: schema.Admits(instance: document), userMessage: $"{path}: {schema.Explain(instance: document)}");
        }
    }
}
