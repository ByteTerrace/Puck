using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Server.Tests;

public sealed class RiserEdgeLawTests {
    private static WorldDefinition Document(SdfSolidPrimitive type, float stepHeight, float chamfer) {
        var document = Fixtures.BuildDocument();
        var canonical = CreationCanonicalizer.Canonicalize(document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema, Name: "riser", Palette: null, Frames: null,
            Shapes: [
                new ShapeDocument(Id: 0, Name: "ground", Type: SdfSolidPrimitive.Box,
                    Position: new Vector3(x: 0f, y: -0.5f, z: 0f), Rotation: Quaternion.Identity, Scale: new Vector3(x: 20f, y: 0.5f, z: 20f),
                    Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0),
                new ShapeDocument(Id: 1, Name: "riser", Type: type,
                    Position: new Vector3(x: 0f, y: 0.05f, z: 3.5f), Rotation: ((type == SdfSolidPrimitive.Prism)
                        ? Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.35f) : Quaternion.Identity), Scale: new Vector3(x: 2f, y: 0.05f, z: 2f),
                    Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0, Chamfer: chamfer),
            ]), source: "riser");
        var kits = document.Kits.ToArray();

        kits[0] = kits[0] with {
            Collider = new WorldCollider.Capsule(Endpoint: Vector3.UnitY, Radius: 0.35f),
            Motion = kits[0].Motion! with { Speed = new WorldSpeed(Value: 1f), StepHeight = stepHeight },
        };
        return document with {
            CreationsRaw = [new WorldPrototype(Id: "riser", Document: canonical.Document, HashRaw: canonical.Hash)],
            KitRowsRaw = kits,
            PlacementsRaw = document.PlacementsRaw! with {
                Rows = [new WorldPlacement(Id: "riser", PrototypeId: "riser", Position: Vector3.Zero,
                    YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            },
        };
    }

    // Prism deliberately exercises the unchanged sharp oriented-bound vocabulary. The authored Box and circular
    // cylinder exercise exact rounded-edge and rim witnesses through the real WorldBody step controller.
    [InlineData(SdfSolidPrimitive.Box, 0f, 0f)]
    [InlineData(SdfSolidPrimitive.Box, 0.05f, 0f)]
    [InlineData(SdfSolidPrimitive.Box, 0.1f, 0f)]
    [InlineData(SdfSolidPrimitive.Box, 0.099f, 0f)]
    [InlineData(SdfSolidPrimitive.Box, 0.3f, 0f)]
    [InlineData(SdfSolidPrimitive.Box, 0f, 0.005f)]
    [InlineData(SdfSolidPrimitive.Box, 0.05f, 0.005f)]
    [InlineData(SdfSolidPrimitive.Box, 0.3f, 0.005f)]
    [InlineData(SdfSolidPrimitive.Cylinder, 0f, 0f)]
    [InlineData(SdfSolidPrimitive.Cylinder, 0.05f, 0f)]
    [InlineData(SdfSolidPrimitive.Cylinder, 0.1f, 0f)]
    [InlineData(SdfSolidPrimitive.Cylinder, 0.099f, 0f)]
    [InlineData(SdfSolidPrimitive.Cylinder, 0.3f, 0f)]
    [InlineData(SdfSolidPrimitive.Prism, 0f, 0f)]
    [InlineData(SdfSolidPrimitive.Prism, 0.05f, 0f)]
    [InlineData(SdfSolidPrimitive.Prism, 0.1f, 0f)]
    [InlineData(SdfSolidPrimitive.Prism, 0.099f, 0f)]
    [InlineData(SdfSolidPrimitive.Prism, 0.3f, 0f)]
    [Theory]
    public void AShallowRiserOrRimRequiresEnoughStepHeight(SdfSolidPrimitive type, float stepHeight, float chamfer) {
        using var fixture = Fixtures.FreshServer(definition: Document(chamfer: chamfer, stepHeight: stepHeight, type: type));
        var body = fixture.JoinSeat();

        body.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 0f, yawRadians: 0f, z: 0f);
        for (var tick = 0; (tick < (Fixtures.DefaultRateHz * 3)); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One));
            fixture.Step();
        }
        Assert.True(condition: body.Grounded);
        if (stepHeight >= 0.1f) {
            Assert.True(condition: ((body.FixedPosition.Y > FixedQ4816.FromDouble(value: 0.08)) &&
                (body.FixedPosition.Z < FixedQ4816.FromInteger(value: -2))), userMessage: $"{body.FixedPosition}");
        } else {
            Assert.True(condition: ((body.FixedPosition.Y < FixedQ4816.FromDouble(value: 0.05)) &&
                (body.FixedPosition.Z > FixedQ4816.FromDouble(value: -1.5))), userMessage: $"{body.FixedPosition}");
        }
    }
}
