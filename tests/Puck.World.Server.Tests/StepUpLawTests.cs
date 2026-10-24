using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A walking kit's <c>motion.stepHeight</c> through the whole step: a body driven slowly at a 10 cm riser on
/// the analytic provider, which reads a riser's face as a wall, walks up onto it only while its kit authors a step
/// height.</summary>
public sealed class StepUpLawTests {
    private static ShapeDocument Box(int id, string name, Vector3 position, Vector3 halfExtents) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Box,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: halfExtents,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    // The author frame is read through the half-turn (-x, y, -z): the riser authored at +z stands ahead of a body at
    // yaw 0, which faces -Z, its near face 1.5 m from the origin.
    private static WorldDefinition Document(float stepHeight, bool ramp = false) {
        var document = Fixtures.BuildDocument();
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "kerb",
                Palette: null,
                Shapes: [
                    Box(
                        halfExtents: new Vector3(x: 20f, y: 0.5f, z: 20f),
                        id: 0,
                        name: "ground",
                        position: new Vector3(x: 0f, y: -0.5f, z: 0f)
                    ),
                    Box(
                        halfExtents: new Vector3(x: 2f, y: 0.05f, z: 2f),
                        id: 1,
                        name: "kerb",
                        position: new Vector3(x: 0f, y: 0.05f, z: 3.5f)
                    ) with {
                        Rotation = (ramp ? Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (MathF.PI / 12f)) : Quaternion.Identity),
                        Position = new Vector3(x: 0f, y: (ramp ? 0.3876f : 0.05f), z: 3.5f),
                        Scale = (ramp ? new Vector3(x: 2.0189f, y: 0.15f, z: 2f) : new Vector3(x: 2f, y: 0.05f, z: 2f)),
                    },
                ],
                Frames: null
            ),
            source: "kerb"
        );
        var kits = document.Kits.ToList();

        // A slow walker: one tick's reach into the riser stays shallower than its height, so the contact solve pushes
        // the body back off the face rather than over it.
        kits[0] = (kits[0] with {
            Collider = new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f),
            Motion = (kits[0].Motion! with {
                Speed = new WorldSpeed(Value: 1f),
                StepHeight = stepHeight,
            }),
        });

        return document with {
            CreationsRaw = [new WorldPrototype(
                Id: "kerb",
                Document: canonical.Document,
                HashRaw: canonical.Hash
            )],
            KitRowsRaw = kits,
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [
                    new WorldPlacement(
                        Id: "kerb",
                        PrototypeId: "kerb",
                        Position: Vector3.Zero,
                        YawDegrees: 0f,
                        Scale: 1f,
                        Solid: new WorldSolid(Margin: 0f)
                    ),
                ],
            }),
        };
    }
    private static FixedVector3 Walk(float stepHeight, bool ramp = false) {
        using var fixture = Fixtures.FreshServer(definition: Document(ramp: ramp, stepHeight: stepHeight));
        var body = fixture.JoinSeat();

        body.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: (ramp ? 1.7f : 0f),
            y: 0f,
            yawRadians: 0f,
            z: 0f
        );

        for (var tick = 0; (tick < (Fixtures.DefaultRateHz * 3)); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                ordinal: 0,
                value: FixedQ4816.One
            ));
            fixture.Step();
        }

        Assert.True(
            condition: body.Grounded,
            userMessage: $"the walker should end standing; it is at {body.FixedPosition}"
        );

        return body.FixedPosition;
    }

    [Fact]
    public void AWalkerStepsUpOntoARiserNoTallerThanItsStepHeightAndOnlyThen() {
        var stepped = Walk(stepHeight: 0.3f);
        var blocked = Walk(stepHeight: 0f);

        Assert.True(
            condition: ((stepped.Y > FixedQ4816.FromDouble(value: 0.08)) && (stepped.Z < FixedQ4816.FromDouble(value: -2))),
            userMessage: $"a 0.3 m step height should carry the walker onto the riser's top; it is at {stepped}"
        );
        Assert.True(
            condition: ((blocked.Y < FixedQ4816.FromDouble(value: 0.05)) && (blocked.Z > FixedQ4816.FromDouble(value: -1.5))),
            userMessage: $"with no step height the riser's face should stop the walker; it is at {blocked}"
        );
    }
    [Fact]
    public void AWalkerStepsSidewaysOntoARampsLowEdgeBelowItsGlobalApex() {
        var stepped = Walk(ramp: true, stepHeight: 0.3f);

        Assert.InRange(((double)stepped.Y), 0.04, 0.3);
        Assert.True(condition: (stepped.Z < FixedQ4816.FromDouble(value: -2)),
            userMessage: $"the ramp's low side should admit the 0.3 m step; the walker is at {stepped}");
    }
}
