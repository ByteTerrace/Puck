using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Untrusted neighbour reports, bodies or geometry, cannot buy more local displacement with more bodies or
/// bigger shapes, cannot herd a body across its ownership face, and cannot choose who yields.</summary>
public sealed class WorldAdjacencyCorrectionLawTests {
    private const string LocalAuthority = "local";

    private static WorldDefinition Document() {
        var basis = Fixtures.BuildDocument();
        return basis with {
            GravityRaw = WorldGravity.Default,
            KitsRaw = basis.KitsRaw! with { Rows = [basis.Kits[0] with {
                Collider = new WorldCollider.Sphere(Radius: 0.5f), BodyContact = WorldBodyContactMode.Solid,
                Motion = basis.Kits[0].Motion with {
                    Holds = [new WorldHold(Name: "still", Bond: BodyHoldBond.Free, Hold: BodyHoldKind.None)],
                },
            }] },
            References = [new WorldReference(SafeName.Parse("beyond-ref"), "beyond")],
            Destinations = [new WorldDestination(SafeName.Parse("beyond"), "beyond-ref",
                WorldDestinationDurability.Persisted, WorldDestinationScope.Global)],
            Adjacencies = [new WorldAdjacency(SafeName.Parse("seam"), "beyond", "seam",
                new WorldAdjacencyBoundary(Vector3.Zero, 0f, 0f, 128f, 128f))],
        };
    }
    // The remote identity a law needs on the far side of the settlement: one the local authority yields to, or not.
    private static string Remote(bool localResponds) {
        for (var suffix = 0; ; suffix++) {
            var candidate = $"remote-{suffix}";
            if (WorldCrossAuthoritySettlement.LocalResponds(LocalAuthority, candidate, "physical-contact") == localResponds) {
                return candidate;
            }
        }
    }
    private static WorldBody Seat(WorldFixture fixture, float z) {
        var body = fixture.JoinSeat();
        body.Pose(new FixedVector3(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.FromDouble(z)),
            FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        return body;
    }
    private static WorldOwnershipFace Face(WorldFixture fixture) =>
        WorldAdjacencyPolicy.OwnershipFaces(fixture.Server.Definition).Single();
    private static bool Crossed(WorldFixture fixture, WorldBody body) =>
        WorldAdjacencyOwnership.Sweep(fixture.Server, pendingOnly: false)
            .Any(hits => hits?.Any(hit => ReferenceEquals(fixture.Server.Body(hit.Seat), body)) == true);

    [Theory]
    [InlineData(32, 0.5f, 100f, 0f)]
    [InlineData(1, 64f, 10000f, 37f)]
    [InlineData(32, 0.5f, 0.25f, 0f)]
    public void PhantomBodiesEveryTickStayWithinLocalReachAndOverlap(int bodies, float remoteRadius, float depth, float yaw) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, z: -0.1f);
        var neighbour = new ReportedBodies(bodies, remoteRadius, Remote(localResponds: true));
        fixture.Server.Adjacencies = new Reports(body, neighbour, depth, yaw, axis: FixedVector3.UnitX);
        var ceiling = FixedQ4816.Min(body.BroadphaseRadius, FixedQ4816.FromDouble(depth));
        var counts = WorldBodyWork.Process;
        var kind = WorldBodyWork.AdjacencyCorrectionClamps;
        for (var tick = 0; tick < 16; tick++) {
            var before = body.FixedPosition;
            var countBefore = counts.Read(kind);
            fixture.Step();
            var correction = body.FixedPosition - before;
            Assert.NotEqual(FixedVector3.Zero, correction);
            Assert.True(correction.IsWithin(ceiling), $"tick {tick}: {correction} exceeds {ceiling}");
            Assert.True(counts.Read(kind) > countBefore, $"tick {tick}: no clamp counted");
        }
        var registry = new CommandRegistry(modules: [new WorldCountersCommandModule([counts])]);
        var readout = registry.Submit("world.counters physics.bodies");
        Assert.False(readout.IsError, readout.Output);
        Assert.Contains($"{kind.Name} {counts.Read(kind)}", readout.Output);
    }

    [Fact]
    public void HonestShallowContactIsUnchangedAndDoesNotCountAClamp() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, z: -0.1f);
        var neighbour = new ReportedBodies(1, 0.5f, Remote(localResponds: true));
        var source = new Reports(body, neighbour, 100f, 0f, axis: FixedVector3.UnitX, spacing: 0.875f);
        fixture.Server.Adjacencies = source;
        source.BeginTick(0);
        var start = body.FixedPosition;
        Assert.True(FixedDynamicBodyContacts.TryCorrection(
            WorldAdjacencyPath.MapPointIntoNeighbour(start, source.Projection.Path), FixedQuaternion.Identity,
            body.ScaledColliderVolumes(), FixedVector3.FromVector3(neighbour.CurrentPosition(0)),
            FixedQuaternion.Identity, neighbour.Collider(0)!.Value.Volumes, 0, out var expected));
        expected = WorldAdjacencyPath.MapVectorIntoSource(expected, source.Projection.Path);
        var counts = WorldBodyWork.Process;
        var before = counts.Read(WorldBodyWork.AdjacencyCorrectionClamps);
        fixture.Step();
        Assert.Equal(start + expected, body.FixedPosition);
        Assert.Equal(before, counts.Read(WorldBodyWork.AdjacencyCorrectionClamps));
    }

    // A neighbour's solid placement embedded in a body standing in the band resolves it through the same door as its
    // bodies: never farther per tick than the budget, and still all the way out over enough ticks.
    [Fact]
    public void NeighbourGeometryInTheBandEjectsNoFurtherThanTheBudget() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, z: 0.1f);
        var depth = 0.1f;
        // The sphere's centre sits 0.5 above its root; the box's +X face lies 0.4 inside it.
        var neighbour = new ReportedGeometry(boxCentre: new Vector3(-1.1f, 0.5f, 0.1f), boxHalfExtent: new Vector3(1f, 2f, 1f));
        var source = new Reports(body, neighbour, depth, 0f, axis: FixedVector3.UnitX);
        neighbour.Place(source.Projection.Path);
        fixture.Server.Adjacencies = source;
        var ceiling = FixedQ4816.Min(body.BroadphaseRadius, FixedQ4816.FromDouble(depth));
        var counts = WorldBodyWork.Process;
        var start = body.FixedPosition;
        var clamped = counts.Read(WorldBodyWork.AdjacencyCorrectionClamps);
        for (var tick = 0; tick < 8; tick++) {
            var before = body.FixedPosition;
            fixture.Step();
            Assert.True((body.FixedPosition - before).IsWithin(ceiling), $"tick {tick}: {body.FixedPosition - before} exceeds {ceiling}");
        }
        Assert.True(counts.Read(WorldBodyWork.AdjacencyCorrectionClamps) > clamped, "the embedded geometry was never clamped");
        Assert.True(body.FixedPosition.X - start.X > FixedQ4816.FromDouble(0.35), $"the body was not resolved out: {body.FixedPosition}");
    }

    // Bounded shoves every tick carry a body with no motion of its own up to its ownership face and no farther, so the
    // crossing sweep never hands it over; the same body walking on crosses.
    [Fact]
    public void NeighbourPushesNeverHerdAStillBodyAcrossItsFaceButWalkingCrosses() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, z: -0.1f);
        var neighbour = new ReportedBodies(1, 0.5f, Remote(localResponds: true));
        fixture.Server.Adjacencies = new Reports(body, neighbour, 100f, 0f, axis: -FixedVector3.UnitZ);
        var face = Face(fixture);
        for (var tick = 0; tick < 64; tick++) {
            fixture.Step();
            Assert.True(face.Owns(body.FixedPosition), $"tick {tick}: pushed to {face.Outward(body.FixedPosition)} past {face.Threshold}");
            Assert.False(Crossed(fixture, body), $"tick {tick}: a neighbour's pushes handed the body over");
        }
        Assert.True(face.Outward(body.FixedPosition) > face.Threshold - FixedQ4816.FromDouble(0.01),
            $"the pushes never reached the face: {face.Outward(body.FixedPosition)} of {face.Threshold}");

        var crossed = false;
        for (var tick = 0; (tick < 30) && !crossed; tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 0, value: -FixedQ4816.One));
            fixture.Step();
            crossed = Crossed(fixture, body);
        }
        Assert.True(crossed, $"the walking body never crossed; outward {face.Outward(body.FixedPosition)} of {face.Threshold}");
    }

    // The neighbour reissues its body under a new generation every tick. Whether the local body yields is decided by
    // the two authorities alone, so no generation changes it either way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerationGrindingCannotChooseTheResponder(bool localResponds) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, z: -0.1f);
        var neighbour = new ReportedBodies(1, 0.5f, Remote(localResponds), grind: true);
        fixture.Server.Adjacencies = new Reports(body, neighbour, 100f, 0f, axis: FixedVector3.UnitX, spacing: 0.875f);
        for (var tick = 0; tick < 32; tick++) {
            var before = body.FixedPosition;
            fixture.Step();
            Assert.Equal(localResponds, body.FixedPosition != before);
        }
    }

    // A .puck document cannot forge the neighbour's delivered poses. This source freezes an intentionally faulty
    // image at the real tick ingress, then drives the production population/contact wrapper without internal access.
    private sealed class Reports : IWorldAdjacencySource {
        private readonly WorldBody m_body;
        private readonly float m_spacing;
        private readonly FixedVector3 m_axis;
        public Reports(WorldBody body, IWorldAdjacencyNeighbour neighbour, float depth, float yaw, FixedVector3 axis, float spacing = 0.5f) {
            m_body = body;
            m_spacing = spacing;
            m_axis = axis;
            Neighbour = neighbour;
            var local = new WorldAdjacencyBoundary(Vector3.Zero, yaw, 0f, 128f, 128f).CompileFrame();
            var remote = new WorldAdjacencyBoundary(Vector3.Zero, 180f, 0f, 128f, 128f).CompileFrame();
            Projection = new("seam", Neighbour,
                [new(remote, local, FixedQ4816.FromDouble(depth), FixedQ4816.Zero)],
                FixedQ4816.FromDouble(depth), true);
        }
        public IWorldAdjacencyNeighbour Neighbour { get; }
        public WorldAdjacencyProjection Projection { get; }
        public WorldEntityAddress LocalEntityAddress(int index) => new(LocalAuthority, 0, index);
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) { departedFrom = default; return false; }
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) { neighbour = Neighbour; return true; }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [Projection];
        public void BeginTick(ulong tick) {
            if (Neighbour is not ReportedBodies bodies) { return; }
            bodies.Reissue();
            for (var index = 0; index < bodies.EntityCapacity; index++) {
                // Sphere roots sit at their feet; align the centres even when the attacker claims a huge radius.
                var point = m_body.FixedPosition + m_axis * FixedQ4816.FromDouble(m_spacing - index * 0.5f)
                    + FixedVector3.UnitY * FixedQ4816.FromDouble(0.5f - bodies.Radius);
                bodies.Positions[index] = WorldAdjacencyPath.MapPointIntoNeighbour(point, Projection.Path).ToVector3();
            }
        }
    }

    private abstract class Reported : IWorldAdjacencyNeighbourContact {
        public abstract string Authority { get; }
        public WorldFaceFrame CounterpartFrame => default;
        public abstract WorldDefinition Definition { get; }
        public int DefinitionRevision => 0;
        public ulong SnapshotTick => 0;
        public int SnapshotRevision => 0;
        public float InterpolationAlpha => 0f;
        public abstract int EntityCapacity { get; }
        public bool IsEntityActive(int index) => true;
        public abstract WorldEntityAddress EntityAddress(int index);
        public string? PlacementId(int index) => null;
        public Vector3 PreviousPosition(int index) => CurrentPosition(index) + Vector3.UnitX;
        public Quaternion PreviousOrientation(int index) => Quaternion.Identity;
        public abstract Vector3 CurrentPosition(int index);
        public Quaternion CurrentOrientation(int index) => Quaternion.Identity;
        public Vector3 BodyColor(int index) => Vector3.Zero;
        public WorldLook Look(int index) => null!;
        public byte CatalogRig(int index) => 0;
        public abstract FixedWorldCollider? Collider(int index);
        public WorldBodyContactMode BodyContact(int index) => WorldBodyContactMode.Solid;
        public abstract bool TryGetSolidField(out WorldSolidField? field, out string reason);
    }

    private sealed class ReportedBodies : Reported {
        private readonly FixedWorldCollider m_collider;
        private readonly bool m_grind;
        private int m_generation;
        public ReportedBodies(int count, float radius, string authority, bool grind = false) {
            Positions = new Vector3[count];
            Radius = radius;
            Authority = authority;
            m_grind = grind;
            m_collider = FixedWorldCollider.Compile(new WorldCollider.Sphere(radius), [])!.Value;
        }
        public Vector3[] Positions { get; }
        public float Radius { get; }
        public override string Authority { get; }
        public override WorldDefinition Definition { get; } = Document();
        public override int EntityCapacity => Positions.Length;
        public void Reissue() { if (m_grind) { m_generation++; } }
        public override WorldEntityAddress EntityAddress(int index) => new(Authority, m_generation, index);
        public override Vector3 CurrentPosition(int index) => Positions[index];
        public override FixedWorldCollider? Collider(int index) => m_collider;
        public override bool TryGetSolidField(out WorldSolidField? field, out string reason) {
            field = null;
            reason = "bodies only";
            return false;
        }
    }

    private sealed class ReportedGeometry(Vector3 boxCentre, Vector3 boxHalfExtent) : Reported {
        private WorldSolidField? m_field;
        private WorldDefinition? m_definition;
        public override string Authority => Remote(localResponds: true);
        public override WorldDefinition Definition => m_definition!;
        public override int EntityCapacity => 0;
        public override WorldEntityAddress EntityAddress(int index) => default;
        public override Vector3 CurrentPosition(int index) => Vector3.Zero;
        public override FixedWorldCollider? Collider(int index) => null;
        // Places the box, authored in local coordinates, into the neighbour's own frame and compiles its field.
        public void Place(IReadOnlyList<WorldAdjacencyFramePair> path) {
            var centre = WorldAdjacencyPath.MapPointIntoNeighbour(FixedVector3.FromVector3(boxCentre), path).ToVector3();
            var document = new CreationDocument(
                Schema: CreationDocument.CurrentSchema, Name: "wall", Palette: null,
                Shapes: [new ShapeDocument(Id: 0, Name: "wall", Type: SdfSolidPrimitive.Box, Position: Vector3.Zero,
                    Rotation: Quaternion.Identity, Scale: boxHalfExtent, Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0)],
                Frames: null);
            var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "wall");
            var creation = new WorldPrototype(Id: "wall", Document: canonical.Document, HashRaw: canonical.Hash);
            var basis = Fixtures.BuildDocument();
            m_definition = basis with {
                CollisionRaw = basis.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
                CreationsRaw = [creation],
                PlacementRowsRaw = [new WorldPlacement(Id: "wall", PrototypeId: creation.Id, Position: centre, YawDegrees: 0f,
                    Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            };
            Assert.True(WorldSolidField.TryBuild(built: out m_field, definition: m_definition, reason: out var reason), reason);
        }
        public override bool TryGetSolidField(out WorldSolidField? field, out string reason) {
            field = m_field;
            reason = string.Empty;
            return (field is not null);
        }
    }
}
