using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for physical contact supplied by compiler-derived adjacency projections.</summary>
public sealed class WorldAdjacencyCornerContactLawTests {
    [Fact]
    public void DerivedCornerProjectionSuppliesGroundWhenBothDirectWorldsEnd() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        source = source with {
            Placements = [source.Placements[0] with { Position = new Vector3(x: 100f, y: 0f, z: 100f) }],
            References = [
                new WorldReference(WorldSafeName.Parse("unused-east-ref"), "unused-east.world.json"),
                new WorldReference(WorldSafeName.Parse("unused-south-ref"), "unused-south.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("unused-east"), "unused-east-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("unused-south"), "unused-south-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("east"), "unused-east", "west", Boundary(yaw: 90f)),
                new WorldAdjacency(WorldSafeName.Parse("south"), "unused-south", "north", Boundary(yaw: 0f)),
            ],
        };
        var corner = Fixtures.BuildGradientUpDocument(gradientUp: false);
        using var fixture = Fixtures.FreshServer(definition: source);
        fixture.Server.Adjacencies = new CornerSource(definition: corner);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        fixture.Server.Body(index: actor.Index)!.Pose(x: 0f, y: 3f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; tick < 480; tick++) {
            fixture.Step();
        }

        var body = fixture.Server.Body(index: actor.Index)!;
        Assert.True(body.Grounded, userMessage: $"the body fell through the derived corner projection; y={body.Position.Y:0.###}");
        Assert.True(body.Position.Y > 0f, userMessage: $"the projected corner ground never arrested the fall; y={body.Position.Y:0.###}");
    }

    private static WorldAdjacencyBoundary Boundary(float yaw) => new(
        Center: Vector3.Zero,
        OutwardYawDegrees: yaw,
        OutwardPitchDegrees: 0f,
        Width: 8f,
        Height: 8f
    );

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
                    new WorldAdjacencyFramePair(Neighbour: south, Source: south, OverlapDepth: depth),
                    new WorldAdjacencyFramePair(Neighbour: east, Source: east, OverlapDepth: depth),
                ],
                OverlapDepth: depth,
                Direct: false
            )];
        }

        public WorldEntityAddress LocalEntityAddress(int index) => new(Authority: "source", Index: index, Generation: 0);
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Overlap;
        public void BeginTick(ulong tick) { }
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;
            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => m_projections;
    }

    private sealed class CornerNeighbour : IWorldAdjacencyNeighbour {
        private readonly WorldSolidField m_field;

        public CornerNeighbour(WorldDefinition definition) {
            Definition = definition;
            Assert.True(WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);
            m_field = field!;
        }

        public string Authority => "corner";
        public WorldDefinition Definition { get; }
        public int DefinitionRevision => 0;
        public WorldFaceFrame CounterpartFrame => Boundary(yaw: 0f).CompileFrame();
        public ulong SnapshotTick => 0;
        public int SnapshotRevision => 0;
        public float InterpolationAlpha => 0f;
        public int EntityCapacity => 0;
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
