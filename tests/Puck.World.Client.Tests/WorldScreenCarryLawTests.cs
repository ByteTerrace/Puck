using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Laws for a carried screen (<see cref="WorldScreen.Carry"/>): while a body wearing the carrying look holds
/// its pose, the screen's picture lies on the front face of the look's box, its right and up the box's as the body
/// holding it looks, and the look emits the box in the screen's material; while the pose does not hold, nothing carries
/// it. The validator refuses a carry whose look, pose or box does not resolve, by name.</summary>
public sealed class WorldScreenCarryLawTests {
    private const string HeldRow = "held";
    private const int ScreenIndex = 0;

    private static readonly Vector3 PictureAt = new(x: 0f, y: 1.2f, z: -0.3f);
    private static readonly Vector3 PictureHalf = new(x: 0.022f, y: 0.0198f, z: 0.001f);

    private static WorldStateRow Held(long raw) => new(
        Name: CellName.Parse(candidate: HeldRow),
        Kind: CellKind.Fixed,
        Cells: [new StateCell(
            Key: CellName.Parse(candidate: "0"),
            Value: CellValue.Fixed(rawBits: raw)
        )]
    );
    private static CreationDocument Holder(string frame = "hold", string picture = "picture") => (CreationFixtures.Rig(
        drivers: [],
        (CreationFixtures.Shape(type: SdfSolidPrimitive.Box, scale: PictureHalf) with {
            Id = 1,
            Name = picture,
            Position = PictureAt,
        }),
        (CreationFixtures.Shape(type: SdfSolidPrimitive.Sphere) with {
            Id = 2,
            Name = "ball",
            Group = 1,
        })
    ) with {
        Frames = [new FrameDocument(
            Name: frame,
            Transforms: [new FrameTransformDocument(Id: 1, Position: PictureAt, Rotation: Quaternion.Identity)]
        )],
    });
    private static WorldLook HolderLook(params string[] poses) => new(
        Name: "holder",
        Source: new WorldLookSource.Creation(PrototypeId: "rig"),
        Scale: 1f,
        Motion: (WorldLookMotion.Default with {
            Poses = poses.ToDictionary(keySelector: pose => pose, elementSelector: _ => $"state.{HeldRow}.$body"),
        })
    );
    private static WorldScreen CarriedScreen(WorldScreenCarry carry) => new(
        HalfDepth: 0.001f,
        HalfHeight: 0.0198f,
        HalfWidth: 0.022f,
        Index: ScreenIndex,
        Origin: new DocumentVector3(value: new Vector3(x: 5f, y: 0.1f, z: 5f)),
        Right: new DocumentVector3(value: Vector3.UnitX),
        Round: 0f,
        Route: WorldScreenRoute.Passive,
        Source: new WorldScreenSource.None(),
        Up: new DocumentVector3(value: Vector3.UnitY),
        Carry: carry
    );
    // The poses are the frame names a body can hold; the creation's timeline names only the frame.
    private static WorldDefinition World(WorldScreenCarry carry, long held = 0L, string frame = "hold", string[]? poses = null) => (CreationFixtures.RigWorld(
        creation: Holder(frame: frame),
        state: [Held(raw: held)]
    ) with {
        LookRowsRaw = [HolderLook(poses: (poses ?? ["hold"]))],
        ScreensRaw = [CarriedScreen(carry: carry)],
    });
    private static string Refusal(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(
        definition: definition,
        reason: out var reason
    )
        ? string.Empty
        : reason
    );
    // Packs one frame of body 0 wearing the holder at the origin, facing the engine's default heading.
    // The surface the frame's program declares for the carried screen; the slots above it are the reserved band's.
    private static SdfScreenSurface Surface(SdfFrame frame) => Assert.Single(
        collection: frame.Program.ScreenSurfaces,
        predicate: surface => (surface.ScreenIndex == ScreenIndex)
    );
    // The stamp wears the world's creation unless a stamped one is given, as a live edit leaves it out of step.
    private static (WorldCreationInstances Pool, WorldClient Client) Pack(WorldDefinition world, WorldPrototype? stamped = null) {
        var creation = (stamped ?? WorldDefinitionRows.FindCreation(creations: world.Creations, id: "rig")!);
        var client = ClientFixtures.Client(definition: world);
        var pool = new WorldCreationInstances();

        pool.Reconcile(
            bodyStamps: [new WorldCreationInstances.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: world.Looks[0])],
            creations: world.Creations,
            dynamics: [],
            placements: []
        );
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: new[] { new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Facts: Physics.Motion.BodyFacts.Grounded,
                Generation: 1,
                Index: 0,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: Vector3.Zero
            ) },
            Revision: 0,
            StepTicks: 1UL,
            Tick: 1UL
        ));
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: (1f / 60f));

        var moved = new SdfMovedTransforms();
        var transforms = new DynamicTransform[pool.DynamicSlotCount];

        moved.Begin(everything: false, tableRows: transforms.Length);
        pool.PackTransforms(client: client, moved: moved, parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f), slotBase: 0, transforms: transforms);

        return (pool, client);
    }

    [Fact]
    public void WhileThePoseHoldsThePictureLiesOnTheBoxsFrontFace_ControlItIsNotCarriedOtherwise() {
        var carry = new WorldScreenCarry(Look: "holder", Pose: "hold", Shape: "picture");
        var heldWorld = World(carry: carry, held: FixedQ4816.One.Value);

        Assert.Equal(expected: string.Empty, actual: Refusal(definition: heldWorld));

        var (pool, client) = Pack(world: heldWorld);

        Assert.True(condition: pool.TryCarriedScreen(screen: heldWorld.Screens[0], client: client, carried: out var carried));

        // The box sits at its author position turned by the creation frame's half turn about up, and its front face
        // (author -Z) faces the turned +Z, so the picture's normal is the turned box's -Z and its right the turned -X.
        var turned = new Vector3(x: -PictureAt.X, y: PictureAt.Y, z: -PictureAt.Z);
        var right = (Vector3)carried.Right;
        var up = (Vector3)carried.Up;
        var normal = Vector3.Cross(vector1: right, vector2: up);

        Assert.Equal(expected: PictureHalf.X, actual: carried.HalfWidth, tolerance: 1e-6f);
        Assert.Equal(expected: PictureHalf.Y, actual: carried.HalfHeight, tolerance: 1e-6f);
        Assert.Equal(expected: 0f, actual: Vector3.Distance(value1: (turned + (normal * PictureHalf.Z)), value2: carried.Origin), tolerance: 1e-4f);
        Assert.Equal(expected: 1f, actual: Vector3.Dot(vector1: up, vector2: Vector3.UnitY), tolerance: 1e-4f);
        Assert.Equal(expected: 1f, actual: Math.Abs(Vector3.Dot(vector1: normal, vector2: Vector3.UnitZ)), tolerance: 1e-4f);

        // The look draws the box in the screen's material: a build with the surface declared accepts it.
        var builder = new SdfProgramBuilder()
            .ScreenSurface(
                halfHeight: carried.HalfHeight,
                halfWidth: carried.HalfWidth,
                screenIndex: ScreenIndex,
                worldOrigin: carried.Origin,
                worldRight: right,
                worldUp: up
            );

        pool.Emit(
            builder: builder,
            colors: new WorldBakedColors(mirror: client.StateMirror),
            maxPlacementScale: 1f,
            probeWorstCase: false,
            screens: heldWorld.Screens,
            slotBase: 0
        );
        Assert.Single(collection: builder.Build().ScreenSurfaces);

        var (restingPool, restingClient) = Pack(world: World(carry: carry));

        Assert.False(condition: restingPool.TryCarriedScreen(screen: heldWorld.Screens[0], client: restingClient, carried: out _));
        Assert.False(condition: restingPool.TryCarriedScreen(screen: (heldWorld.Screens[0] with { Carry = null }), client: restingClient, carried: out _));

        // The frame the residency polls: the box's face while held, the authored frame at rest, nothing for any other slot.
        var held = pool.ScreenSurfaceTransforms(client: client);
        var resting = restingPool.ScreenSurfaceTransforms(client: restingClient);

        Assert.Equal(expected: SdfProgramBuilder.MaxScreenSurfaces, actual: held.Count);
        Assert.Null(@object: held[ScreenIndex + 1]());
        Assert.Equal(expected: ((Vector3)carried.Origin).X, actual: held[ScreenIndex]()!.Value.Origin.X, tolerance: 1e-6f);
        Assert.Equal(expected: 1f, actual: held[ScreenIndex]()!.Value.Right.Length(), tolerance: 1e-5f);
        Assert.Equal(expected: 1f, actual: held[ScreenIndex]()!.Value.Up.Length(), tolerance: 1e-5f);
        Assert.Equal(expected: new SdfScreenSurfaceTransform(Origin: new Vector3(x: 5f, y: 0.1f, z: 5f), Right: Vector3.UnitX, Up: Vector3.UnitY, HalfWidth: 0.022f, HalfHeight: 0.0198f), actual: resting[ScreenIndex]());
        Assert.Same(expected: heldWorld.Screens[0], actual: restingPool.Carried(screen: heldWorld.Screens[0], client: restingClient));
        Assert.NotSame(expected: heldWorld.Screens[0], actual: pool.Carried(screen: heldWorld.Screens[0], client: client));
        Assert.Throws<ArgumentNullException>(testCode: () => pool.ScreenSurfaceTransforms(client: null!));
    }
    [Fact]
    public void ACarryWhoseLookPoseOrBoxDoesNotResolveIsRefusedByName() {
        foreach (var (carry, refusal) in new (WorldScreenCarry, string)[] {
            (new WorldScreenCarry(Look: "nobody", Pose: "hold", Shape: "picture"), "screens[0].carry.look 'nobody' names no creation look row"),
            (new WorldScreenCarry(Look: "holder", Pose: "wave", Shape: "picture"), "screens[0].carry.pose 'wave' is not a pose of look 'holder'"),
            (new WorldScreenCarry(Look: "holder", Pose: "hold", Shape: "ball"), "screens[0].carry.shape 'ball' must be an ungrouped box"),
            (new WorldScreenCarry(Look: "holder", Pose: "hold", Shape: "missing"), "screens[0].carry.shape 'missing' names no shape"),
        }) {
            Assert.Contains(
                actualString: Refusal(definition: World(carry: carry)),
                expectedSubstring: refusal,
                comparisonType: StringComparison.Ordinal
            );
        }
    }
    // A carry is read against the creation the stamp wears, which a live edit can leave out of step with the look's: a
    // timeline without the carry's pose frame, or without its box, carries nothing and draws the box in no screen material.
    [Fact]
    public void AStampWhoseCreationLacksThePoseFrameOrTheBoxCarriesNothing() {
        var carry = new WorldScreenCarry(Look: "holder", Pose: "hold", Shape: "picture");
        var world = World(carry: carry, held: FixedQ4816.One.Value, frame: "wave", poses: ["hold", "wave"]);
        var (framePool, frameClient) = Pack(world: world);

        Assert.False(condition: framePool.TryCarriedScreen(screen: world.Screens[0], client: frameClient, carried: out _));

        var heldWorld = World(carry: carry, held: FixedQ4816.One.Value);
        var (boxPool, boxClient) = Pack(world: heldWorld, stamped: (heldWorld.Creations[^1] with { Document = Holder(picture: "other") }));

        Assert.False(condition: boxPool.TryCarriedScreen(screen: heldWorld.Screens[0], client: boxClient, carried: out _));
    }
    // The scene emitter watches which screens a body carries, rebuilds the program on a pick-up and a put-down, and has
    // the carried screen's surface at the box's face and no slab drawn for it.
    [Fact]
    public void APickUpAndAPutDownRebuildTheSceneWithTheSurfaceAtTheBoxThenAsAuthored() {
        var carry = new WorldScreenCarry(Look: "holder", Pose: "hold", Shape: "picture");
        var scene = new Scene(carry: carry);
        var resting = scene.Run();
        var authored = Surface(frame: resting);

        Assert.Equal(expected: new Vector3(x: 5f, y: 0.1f, z: 5f), actual: authored.Origin);

        scene.Hold(raw: FixedQ4816.One.Value);

        var lifted = scene.Run();
        var surface = Surface(frame: lifted);

        Assert.True(condition: scene.Pool.TryCarriedScreen(screen: scene.Client.Definition.Screens[0], client: scene.Client, carried: out var carried));
        Assert.Equal(expected: 0f, actual: Vector3.Distance(value1: surface.Origin, value2: carried.Origin), tolerance: 1e-4f);
        Assert.True(condition: (lifted.Program.InstructionCount < resting.Program.InstructionCount), userMessage: "the carried screen still draws its slab.");

        scene.Hold(raw: 0L);

        var dropped = scene.Run();

        Assert.Equal(expected: authored, actual: Surface(frame: dropped));
        Assert.Equal(expected: resting.Program.InstructionCount, actual: dropped.Program.InstructionCount);
    }

    // The real scene emitter over one client, composed as a presenter composes it, with body 0 wearing the holder.
    private sealed class Scene {
        private const float FrameSeconds = (1f / 60f);

        private readonly Guid m_activation = Guid.NewGuid();
        private readonly WorldScreenCarry m_carry;
        private readonly WorldSceneEmitter m_emitter;
        private readonly SdfCompositionFrameSource m_frames;
        private long m_sequence = 1;
        private ulong m_tick;

        public Scene(WorldScreenCarry carry) {
            m_carry = carry;

            var definition = World(carry: carry);
            var routes = new WorldSeatAuthorityRouter();

            Client = new WorldClient(
                composition: new WorldCompositionState(),
                definition: definition,
                roster: new PlayerRoster(
                    definition: definition,
                    link: new SilentLink(),
                    seatBindings: new WorldSeatBindings(definition: definition)
                ),
                seatRouter: routes
            );
            Client.DeliverDefinition(definition: definition, version: new WorldDocumentVersion(Activation: m_activation, Sequence: m_sequence));
            m_emitter = new WorldSceneEmitter(
                animator: Pool,
                audio: new SilentAudio(),
                client: Client,
                continuum: new WorldContinuum(
                    Client,
                    routes,
                    new NoNeighbours()
                ),
                text: new WorldTextCatalog(source: new(
                    Definition: definition,
                    SourcePath: "unused.world.json"
                ))
            );
            m_frames = new SdfCompositionFrameSource(
                dresser: new BareDresser(),
                emitters: [m_emitter]
            );
        }

        public WorldClient Client { get; }
        public WorldCreationInstances Pool { get; } = new();

        // Delivers the held cell's new value the way a mutation's definition delivery reaches the client.
        public void Hold(long raw) => Client.DeliverDefinition(
            definition: World(carry: m_carry, held: raw),
            version: new WorldDocumentVersion(Activation: m_activation, Sequence: ++m_sequence)
        );
        // Runs frames until the pose the stamp latched and the revision watching it have settled, and returns the last.
        public SdfFrame Run() {
            SdfFrame? frame = null;

            for (var count = 0; (count < 3); count++) {
                Client.DeliverSnapshot(snapshot: new WorldSnapshot(
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
                        Position: Vector3.Zero
                    )]),
                    Revision: 1,
                    StepTicks: 1UL,
                    Tick: ++m_tick
                ));
                Client.UpdateRenderPoses(alpha: 1f);
                m_emitter.Tick(deltaSeconds: FrameSeconds);
                Pool.Tick(deltaSeconds: FrameSeconds);
                frame = m_frames.CaptureFrame(
                    deltaSeconds: FrameSeconds,
                    height: 64,
                    interpolationAlpha: 0f,
                    width: 64
                );
            }

            return frame!;
        }
    }
    // Dresses nothing: the law reads the program the emitter composed.
    private sealed class BareDresser : ISdfFrameDresser {
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) => new(
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
