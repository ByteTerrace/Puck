using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for the cost report's presentation dimension and the frame-graph schema: every <c>views.graphs</c> row appears
/// with its extent ceiling, rate and passes, a document-only analysis names why its passes are unplanned, every bound
/// parameter is priced in bytes per tick and per frame against ceilings that refuse naming the graph and the binding, and the
/// generated <c>puck.render.graph.v1</c> schema admits a graph and every checked-in graph document.
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

    // Two graph rows binding every kind of parameter: a stepping scalar, a whole keyed row of 8 cells, a literal, an
    // advancing scalar, an eased follower and its stored truth.
    private static WorldDefinition Bound(long bytesPerTick, long bytesPerFrame) => WorldDefinitionSerialization.Deserialize(utf8Json: BoundJson(
        bytesPerFrame: bytesPerFrame,
        bytesPerTick: bytesPerTick
    ));
    private static byte[] BoundJson(long bytesPerTick, long bytesPerFrame) => System.Text.Encoding.UTF8.GetBytes(s: $$"""
        {
          "schema": "puck.world.definition.v1",
          "dynamics": [{ "f": 2, "name": "ease", "r": 0, "zeta": 0.7 }],
          "state": {
            "world": [
              { "kind": "Int", "max": 10, "min": 0, "name": "level", "value": 0 },
              { "advance": { "perSecondDenominator": 1, "perSecondNumerator": 6 }, "kind": "Int", "max": 1000, "min": 0, "name": "clock", "value": 0 },
              { "cells": [{ "dynamics": { "row": "ease" }, "key": "0", "value": "0" }], "kind": "Fixed", "max": "1", "min": "-1", "name": "eased" },
              { "capacity": 8, "kind": "Int", "max": 9, "min": 0, "name": "cells" }
            ]
          },
          "views": {
            "graphBudget": { "bytesPerFrame": {{bytesPerFrame}}, "bytesPerTick": {{bytesPerTick}} },
            "graphs": [
              { "name": "board", "parameters": { "draw": { "level": "state.level", "tiles": "state.cells", "width": 8 } }, "source": "board.graph.json" },
              { "name": "gauge", "parameters": { "fill": { "clock": "state.clock", "eased": "state.eased.0", "stored": "state.eased.0.$target" } }, "source": "gauge.graph.json" }
            ]
          }
        }
        """);

    [Fact]
    public void EveryBoundParameterIsPricedInBytesPerTickAndPerFrame() {
        const long Scalar = WorldBindingCost.ScalarBytes;
        var presentation = WorldCostReport.Generate(definition: Bound(bytesPerFrame: 16, bytesPerTick: 1024)).Presentation;

        Assert.Equal(
            actual: presentation.Bindings.Select(selector: static line => (line.Pipeline, line.Binding, line.Elements, line.BytesPerTick, line.BytesPerFrame)),
            expected: [
                ("board", "draw.level", 1, Scalar, 0L),
                ("board", "draw.tiles", 8, (8L * WorldBindingCost.ArrayElementBytes), 0L),
                ("board", "draw.width", 0, 0L, 0L),
                ("gauge", "fill.clock", 1, Scalar, Scalar),
                ("gauge", "fill.eased", 1, Scalar, Scalar),
                ("gauge", "fill.stored", 1, Scalar, 0L),
            ]
        );
        Assert.Equal(expected: (144L, 8L, 1024L, 16L), actual: (presentation.BytesPerTick, presentation.BytesPerFrame, presentation.BytesPerTickCeiling, presentation.BytesPerFrameCeiling));
        Assert.Contains(expectedSubstring: "board draw.tiles (state.cells) 128 B/tick 0 B/frame", actualString: presentation.Describe());
        Assert.Contains(expectedSubstring: "144 B/tick of 1024, 8 B/frame of 16", actualString: presentation.Describe());
    }
    [Fact]
    public void ACeilingTheBindingsExceedRefusesNamingTheGraphAndTheBinding() {
        // A document is validated as it is read, so a ceiling its bindings exceed refuses the read.
        static string Refusal(long bytesPerTick, long bytesPerFrame) {
            try {
                _ = WorldDefinitionSerialization.Deserialize(utf8Json: BoundJson(
                    bytesPerFrame: bytesPerFrame,
                    bytesPerTick: bytesPerTick
                ));

                return string.Empty;
            } catch (InvalidDataException exception) {
                return exception.Message;
            }
        }

        Assert.Equal(expected: string.Empty, actual: Refusal(bytesPerFrame: 8, bytesPerTick: 144));
        Assert.Contains(
            actualString: Refusal(bytesPerFrame: 8, bytesPerTick: 100),
            expectedSubstring: "views.graphBudget.bytesPerTick 100: graph 'board' binding 'draw.tiles' (state.cells, 128 bytes a tick) brings the bound parameters to 132 bytes a tick."
        );
        Assert.Contains(
            actualString: Refusal(bytesPerFrame: 4, bytesPerTick: 144),
            expectedSubstring: "views.graphBudget.bytesPerFrame 4: graph 'gauge' binding 'fill.eased' (state.eased.0, 4 bytes a frame) brings the bound parameters to 8 bytes a frame."
        );
        Assert.Contains(
            actualString: Refusal(bytesPerFrame: 0, bytesPerTick: -1),
            expectedSubstring: "views.graphBudget.bytesPerTick -1 must be non-negative."
        );
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
                searchPattern: "*.graph.json"
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
