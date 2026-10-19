using System.Numerics;

using Puck.Assets.Documents;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A delivered edit of a creation's rig rows (a shape's <c>parent</c>, a first <c>swings</c> row) moves the
/// shape's slot on the frames after the delivery, through the real scene emitter and composition, whether the program
/// rebuilds in the frame or in the background, and <see cref="WorldStampPool.TryBodyRig"/> reads the new chain
/// back.</summary>
public sealed class WorldStampPoolLiveRigEditLawTests {
    private const float FrameSeconds = (1f / 60f);

    // The body's registration takes the pool's first entry; its shape slots follow its root.
    private static int ArmSlot => (WorldRigCatalog.DynamicTransformCapacity + 2);

    private static ShapeDocument Arm(string? parent = null, IReadOnlyList<ShapeSwingDocument>? swings = null) => new(
        Id: 1,
        Name: "arm",
        Type: SdfSolidPrimitive.Sphere,
        Position: new Vector3(x: 0.4f, y: 0.2f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: (Vector3.One * 0.1f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Parent: parent,
        Swings: swings
    );
    private static WorldPrototype Creation(ShapeDocument arm) => CreationDomainParentLawTests.Prototype(document: new CreationDocument(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: [new ShapeDocument(
            Id: 0,
            Name: "torso",
            Type: SdfSolidPrimitive.Capsule,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 0.1f, y: 0.5f, z: 0.1f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0,
            Swings: [Swing()]
        ), arm],
        Frames: null,
        Drivers: [new CreationDriverDocument(
            Cadence: 1f,
            Name: "sway",
            Signal: CreationDriverDocument.SignalTime
        )]
    ));
    private static ShapeSwingDocument Swing() => new(
        Amplitude: 1f,
        Axis: Vector3.UnitZ,
        Driver: "sway",
        Pivot: Vector3.UnitY
    );

    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void ARigEditMovesTheShapeOnTheFramesAfterItsDelivery(bool reparent, bool background) {
        var scene = new Scene(creation: Creation(arm: Arm()), background: background);
        var torso = scene.Travel(slot: (ArmSlot - 1));
        var still = scene.Travel(slot: ArmSlot);

        Assert.True(condition: ((torso > 0.05f) && (still < 1e-5f)), userMessage: $"before the edit the torso travelled {torso} and the arm {still}.");

        scene.Edit(creation: Creation(arm: (reparent ? Arm(parent: "torso") : Arm(swings: [Swing()]))));

        var moving = scene.Travel(slot: ArmSlot);

        Assert.True(condition: (moving > 0.05f), userMessage: $"after the edit the arm travelled only {moving}.");
        Assert.True(condition: scene.Pool.TryBodyRig(bodyIndex: 0, shapeName: "arm", state: out var state));

        var shape = state!.Shape!.Value;

        Assert.Equal(expected: (reparent ? "torso" : null), actual: shape.Parent);
        Assert.True(condition: ((shape.TurnRadians > 0f) || (shape.Delta != Vector3.Zero)), userMessage: "the read-back shows no chained delta on a swinging arm.");
        Assert.Equal(expected: -1, actual: (scene.Pool.TryBodyRig(bodyIndex: 0, shapeName: "absent", state: out var absent) ? absent!.Shape!.Value.Index : 0));
    }
    // TryShapePosition answers where the frame drew each shape — the torso's own swing, and the arm carried by its
    // parent chain — so a placement anchor (the audio director, the overlay facts, a camera view) rides the moving part.
    [Fact]
    public void AShapePositionFollowsTheSwingTheFrameDrew() {
        const string Seat = "seat";

        var scene = new Scene(creation: Creation(arm: Arm(parent: "torso")), background: false, placementId: Seat);
        var rest = Arm().Position.Value;
        var travel = 0f;

        for (var frame = 0; (frame < 120); frame++) {
            scene.Frame();

            foreach (var (shapeId, slot) in ((int, int)[])[(0, (ArmSlot - 1)), (1, ArmSlot)]) {
                Assert.True(condition: scene.Pool.TryShapePosition(client: scene.Client, placementId: Seat, position: out var read, shapeId: shapeId));

                var drawn = scene.Drawn(slot: slot);

                Assert.True(condition: (Vector3.Distance(value1: read, value2: drawn) < 1e-5f), userMessage: $"frame {frame}: shape {shapeId} read {read}, drawn at {drawn}.");

                if (shapeId == 1) {
                    travel = MathF.Max(x: travel, y: Vector3.Distance(value1: read, value2: rest));
                }
            }
        }

        Assert.True(condition: (travel > 0.05f), userMessage: $"the arm read only {travel} from its rest position.");
    }

    // The real scene emitter over one client, composed as a presenter composes it, with body 0 wearing the creation.
    private sealed class Scene {
        private readonly Guid m_activation = Guid.NewGuid();
        private readonly WorldClient m_client;
        private readonly RecordingDresser m_dresser = new();
        private readonly WorldSceneEmitter m_emitter;
        private readonly SdfCompositionFrameSource m_frames;
        private readonly string? m_placementId;
        private long m_sequence = 1;
        private ulong m_tick;

        public Scene(WorldPrototype creation, bool background, string? placementId = null) {
            m_placementId = placementId;

            var definition = CreationDomainParentLawTests.Definition(creation: creation);

            // An inhabitant's placement row, which the body's stamp census resolves its creation through.
            if (placementId is not null) {
                definition = (definition with {
                    PlacementRowsRaw = [new WorldPlacement(
                        Id: placementId,
                        Position: new DocumentVector3(value: Vector3.Zero),
                        PrototypeId: creation.Id,
                        Scale: 1f,
                        YawDegrees: 0f
                    )],
                });
            }
            var routes = new WorldSeatAuthorityRouter();

            m_client = new WorldClient(
                composition: new WorldCompositionState(),
                definition: definition,
                roster: new PlayerRoster(
                    definition: definition,
                    link: new SilentLink(),
                    seatBindings: new WorldSeatBindings(definition: definition)
                ),
                seatRouter: routes
            );
            m_client.DeliverDefinition(definition: definition, version: new WorldDocumentVersion(Activation: m_activation, Sequence: m_sequence));
            m_emitter = new WorldSceneEmitter(
                animator: Pool,
                audio: new SilentAudio(),
                client: m_client,
                continuum: new WorldContinuum(
                    m_client,
                    routes,
                    new NoNeighbours()
                ),
                text: new WorldTextCatalog(source: new(
                    Definition: definition,
                    SourcePath: "unused.world.json"
                ))
            );
            m_frames = new SdfCompositionFrameSource(
                dresser: m_dresser,
                emitters: [m_emitter]
            ) { BuildsInBackground = background };
        }

        public WorldStampPool Pool { get; } = new();

        // Delivers the creation's edit the way a mutation's definition delivery reaches the client.
        public void Edit(WorldPrototype creation) => m_client.DeliverDefinition(
            definition: CreationDomainParentLawTests.Definition(creation: creation),
            version: new WorldDocumentVersion(Activation: m_activation, Sequence: ++m_sequence)
        );
        // Runs two seconds of frames and returns the slot's furthest travel from where the first of them drew it.
        public float Travel(int slot) {
            Frame();

            var start = m_dresser.Transforms[slot].Position;
            var travel = 0f;

            for (var frame = 0; (frame < 120); frame++) {
                Frame();
                travel = MathF.Max(x: travel, y: Vector3.Distance(value1: start, value2: m_dresser.Transforms[slot].Position));
            }

            return travel;
        }

        public WorldClient Client => m_client;

        // The position the last frame drew the slot at.
        public Vector3 Drawn(int slot) => m_dresser.Transforms[slot].Position;
        public void Frame() {
            m_client.DeliverSnapshot(snapshot: new WorldSnapshot(
                Authority: "a",
                Entries: ((EntitySnapshot[])[new EntitySnapshot(
                    Active: true,
                    BodyColor: Vector3.One,
                    CatalogRig: 0,
                    Continuity: EntityContinuity.Continuous,
                    Generation: 1,
                    Index: 0,
                    Kit: 0,
                    Look: 0,
                    Orientation: Quaternion.Identity,
                    PlacementId: m_placementId,
                    Position: Vector3.Zero
                )]),
                Revision: 1,
                StepTicks: 1UL,
                Tick: ++m_tick
            ));
            m_client.UpdateRenderPoses(alpha: 1f);
            m_emitter.Tick(deltaSeconds: FrameSeconds);
            Pool.Tick(deltaSeconds: FrameSeconds);
            _ = m_frames.CaptureFrame(
                deltaSeconds: FrameSeconds,
                height: 64,
                interpolationAlpha: 0f,
                width: 64
            );
        }
    }
    // Keeps the transforms each frame hands it.
    private sealed class RecordingDresser : ISdfFrameDresser {
        public DynamicTransform[] Transforms { get; private set; } = [];

        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            Transforms = transforms;

            return new SdfFrame(
                Program: program,
                ProgramChanged: false,
                Time: 0f,
                Views: []
            ) {
                DynamicTransforms = transforms,
                MeshDraws = meshDraws,
                MeshDrawsRevision = meshDrawsRevision,
                MovedTransforms = moved,
            };
        }
    }
    private sealed class SilentAudio : IWorldAudioCueSink {
        public void SubmitCue(string eventToken, Vector3? site) { }
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }
}
