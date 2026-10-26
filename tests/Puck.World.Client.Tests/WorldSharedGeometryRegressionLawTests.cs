using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class WorldSharedGeometryRegressionLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void EqualScaleCatalogRestylesRebuildTheSelectedRigAndMotion(bool session) {
        var definition = new WorldDefinition() with {
            LookRowsRaw = [
            new WorldLook("first", new WorldLookSource.Catalog(0), 1, WorldLookMotion.Default),
            new WorldLook("second", new WorldLookSource.Catalog(1), 1, WorldLookMotion.Default with { GaitAmplitude = 0 }),
            new WorldLook("still", new WorldLookSource.Catalog(1), 1, WorldLookMotion.Default),
        ],
        };
        var scene = new Scene(definition, session);

        scene.Deliver(0, 1);
        var first = scene.Frame();

        scene.Deliver(1, 2);
        var changed = scene.Frame();
        var fresh = new Scene(definition, session);

        fresh.Deliver(1, 1);
        Assert.NotSame(first.Program, changed.Program);
        Assert.Equal(fresh.Frame().Program.Words.ToArray(), changed.Program.Words.ToArray());
        scene.Deliver(2, 3);
        Assert.NotSame(changed.Program, scene.Frame().Program);
    }
    [Fact]
    public void ARigidInhabitantLightKeepsItsShapesOffsetAndRotation() {
        var scene = new Scene(RigidDefinition());

        scene.Deliver(0, 1, "placed");
        var frame = scene.Frame();

        Assert.True(scene.Pool.TryShapePosition("placed", 0, scene.Client, out var tip));
        var anchor = WorldLightAnchorResolver.Resolve(new WorldAnchor.Placement("placed", 0), scene.Client, scene.Pool, frame.DynamicTransforms.ToArray());

        Assert.NotNull(anchor);
        Assert.Equal(tip, anchor.Value.Position);
        Assert.True(scene.Pool.Rigid.TryShapePose(8, 0, scene.Client, out var pose));
        Assert.Equal(pose.Orientation, anchor.Value.Orientation);
    }
    [Fact]
    public void RigidIsolationKeepsOnlyTheSelectedShapeAndItsPaints() {
        var scene = new Scene(RigidDefinition());

        scene.Deliver(0, 1, "placed");
        var before = scene.Frame();

        scene.Pool.Isolation.Set(new WorldShapeIsolation.Selection("rigid", ["tip"]));
        var isolated = scene.Frame();

        Assert.Equal(2, before.Program.Instructions.Count(i => (i.Op == SdfOp.ShapeBlend)));
        Assert.Single(isolated.Program.Instructions, i => (i.Op == SdfOp.ShapeBlend));
        var painted = isolated.Program.Materials.Where(m => (m.Paints is not null)).ToArray();
        Assert.NotEmpty(painted);
        Assert.All(painted, m => Assert.Single(m.Paints!));
    }
    [InlineData(1)]
    [InlineData(5)]
    [Theory]
    public void ARigidFollowerCarriesOrReseedsWhenPortalHistoryExpires(int crossings) {
        var definition = RigidDefinition();

        definition = definition with {
            LookRowsRaw = [definition.Looks[0] with { Motion = WorldLookMotion.Default with { Dynamics = "ease" } }],
            DynamicsRaw = [new Puck.State.DynamicsRow("ease", 1, 1, 0)],
        };
        var scene = new Scene(definition);

        scene.Deliver(0, 1, "placed");
        var first = scene.Frame().MeshInstances[0].Transform;
        var carry = new WorldPoseCarry(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f), new Vector3(0, 6, 0));
        var expectedPosition = first.Position;
        var expectedOrientation = first.Orientation;

        for (var index = 0; (index < crossings); index++) {
            expectedPosition = carry.Point(expectedPosition);
            expectedOrientation = carry.Attitude(expectedOrientation);
            scene.Deliver(0, ((ulong)(index + 2)), "placed", expectedPosition, EntityContinuity.Carry(carry), expectedOrientation);
        }
        var next = scene.Frame().MeshInstances[0].Transform;

        Assert.True((Vector3.Distance(expectedPosition, next.Position) < 0.0001f));
        Assert.True((MathF.Abs(Quaternion.Dot(expectedOrientation, next.Orientation)) > 0.9999f));
    }

    private static WorldDefinition RigidDefinition() {
        var tip = CreationFixtures.UnitSphereShape with { Id = 0, Name = "tip", Position = new Vector3(0, 2, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f) };
        var creation = CreationFixtures.Prototype(CreationFixtures.Document(name: "rigid", shapes: [tip, tip with { Id = 1, Name = "other", Position = new Vector3(0, 4, 0) }],
            palette: [new PaletteEntryDocument("#FFFFFF", null, null, null, Paints: [
                new PalettePaintDocument(Color: "#FF0000", Parent: "tip", Stroke: 0.01f, Type: SdfSolidPrimitive.Plane),
                new PalettePaintDocument(Color: "#00FF00", Parent: "other", Stroke: 0.01f, Type: SdfSolidPrimitive.Plane),
            ])]), id: "rigid");

        return new WorldDefinition() with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook("rigid", new WorldLookSource.Creation("rigid"), 1, WorldLookMotion.Default)],
            PlacementRowsRaw = [new WorldPlacement("placed", "rigid", Vector3.Zero, YawDegrees: 0, Scale: 1, Inhabit: new WorldPlacementInhabit(null, "rigid", IntentSource.Idle))],
        };
    }

    private sealed class Scene {
        public WorldClient Client { get; }
        public WorldCreationInstances Pool { get; } = new();

        private readonly WorldSceneEmitter? m_local;
        private readonly WorldSessionMirror? m_session;
        private readonly SdfCompositionFrameSource m_frames;

        public Scene(WorldDefinition definition, bool session = false) {
            Client = ClientFixtures.Client(definition);
            if (session) {
                m_session = new WorldSessionMirror(definition);
                m_session.DeliverDefinition(definition, default);
                var emitter = new WorldSessionSceneEmitter(domains: new WorldValueDomainGuard(), mirror: m_session, effectiveCameraName: null);

                m_frames = new SdfCompositionFrameSource([emitter], emitter);
            } else {
                var routes = new WorldSeatAuthorityRouter();

                m_local = new WorldSceneEmitter(Client, Pool, new SilentAudio(), new WorldContinuum(Client, routes, new NoNeighbours()), new WorldTextCatalog());
                m_frames = new SdfCompositionFrameSource([m_local], new Dresser());
            }
        }

        public void Deliver(byte look, ulong tick, string? placement = null, Vector3 position = default, EntityContinuity? continuity = null, Quaternion? orientation = null) {
            var snapshot = new WorldSnapshot(tick, ((int)tick), 1, new[] { new EntitySnapshot(8, position, (orientation ?? Quaternion.Identity), Vector3.One, true, 0, look, 0, (continuity ?? EntityContinuity.Continuous), 1, placement) }, "a");

            Client.DeliverSnapshot(snapshot);
            Client.UpdateRenderPoses(1);
            m_session?.DeliverSnapshot(snapshot);
        }
        public SdfFrame Frame() { m_local?.Tick((1f / 60)); return m_frames.CaptureFrame(64, 64, (1f / 60), 1); }
    }
    private sealed class Dresser : ISdfFrameDresser {
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) => new(Program: program, ProgramChanged: false, Time: 0, Views: []) { DynamicTransforms = transforms, MovedTransforms = moved };
    }
    private sealed class SilentAudio : IWorldAudioCueSink { public void SubmitCue(string eventToken, Vector3? site) { } }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) { neighbour = null; return false; }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) { departedFrom = default; return false; }
    }
}
