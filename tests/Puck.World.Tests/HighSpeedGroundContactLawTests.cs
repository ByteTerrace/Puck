using System.Numerics;

using Puck.Forge.Authoring;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Server;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves a body's authored terminal fall cannot step completely through a thin solid floor at a supported
/// simulation rate. This is the jetpack descent falsifier: both endpoints may lie outside the slab, so an endpoint-only
/// overlap test would silently miss the floor.</summary>
public sealed class HighSpeedGroundContactLawTests {
    [Theory]
    [InlineData(-40f)]
    [InlineData(-8f)]
    public void FieldContactExtractsAnEdgePenetrationTowardTheApproachedTop(float verticalVelocity) {
        var definition = ThinFloorDocument(requireField: true);
        Assert.True(WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);
        var collider = FixedWorldCollider.Compile(collider: definition.Kits[0].Collider, creations: definition.Creations)!.Value;
        var position = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -0.2),
            Z: FixedQ4816.FromDouble(value: 11.47));
        var velocity = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: verticalVelocity),
            Z: FixedQ4816.Zero);

        var resolution = field!.Resolve(position: ref position, velocity: ref velocity, orientation: FixedQuaternion.Identity, volumes: collider.Volumes);

        Assert.True(position.Y > FixedQ4816.Zero, userMessage: $"edge penetration extracted below the floor: {(double)position.Y:0.###}");
        Assert.True(resolution.Grounded, userMessage: "edge penetration did not settle as an approached top contact");
    }

    [Fact]
    public void DynamicDepenetrationCannotPushAStandingBodyThroughTheFloor() {
        var source = ThinFloorDocument();
        var definition = source with {
            Kits = source.Kits.Select(selector: kit => kit with { BodyContact = WorldBodyContactMode.Solid }).ToArray(),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var slot = 0; slot < 4; slot++) {
            var actor = WorldPrincipal.Seat(slot: slot);
            Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        }

        var standing = fixture.Server.Body(index: 0)!;
        standing.Pose(x: 0f, y: 1f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        for (var tick = 0; tick < 60; tick++) {
            fixture.Step(stepTicks: EngineTicks.PerRate(ratePerSecond: 60));
        }
        Assert.True(standing.Grounded);

        // Three independently authoritative bodies arriving just above the same standing body produce three legal
        // pairwise corrections. The terrain solver has already run this tick; their sum must still not move the
        // standing body through the slab's midplane and let the next terrain pass eject it from the wrong side.
        for (var index = 1; index < 4; index++) {
            fixture.Server.Body(index: index)!.Pose(x: 0f, y: 1.6f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        }

        for (var tick = 0; tick < 30; tick++) {
            fixture.Step(stepTicks: EngineTicks.PerRate(ratePerSecond: 60));
        }

        Assert.True(standing.Position.Y >= 0f, userMessage: $"dynamic contacts pushed the standing body through the floor; y={standing.Position.Y:0.###}");
    }

    [Theory]
    [InlineData(30U)]
    [InlineData(60U)]
    public void TerminalFallLandsOnThinAuthoredFloor(uint rateHz) {
        var stepTicks = EngineTicks.PerRate(ratePerSecond: rateHz);
        var terminalStep = (40f / rateHz);

        // Sweep one complete terminal-step phase. A single convenient starting height can land one sample inside the
        // slab by luck while its neighbour skips from above the expanded contact band to below it.
        for (var phase = 0; (phase < 24); phase++) {
            AssertLanding(rateHz: rateHz, stepTicks: stepTicks, startY: (8f + (terminalStep * phase / 24f)), x: 0f, z: 0f);
            AssertLanding(rateHz: rateHz, stepTicks: stepTicks, startY: (8f + (terminalStep * phase / 24f)), x: 10.7f, z: 10.77f);
        }

        static void AssertLanding(uint rateHz, ulong stepTicks, float startY, float x, float z) {
            using var fixture = Fixtures.FreshServer(definition: ThinFloorDocument(requireField: true));
            var actor = WorldPrincipal.Seat(slot: 0);

            Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
            fixture.Server.Body(index: actor.Index)!.Pose(x: x, y: startY, z: z, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

            for (var tick = 0; (tick < (rateHz * 4U)); tick++) {
                fixture.Step(stepTicks: stepTicks);
            }

            var body = fixture.Server.Body(index: actor.Index)!;
            Assert.True(body.Grounded, userMessage: $"body was not grounded after a four-second fall at {rateHz} Hz from ({x:0.###}, {startY:0.###}, {z:0.###}); y={body.Position.Y:0.###}");
            Assert.True(body.Position.Y >= 0f, userMessage: $"body escaped the floor at {rateHz} Hz from ({x:0.###}, {startY:0.###}, {z:0.###}); y={body.Position.Y:0.###}");
        }
    }

    private static WorldDefinition ThinFloorDocument(bool requireField = false) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "floor",
            Type: AvatarPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 24f, y: 0.1f, z: 24f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "thin-floor",
            Intent: CreatorIntent.Object,
            BakeStyle: null,
            Palette: null,
            Shapes: [shape],
            Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "thin-floor");
        var creation = new WorldCreation(Id: "floor", Document: canonical.Document, Hash: canonical.Hash);

        return source with {
            Collision = source.Collision with {
                Requirements = (requireField ? [WorldContactRequirement.SmoothUnionContact] : []),
            },
            Kits = source.Kits.Select(selector: kit => kit with {
                Motion = ((WorldMotionModel.Grounded)kit.Motion) with { MaxFallSpeed = 40f },
            }).ToArray(),
            Creations = [creation],
            Placements = [new WorldPlacement(Id: "floor", CreationId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };
    }
}
