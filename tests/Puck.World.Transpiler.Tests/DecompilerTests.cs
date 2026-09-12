using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class DecompilerTests {
    [Fact]
    public void TestPipelineWorldDecompilationAndRoundTrip() {
        var jsonPath = Path.Combine(ShippedWorlds.FindDirectory(), "pipeline.world.json");
        var originalJson = File.ReadAllText(jsonPath);
        var originalNode = JsonNode.Parse(originalJson) as JsonObject;
        Assert.NotNull(originalNode);

        // 1. Decompile original JSON -> .puck DSL
        var decompiledPuck = WorldDecompiler.Decompile(originalNode);
        Assert.Contains("schema: \"puck.world.def.v1\"", decompiledPuck);
        Assert.Contains("host {", decompiledPuck);
        Assert.Contains("views {", decompiledPuck);
        Assert.Contains("layout \"pipeline\" {", decompiledPuck);
        Assert.Contains("orbit(distance: 0.01, pitch: 0, yaw: 0)", decompiledPuck);
        Assert.Contains("fieldOfView(fieldOfViewRadians: 0.001)", decompiledPuck);
        Assert.Contains("pipeline \"ink\" {", decompiledPuck);

        // 2. Parse .puck DSL -> AST
        var ast = PuckParser.ParseDocument(decompiledPuck);

        // 3. Lower AST -> JSON
        var recompJsonObject = WorldDocumentEmitter.Lower(ast);

        // 4. Assert round-trip equivalence
        Assert.Equal(originalNode["schema"]?.ToString(), recompJsonObject["schema"]?.ToString());

        var origHost = Assert.IsType<JsonObject>(originalNode["host"]);
        var recompHost = Assert.IsType<JsonObject>(recompJsonObject["host"]);
        Assert.Equal(origHost["width"]?.GetValue<long>(), recompHost["width"]?.GetValue<long>());
        Assert.Equal(origHost["height"]?.GetValue<long>(), recompHost["height"]?.GetValue<long>());
        Assert.Equal(origHost["targetHertz"]?.GetValue<long>(), recompHost["targetHertz"]?.GetValue<long>());
        Assert.Equal(origHost["backend"]?.ToString(), recompHost["backend"]?.ToString());

        var origViews = Assert.IsType<JsonObject>(originalNode["views"]);
        var recompViews = Assert.IsType<JsonObject>(recompJsonObject["views"]);

        var origLayouts = Assert.IsType<JsonArray>(origViews["layouts"]);
        var recompLayouts = Assert.IsType<JsonArray>(recompViews["layouts"]);
        Assert.Equal(origLayouts.Count, recompLayouts.Count);

        var origRig = Assert.IsType<JsonObject>(origViews["seatRig"]);
        var recompRig = Assert.IsType<JsonObject>(recompViews["seatRig"]);
        Assert.Equal(origRig["name"]?.ToString(), recompRig["name"]?.ToString());
        Assert.Equal(origRig["version"]?.ToString(), recompRig["version"]?.ToString());

        var origOps = Assert.IsType<JsonArray>(origRig["operations"]);
        var recompOps = Assert.IsType<JsonArray>(recompRig["operations"]);
        Assert.Equal(origOps.Count, recompOps.Count);
        Assert.Equal(origOps[0]?["$type"]?.ToString(), recompOps[0]?["$type"]?.ToString());
        Assert.Equal(origOps[0]?["distance"]?.GetValue<double>(), recompOps[0]?["distance"]?.GetValue<double>());
    }

    [Fact]
    public void TestSolitaireWorldDecompilationAndRoundTrip() {
        var jsonPath = Path.Combine(ShippedWorlds.FindDirectory(), "games", "solitaire.world.json");
        var originalJson = File.ReadAllText(jsonPath);
        var originalNode = JsonNode.Parse(originalJson) as JsonObject;
        Assert.NotNull(originalNode);

        // Decompile
        var puck = WorldDecompiler.Decompile(originalNode);
        Assert.Contains("import \"klondike.world.json\"", puck);
        Assert.Contains("export action", puck);
        Assert.Contains("export binding", puck);
        Assert.Contains("export read", puck);

        // Recompile
        var ast = PuckParser.ParseDocument(puck);
        var recomp = WorldDocumentEmitter.Lower(ast);

        // Verify imports and exports preserved
        var imports = Assert.IsType<JsonArray>(recomp["imports"]);
        Assert.Equal(3, imports.Count);

        var exports = Assert.IsType<JsonObject>(recomp["exports"]);
        var actions = Assert.IsType<JsonArray>(exports["actions"]);
        Assert.Equal(4, actions.Count);
        Assert.Contains(actions, a => a?.ToString() == "solitaire");
        Assert.Contains(actions, a => a?.ToString() == "solitaireKlondike");
    }
}
