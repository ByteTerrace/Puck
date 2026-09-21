using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class WorldCompositionLinksTests {
    [InlineData("spawnPoints [{ id: 7 }]\nspawn arrival { at [0, 0, 0] }")]
    [InlineData("prototypes [{ id: 7 }]\nground floor { size [8m, 8m] }")]
    [Theory]
    public void EndpointSugarRefusesMalformedExistingIdsWithoutThrowing(string source) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            defaultSchema: "puck.world.definition.v1",
            imports: ImportHandling.Ignore,
            source: source
        );

        Assert.Contains(collection: compilation.Diagnostics, filter: static diagnostic => diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "id is not text"));
    }
    [Fact]
    public void GroundWithoutCenterEmitsAValidWorldAtTheOrigin() {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            defaultSchema: "puck.world.definition.v1",
            imports: ImportHandling.Ignore,
            source: "ground floor { size [12m, 8m] }"
        );

        var json = compilation.RequireJson();
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: json.ToJsonString()));

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition, out var reason, neighbours: null), userMessage: reason);
        Assert.Equal(expected: "ground-floor", actual: definition.Creations.Single().Id);
    }
    [Fact]
    public void CompositionEndpointUnitsLowerToWorldUnitsAndDegrees() {
        var compilation = WorldCompiler.Compile(
            allowMultiple: true,
            cancellationToken: TestContext.Current.CancellationToken,
            defaultSchema: "puck.world.definition.v1",
            imports: ImportHandling.Ignore,
            source: """
                module patch(origin: Point) {
                  ground floor { center: origin size [12m, 8m] }
                  spawn arrival { at: origin + [0, 0, 4m] yaw: 90deg }
                }
                module region(origin: Point) {
                  use patch as wing(origin: origin)
                }
                world west = region(origin: [0m, 0m, 0m])
                world east = region(origin: [12m, 0m, 0m])
                border west.wing.floor.east, east.wing.floor.west { height: 6m hysteresis: 2m }
                """
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport());
        var west = compilation.Worlds.Single(predicate: output => (output.Name == "west")).Json;

        Assert.True(condition: DocumentNumbers.TryExact(west["adjacencies"]![0]!["hysteresis"], out var hysteresis));
        Assert.True(condition: DocumentNumbers.TryExact(west["spawnPoints"]![0]!["yawDegrees"], out var yaw));
        Assert.Equal(actual: hysteresis, expected: 2m);
        Assert.Equal(actual: yaw, expected: 90m);
        Assert.Null(@object: west["$composition"]);
    }
    [Fact]
    public void GroundBorderGeneratesReciprocalTopology() {
        var worlds = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal) {
            ["west"] = Ground(name: "yard", x: 0),
            ["east"] = Ground(name: "yard", x: 20),
        };
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink(
            Kind: "border", LeftWorld: "west", LeftEndpoint: "east",
            RightWorld: "east", RightEndpoint: "west",
            Options: new JsonObject { ["height"] = 12 }, Origin: new SourceOrigin(SourceSpan.None, null, null)
        )], diagnostics);

        Assert.False(condition: diagnostics.HasErrors);
        var west = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: worlds["west"]["adjacencies"])));
        var east = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: worlds["east"]["adjacencies"])));

        Assert.Equal(expected: "west", actual: west["counterpart"]?.GetValue<string>());
        Assert.Equal(expected: "east", actual: east["counterpart"]?.GetValue<string>());
        Assert.Equal(expected: 90d, actual: west["boundary"]?["outwardYawDegrees"]?.GetValue<double>());
        Assert.Equal(expected: -90d, actual: east["boundary"]?["outwardYawDegrees"]?.GetValue<double>());
        Assert.Equal(expected: "east.world.json", actual: worlds["west"]["references"]?[0]?["document"]?.GetValue<string>());
    }
    [Fact]
    public void PitchedBorderUsesWrittenFrameAndCarriesAuthoredHysteresis() {
        var worlds = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal) { ["island"] = [], ["corner"] = [] };
        var diagnostics = new DiagnosticBag();
        var options = new JsonObject {
            ["center"] = new JsonArray(0, 80, 0),
            ["pitch"] = -90,
            ["width"] = 90,
            ["height"] = 90,
        };

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("border", "island", "under", "corner", "sky", options, SourceSpan.None)], diagnostics);

        Assert.False(condition: diagnostics.HasErrors);
        Assert.Equal(expected: -90d, actual: worlds["island"]["adjacencies"]?[0]?["boundary"]?["outwardPitchDegrees"]?.GetValue<double>());
        Assert.Equal(expected: 90d, actual: worlds["corner"]["adjacencies"]?[0]?["boundary"]?["outwardPitchDegrees"]?.GetValue<double>());

        options["hysteresis"] = 2;
        var widened = new Dictionary<string, JsonObject> { ["a"] = [], ["b"] = [] };
        var widenedDiagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(widened, [new WorldCompositionLink("border", "a", "under", "b", "sky", options, SourceSpan.None)], widenedDiagnostics);
        Assert.False(condition: widenedDiagnostics.HasErrors);
        Assert.Equal(expected: 2, actual: widened["a"]["adjacencies"]?[0]?["hysteresis"]?.GetValue<int>());
    }
    [Fact]
    public void ExplicitBorderRefusesNonnumericFrameMembers() {
        var worlds = new Dictionary<string, JsonObject> { ["a"] = [], ["b"] = [] };
        var diagnostics = new DiagnosticBag();
        var options = new JsonObject {
            ["center"] = new JsonArray(0, "high", 0),
            ["yaw"] = "north",
            ["pitch"] = "down",
            ["width"] = 4,
            ["height"] = 4,
        };

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("border", "a", "edge", "b", "edge", options, SourceSpan.None)], diagnostics);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Empty(collection: worlds["a"]);
        Assert.Empty(collection: worlds["b"]);
    }
    [Fact]
    public void AutomaticBorderRefusesGroundSidesThatDoNotMeet() {
        var worlds = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal) {
            ["west"] = Ground(name: "yard", x: 0),
            ["east"] = Ground(name: "yard", x: 50),
        };
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("border", "west", "east", "east", "west", new JsonObject(), SourceSpan.None)], diagnostics);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(expectedSubstring: "do not occupy the same edge", actualString: diagnostics[0].Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void AutomaticBorderRefusesSidesWithTheSameOutwardNormal() {
        var worlds = new Dictionary<string, JsonObject> { ["a"] = Ground(name: "floor", x: 0), ["b"] = Ground(name: "floor", x: 0) };
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("border", "a", "north", "b", "north", new JsonObject(), SourceSpan.None)], diagnostics);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(expectedSubstring: "must face one another", actualString: diagnostics[0].Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void AutomaticBorderRefusesPartialExplicitFrameOptions() {
        var worlds = new Dictionary<string, JsonObject> { ["a"] = Ground(name: "floor", x: 0), ["b"] = Ground(name: "floor", x: 20) };
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("border", "a", "east", "b", "west", new JsonObject { ["yaw"] = 30 }, SourceSpan.None)], diagnostics);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(expectedSubstring: "explicit border frame requires", actualString: diagnostics[0].Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void DoorBuildsMappedReturnArchAndBothDocumentsValidate() {
        var archPrototype = new JsonObject {
            ["id"] = "arch",
            ["document"] = new JsonObject {
                ["schema"] = "puck.creation.v1",
                ["name"] = "arch",
                ["palette"] = new JsonArray(new JsonObject { ["color"] = "#808080" }),
                ["shapes"] = new JsonArray(new JsonObject {
                    ["id"] = 0,
                    ["type"] = "Box",
                    ["position"] = new JsonArray(0, 0, 0),
                    ["rotation"] = new JsonArray(0, 0, 0, 1),
                    ["scale"] = new JsonArray(1, 2, 0.1),
                    ["material"] = 0,
                    ["blend"] = "Union",
                    ["smooth"] = 0,
                }),
                ["behavior"] = new JsonObject { ["faces"] = new JsonArray(new JsonObject { ["name"] = "portal", ["shapeId"] = 0 }) },
            },
        };
        var source = new JsonObject {
            ["schema"] = "puck.world.definition.v1",
            ["documentId"] = "island",
            ["prototypes"] = new JsonArray(archPrototype),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject {
                    ["id"] = "arch1",
                    ["prototypeId"] = "arch",
                    ["position"] = new JsonArray(0, 0, 0),
                    ["yawDegrees"] = 0,
                    ["scale"] = 1,
                    ["faceSources"] = new JsonArray(new JsonObject { ["face"] = "portal", ["source"] = new JsonObject { ["$type"] = "none" } }),
                }),
            },
        };
        var destination = new JsonObject {
            ["schema"] = "puck.world.definition.v1",
            ["documentId"] = "parlor",
            ["spawnPoints"] = new JsonArray(new JsonObject { ["id"] = "arrival", ["position"] = new JsonArray(3, 0, 4), ["yawDegrees"] = 90 }),
        };
        var worlds = new Dictionary<string, JsonObject> { ["island"] = source, ["parlor"] = destination };
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("door", "island", "arch1", "parlor", "arrival", new JsonObject(), SourceSpan.None)], diagnostics);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport());
        Assert.Equal(expected: "mapped", actual: source["placements"]?["rows"]?[0]?["faceSources"]?[0]?["portal"]?["arrival"]?.GetValue<string>());
        Assert.Equal(expected: "return-island-arch1/portal", actual: source["placements"]?["rows"]?[0]?["faceSources"]?[0]?["portal"]?["counterpart"]?.GetValue<string>());
        Assert.Equal(expected: 3, actual: destination["placements"]?["rows"]?[0]?["position"]?[0]?.GetValue<int>());
        foreach (var world in worlds.Values) {
            var definition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: world.ToJsonString()));

            Assert.True(condition: WorldDefinitionValidator.TryValidate(definition, out var reason, neighbours: null), userMessage: reason);
        }
    }
    [Fact]
    public void AuthoredDoorCompilesThroughModulesAndCreatesItsReturnArch() {
        var compilation = WorldCompiler.Compile(
            allowMultiple: true,
            cancellationToken: TestContext.Current.CancellationToken,
            defaultSchema: "puck.world.definition.v1",
            imports: ImportHandling.Ignore,
            sourcePath: "composition.puck",
            source: """
                module islandRoom() {
                  prototypes [{ id: "arch", document {
                    schema: "puck.creation.v1",
                    palette [{ color: "#808080" }],
                    shapes [{ id: 0, type: "Box", position [0, 0, 0], rotation [0, 0, 0, 1], scale [1, 2, 0.1], material: 0, blend: "Union", smooth: 0 }],
                    behavior { faces [{ name: "portal", shapeId: 0 }] }
                  } }]
                  placements { rows [{
                    id: "arch1", prototypeId: "arch", position [0, 0, 0], yawDegrees: 0, scale: 1,
                    faceSources [{ face: "portal", source { "$type": "none" } }]
                  }] }
                }
                module parlorRoom() {
                  spawn arrival { at [3m, 0m, 4m] yaw: 90deg }
                }
                world island = islandRoom()
                world parlor = parlorRoom()
                door island.arch1, parlor.arrival
                """
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport());
        var islandOutput = compilation.Worlds.Single(predicate: output => (output.Name == "island"));
        var parlorOutput = compilation.Worlds.Single(predicate: output => (output.Name == "parlor"));
        var island = islandOutput.Json;
        var parlor = parlorOutput.Json;

        Assert.Equal(expected: "return-island-arch1/portal", actual: island["placements"]?["rows"]?[0]?["faceSources"]?[0]?["portal"]?["counterpart"]?.GetValue<string>());
        Assert.Equal(expected: "return-island-arch1", actual: parlor["placements"]?["rows"]?[0]?["id"]?.GetValue<string>());
        Assert.True(condition: islandOutput.SourceMap.TryGetOrigin("/placements/rows/0/faceSources/0/portal", out var portalOrigin));
        Assert.Equal(expected: "composition.puck", actual: portalOrigin.SourcePath);
        foreach (var world in compilation.Worlds) {
            var definition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: world.Json.ToJsonString()));

            Assert.True(condition: WorldDefinitionValidator.TryValidate(definition, out var reason, neighbours: null), userMessage: reason);
        }
    }
    [Fact]
    public void GeneratedLinkRowsRetainTheDeferredSourceOrigin() {
        var worlds = new Dictionary<string, JsonObject> { ["a"] = [], ["b"] = [] };
        var maps = new Dictionary<string, SourceMap> { ["a"] = new(), ["b"] = new() };
        var diagnostics = new DiagnosticBag();
        var span = new SourceSpan(Column: 1, Length: 18, Line: 4, Offset: 20);
        SourceOrigin origin;

        using (maps["a"].PushOrigin(sourcePath: "composition.puck", moduleInstance: "generated"))
        using (maps["a"].PushOrigin(moduleInstance: "second")) {
            origin = maps["a"].CaptureOrigin(span);
        }
        var options = new JsonObject { ["center"] = new JsonArray(0, 0, 0), ["width"] = 4, ["height"] = 6 };

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("border", "a", "east", "b", "west", options, origin)], diagnostics, maps);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport());
        Assert.True(condition: maps["a"].TryGetOrigin("/adjacencies/0", out var registered));
        Assert.Equal(expected: origin, actual: registered);
    }
    [Fact]
    public void DoorRefusesMalformedPlacementIdWithoutMutatingTopology() {
        var source = new JsonObject {
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject {
                    ["id"] = 42,
                    ["prototypeId"] = "arch",
                    ["faceSources"] = new JsonArray(),
                }),
            },
        };
        var destination = new JsonObject {
            ["spawnPoints"] = new JsonArray(new JsonObject {
                ["id"] = "arrival",
                ["position"] = new JsonArray(0, 0, 0),
                ["yawDegrees"] = 0,
            }),
        };
        var worlds = new Dictionary<string, JsonObject> { ["source"] = source, ["destination"] = destination };
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("door", "source", "arch1", "destination", "arrival", new JsonObject(), SourceSpan.None)], diagnostics);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Null(@object: source["references"]);
        Assert.Null(@object: destination["references"]);
    }
    [Fact]
    public void DoorRefusesADependentSourceOnAnyCopiedFace() {
        var (worlds, source, destination) = DoorWorlds(
            new JsonObject { ["face"] = "portal", ["source"] = new JsonObject { ["$type"] = "none" } },
            new JsonObject { ["face"] = "status", ["source"] = new JsonObject { ["$type"] = "machine", ["instance"] = "missing" } }
        );
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("door", "source", "arch1", "destination", "arrival", new JsonObject(), SourceSpan.None)], diagnostics);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(expectedSubstring: "dependent face source 'machine'", actualString: diagnostics[0].Message, comparisonType: StringComparison.Ordinal);
        Assert.Null(@object: source["references"]);
        Assert.Null(@object: destination["placements"]);
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void DoorRefusesToOverwriteOrCopyAnAuthoredPortal(bool onSelectedFace) {
        var selected = new JsonObject {
            ["face"] = (onSelectedFace ? "portal" : "status"),
            ["source"] = new JsonObject { ["$type"] = "none" },
            ["portal"] = new JsonObject { ["destination"] = "existing", ["travel"] = "body", ["arrival"] = "mapped", ["counterpart"] = "other/portal" },
        };

        var (worlds, source, destination) = (onSelectedFace ? DoorWorlds(selected) : DoorWorlds(
            new JsonObject { ["face"] = "portal", ["source"] = new JsonObject { ["$type"] = "none" } }, selected));
        var diagnostics = new DiagnosticBag();

        WorldCompositionLinks.Apply(worlds, [new WorldCompositionLink("door", "source", "arch1", "destination", "arrival", new JsonObject(), SourceSpan.None)], diagnostics);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(expectedSubstring: "already declares a portal", actualString: diagnostics[0].Message, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "existing", actual: selected["portal"]?["destination"]?.GetValue<string>());
        Assert.Null(@object: source["references"]);
        Assert.Null(@object: destination["placements"]);
    }

    private static (Dictionary<string, JsonObject> Worlds, JsonObject Source, JsonObject Destination) DoorWorlds(params JsonObject[] faces) {
        var source = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "arch", ["document"] = new JsonObject() }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject {
                    ["id"] = "arch1",
                    ["prototypeId"] = "arch",
                    ["position"] = new JsonArray(0, 0, 0),
                    ["yawDegrees"] = 0,
                    ["scale"] = 1,
                    ["faceSources"] = new JsonArray(faces),
                }),
            },
        };
        var destination = new JsonObject {
            ["spawnPoints"] = new JsonArray(new JsonObject { ["id"] = "arrival", ["position"] = new JsonArray(0, 0, 0), ["yawDegrees"] = 0 }),
        };

        return (new Dictionary<string, JsonObject> { ["source"] = source, ["destination"] = destination }, source, destination);
    }
    private static JsonObject Ground(string name, double x) => new() {
        ["$composition"] = new JsonObject {
            ["grounds"] = new JsonArray(new JsonObject {
                ["name"] = name,
                ["center"] = new JsonArray(x, 0, 0),
                ["size"] = new JsonArray(20, 20),
            }),
        },
    };
}
