using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class DecompilerTests {
    // A quoted name is printed in the reader's own string grammar, so a quote, a backslash, a line break or a control
    // character in it reads back as itself.
    [InlineData("say \"hi\"")]
    [InlineData("back\\slash")]
    [InlineData("two\nlines\tand\u0001")]
    [Theory]
    public void AQuotedRuleNameReadsBackAsItself(string name) {
        var document = WorldSources.LowerClean(body: "state {\n    world {\n        slot flag = 0\n    }\n}\n\nrule \"r\" {\n    flag = 1\n}\n");

        document["rules"]![0]!["name"] = name;
        _ = WorldSources.AssertRoundTrips(original: document);
    }
    [Fact]
    public void TestPipelineWorldDecompilationAndRoundTrip() {
        var jsonPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: "pipeline.world.json"
        );
        var originalJson = File.ReadAllText(path: jsonPath);
        var originalNode = (JsonNode.Parse(originalJson) as JsonObject);

        Assert.NotNull(@object: originalNode);

        // 1. Decompile original JSON -> .puck DSL
        var decompiledPuck = WorldDecompiler.Decompile(root: originalNode);

        Assert.Contains(
            actualString: decompiledPuck,
            expectedSubstring: "schema: \"puck.world.definition.v1\""
        );
        Assert.Contains(
            actualString: decompiledPuck,
            expectedSubstring: "host {"
        );
        Assert.Contains(
            actualString: decompiledPuck,
            expectedSubstring: "views {"
        );
        Assert.Contains(
            actualString: decompiledPuck,
            expectedSubstring: "layout \"pipeline\" {"
        );
        Assert.Contains(
            actualString: decompiledPuck,
            expectedSubstring: "orbit(distance: 0.01, pitch: 0, yaw: 0)"
        );
        Assert.Contains(
            actualString: decompiledPuck,
            expectedSubstring: "fieldOfView(fieldOfViewRadians: 0.001)"
        );
        Assert.Contains(
            actualString: decompiledPuck,
            expectedSubstring: "graph \"ink\" {"
        );

        // 2. Recompile the printed source
        var recompJsonObject = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: decompiledPuck
        ).RequireJson();

        // 4. Assert round-trip equivalence
        Assert.Equal(
            originalNode["schema"]?.ToString(),
            recompJsonObject["schema"]?.ToString()
        );

        var origHost = Assert.IsType<JsonObject>(@object: originalNode["host"]);
        var recompHost = Assert.IsType<JsonObject>(@object: recompJsonObject["host"]);

        Assert.Equal(
            origHost["width"]?.GetValue<long>(),
            recompHost["width"]?.GetValue<long>()
        );
        Assert.Equal(
            origHost["height"]?.GetValue<long>(),
            recompHost["height"]?.GetValue<long>()
        );
        Assert.Equal(
            origHost["targetHertz"]?.GetValue<long>(),
            recompHost["targetHertz"]?.GetValue<long>()
        );
        Assert.Equal(
            origHost["backend"]?.ToString(),
            recompHost["backend"]?.ToString()
        );

        var origViews = Assert.IsType<JsonObject>(@object: originalNode["views"]);
        var recompViews = Assert.IsType<JsonObject>(@object: recompJsonObject["views"]);

        var origLayouts = Assert.IsType<JsonArray>(@object: origViews["layouts"]);
        var recompLayouts = Assert.IsType<JsonArray>(@object: recompViews["layouts"]);

        Assert.Equal(
            origLayouts.Count,
            recompLayouts.Count
        );

        var origRig = Assert.IsType<JsonObject>(@object: origViews["seatRig"]);
        var recompRig = Assert.IsType<JsonObject>(@object: recompViews["seatRig"]);

        Assert.Equal(
            origRig["name"]?.ToString(),
            recompRig["name"]?.ToString()
        );
        Assert.Equal(
            origRig["version"]?.ToString(),
            recompRig["version"]?.ToString()
        );

        var origOps = Assert.IsType<JsonArray>(@object: origRig["operations"]);
        var recompOps = Assert.IsType<JsonArray>(@object: recompRig["operations"]);

        Assert.Equal(
            origOps.Count,
            recompOps.Count
        );
        Assert.Equal(
            origOps[0]?["$type"]?.ToString(),
            recompOps[0]?["$type"]?.ToString()
        );
        Assert.Equal(
            origOps[0]?["distance"].AsNumber(),
            recompOps[0]?["distance"].AsNumber()
        );
    }
    [Fact]
    public void TestSolitaireWorldDecompilationAndRoundTrip() {
        var originalNode = ShippedWorlds.Compile(relativePath: "games/solitaire.puck").RequireJson();
        var originalJson = originalNode.ToJsonString();

        Assert.NotNull(@object: originalNode);

        // Decompile
        var puck = WorldDecompiler.Decompile(root: originalNode);

        Assert.Contains(
            actualString: puck,
            expectedSubstring: "import \"klondike\""
        );
        Assert.Contains(
            actualString: puck,
            expectedSubstring: "export action"
        );
        Assert.Contains(
            actualString: puck,
            expectedSubstring: "export binding"
        );
        Assert.Contains(
            actualString: puck,
            expectedSubstring: "export read"
        );

        // Recompile
        var recomp = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: puck
        ).RequireJson();

        // Verify imports and exports preserved
        var imports = Assert.IsType<JsonArray>(@object: recomp["imports"]);

        Assert.Equal(
            3,
            imports.Count
        );

        var exports = Assert.IsType<JsonObject>(@object: recomp["exports"]);
        var actions = Assert.IsType<JsonArray>(@object: exports["actions"]);

        Assert.Equal(
            4,
            actions.Count
        );
        Assert.Contains(
            collection: actions,
            filter: a => (a?.ToString() == "solitaire")
        );
        Assert.Contains(
            collection: actions,
            filter: a => (a?.ToString() == "solitaireKlondike")
        );
    }
}
