using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for physical contact supplied by adjacency projections: the derived diagonal peer, and the strip a
/// body walks between its own world's authored edge and the far side of the ownership deadband.</summary>
public sealed class WorldAdjacencyCornerContactLawTests {
    [Fact]
    public void DerivedCornerProjectionSuppliesGroundWhenBothDirectWorldsEnd() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);

        source = source with {
            PlacementRowsRaw = [source.Placements[0] with { Position = new Vector3(x: 100f, y: 0f, z: 100f) }],
            References = [
                new WorldReference(WorldSafeName.Parse(candidate: "unused-east-ref"), "unused-east.world.json"),
                new WorldReference(WorldSafeName.Parse(candidate: "unused-south-ref"), "unused-south.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse(candidate: "unused-east"), "unused-east-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse(candidate: "unused-south"), "unused-south-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse(candidate: "east"), "unused-east", "west", Boundary(yaw: 90f)),
                new WorldAdjacency(WorldSafeName.Parse(candidate: "south"), "unused-south", "north", Boundary(yaw: 0f)),
            ],
        };
        var corner = Fixtures.BuildGradientUpDocument(gradientUp: false);
        using var fixture = Fixtures.FreshServer(definition: source);

        fixture.Server.Adjacencies = new CornerSource(definition: corner);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Server.Body(index: actor.Index)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 3f, yawRadians: 0f, z: 0f);

        for (var tick = 0; (tick < 480); tick++) {
            fixture.Step();
        }

        var body = fixture.Server.Body(index: actor.Index)!;

        Assert.True(body.Grounded, userMessage: $"the body fell through the derived corner projection; y={body.Position.Y:0.###}");
        Assert.True((body.Position.Y > 0f), userMessage: $"the projected corner ground never arrested the fall; y={body.Position.Y:0.###}");
    }
    /// <summary>The seam strip a crossing body occupies is served for its whole length. Local ground ends at the
    /// authored edge, ownership changes hands at the far side of the deadband, and the ticks in between — plus the
    /// ticks a handoff takes to drain, during which the body keeps walking — must all get vertical support from the
    /// mirrored neighbour floor. A world whose derived overlap equals its own ownership threshold has no margin at
    /// all, which is the ordinary case for a document declaring no long-range targeting.</summary>
    [Fact]
    public void WalkingTheSeamStripRetainsGroundPastTheOwnershipThreshold() {
        var local = SeamDocument(floorCenterZ: -FloorHalfExtent);

        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(definition: local, depth: out var hysteresis, reason: out var hysteresisReason), userMessage: hysteresisReason);
        Assert.True(WorldAdjacencyPolicy.TryVerticalSettleDeadband(definition: local, depth: out var settle, reason: out var settleReason), userMessage: settleReason);

        var sourceFrame = SeamBoundary(outwardYaw: 0f).CompileFrame();
        var threshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in sourceFrame, reciprocalHysteresis: hysteresis, verticalSettleDeadband: settle);

        using var walk = SeamWalk(local: local);
        var restingY = walk.Settle();
        var handoff = ((float)((double)threshold));

        // Walk far enough past the handoff plane to cover a transfer that takes many ticks to drain, and no farther
        // than the neighbour's own floor reaches.
        while (walk.Body.Position.Z < (handoff + 4f)) {
            walk.Step(forward: -FixedQ4816.One);

            Assert.True((walk.Body.Position.Y >= (restingY - GroundTolerance)),
                userMessage: $"the body lost ground in the seam strip at z={walk.Body.Position.Z:0.####} (authored edge 0, handoff {handoff:0.####}); y={walk.Body.Position.Y:0.####} resting={restingY:0.####}");
            Assert.True(((walk.Body.Position.Z <= 0f) || walk.Body.Grounded),
                userMessage: $"the body was airborne over the neighbour's floor at z={walk.Body.Position.Z:0.####}");
        }

        Assert.True((walk.Body.Position.Z > handoff), userMessage: "the walk never reached the handoff plane");
    }
    /// <summary>Walking off the far rim of the neighbour's own floor still falls: the overlap defers the ground
    /// decision to the neighbour's geometry rather than manufacturing ground everywhere past a boundary.</summary>
    [Fact]
    public void WalkingOffTheNeighboursFarRimStillFalls() {
        using var walk = SeamWalk(local: SeamDocument(floorCenterZ: -FloorHalfExtent));
        var restingY = walk.Settle();
        var rim = (FloorHalfExtent * 2f);

        while ((walk.Body.Position.Z < (rim + 2f)) && (walk.Body.Position.Y > (restingY - 1f))) {
            walk.Step(forward: -FixedQ4816.One);
        }

        Assert.True((walk.Body.Position.Z > rim), userMessage: $"the body stopped before the neighbour's far rim at z={walk.Body.Position.Z:0.###}");
        Assert.True((walk.Body.Position.Y < (restingY - 1f)), userMessage: $"the body was still supported past the neighbour's own floor; y={walk.Body.Position.Y:0.###}");
    }
    /// <summary>Walking off this world's own outer rim, away from any authored boundary, still falls.</summary>
    [Fact]
    public void WalkingOffTheOwnOuterRimStillFalls() {
        using var walk = SeamWalk(local: SeamDocument(floorCenterZ: -FloorHalfExtent));
        var restingY = walk.Settle();
        var rim = (-FloorHalfExtent * 2f);

        while ((walk.Body.Position.Z > (rim - 2f)) && (walk.Body.Position.Y > (restingY - 1f))) {
            walk.Step(forward: FixedQ4816.One);
        }

        Assert.True((walk.Body.Position.Y < (restingY - 1f)), userMessage: $"the body was supported past its own outer rim; y={walk.Body.Position.Y:0.###} z={walk.Body.Position.Z:0.###}");
    }
    /// <summary>A neighbour that has delivered no compiled field answers no, and the wrapper then leaves the local
    /// field's own decision byte-identical — which is what lets the authored <c>unavailable: closed</c> treatment be
    /// the one thing that decides the outcome, rather than a silently different trajectory.</summary>
    [Fact]
    public void StarvedNeighbourDeliveryLeavesTheLocalAnswerUntouched() {
        var local = SeamDocument(floorCenterZ: -FloorHalfExtent);

        using var starved = SeamWalk(local: local, neighbourHasField: false);
        using var bare = SeamWalk(local: local, attachSource: false);

        _ = starved.Settle();
        _ = bare.Settle();

        for (var tick = 0; (tick < 600); tick++) {
            starved.Step(forward: -FixedQ4816.One);
            bare.Step(forward: -FixedQ4816.One);

            Assert.Equal(expected: bare.Body.FixedPosition, actual: starved.Body.FixedPosition);
            Assert.Equal(expected: bare.Body.Grounded, actual: starved.Body.Grounded);
        }
    }

    private const float BoxLocalHalfExtent = 1.04f;
    private const float FloorHalfExtent = 12f;
    private const float GroundTolerance = 0.01f;

    private static WorldAdjacencyBoundary Boundary(float yaw) => new(
        Center: Vector3.Zero,
        OutwardYawDegrees: yaw,
        OutwardPitchDegrees: 0f,
        Width: 8f,
        Height: 8f
    );
    private static WorldAdjacencyBoundary SeamBoundary(float outwardYaw) => new(
        Center: Vector3.Zero,
        OutwardYawDegrees: outwardYaw,
        OutwardPitchDegrees: 0f,
        Width: (FloorHalfExtent * 2f),
        Height: 16f
    );
    // Two flat worlds meeting exactly at the z = 0 plane: the local floor spans z in [-24, 0] and the neighbour's
    // spans [0, 24], so neither has any geometry on the other's side of the seam.
    private static WorldDefinition SeamDocument(float floorCenterZ) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var scale = (FloorHalfExtent / BoxLocalHalfExtent);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "ground",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: scale, y: 0.1f, z: scale),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "ground",
            Palette: null,
            Shapes: [shape],
            Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "ground");
        var creation = new WorldPrototype(Id: "ground", Document: canonical.Document, HashRaw: canonical.Hash);
        var spawn = (floorCenterZ / 2f);

        return source with {
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "ground", PrototypeId: creation.Id, Position: new Vector3(x: 0f, y: 0f, z: floorCenterZ), YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            SpawnPointsRaw = [
                new WorldSpawnPoint(Id: "seat-1", Position: new Vector3(x: 0f, y: 1f, z: spawn)),
                new WorldSpawnPoint(Id: "seat-2", Position: new Vector3(x: 2f, y: 1f, z: spawn)),
                new WorldSpawnPoint(Id: "seat-3", Position: new Vector3(x: 4f, y: 1f, z: spawn)),
                new WorldSpawnPoint(Id: "seat-4", Position: new Vector3(x: 6f, y: 1f, z: spawn)),
            ],
            References = [new WorldReference(WorldSafeName.Parse(candidate: "beyond-ref"), "beyond.world.json")],
            Destinations = [new WorldDestination(WorldSafeName.Parse(candidate: "beyond"), "beyond-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global)],
            Adjacencies = [new WorldAdjacency(WorldSafeName.Parse(candidate: "seam"), "beyond", "seam", SeamBoundary(outwardYaw: ((floorCenterZ < 0f) ? 0f : 180f)))],
        };
    }
    private static SeamWalker SeamWalk(WorldDefinition local, bool neighbourHasField = true, bool attachSource = true) {
        var neighbourDefinition = SeamDocument(floorCenterZ: FloorHalfExtent);

        Assert.True(WorldAdjacencyPolicy.TryDeriveOverlap(depth: out var depth, local: local, neighbour: neighbourDefinition, reason: out var depthReason), userMessage: depthReason);
        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(definition: local, depth: out var hysteresis, reason: out var hysteresisReason), userMessage: hysteresisReason);
        Assert.True(WorldAdjacencyPolicy.TryVerticalSettleDeadband(definition: local, depth: out var settle, reason: out var settleReason), userMessage: settleReason);

        var sourceFrame = SeamBoundary(outwardYaw: 0f).CompileFrame();
        var neighbourFrame = SeamBoundary(outwardYaw: 180f).CompileFrame();
        var fixture = Fixtures.FreshServer(definition: local);

        if (attachSource) {
            fixture.Server.Adjacencies = new SeamSource(
                neighbour: new SeamNeighbour(counterpartFrame: neighbourFrame, definition: neighbourDefinition, hasField: neighbourHasField),
                sourceFrame: sourceFrame,
                neighbourFrame: neighbourFrame,
                depth: depth,
                ownershipThreshold: WorldAdjacencyPolicy.OwnershipThreshold(frame: in sourceFrame, reciprocalHysteresis: hysteresis, verticalSettleDeadband: settle));
        }

        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        return new SeamWalker(fixture: fixture, body: fixture.Server.Body(index: actor.Index)!);
    }

    private sealed class SeamWalker(WorldFixture fixture, WorldBody body) : IDisposable {
        public WorldBody Body => body;

        /// <summary>Settles the body onto its own floor and returns the resting height every later sample is
        /// compared against. Measured, never a pinned pose.</summary>
        public float Settle() {
            body.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 1f, yawRadians: 0f, z: -2f);

            for (var tick = 0; (tick < 240); tick++) {
                fixture.Step();
            }

            Assert.True(body.Grounded, userMessage: "the body never settled on its own floor");

            return body.Position.Y;
        }
        public void Step(FixedQ4816 forward) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 0, value: forward));
            fixture.Step();
        }
        public void Dispose() => fixture.Dispose();
    }
    private sealed class SeamSource : IWorldAdjacencySource {
        private readonly WorldAdjacencyProjection[] m_projections;

        public SeamSource(SeamNeighbour neighbour, WorldFaceFrame sourceFrame, WorldFaceFrame neighbourFrame, FixedQ4816 depth, FixedQ4816 ownershipThreshold) {
            m_projections = [new WorldAdjacencyProjection(
                Name: "seam",
                Neighbour: neighbour,
                Path: [new WorldAdjacencyFramePair(Neighbour: neighbourFrame, OverlapDepth: depth, OwnershipThreshold: ownershipThreshold, Source: sourceFrame)],
                OverlapDepth: depth,
                Direct: true
            )];
        }

        public WorldEntityAddress LocalEntityAddress(int index) => new(Authority: "source", Generation: 0, Index: index);
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Overlap;
        public void BeginTick(ulong tick) { }
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;
            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => m_projections;
    }
    private sealed class SeamNeighbour : IWorldAdjacencyNeighbourContact {
        private readonly WorldSolidField? m_field;

        public SeamNeighbour(WorldDefinition definition, WorldFaceFrame counterpartFrame, bool hasField) {
            Definition = definition;
            CounterpartFrame = counterpartFrame;
            Assert.True(WorldSolidField.TryBuild(built: out var field, definition: definition, reason: out var reason), userMessage: reason);
            m_field = (hasField ? field : null);
        }

        public string Authority => "neighbour";
        public WorldFaceFrame CounterpartFrame { get; }
        public WorldDefinition Definition { get; }
        public int DefinitionRevision => 0;
        public int EntityCapacity => 0;
        public float InterpolationAlpha => 0f;
        public int SnapshotRevision => 0;
        public ulong SnapshotTick => 0;

        public bool IsEntityActive(int index) => false;
        public WorldEntityAddress EntityAddress(int index) => default;
        public Vector3 PreviousPosition(int index) => Vector3.Zero;
        public Quaternion PreviousOrientation(int index) => Quaternion.Identity;
        public Vector3 CurrentPosition(int index) => Vector3.Zero;
        public Quaternion CurrentOrientation(int index) => Quaternion.Identity;
        public Vector3 BodyColor(int index) => Vector3.Zero;
        public WorldLook Look(int index) => null!;
        public byte CatalogRig(int index) => 0;
        public FixedWorldCollider? Collider(int index) => null;
        public WorldBodyContactMode BodyContact(int index) => WorldBodyContactMode.Overlap;
        public bool TryGetSolidField(out WorldSolidField? field, out string reason) {
            field = m_field;
            reason = ((m_field is null) ? "the neighbour has delivered no compiled field" : string.Empty);
            return (m_field is not null);
        }
    }
    private sealed class CornerSource : IWorldAdjacencySource {
        private readonly CornerNeighbour m_neighbour;
        private readonly WorldAdjacencyProjection[] m_projections;

        public CornerSource(WorldDefinition definition) {
            m_neighbour = new CornerNeighbour(definition: definition);
            var east = Boundary(yaw: 90f).CompileFrame();
            var south = Boundary(yaw: 0f).CompileFrame();
            var depth = FixedQ4816.One;

            m_projections = [new WorldAdjacencyProjection(
                Name: "corner:east+south",
                Neighbour: m_neighbour,
                Path: [
                    new WorldAdjacencyFramePair(Neighbour: south, Source: south, OverlapDepth: depth, OwnershipThreshold: FixedQ4816.Zero),
                    new WorldAdjacencyFramePair(Neighbour: east, Source: east, OverlapDepth: depth, OwnershipThreshold: FixedQ4816.Zero),
                ],
                OverlapDepth: depth,
                Direct: false
            )];
        }

        public WorldEntityAddress LocalEntityAddress(int index) => new(Authority: "source", Generation: 0, Index: index);
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Overlap;
        public void BeginTick(ulong tick) { }
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;
            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => m_projections;
    }
    private sealed class CornerNeighbour : IWorldAdjacencyNeighbourContact {
        private readonly WorldSolidField m_field;

        public CornerNeighbour(WorldDefinition definition) {
            Definition = definition;
            Assert.True(WorldSolidField.TryBuild(built: out var field, definition: definition, reason: out var reason), userMessage: reason);
            m_field = field!;
        }

        public string Authority => "corner";
        public WorldFaceFrame CounterpartFrame => Boundary(yaw: 0f).CompileFrame();
        public WorldDefinition Definition { get; }
        public int DefinitionRevision => 0;
        public int EntityCapacity => 0;
        public float InterpolationAlpha => 0f;
        public int SnapshotRevision => 0;
        public ulong SnapshotTick => 0;

        public bool IsEntityActive(int index) => false;
        public WorldEntityAddress EntityAddress(int index) => default;
        public Vector3 PreviousPosition(int index) => Vector3.Zero;
        public Quaternion PreviousOrientation(int index) => Quaternion.Identity;
        public Vector3 CurrentPosition(int index) => Vector3.Zero;
        public Quaternion CurrentOrientation(int index) => Quaternion.Identity;
        public Vector3 BodyColor(int index) => Vector3.Zero;
        public WorldLook Look(int index) => null!;
        public byte CatalogRig(int index) => 0;
        public FixedWorldCollider? Collider(int index) => null;
        public WorldBodyContactMode BodyContact(int index) => WorldBodyContactMode.Overlap;
        public bool TryGetSolidField(out WorldSolidField? field, out string reason) {
            field = m_field;
            reason = string.Empty;
            return true;
        }
    }
}
