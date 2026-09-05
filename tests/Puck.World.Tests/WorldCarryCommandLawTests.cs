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
    // Inhabitants claim the highest free body slot downward in placement order, so with two extra placements the
    // first-authored placement (dual) lands at the top of the range and the second (ball) one slot below it.
    private const int DualIndex = (WorldBodiesLimits.LocalSeatCount + 1);
    private const int DualBallIndex = WorldBodiesLimits.LocalSeatCount;

    // A kit that authors both facets — a body that can itself carry another body while also being a valid carry
    // target, the shape a mutual or chained carry relies on. Seat 0 still wears the carry-only carrier kit above.
    private static WorldDefinition MutualCarryTestDocument() {
        var source = Fixtures.BuildDocument();
        // Also carries a Rigid facet — the mutual-carry test below needs seat 0 itself to be a valid carry target,
        // matching the shape a mutual carry relies on: both parties carry Rigid and Carry.
        var carrierKit = source.Kits[0] with {
            Collider = new WorldCollider.Sphere(Radius: 0.3f),
            BodyContact = WorldBodyContactMode.Solid,
            Rigid = new WorldRigid(Mass: 60f, Restitution: 0.2f, Friction: 0.4f, RollingFriction: 0.1f, LinearDamping: 0f, AngularDamping: 0f),
            Carry = new WorldCarry(
                Offset: new Vector3(x: 0f, y: 1f, z: -0.6f),
                MassEquivalent: 60f,
                MaxCarryFraction: 1f,
                MaxReach: 1.5f
            ),
        };
        var dualKit = source.Kits[0] with {
            Name = "dual",
            Collider = new WorldCollider.Sphere(Radius: 0.15f),
            BodyContact = WorldBodyContactMode.Solid,
            Rigid = new WorldRigid(Mass: 0.3f, Restitution: 0.2f, Friction: 0.4f, RollingFriction: 0.1f, LinearDamping: 0f, AngularDamping: 0f),
            Carry = new WorldCarry(
                Offset: new Vector3(x: 0f, y: 1f, z: -0.6f),
                MassEquivalent: 60f,
                MaxCarryFraction: 1f,
                MaxReach: 1.5f
            ),
        };
        var ballKit = source.Kits[0] with {
            Name = "ball",
            Collider = new WorldCollider.Sphere(Radius: 0.15f),
            BodyContact = WorldBodyContactMode.Solid,
            Rigid = new WorldRigid(Mass: 0.3f, Restitution: 0.2f, Friction: 0.4f, RollingFriction: 0.1f, LinearDamping: 0f, AngularDamping: 0f),
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
            KitRowsRaw = [carrierKit, dualKit, ballKit],
            DefaultSeatKitRaw = carrierKit.Name,
            PopulationRaw = source.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 2) },
            CreationsRaw = [creation],
            PlacementRowsRaw = [
                // Within the carrier's (seat 0, at the origin) reach.
                new WorldPlacement(
                    Id: "dual-placement",
                    PrototypeId: creation.Id,
                    Position: new DocumentVector3(value: new Vector3(x: 1f, y: 0f, z: 0f)),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Inhabit: new WorldPlacementInhabit(Kit: "dual", Look: null, Source: IntentSource.Idle, Distribution: WorldDistribution.Default)
                ),
                // Within the dual body's own reach, never the carrier's.
                new WorldPlacement(
                    Id: "dual-ball-placement",
                    PrototypeId: creation.Id,
                    Position: new DocumentVector3(value: new Vector3(x: 1f, y: 0f, z: -1f)),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Inhabit: new WorldPlacementInhabit(Kit: "ball", Look: null, Source: IntentSource.Idle, Distribution: WorldDistribution.Default)
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
    private static WorldDefinition WithCarrierScale(WorldDefinition source, FixedQ4816 scale) {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "scale"),
            Kind: CellKind.Fixed,
            Min: FixedQ4816.FromDouble(value: 0.05d).Value,
            Max: FixedQ4816.One.Value,
            Capacity: 8,
            Cells: [new WorldStateCell(Key: CellName.Parse(candidate: "0"), Value: scale.Value)]
        );

        return source with {
            PopulationRaw = source.Population with { ScaleRow = "scale" },
            StateRaw = (source.StateRaw ?? new WorldStateSection()) with {
                World = [.. (source.StateRaw?.World ?? []), row],
            },
        };
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
    public void CarryUsesTheCarrierFinalPostContactPoseInTheSameTick() {
        using var fixture = JoinedCarrier(definition: MutualCarryTestDocument());
        var carrier = fixture.Server.Body(index: CarrierIndex)!;
        var passenger = fixture.Server.Body(index: DualIndex)!;
        var obstacle = fixture.Server.Body(index: DualBallIndex)!;

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: DualIndex,
            reason: out var beginReason
        ), userMessage: beginReason);

        carrier.Pose(
            x: (float)(double)obstacle.FixedPosition.X,
            y: (float)(double)obstacle.FixedPosition.Y,
            z: (float)(double)obstacle.FixedPosition.Z,
            yawRadians: 0f,
            pitchRadians: 0f,
            rollRadians: 0f
        );
        var overlappingPosition = carrier.FixedPosition;

        fixture.Step();

        Assert.NotEqual(expected: overlappingPosition, actual: carrier.FixedPosition);
        Assert.Equal(
            expected: (carrier.FixedPosition + carrier.FixedOrientation.Rotate(vector: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.FromDouble(value: -0.6d)
            ))),
            actual: passenger.FixedPosition
        );
    }

    [Fact]
    public void CarryOffsetScalesWithCarrierGeometry() {
        var half = FixedQ4816.FromDouble(value: 0.5d);
        using var fixture = JoinedCarrier(definition: WithCarrierScale(
            source: CarryTestDocument(ballDistance: 0.5f),
            scale: half
        ));
        var carrier = fixture.Server.Body(index: CarrierIndex)!;
        var ball = fixture.Server.Body(index: BallIndex)!;

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var beginReason
        ), userMessage: beginReason);

        fixture.Step();

        Assert.Equal(
            expected: (carrier.FixedPosition + carrier.FixedOrientation.Rotate(vector: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromDouble(value: 0.5d),
                Z: FixedQ4816.FromDouble(value: -0.3d)
            ))),
            actual: ball.FixedPosition
        );
    }

    [Fact]
    public void ActiveCarryPassesAllocateNothingInSteadyState() {
        using var fixture = JoinedCarrier(definition: CarryTestDocument());

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var beginReason
        ), userMessage: beginReason);

        for (var warmup = 0; warmup < 32; warmup++) {
            fixture.Server.Population.PrepareCarriedBodies();
            fixture.Server.Population.UpdateCarriedBodies();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 1_000; iteration++) {
            fixture.Server.Population.PrepareCarriedBodies();
            fixture.Server.Population.UpdateCarriedBodies();
        }

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
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
    public void CarrierAlreadyCarriedByAnotherBodyCannotItselfBeginACarryAndControlUncarriedSucceeds() {
        using var fixture = JoinedCarrier(definition: MutualCarryTestDocument());
        var carrier = fixture.Server.Body(index: CarrierIndex)!;
        var dual = fixture.Server.Body(index: DualIndex)!;
        var ball = fixture.Server.Body(index: DualBallIndex)!;

        // Control: proves the "dual" kit's own carry facet works before the relationship exists.
        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: DualIndex,
            reason: out var controlReason
        ), userMessage: controlReason);
        Assert.Equal(expected: DualIndex, actual: carrier.Carrying);
        Assert.Equal(expected: CarrierIndex, actual: dual.CarriedBy);

        // A body already carried by another may not itself begin carrying — including carrying its own carrier
        // back, which would otherwise leave both bodies' Advance a no-op while FollowCarrier derives each pose from
        // the other, running positions apart without bound.
        Assert.False(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: DualIndex,
            targetIndex: CarrierIndex,
            reason: out var mutualReason
        ));
        Assert.Contains(actualString: mutualReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "already carried by");
        Assert.Null(@object: dual.Carrying);
        Assert.Equal(expected: CarrierIndex, actual: dual.CarriedBy);

        // Being carried excludes a body from carrying anything else too, not just its own carrier — the dual body
        // cannot pick up the unrelated ball while itself carried.
        Assert.False(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: DualIndex,
            targetIndex: DualBallIndex,
            reason: out var carriedCannotCarryReason
        ));
        Assert.Contains(actualString: carriedCannotCarryReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "already carried by");
        Assert.Null(@object: ball.CarriedBy);
    }

    [Fact]
    public void TargetAlreadyCarryingSomethingRefusesBeingPickedUpAndControlIdleTargetSucceeds() {
        using var fixture = JoinedCarrier(definition: MutualCarryTestDocument());
        var dual = fixture.Server.Body(index: DualIndex)!;
        var ball = fixture.Server.Body(index: DualBallIndex)!;

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: DualIndex,
            targetIndex: DualBallIndex,
            reason: out var beginReason
        ), userMessage: beginReason);
        Assert.Equal(expected: DualBallIndex, actual: dual.Carrying);

        // A body already carrying something may not itself be picked up — refuses the chain A carries B, C picks
        // up B, each link deriving its pose from the last.
        Assert.False(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: DualIndex,
            reason: out var chainReason
        ));
        Assert.Contains(actualString: chainReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "already carrying");
        Assert.Null(@object: fixture.Server.Body(index: CarrierIndex)!.Carrying);

        // Control: releasing the dual body's own carry re-opens it as a valid pickup target.
        Assert.True(condition: fixture.Server.Population.TryEndCarry(carrierIndex: DualIndex, reason: out var endReason), userMessage: endReason);
        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: DualIndex,
            reason: out var controlReason
        ), userMessage: controlReason);
        _ = ball;
    }

    // A checkpoint whose residue names a carry partner outside the population, below the -1 "none" sentinel, or the
    // body itself refuses at restore — the same door a doctored flock target already refuses through — rather than
    // indexing out of range on the next tick's UpdateCarriedBodies sweep. The undoctored capture is the control: it
    // restores with the live relationship intact on both sides.
    [Fact]
    public void CheckpointNamingAnInvalidCarryPartnerRefusesAtRestoreAndControlRestoresIntact() {
        using var fixture = JoinedCarrier(definition: CarryTestDocument());

        Assert.True(condition: fixture.Server.Population.TryBeginCarry(
            carrierIndex: CarrierIndex,
            targetIndex: BallIndex,
            reason: out var beginReason
        ), userMessage: beginReason);
        fixture.Step();
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);
        Assert.Equal(expected: BallIndex, actual: checkpoint!.Population.Entries.Single(predicate: entry => entry.Index == CarrierIndex).Residue.Carrying);
        Assert.Equal(expected: CarrierIndex, actual: checkpoint.Population.Entries.Single(predicate: entry => entry.Index == BallIndex).Residue.CarriedBy);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
        Assert.NotNull(@object: restoredDefinition.Kits[checkpoint.Population.SeatKit].Carry);
        Assert.NotNull(@object: restoredDefinition.Kits[checkpoint.Population.Entries.Single(predicate: entry => entry.Index == BallIndex).KitIndex].Rigid);
        using var controlMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
        var (control, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: controlMachines,
            profiles: FreshProfiles(definition: restoredDefinition)
        );

        Assert.Equal(expected: BallIndex, actual: control.Body(index: CarrierIndex)!.Carrying);
        Assert.Equal(expected: CarrierIndex, actual: control.Body(index: BallIndex)!.CarriedBy);

        var capacity = fixture.Server.Population.Capacity;

        foreach (var (carrying, carriedBy) in new (int Carrying, int CarriedBy)[] {
            (capacity, -1),
            (-2, -1),
            (CarrierIndex, -1),
            (BallIndex, capacity),
            (BallIndex, CarrierIndex),
        }) {
            var doctored = (checkpoint with {
                Population = (checkpoint.Population with {
                    Entries = checkpoint.Population.Entries
                        .Select(selector: entry => ((entry.Index == CarrierIndex)
                            ? (entry with { Residue = (entry.Residue with { Carrying = carrying, CarriedBy = carriedBy }) })
                            : entry))
                        .ToArray(),
                }),
            });
            using var machines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
            var exception = Assert.Throws<InvalidOperationException>(testCode: () => WorldServer.FromCheckpoint(
                checkpoint: doctored,
                instanceIdentity: "boot",
                machines: machines,
                profiles: FreshProfiles(definition: restoredDefinition)
            ));

            Assert.Contains(actualString: exception.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "invalid carr");
        }

        var brokenMirror = (checkpoint with {
            Population = (checkpoint.Population with {
                Entries = checkpoint.Population.Entries
                    .Select(selector: entry => ((entry.Index == BallIndex)
                        ? (entry with { Residue = (entry.Residue with { CarriedBy = -1 }) })
                        : entry))
                    .ToArray(),
            }),
        });
        using var mirrorMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
        var mirrorException = Assert.Throws<InvalidOperationException>(testCode: () => WorldServer.FromCheckpoint(
            checkpoint: brokenMirror,
            instanceIdentity: "boot",
            machines: mirrorMachines,
            profiles: FreshProfiles(definition: restoredDefinition)
        ));

        Assert.Contains(actualString: mirrorException.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "without one valid mirrored carry relationship");

        var chained = (checkpoint with {
            Population = (checkpoint.Population with {
                Entries = checkpoint.Population.Entries
                    .Select(selector: entry => ((entry.Index == CarrierIndex)
                        ? (entry with { Residue = (entry.Residue with { CarriedBy = BallIndex }) })
                        : ((entry.Index == BallIndex)
                            ? (entry with { Residue = (entry.Residue with { Carrying = CarrierIndex }) })
                            : entry)))
                    .ToArray(),
            }),
        });
        using var chainMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
        var chainException = Assert.Throws<InvalidOperationException>(testCode: () => WorldServer.FromCheckpoint(
            checkpoint: chained,
            instanceIdentity: "boot",
            machines: chainMachines,
            profiles: FreshProfiles(definition: restoredDefinition)
        ));

        Assert.Contains(actualString: chainException.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot be both a carrier and carried");
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );

    private static WorldOwnedWorlds FreshProfiles(WorldDefinition definition) => new(
        directory: Directory.CreateTempSubdirectory(prefix: "puck-carry-tests-").FullName,
        machineId: Guid.NewGuid(),
        template: definition
    );

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
