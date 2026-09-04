using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the <c>body.carry</c>/<c>body.release</c> command surface: the <see cref="WorldCommand.CarryBody"/>/
/// <see cref="WorldCommand.ReleaseCarry"/> wire leaves, <c>WorldBody.TryBeginCarry</c>'s refusal set, and
/// <see cref="WorldPopulation.UpdateCarriedBodies"/>'s per-tick pose-follow.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldCarryCommandLawTests {
    private const int CarrierIndex = 0;
    private const int BallIndex = WorldBodiesLimits.LocalSeatCount;

    // Seat 0 wears the carrier kit (a carry facet, no rigid facet); an inhabited placement one body slot past the
    // seats wears a separate rigid "ball" kit — a carrier and its target can never share ONE kit row the way
    // WorldRigidDynamicsLawTests' two-rigid-ball fixture does, since a carry-capable body and a rigid one are
    // mutually exclusive facets on the SAME kit.
    private static WorldDefinition CarryTestDocument(float ballMass = 0.3f, float maxCarryFraction = 1f, float carrierMassEquivalent = 60f, float maxReach = 1.5f, float ballDistance = 1f) {
        var source = Fixtures.BuildDocument();
        var carrierKit = source.Kits[0] with {
            Carry = new WorldCarry(
                Offset: new Vector3(x: 0f, y: 1f, z: -0.6f),
                MassEquivalent: carrierMassEquivalent,
                MaxCarryFraction: maxCarryFraction,
                MaxReach: maxReach
            ),
        };
        var ballKit = source.Kits[0] with {
            Name = "ball",
            Collider = new WorldCollider.Sphere(Radius: 0.15f),
            BodyContact = WorldBodyContactMode.Solid,
            Rigid = new WorldRigid(Mass: ballMass, Restitution: 0.2f, Friction: 0.4f, RollingFriction: 0.1f, LinearDamping: 0f, AngularDamping: 0f),
            Carry = null,
        };
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.15f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var ballDocument = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "carry-ball",
            Palette: null,
            Shapes: [shape],
            Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: ballDocument, source: "carry-ball");
        var creation = new WorldPrototype(Id: "carry-ball", Document: canonical.Document, HashRaw: canonical.Hash);

        return source with {
            KitRowsRaw = [carrierKit, ballKit],
            DefaultSeatKitRaw = carrierKit.Name,
            PopulationRaw = source.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) },
            CreationsRaw = [creation],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: "carry-ball-placement",
                    PrototypeId: creation.Id,
                    Position: new DocumentVector3(value: new Vector3(x: ballDistance, y: 0f, z: 0f)),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Inhabit: new WorldPlacementInhabit(
                        Kit: "ball",
                        Look: null,
                        Source: IntentSource.Idle,
                        Distribution: WorldDistribution.Default
                    )
                ),
            ],
        };
    }
    private static WorldFixture JoinedCarrier(WorldDefinition definition) {
        var fixture = Fixtures.FreshServer(definition: definition);
        var seat = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        return fixture;
    }

    [Fact]
    public void CarryFacetRoundTripsThroughDocumentSerialization() {
        var doc = CarryTestDocument();

        Assert.NotNull(@object: doc.Kits[0].Carry);
        Assert.Equal(expected: "traveler", actual: doc.Kits[0].Name);
        Assert.Equal(expected: "ball", actual: doc.Kits[1].Name);
        Assert.NotNull(@object: doc.Kits[1].Rigid);
        Assert.Null(@object: doc.Kits[1].Carry);
        Assert.Equal(expected: "traveler", actual: doc.DefaultSeatKit);

        var bytes = WorldDefinitionSerialization.Serialize(definition: doc);
        var roundTripped = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

        Assert.NotNull(@object: roundTripped.Kits[0].Carry);
    }

    [Fact]
    public void CarryFollowsCarrierPoseAndReleaseHandsOffVelocity() {
        using var fixture = JoinedCarrier(definition: CarryTestDocument());
        var carrier = fixture.Server.Body(index: CarrierIndex)!;
        var ball = fixture.Server.Body(index: BallIndex)!;

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var beginReason
        ), userMessage: beginReason);
        Assert.Equal(expected: BallIndex, actual: carrier.Carrying);
        Assert.Equal(expected: CarrierIndex, actual: ball.CarriedBy);

        fixture.Step();

        var carrierPose = (carrier.FixedPosition + carrier.FixedOrientation.Rotate(vector: new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0d),
            Y: FixedQ4816.FromDouble(value: 1d),
            Z: FixedQ4816.FromDouble(value: -0.6d)
        )));

        Assert.Equal(expected: carrierPose, actual: ball.FixedPosition);

        // Teleport the carrier and step again — a carried body must track the FRESH carrier pose every tick, never
        // a stale one; this is the control a one-tick lag bug (following last tick's carrier position) would fail.
        carrier.Pose(x: 3f, y: 0f, z: 3f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        fixture.Step();

        var movedCarrierPose = (carrier.FixedPosition + carrier.FixedOrientation.Rotate(vector: new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0d),
            Y: FixedQ4816.FromDouble(value: 1d),
            Z: FixedQ4816.FromDouble(value: -0.6d)
        )));

        Assert.Equal(expected: movedCarrierPose, actual: ball.FixedPosition);
        Assert.NotEqual(expected: carrierPose, actual: movedCarrierPose);

        Assert.True(condition: fixture.Server.Population.TryEndCarry(carrierIndex: CarrierIndex, reason: out var endReason), userMessage: endReason);
        Assert.Null(@object: carrier.Carrying);
        Assert.Null(@object: ball.CarriedBy);

        // Released — re-enters the solver; a further step must not re-snap it to the carrier.
        var releasedPosition = ball.FixedPosition;

        fixture.Step();

        Assert.NotEqual(expected: movedCarrierPose, actual: ball.FixedPosition);
        _ = releasedPosition;
    }

    [Fact]
    public void CarryRefusesOverCapacityMassAndControlUnderCapacitySucceeds() {
        // Control: mass 0.3 against a 60kg-equivalent carrier at fraction 1 — comfortably under the ceiling.
        using var control = JoinedCarrier(definition: CarryTestDocument(ballMass: 0.3f));

        Assert.True(condition: control.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var controlReason
        ), userMessage: controlReason);

        // A ball heavier than the carrier's own carry ceiling (60kg equivalent × fraction 1) is refused by name.
        using var fixture = JoinedCarrier(definition: CarryTestDocument(ballMass: 90f));

        Assert.False(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "exceeds this body's carry ceiling");
        Assert.Null(@object: fixture.Server.Body(index: CarrierIndex)!.Carrying);
        Assert.Null(@object: fixture.Server.Body(index: BallIndex)!.CarriedBy);
    }

    [Fact]
    public void CarryRefusesOutOfReachAndControlWithinReachSucceeds() {
        // Control: 1 world unit apart, well inside the default 1.5-unit reach.
        using var control = JoinedCarrier(definition: CarryTestDocument(ballDistance: 1f));

        Assert.True(condition: control.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var controlReason
        ), userMessage: controlReason);

        using var fixture = JoinedCarrier(definition: CarryTestDocument(ballDistance: 10f));

        Assert.False(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "out of reach");
    }

    [Fact]
    public void CarryBodyWireLeafRoundTripsThroughSubmissionCodec() {
        var command = new WorldCommand.CarryBody(
            Principal: WorldPrincipal.Seat(slot: 0),
            EntityIndex: CarrierIndex,
            TargetIndex: BallIndex
        );

        Assert.True(condition: WorldSubmissionCodec.TryEncodeCommand(
            command: command,
            bytes: out var bytes,
            failure: out var encodeFailure
        ), userMessage: encodeFailure.ToString());
        Assert.True(condition: WorldSubmissionCodec.TryDecodeCommand(
            bytes: bytes,
            command: out var decoded,
            failure: out var decodeFailure
        ), userMessage: decodeFailure.ToString());

        var roundTripped = Assert.IsType<WorldCommand.CarryBody>(@object: decoded);

        Assert.Equal(expected: command.Principal, actual: roundTripped.Principal);
        Assert.Equal(expected: command.EntityIndex, actual: roundTripped.EntityIndex);
        Assert.Equal(expected: command.TargetIndex, actual: roundTripped.TargetIndex);
    }

    [Fact]
    public void ReleaseCarryWireLeafRoundTripsThroughSubmissionCodec() {
        var command = new WorldCommand.ReleaseCarry(
            Principal: WorldPrincipal.Seat(slot: 0),
            EntityIndex: CarrierIndex
        );

        Assert.True(condition: WorldSubmissionCodec.TryEncodeCommand(
            command: command,
            bytes: out var bytes,
            failure: out var encodeFailure
        ), userMessage: encodeFailure.ToString());
        Assert.True(condition: WorldSubmissionCodec.TryDecodeCommand(
            bytes: bytes,
            command: out var decoded,
            failure: out var decodeFailure
        ), userMessage: decodeFailure.ToString());

        var roundTripped = Assert.IsType<WorldCommand.ReleaseCarry>(@object: decoded);

        Assert.Equal(expected: command.Principal, actual: roundTripped.Principal);
        Assert.Equal(expected: command.EntityIndex, actual: roundTripped.EntityIndex);
    }
}
