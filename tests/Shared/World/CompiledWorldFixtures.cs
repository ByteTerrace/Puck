using Puck.Abstractions;
using Puck.Testing;

namespace Puck.World.Testing;

/// <summary>The compiled-world fixture: a world with a drawn site and a patch row, written beside its patch, compiled
/// and booted through the compiled-world cache.</summary>
internal static partial class CompiledWorldFixtures {
    // A world with a first-fill draw site, so DEFN carries drawn cells, and a patch row, so ASST reads a file.
    internal const string World = """
        {
          "schema": "puck.world.definition.v1",
          "documentId": "compiled-world-law",
          "bodies": { "localSeats": 1 },
          "simulation": { "rateHz": 240 },
          "spawnPoints": [ { "id": "origin", "position": [ 0, 0, 0 ] } ],
          "channels": [
            { "name": "forward", "shape": "Bipolar", "role": "MoveAdvance" },
            { "name": "strafe", "shape": "Bipolar", "role": "MoveStrafe" },
            { "name": "turn", "shape": "Bipolar", "role": "Turn" }
          ],
          "collision": { "requirements": [], "contactSkin": 0.02, "maxIterations": 4, "maxSlopeDegrees": 60, "gradientProbe": 0 },
          "bodyMotionPrograms": [
            {
              "name": "grounded",
              "version": "puck.body.program.v1",
              "kind": "Motion",
              "operations": [ "ResolveYawAttitudeAndPlanarFrame", "ResolveHold", "ComputePlanarTargetVelocity", "ShapeVelocity", "SnapYawToPlanarIntent", "ApplyHold", "IntegratePlanarAndVerticalVelocity", "CommitPose" ]
            }
          ],
          "kits": {
            "rows": [
              {
                "name": "idle",
                "bodyMotionProgram": "grounded",
                "motion": {
                  "speed": { "value": 4 },
                  "turn": { "rate": 2 },
                  "shaping": [ { "along": {} } ],
                  "holds": [
                    { "name": "ground", "bond": "Surface", "cone": [ 0, 60 ], "hold": "Gravity", "reach": 1.2, "gravity": { "rise": 20, "fall": 30 }, "envelope": { "sinkSpeed": 30 } },
                    { "name": "air", "bond": "Free", "hold": "Gravity", "gravity": { "rise": 20, "fall": 30 }, "envelope": { "sinkSpeed": 30 } }
                  ]
                }
              }
            ]
          },
          "defaultSeatKit": "idle",
          "views": {
            "layouts": [],
            "seatControl": { "yawReference": "World", "minPitch": -0.35, "maxPitch": 1.2 },
            "seatRig": {
              "name": "seatChase",
              "version": "puck.camera.program.v1",
              "operations": [
                { "$type": "orbit", "distance": 5.4626001, "yaw": 0, "pitch": 0.4145069, "pivotOffset": [ 0, 0, 0 ] },
                { "$type": "lookAt", "subject": { "$type": "reference" }, "targetOffset": [ 0, 1, 0 ], "worldAxes": false },
                { "$type": "fieldOfView", "fieldOfViewRadians": 0.9599311 }
              ]
            }
          },
          "state": {
            "world": [
              { "name": "tiles", "kind": "Fixed", "field": { "initial": 0, "min": 0, "max": 4, "heightScale": 0, "paint": [ { "$type": "draw", "generator": { "source": "WeightedNumeric", "mode": "RestartOnExhaustion", "weighted": [ { "value": 16384, "weight": 1, "multiplicity": 2 }, { "value": 32768, "weight": 1 }, { "value": 65536, "weight": 1 } ] } } ] }, "domain": { "$type": "cellsOf", "topology": "grid" } }
            ],
            "lattices": [
              { "$type": "field", "name": "grid", "origin": [ 0, 0, 0 ], "cellSize": 1, "width": 4, "depth": 1, "layers": 1, "stepEveryTicks": 1, "reactions": [] }
            ]
          },
          "patches": [
            { "name": "stinger", "source": "PATCH", "hash": "5d8f53b2714ddc863bf96e4817055838e3b72d705cba99bb050ccd6a00c472b0" }
          ]
        }
        """;

    internal static void WritePatchBeside(TemporaryDirectory directory) {
        if (!File.Exists(path: directory.PathOf(name: PatchBeside))) {
            _ = directory.WriteBytes(bytes: File.ReadAllBytes(path: ShippedPatch()), name: PatchBeside);
        }
    }

    // The patch file every world here names beside itself: a row's source resolves beside its document.
    internal const string PatchBeside = "patches/stinger.synth.json";

    internal static string ShippedPatch() => PuckPaths.Shipped(relativePath: "Assets/worlds/patches/stinger.synth.json");
}
