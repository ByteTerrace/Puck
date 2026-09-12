using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class EmitterTests {
    [Fact]
    public void TestMinimalSyntheticWorldEmission() {
        const string source = """
            schema: "puck.world.def.v1"
            basis: "worlds/standard.basis.json"

            host {
                width: 1280
                height: 720
                fullscreen: false
                targetHertz: 60
            }
            """;

        var doc = PuckParser.ParseDocument(source);
        var jsonObj = WorldDocumentEmitter.Lower(doc);

        Assert.Equal("puck.world.def.v1", jsonObj["schema"]?.ToString());
        Assert.Equal("worlds/standard.basis.json", jsonObj["basis"]?.ToString());

        var host = Assert.IsType<JsonObject>(jsonObj["host"]);
        Assert.Equal(1280L, host["width"]?.GetValue<long>());
        Assert.Equal(720L, host["height"]?.GetValue<long>());
        Assert.False(host["fullscreen"]?.GetValue<bool>());
        Assert.Equal(60L, host["targetHertz"]?.GetValue<long>());

        var canonicalJson = WorldDocumentEmitter.CompileToJson(doc);
        Assert.Contains("\"schema\": \"puck.world.def.v1\"", canonicalJson);
        Assert.Contains("\"width\": 1280", canonicalJson);
        Assert.EndsWith("\n", canonicalJson);
    }

    [Fact]
    public void TestStudyWorldParity() {
        const string puckStudy = """
            schema: "puck.world.def.v1"

            host {
                authority: null
                backend: "auto"
                exitAfterSeconds: 0
                fullscreen: false
                genlock: null
                height: 900
                journalDepth: 2000
                listen: null
                presentMode: "Immediate"
                presentation: "Windowed"
                rayQuery: true
                surfaceFormat: "r8g8b8a8"
                targetHertz: 60
                timing: false
                width: 1280
            }

            views {
                layout "study" {
                    seatCount: 0
                    slots [
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
                    transitionSeconds: 0.25
                }

                seatControl {
                    maxPitch: 0.01
                    minPitch: -0.01
                    yawReference: "World"
                }

                seatRig "study" {
                    version: "puck.camera.v1"
                    operations [
                        orbit(distance: 0.01, pitch: 0, yaw: 0)
                        fieldOfView(fieldOfViewRadians: 0.001)
                    ]
                }

                study "moth-study" {
                    camera: null
                    source: "../studies/moth.glsl"
                    timeScale: 1
                }
            }
            """;

        var doc = PuckParser.ParseDocument(puckStudy);
        var lowered = WorldDocumentEmitter.Lower(doc);

        var views = Assert.IsType<JsonObject>(lowered["views"]);
        var layouts = Assert.IsType<JsonArray>(views["layouts"]);
        Assert.Single(layouts);
        var layout0 = Assert.IsType<JsonObject>(layouts[0]);
        Assert.Equal("study", layout0["name"]?.ToString());
        Assert.Equal(0.25, layout0["transitionSeconds"]?.GetValue<double>());

        var seatRig = Assert.IsType<JsonObject>(views["seatRig"]);
        Assert.Equal("study", seatRig["name"]?.ToString());
        Assert.Equal("puck.camera.v1", seatRig["version"]?.ToString());

        var ops = Assert.IsType<JsonArray>(seatRig["operations"]);
        Assert.Equal(2, ops.Count);
        var op0 = Assert.IsType<JsonObject>(ops[0]);
        Assert.Equal("orbit", op0["$type"]?.ToString());
        Assert.Equal(0.01, op0["distance"]?.GetValue<double>());

        var op1 = Assert.IsType<JsonObject>(ops[1]);
        Assert.Equal("fieldOfView", op1["$type"]?.ToString());
        Assert.Equal(0.001, op1["fieldOfViewRadians"]?.GetValue<double>());
    }

    [Fact]
    public void TestTemplateExpansionAndLetSubstitution() {
        const string source = """
            schema: "puck.world.def.v1"

            let defaultGravity = [0, -9.81, 0]
            let interval = 0.25s

            template spawnRig(rigId, rigPitch = 0rad) {
                seatRig rigId {
                    pitchRadians: rigPitch
                }
            }

            host {
                tickIntervalSeconds: interval
                physics {
                    gravity: defaultGravity
                }
            }

            views {
                spawnRig("pilot", 45deg)
            }
            """;

        var doc = PuckParser.ParseDocument(source);
        var lowered = WorldDocumentEmitter.Lower(doc);

        var host = Assert.IsType<JsonObject>(lowered["host"]);
        Assert.Equal(0.25, host["tickIntervalSeconds"]?.GetValue<double>());

        var physics = Assert.IsType<JsonObject>(host["physics"]);
        var grav = Assert.IsType<JsonArray>(physics["gravity"]);
        Assert.Equal(3, grav.Count);
        Assert.Equal(-9.81, grav[1]?.GetValue<double>());

        var views = Assert.IsType<JsonObject>(lowered["views"]);
        var seatRig = Assert.IsType<JsonObject>(views["seatRig"]);
        Assert.Equal("pilot", seatRig["name"]?.ToString());
        Assert.True(seatRig["pitchRadians"]?.GetValue<double>() > 0.78);
    }

    [Fact]
    public void TestAddonAutoHashing() {
        const string source = """
            schema: "puck.world.def.v1"

            addon "test-addon" {
                source: "addons/game.wasm"
                hash: "auto"
            }
            """;

        var doc = PuckParser.ParseDocument(source);
        var lowered = WorldDocumentEmitter.Lower(doc);

        var addons = Assert.IsType<JsonArray>(lowered["addons"]);
        Assert.Single(addons);
        var addon0 = Assert.IsType<JsonObject>(addons[0]);
        var hash = addon0["hash"]?.ToString();
        Assert.NotNull(hash);
        Assert.StartsWith("sha256-64/", hash);
        Assert.Equal(26, hash.Length); // "sha256-64/" is 10 chars + 16 hex = 26 chars
    }
}
