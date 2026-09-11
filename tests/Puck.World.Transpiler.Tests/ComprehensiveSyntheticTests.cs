using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ComprehensiveSyntheticTests {
    [Fact]
    public void TestComprehensiveNookAndCrannySynthesis() {
        const string comprehensivePuck = """
            schema: "puck.world.def.v1"
            basis: "worlds/standard.basis.json"
            documentId: "comprehensive-synthetic-v1"

            // Compile-time constants
            let defaultGravity = [0, -9.81, 0]
            let tickRate = 0.25s
            let targetFps = 60hz
            let turnAngle = 45deg
            let zeroAngle = 0rad
            let feltColor = #1b4d3e
            let accentColor = #ffaa00ff
            let doubleFps = 60 * 2

            // Module imports
            import "extensions/rules.world.puck" as rules
            import "modules/arcade.world.json"

            // Facet exports
            export read state, scores, tableStatus
            export action step, reset, deal
            export binding input, pointer, pad

            // Parametric templates
            template seatRigPreset(rigName, angle = 0rad, dist = 2.5m) {
                seatRig rigName {
                    version: "puck.camera.v1"
                    operations: [
                        orbit(distance: dist, pitch: angle, yaw: 0)
                        fov(fieldOfViewRadians: 0.001)
                    ]
                }
            }

            // Host settings block
            host {
                authority: null
                backend: "auto"
                exitAfterSeconds: 0
                fullscreen: false
                genlock: null
                height: 720
                journalDepth: 2000
                listen: null
                presentMode: "Immediate"
                presentation: "Windowed"
                rayQuery: true
                surfaceFormat: "r8g8b8a8"
                targetHertz: targetFps
                timing: false
                width: 1280
            }

            // Views section
            views {
                layout "arena" {
                    seatCount: 2
                    slots: [
                        {
                            camera: null
                            height: 1
                            study: "moth-study"
                            width: 1
                            x: 0
                            y: 0
                        }
                    ]
                    transitionRenderScale: 1
                    transitionSeconds: tickRate
                }

                seatControl {
                    maxPitch: 0.01
                    minPitch: -0.01
                    yawReference: "World"
                }

                seatRigPreset("pilot", turnAngle, 2.5m)

                study "moth-study" {
                    camera: null
                    source: "../studies/moth.glsl"
                    timeScale: 1
                }
            }

            // Addon with auto-hashing
            addon "rules-engine" {
                source: "addons/rules.wasm"
                hash: "auto"
            }

            // State catalog with world cells
            state {
                world: [
                    {
                        name: "tableState"
                        kind: "Int"
                        capacity: 1
                        cells: [
                            {
                                key: "feltColor"
                                value: feltColor
                            }
                            {
                                key: "activeRound"
                                value: 1
                            }
                        ]
                    }
                ]
            }
            """;

        // 1. Parse .puck into AST
        var ast = PuckParser.ParseDocument(comprehensivePuck);
        Assert.Equal("puck.world.def.v1", ast.Schema);
        Assert.Equal("worlds/standard.basis.json", ast.Basis);

        // 2. Lower AST into JsonObject
        var json = WorldDocumentEmitter.Lower(ast);

        // Assert schema, basis, documentId
        Assert.Equal("puck.world.def.v1", json["schema"]?.ToString());
        Assert.Equal("worlds/standard.basis.json", json["basis"]?.ToString());
        Assert.Equal("comprehensive-synthetic-v1", json["documentId"]?.ToString());

        // Assert imports
        var imports = Assert.IsType<JsonArray>(json["imports"]);
        Assert.Equal(2, imports.Count);
        var imp0 = Assert.IsType<JsonObject>(imports[0]);
        Assert.Equal("extensions/rules.world.puck", imp0["document"]?.ToString());
        Assert.Equal("rules", imp0["as"]?.ToString());

        // Assert exports
        var exports = Assert.IsType<JsonObject>(json["exports"]);
        var reads = Assert.IsType<JsonArray>(exports["reads"]);
        Assert.Equal(3, reads.Count);
        Assert.Contains(reads, r => r?.ToString() == "tableStatus");

        var actions = Assert.IsType<JsonArray>(exports["actions"]);
        Assert.Equal(3, actions.Count);
        Assert.Contains(actions, a => a?.ToString() == "deal");

        // Assert host evaluations
        var host = Assert.IsType<JsonObject>(json["host"]);
        Assert.Equal(60L, host["targetHertz"]?.GetValue<long>());
        Assert.Equal(1280L, host["width"]?.GetValue<long>());
        Assert.Equal(720L, host["height"]?.GetValue<long>());

        // Assert views and template expansion
        var views = Assert.IsType<JsonObject>(json["views"]);
        var layouts = Assert.IsType<JsonArray>(views["layouts"]);
        Assert.Single(layouts);
        var layout0 = Assert.IsType<JsonObject>(layouts[0]);
        Assert.Equal("arena", layout0["name"]?.ToString());
        Assert.Equal(0.25, layout0["transitionSeconds"]?.GetValue<double>());

        var seatRig = Assert.IsType<JsonObject>(views["seatRig"]);
        Assert.Equal("pilot", seatRig["name"]?.ToString());
        var ops = Assert.IsType<JsonArray>(seatRig["operations"]);
        Assert.Equal(2, ops.Count);
        var orbitOp = Assert.IsType<JsonObject>(ops[0]);
        Assert.Equal("orbit", orbitOp["$type"]?.ToString());
        Assert.Equal(2.5, orbitOp["distance"]?.GetValue<double>());
        Assert.True(orbitOp["pitch"]?.GetValue<double>() > 0.78); // 45 deg in rad

        // Assert addon auto-hash
        var addons = Assert.IsType<JsonArray>(json["addons"]);
        Assert.Single(addons);
        var addon0 = Assert.IsType<JsonObject>(addons[0]);
        Assert.Equal("rules-engine", addon0["name"]?.ToString());
        var addonHash = addon0["hash"]?.ToString();
        Assert.NotNull(addonHash);
        Assert.StartsWith("sha256-64/", addonHash);
        Assert.Equal(26, addonHash.Length);

        // Assert state world cells
        var state = Assert.IsType<JsonObject>(json["state"]);
        var worldArr = Assert.IsType<JsonArray>(state["world"]);
        Assert.Single(worldArr);
        var tableState = Assert.IsType<JsonObject>(worldArr[0]);
        Assert.Equal("tableState", tableState["name"]?.ToString());
        var cells = Assert.IsType<JsonArray>(tableState["cells"]);
        Assert.Equal(2, cells.Count);
        Assert.Equal("#1b4d3e", cells[0]?["value"]?.ToString());

        // 3. Compile to canonical JSON
        var canonicalJson = WorldDocumentEmitter.CompileToJson(ast);
        Assert.NotEmpty(canonicalJson);

        // 4. Decompile back to .puck
        var decompiledPuck = WorldDecompiler.Decompile(json);
        Assert.NotEmpty(decompiledPuck);
        Assert.Contains("schema: \"puck.world.def.v1\"", decompiledPuck);
        Assert.Contains("basis: \"worlds/standard.basis.json\"", decompiledPuck);
        Assert.Contains("documentId: \"comprehensive-synthetic-v1\"", decompiledPuck);
        Assert.Contains("import \"extensions/rules.world.puck\" as rules", decompiledPuck);
        Assert.Contains("export read state, scores, tableStatus", decompiledPuck);
        Assert.Contains("host {", decompiledPuck);
        Assert.Contains("views {", decompiledPuck);
        Assert.Contains("seatRig \"pilot\" {", decompiledPuck);
        Assert.Contains("orbit(distance: 2.5", decompiledPuck);

        // 5. Round-trip: transpile decompiled .puck back to JSON
        var roundTripAst = PuckParser.ParseDocument(decompiledPuck);
        var roundTripJson = WorldDocumentEmitter.Lower(roundTripAst);

        // 6. Canonical JSON equality
        var roundTripCanonical = WorldDocumentEmitter.CompileToJson(roundTripAst);
        Assert.Equal(
            WorldDocumentEmitter.CompileToJson(PuckParser.ParseDocument(WorldDecompiler.Decompile(roundTripJson))),
            roundTripCanonical
        );
    }

    [Fact]
    public void TestCreationDocumentDecompileAndRoundTrip() {
        // `solid`/"solids" targeted keys CreationDocument never carried ("kind" among them); the sugar wave renames
        // the collector in place to `shape`/"shapes", CreationDocument.Shapes' own key, with "type" replacing "kind"
        // (§4.1, §0-A6). This test now spells the renamed sugar with real SdfSolidPrimitive names.
        const string creationPuck = """
            schema: "puck.creation.v1"
            documentId: "chess-board-creation"

            material "wood" {
                roughness: 0.35
                metallic: 0.0
                albedo: #8b5a2b
            }

            shape Box "board" {
                dimensions: [1.2, 0.05, 1.2]
                material: "wood"
            }

            shape Superellipsoid "cushion" {
                radii: [0.1, 0.05, 1.2]
                exponents: [0.2, 0.2]
            }
            """;

        var ast = PuckParser.ParseDocument(creationPuck, defaultSchema: "puck.creation.v1");
        var json = WorldDocumentEmitter.Lower(ast);

        Assert.Equal("puck.creation.v1", json["schema"]?.ToString());
        Assert.Equal("chess-board-creation", json["documentId"]?.ToString());

        var materials = Assert.IsType<JsonArray>(json["materials"]);
        Assert.Single(materials);
        var mat0 = Assert.IsType<JsonObject>(materials[0]);
        Assert.Equal("wood", mat0["name"]?.ToString());
        Assert.Equal("#8b5a2b", mat0["albedo"]?.ToString());

        var shapes = Assert.IsType<JsonArray>(json["shapes"]);
        Assert.Equal(2, shapes.Count);
        var shape0 = Assert.IsType<JsonObject>(shapes[0]);
        Assert.Equal("board", shape0["name"]?.ToString());
        Assert.Equal("Box", shape0["type"]?.ToString());

        var shape1 = Assert.IsType<JsonObject>(shapes[1]);
        Assert.Equal("cushion", shape1["name"]?.ToString());
        Assert.Equal("Superellipsoid", shape1["type"]?.ToString());

        // Decompile
        var decompiled = WorldDecompiler.Decompile(json);
        Assert.Contains("material \"wood\" {", decompiled);

        // Round-trip
        var recompAst = PuckParser.ParseDocument(decompiled, defaultSchema: "puck.creation.v1");
        var recompJson = WorldDocumentEmitter.Lower(recompAst);
        Assert.Equal(
            WorldDocumentEmitter.CompileToJson(recompAst),
            WorldDocumentEmitter.CompileToJson(ast)
        );
    }

    [Fact]
    public void TestCartridgeDocumentRoundTrip() {
        const string cartPuck = """
            schema: "puck.cartridge.v1"
            documentId: "tetris-cart"

            cartridge {
                title: "Tetris AGB"
                rom: "roms/tetris.agb"
                hash: "auto"
                saveType: "Sram32k"
            }
            """;

        var ast = PuckParser.ParseDocument(cartPuck, defaultSchema: "puck.cartridge.v1");
        var json = WorldDocumentEmitter.Lower(ast);

        Assert.Equal("puck.cartridge.v1", json["schema"]?.ToString());
        Assert.Equal("tetris-cart", json["documentId"]?.ToString());

        var cart = Assert.IsType<JsonObject>(json["cartridge"]);
        Assert.Equal("Tetris AGB", cart["title"]?.ToString());
        Assert.Equal("roms/tetris.agb", cart["rom"]?.ToString());
        var hash = cart["hash"]?.ToString();
        Assert.NotNull(hash);
        Assert.StartsWith("sha256-64/", hash);

        // Round-trip
        var decompiled = WorldDecompiler.Decompile(json);
        Assert.Contains("schema: \"puck.cartridge.v1\"", decompiled);
        Assert.Contains("cartridge {", decompiled);
        Assert.Contains("title: \"Tetris AGB\"", decompiled);

        var recompAst = PuckParser.ParseDocument(decompiled, defaultSchema: "puck.cartridge.v1");
        Assert.Equal(
            WorldDocumentEmitter.CompileToJson(recompAst),
            WorldDocumentEmitter.CompileToJson(ast)
        );
    }
}

