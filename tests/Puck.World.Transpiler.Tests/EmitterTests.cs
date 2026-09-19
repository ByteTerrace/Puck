using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class EmitterTests {
    [Fact]
    public void TestAddonAutoHashing() {
        const string Source = """
            schema: "puck.world.definition.v1"

            addon "test-addon" {
                source: "addons/game.wasm"
                hash: "auto"
            }
            """;

        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Source
        ).RequireJson();

        var addons = Assert.IsType<JsonArray>(@object: lowered["addons"]);

        Assert.Single(collection: addons);
        var addon0 = Assert.IsType<JsonObject>(@object: addons[0]);
        var hash = addon0["hash"]?.ToString();

        Assert.NotNull(@object: hash);
        Assert.StartsWith(
            actualString: hash,
            expectedStartString: "sha256-64/"
        );
        Assert.Equal(
            26,
            hash.Length
        ); // "sha256-64/" is 10 chars + 16 hex = 26 chars
    }
    [Fact]
    public void TestMinimalSyntheticWorldEmission() {
        const string Source = """
            schema: "puck.world.definition.v1"
            basis: "worlds/standard.basis.json"

            host {
                width: 1280
                height: 720
                fullscreen: false
                targetHertz: 60
            }
            """;

        var jsonObj = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Source
        ).RequireJson();

        Assert.Equal(
            "puck.world.definition.v1",
            jsonObj["schema"]?.ToString()
        );
        Assert.Equal(
            "worlds/standard.basis.json",
            jsonObj["basis"]?.ToString()
        );

        var host = Assert.IsType<JsonObject>(@object: jsonObj["host"]);

        Assert.Equal(
            1280L,
            host["width"]?.GetValue<long>()
        );
        Assert.Equal(
            720L,
            host["height"]?.GetValue<long>()
        );
        Assert.False(condition: host["fullscreen"]?.GetValue<bool>());
        Assert.Equal(
            60L,
            host["targetHertz"]?.GetValue<long>()
        );

        var canonicalJson = Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: jsonObj));

        Assert.Contains(
            actualString: canonicalJson,
            expectedSubstring: "\"schema\": \"puck.world.definition.v1\""
        );
        Assert.Contains(
            actualString: canonicalJson,
            expectedSubstring: "\"width\": 1280"
        );
        Assert.EndsWith(
            actualString: canonicalJson,
            expectedEndString: "\n"
        );
    }
    [Fact]
    public void TestPipelineWorldParity() {
        const string PuckPipeline = """
            schema: "puck.world.definition.v1"

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
                layout "pipeline" {
                    seatCount: 0
                    slots [
                        {
                            camera: null
                            height: 1
                            pipeline: "moth-pipeline"
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

                seatRig "pipeline" {
                    version: "puck.camera.program.v1"
                    operations [
                        orbit(distance: 0.01, pitch: 0, yaw: 0)
                        fieldOfView(fieldOfViewRadians: 0.001)
                    ]
                }

                pipeline "moth-pipeline" {
                    camera: null
                    source: "../pipelines/moth.glsl"
                    timeScale: 1
                }
            }
            """;

        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: PuckPipeline
        ).RequireJson();

        var views = Assert.IsType<JsonObject>(@object: lowered["views"]);
        var layouts = Assert.IsType<JsonArray>(@object: views["layouts"]);

        Assert.Single(collection: layouts);
        var layout0 = Assert.IsType<JsonObject>(@object: layouts[0]);

        Assert.Equal(
            "pipeline",
            layout0["name"]?.ToString()
        );
        Assert.Equal(
            0.25,
            layout0["transitionSeconds"].AsNumber()
        );

        var seatRig = Assert.IsType<JsonObject>(@object: views["seatRig"]);

        Assert.Equal(
            "pipeline",
            seatRig["name"]?.ToString()
        );
        Assert.Equal(
            "puck.camera.program.v1",
            seatRig["version"]?.ToString()
        );

        var ops = Assert.IsType<JsonArray>(@object: seatRig["operations"]);

        Assert.Equal(
            2,
            ops.Count
        );
        var op0 = Assert.IsType<JsonObject>(@object: ops[0]);

        Assert.Equal(
            "orbit",
            op0["$type"]?.ToString()
        );
        Assert.Equal(
            0.01,
            op0["distance"].AsNumber()
        );

        var op1 = Assert.IsType<JsonObject>(@object: ops[1]);

        Assert.Equal(
            "fieldOfView",
            op1["$type"]?.ToString()
        );
        Assert.Equal(
            0.001,
            op1["fieldOfViewRadians"].AsNumber()
        );
    }
    [Fact]
    public void TestTemplateExpansionAndLetSubstitution() {
        const string Source = """
            schema: "puck.world.definition.v1"

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

        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Source
        ).RequireJson();

        var host = Assert.IsType<JsonObject>(@object: lowered["host"]);

        Assert.Equal(
            0.25,
            host["tickIntervalSeconds"].AsNumber()
        );

        var physics = Assert.IsType<JsonObject>(@object: host["physics"]);
        var grav = Assert.IsType<JsonArray>(@object: physics["gravity"]);

        Assert.Equal(
            3,
            grav.Count
        );
        Assert.Equal(
            -9.81,
            grav[1].AsNumber()
        );

        var views = Assert.IsType<JsonObject>(@object: lowered["views"]);
        var seatRig = Assert.IsType<JsonObject>(@object: views["seatRig"]);

        Assert.Equal(
            "pilot",
            seatRig["name"]?.ToString()
        );
        Assert.True(condition: (seatRig["pitchRadians"].AsNumber() > 0.78));
    }
}
