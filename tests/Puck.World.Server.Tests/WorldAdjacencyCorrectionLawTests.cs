using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Untrusted neighbour body reports cannot buy more local displacement with more bodies or bigger shapes.</summary>
public sealed class WorldAdjacencyCorrectionLawTests {
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

    [Theory]
    [InlineData(32, 0.5f, 100f, 0f)]
    [InlineData(1, 64f, 10000f, 37f)]
    [InlineData(32, 0.5f, 0.25f, 0f)]
    public void PhantomBodiesEveryTickStayWithinLocalReachAndOverlap(int bodies, float remoteRadius, float depth, float yaw) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = fixture.JoinSeat();
        body.Pose(new FixedVector3(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.FromDouble(-0.1)),
            FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        var source = new Reports(body, bodies, remoteRadius, depth, yaw);
        fixture.Server.Adjacencies = source;
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
        var body = fixture.JoinSeat();
        body.Pose(new FixedVector3(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.FromDouble(-0.1)),
            FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        var source = new Reports(body, 1, 0.5f, 100f, 0f, spacing: 0.875f);
        fixture.Server.Adjacencies = source;
        source.BeginTick(0);
        var neighbour = source.Neighbour;
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

    // A .puck document cannot forge the neighbour's delivered poses. This source freezes an intentionally faulty
    // image at the real tick ingress, then drives the production population/contact wrapper without internal access.
    private sealed class Reports : IWorldAdjacencySource {
        private readonly WorldBody m_body;
        private readonly float m_spacing;
        private readonly float m_remoteRadius;
        public Reports(WorldBody body, int count, float radius, float depth, float yaw, float spacing = 0.5f) {
            m_body = body;
            m_spacing = spacing;
            m_remoteRadius = radius;
            Neighbour = new ReportedBodies(count, radius);
            var local = new WorldAdjacencyBoundary(Vector3.Zero, yaw, 0f, 128f, 128f).CompileFrame();
            var remote = new WorldAdjacencyBoundary(Vector3.Zero, 180f, 0f, 128f, 128f).CompileFrame();
            Projection = new("seam", Neighbour,
                [new(remote, local, FixedQ4816.FromDouble(depth), FixedQ4816.Zero)],
                FixedQ4816.FromDouble(depth), true);
        }
        public ReportedBodies Neighbour { get; }
        public WorldAdjacencyProjection Projection { get; }
        public WorldEntityAddress LocalEntityAddress(int index) => new("local", 0, index);
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) { departedFrom = default; return false; }
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) { neighbour = Neighbour; return true; }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [Projection];
        public void BeginTick(ulong tick) {
            for (var index = 0; index < Neighbour.EntityCapacity; index++) {
                // Sphere roots sit at their feet; align the centres even when the attacker claims a huge radius.
                var point = m_body.FixedPosition + FixedVector3.UnitX * FixedQ4816.FromDouble(m_spacing - index * 0.5f)
                    + FixedVector3.UnitY * FixedQ4816.FromDouble(0.5f - m_remoteRadius);
                Neighbour.Positions[index] = WorldAdjacencyPath.MapPointIntoNeighbour(point, Projection.Path).ToVector3();
            }
        }
    }

    private sealed class ReportedBodies : IWorldAdjacencyNeighbour {
        private readonly FixedWorldCollider m_collider;
        private readonly WorldEntityAddress[] m_addresses;
        public ReportedBodies(int count, float radius) {
            Positions = new Vector3[count];
            m_collider = FixedWorldCollider.Compile(new WorldCollider.Sphere(radius), [])!.Value;
            m_addresses = new WorldEntityAddress[count];
            for (var index = 0; index < count; index++) {
                var generation = 0;
                var local = new WorldEntityAddress("local", 0, 0);
                WorldEntityAddress remote;
                do { remote = new("remote", generation++, index); }
                while (!WorldCrossAuthoritySettlement.LocalResponds(local, remote, "physical-contact"));
                m_addresses[index] = remote;
            }
        }
        public Vector3[] Positions { get; }
        public string Authority => "remote";
        public WorldFaceFrame CounterpartFrame => default;
        public WorldDefinition Definition { get; } = Document();
        public int DefinitionRevision => 0;
        public ulong SnapshotTick => 0;
        public int SnapshotRevision => 0;
        public float InterpolationAlpha => 0f;
        public int EntityCapacity => Positions.Length;
        public bool IsEntityActive(int index) => true;
        public WorldEntityAddress EntityAddress(int index) => m_addresses[index];
        public string? PlacementId(int index) => null;
        public Vector3 PreviousPosition(int index) => Positions[index] + Vector3.UnitX;
        public Quaternion PreviousOrientation(int index) => Quaternion.Identity;
        public Vector3 CurrentPosition(int index) => Positions[index];
        public Quaternion CurrentOrientation(int index) => Quaternion.Identity;
        public Vector3 BodyColor(int index) => Vector3.Zero;
        public WorldLook Look(int index) => null!;
        public byte CatalogRig(int index) => 0;
        public FixedWorldCollider? Collider(int index) => m_collider;
        public WorldBodyContactMode BodyContact(int index) => WorldBodyContactMode.Solid;
    }
}
