using System.Numerics;

using Puck.World.Authoring;
using Puck.Hosting;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Server;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves a body's authored terminal fall cannot step completely through a thin solid floor at a supported
/// simulation rate. This is the jetpack descent falsifier: both endpoints may lie outside the slab, so an endpoint-only
/// overlap test would silently miss the floor.</summary>
public sealed class HighSpeedGroundContactLawTests {
    [InlineData(-40f)]
    [InlineData(-8f)]
    [Theory]
    public void FieldContactExtractsAnEdgePenetrationTowardTheApproachedTop(float verticalVelocity) {
        var definition = ThinFloorDocument(requireField: true);

        Assert.True(WorldSolidField.TryBuild(built: out var field, definition: definition, reason: out var reason), userMessage: reason);
        var collider = FixedWorldCollider.Compile(collider: definition.Kits[0].Collider, creations: definition.Creations)!.Value;
        var position = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -0.2),
            Z: FixedQ4816.FromDouble(value: 7.9));
        var velocity = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: verticalVelocity),
            Z: FixedQ4816.Zero);

        var resolution = field!.Resolve(position: ref position, velocity: ref velocity, orientation: FixedQuaternion.Identity, up: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), volumes: collider.Volumes);

        Assert.True((position.Y > FixedQ4816.Zero), userMessage: $"edge penetration extracted below the floor: {((double)position.Y):0.###}");
        Assert.True(resolution.Grounded, userMessage: "edge penetration did not settle as an approached top contact");
    }
    [Fact]
    public void AnisotropicallyScaledFloorHasNoContactBeyondItsRenderedEdge() {
        var definition = ThinFloorDocument(requireField: true);

        Assert.True(WorldSolidField.TryBuild(built: out var field, definition: definition, reason: out var reason), userMessage: reason);
        var collider = FixedWorldCollider.Compile(collider: definition.Kits[0].Collider, creations: definition.Creations)!.Value;
        var position = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 20L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero);
        var originalPosition = position;
        var velocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: -8L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero);
        var originalVelocity = velocity;

        Assert.True(condition: field!.Probe(distance: out var distance, gradient: out _, material: out _, position: in position));
        var resolution = field.Resolve(position: ref position, velocity: ref velocity,
            orientation: FixedQuaternion.Identity, up: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), volumes: collider.Volumes);

        Assert.True((distance > FixedQ4816.FromDouble(value: 10.5)),
            userMessage: $"the floor's contact metric still reports a distant point as nearby; distance={((double)distance):0.###}");
        Assert.Equal(actual: position, expected: originalPosition);
        Assert.Equal(actual: velocity, expected: originalVelocity);
        Assert.False(condition: resolution.Grounded);
        Assert.Equal(expected: FixedVector3.Zero, actual: resolution.ObstructionNormal);
    }
    [Fact]
    public void DynamicDepenetrationCannotPushAStandingBodyThroughTheFloor() {
        var source = ThinFloorDocument();
        var definition = source with {
            KitRowsRaw = source.Kits.Select(selector: kit => kit with { BodyContact = WorldBodyContactMode.Solid }).ToArray(),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var slot = 0; (slot < 4); slot++) {
            var actor = WorldPrincipal.Seat(slot: slot);

            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        }

        var standing = fixture.Server.Body(index: 0)!;

        standing.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 1f, yawRadians: 0f, z: 0f);
        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step(stepTicks: EngineTicks.PerRate(ratePerSecond: 60));
        }
        Assert.True(condition: standing.Grounded);

        // Three independently authoritative bodies arriving just above the same standing body produce three legal
        // pairwise corrections. The terrain solver has already run this tick; their sum must still not move the
        // standing body through the slab's midplane and let the next terrain pass eject it from the wrong side.
        for (var index = 1; (index < 4); index++) {
            fixture.Server.Body(index: index)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 1.6f, yawRadians: 0f, z: 0f);
        }

        for (var tick = 0; (tick < 30); tick++) {
            fixture.Step(stepTicks: EngineTicks.PerRate(ratePerSecond: 60));
        }

        Assert.True((standing.Position.Y >= 0f), userMessage: $"dynamic contacts pushed the standing body through the floor; y={standing.Position.Y:0.###}");
    }
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(500)]
    [Theory]
    public void AdjacencyContinuationSweepsDestinationTerrainBeforeAnotherAuthorityStep(int distance) {
        using var fixture = Fixtures.FreshServer(definition: ThinFloorDocument(requireField: true));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var trajectory = new WorldContinuumTrajectory(
            PreviousPosition: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: distance), Z: FixedQ4816.Zero),
            SourceTick: 17,
            ContinuumStartEngineTick: 0,
            ContinuumEndEngineTick: EngineTicks.PerRate(ratePerSecond: 30),
            ConsumedThroughEngineTick: EngineTicks.PerRate(ratePerSecond: 30),
            BoundaryEvents: 1);

        Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: -distance), Z: FixedQ4816.Zero),
            yawRadians: FixedQ4816.Zero,
            planarVelocity: FixedVector3.Zero,
            verticalVelocity: FixedQ4816.FromInteger(value: -(distance * 8)),
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            continuum: trajectory));

        Assert.True((body.FixedPosition.Y >= FixedQ4816.Zero),
            userMessage: $"destination continuation skipped the intervening thin floor; y={((double)body.FixedPosition.Y):0.###}");
        Assert.True(condition: body.Grounded);
        Assert.Equal(expected: trajectory, actual: body.PendingContinuum);
    }
    [InlineData(30U)]
    [InlineData(60U)]
    [Theory]
    public void TerminalFallLandsOnThinAuthoredFloor(uint rateHz) {
        var stepTicks = EngineTicks.PerRate(ratePerSecond: rateHz);
        var terminalStep = (40f / rateHz);

        // Sweep one complete terminal-step phase. A single convenient starting height can land one sample inside the
        // slab by luck while its neighbour skips from above the expanded contact band to below it.
        for (var phase = 0; (phase < 24); phase++) {
            AssertLanding(rateHz: rateHz, startY: (8f + ((terminalStep * phase) / 24f)), stepTicks: stepTicks, x: 0f, z: 0f);
            AssertLanding(rateHz: rateHz, startY: (8f + ((terminalStep * phase) / 24f)), stepTicks: stepTicks, x: 7.7f, z: 7.77f);
        }

        static void AssertLanding(uint rateHz, ulong stepTicks, float startY, float x, float z) {
            using var fixture = Fixtures.FreshServer(definition: ThinFloorDocument(requireField: true));
            var actor = WorldPrincipal.Seat(slot: 0);

            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
            fixture.Server.Body(index: actor.Index)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: x, y: startY, yawRadians: 0f, z: z);

            for (var tick = 0; (tick < (rateHz * 4U)); tick++) {
                fixture.Step(stepTicks: stepTicks);
            }

            var body = fixture.Server.Body(index: actor.Index)!;

            Assert.True(body.Grounded, userMessage: $"body was not grounded after a four-second fall at {rateHz} Hz from ({x:0.###}, {startY:0.###}, {z:0.###}); y={body.Position.Y:0.###}");
            Assert.True((body.Position.Y >= 0f), userMessage: $"body escaped the floor at {rateHz} Hz from ({x:0.###}, {startY:0.###}, {z:0.###}); y={body.Position.Y:0.###}");
        }
    }

    // CreationGeometry's unit box half-extent is 1 (was 0.34); this ratio (old unit + round) / (new unit + round)
    // keeps the compiled floor's world-space reach the same as before the unit-size table changed.
    private const float BoxUnitRatio = (0.38f / 1.04f);

    private static WorldDefinition ThinFloorDocument(bool requireField = false) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "floor",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: (new Vector3(x: 24f, y: 0.1f, z: 24f) * BoxUnitRatio),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "thin-floor",
            Palette: null,
            Shapes: [shape],
            Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "thin-floor");
        var creation = new WorldPrototype(Id: "floor", Document: canonical.Document, HashRaw: canonical.Hash);

        return source with {
            CollisionRaw = source.Collision with {
                Requirements = (requireField ? [WorldContactRequirement.SmoothUnionContact] : []),
            },
            KitRowsRaw = source.Kits.Select(selector: kit => kit with {
                Motion = kit.Motion with {
                    Holds = [
                        kit.Motion.Holds![0] with { Gravity = (kit.Motion.Holds![0].Gravity! with { Terminal = 40f }) },
                    ],
                },
            }).ToArray(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "floor", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };
    }
}
