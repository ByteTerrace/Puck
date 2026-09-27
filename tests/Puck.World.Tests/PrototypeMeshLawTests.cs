using System.Numerics;
using System.Text;
using Puck.Assets.Documents;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a prototype's inline mesh round-trips through the document, every malformed mesh is refused by name, each
/// static placement instance reaches the frame's mesh draws as the prototype's engine-frame triangles under the
/// placement's scale, yaw and position, and a stamp (an animated or attached placement) draws it at its root this frame.
/// <para>Each refusal is paired with a control that differs in one authored field.</para>
/// </summary>
public sealed class PrototypeMeshLawTests {
    private const string PlacementId = "slab-at-gate";
    private const string PrototypeId = "slab";

    // A unit quad in the author frame's XY plane, facing +Z, on the palette's second (blue) entry.
    private static WorldPrototypeMesh Quad() => new(
        Indices: [0U, 1U, 2U, 0U, 2U, 3U],
        Material: 1,
        Vertices: [
            new Vector3(x: 0f, y: 0f, z: 0f),
            new Vector3(x: 1f, y: 0f, z: 0f),
            new Vector3(x: 1f, y: 1f, z: 0f),
            new Vector3(x: 0f, y: 1f, z: 0f),
        ]
    );
    private static WorldPrototype Slab(WorldPrototypeMesh? mesh) => (CreationFixtures.Prototype(document: CreationFixtures.Document(
        name: PrototypeId,
        palette: CreationFixtures.GreyAndBlue,
        shapes: [CreationFixtures.UnitSphereShape]
    )) with { Mesh = mesh });
    private static WorldPlacement Placement(WorldPlacementInhabit? inhabit = null) => new(
        Id: PlacementId,
        Inhabit: inhabit,
        Position: new DocumentVector3(value: new Vector3(
            x: 2f,
            y: 0f,
            z: -3f
        )),
        PrototypeId: PrototypeId,
        Scale: 2f,
        YawDegrees: 90f
    );
    // An inhabited placement's body takes one population slot past the local seats, as the other inhabit laws grant it.
    private static WorldDefinition With(WorldPrototypeMesh? mesh, WorldPlacementInhabit? inhabit = null) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [Slab(mesh: mesh)],
            PlacementRowsRaw = [Placement(inhabit: inhabit)],
            PopulationRaw = ((inhabit is null)
                ? document.PopulationRaw
                : (document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) })),
        });
    }
    private static WorldPlacementInhabit Inhabit() => new(
        Distribution: WorldDistribution.Default,
        Kit: Fixtures.SeatKitName,
        Look: null,
        Source: IntentSource.Idle
    );
    // The slab with a timeline frame that lifts its sphere, so its placement is an animated stamp.
    private static WorldDefinition Animated(WorldPrototypeMesh? mesh) => (With(mesh: mesh) with {
        CreationsRaw = [(CreationFixtures.Prototype(document: (CreationFixtures.Document(
            name: PrototypeId,
            palette: CreationFixtures.GreyAndBlue,
            shapes: [CreationFixtures.UnitSphereShape]
        ) with { Frames = [new FrameDocument(Name: "lift", Transforms: [new FrameTransformDocument(Id: CreationFixtures.UnitSphereShape.Id, Position: (CreationFixtures.UnitSphereShape.Position.Value + Vector3.UnitY), Rotation: CreationFixtures.UnitSphereShape.Rotation, Scale: CreationFixtures.UnitSphereShape.Scale)])] })) with { Mesh = mesh })],
    });
    // The slab's placement attached to body 0, one unit above it.
    private static WorldDefinition Attached(WorldPrototypeMesh? mesh) {
        var definition = With(mesh: mesh);

        return (definition with {
            PlacementRowsRaw = [definition.Placements.Single() with {
                Attach = new WorldPlacementAttach(
                    BodyIndex: 0,
                    LocalOffset: new DocumentVector3(value: new Vector3(
                        x: 0f,
                        y: 1f,
                        z: 0f
                    ))
                ),
            }],
        });
    }
    private static List<SdfMeshDraw> Draws(WorldDefinition definition) {
        var draws = new List<SdfMeshDraw>();

        WorldPlacementStamper.EmitStatic(
            builder: new SdfProgramBuilder(),
            creations: definition.Creations,
            definition: definition,
            meshDraws: draws,
            placements: definition.Placements
        );

        return draws;
    }

    /// <summary>The mesh serializes as <c>mesh</c> with its vertices, indices and material, and parses back equal; a
    /// prototype without one writes no member.</summary>
    [Fact]
    public void TheDocumentRoundTripsItsMesh() {
        var definition = With(mesh: Quad());
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition);
        var reparsed = WorldDefinitionSerialization.Deserialize(utf8Json: bytes).Creations.Single().Mesh;

        Assert.Contains(expectedSubstring: "\"mesh\"", actualString: Encoding.UTF8.GetString(bytes: bytes));
        Assert.NotNull(@object: reparsed);
        Assert.Equal(expected: Quad().Vertices, actual: reparsed.Vertices);
        Assert.Equal(expected: Quad().Indices, actual: reparsed.Indices);
        Assert.Equal(expected: 1, actual: reparsed.Material);
        Assert.DoesNotContain(expectedSubstring: "\"mesh\"", actualString: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: With(mesh: null))));
    }
    /// <summary>DENIAL: every malformed mesh, each by name. CONTROL: the well-formed quad validates.</summary>
    [Fact]
    public void AMalformedMeshIsRefusedByName() {
        var quad = Quad();

        Laws.Validates(definition: With(mesh: quad), locally: true);
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [], Vertices = [] })), locally: true, needle: "prototypes[0].mesh is empty");
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [0U, 1U, 2U, 0U] })), locally: true, needle: "prototypes[0].mesh.indices holds 4 entries, not a whole number of triangles");
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [0U, 1U, 4U] })), locally: true, needle: "prototypes[0].mesh.indices[2] names vertex 4, past the mesh's 4 vertices");
        Laws.Refuses(definition: With(mesh: (quad with { Vertices = [quad.Vertices[0], quad.Vertices[1], new Vector3(x: float.NaN, y: 1f, z: 0f), quad.Vertices[3]] })), locally: true, needle: "prototypes[0].mesh.vertices[2] is not finite");
        Laws.Refuses(definition: With(mesh: (quad with { Indices = [0U, 1U, 1U] })), locally: true, needle: "prototypes[0].mesh triangle 0 is degenerate: it names vertex 1 twice");
        Laws.Refuses(definition: With(mesh: (quad with { Vertices = [quad.Vertices[0], quad.Vertices[1], new Vector3(x: 2f, y: 0f, z: 0f), quad.Vertices[3]], Indices = [0U, 1U, 2U] })), locally: true, needle: "prototypes[0].mesh triangle 0 is degenerate: its vertices 0, 1 and 2 enclose no area");
        Laws.Refuses(definition: With(mesh: (quad with { Material = 2 })), locally: true, needle: "prototypes[0].mesh.material 2 names no entry of the creation's 2-entry palette");
    }
    /// <summary>A mesh validates wherever its creation renders through the stamp pool: on an animated creation, under an
    /// inhabited placement and under an attached one.</summary>
    [Fact]
    public void AMeshValidatesOnEveryStamp() {
        Laws.Validates(definition: Animated(mesh: Quad()), locally: true);
        Laws.Validates(definition: With(inhabit: Inhabit(), mesh: Quad()), locally: true);
        Laws.Validates(definition: Attached(mesh: Quad()), locally: true);
    }
    /// <summary>An animated placement's mesh draws from the stamp pool at the placement's root, the pose a static
    /// placement's draw takes (scale 2, yaw 90°, position (2, 0, -3)), and not from the static path. A frame in which
    /// nothing moved hands the engine the list and revision it already has.</summary>
    [Fact]
    public void AnAnimatedPlacementDrawsItsMeshAtItsRoot() {
        var definition = Animated(mesh: Quad());
        var scene = new Scene(definition: definition);

        Assert.Empty(collection: Draws(definition: definition));

        var first = scene.Frame();
        var draw = Assert.Single(collection: first);

        Assert.Equal(expected: (Assert.Single(collection: new Scene(definition: Animated(mesh: (Quad() with { Material = 0 }))).Frame()).Material + 1), actual: draw.Material);
        AssertNear(expected: new Vector3(x: 2f, y: 0f, z: -1f), actual: Vector3.Transform(position: draw.Mesh.Positions.Span[1], matrix: draw.ObjectToWorld));
        AssertNear(expected: new Vector3(x: 2f, y: 2f, z: -3f), actual: Vector3.Transform(position: draw.Mesh.Positions.Span[3], matrix: draw.ObjectToWorld));
        var revision = scene.Revision;

        Assert.Same(expected: first, actual: scene.Frame());
        Assert.Equal(expected: revision, actual: scene.Revision);
        Assert.Empty(collection: new Scene(definition: Animated(mesh: null)).Frame());
    }
    /// <summary>A moving mesh stamp allocates nothing per frame: over frames that move the body under an attached mesh
    /// placement, the scene allocates exactly what it does for the same placement without a mesh, because the pool and
    /// every composition rewrite their draw lists in place under a revision.</summary>
    [Fact]
    public void AMovingMeshStampAllocatesNothingPerFrame() {
        static long Allocated(WorldPrototypeMesh? mesh) {
            var scene = new Scene(definition: Attached(mesh: mesh));

            void Move(int frame) {
                scene.Deliver(
                    position: new Vector3(x: (frame * 0.25f), y: 0f, z: 0f),
                    tick: ((ulong)(frame + 1))
                );
                _ = scene.Frame();
            }

            for (var frame = 0; (frame < 64); frame++) {
                Move(frame: frame);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var frame = 64; (frame < 96); frame++) {
                Move(frame: frame);
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before);
        }

        var meshless = Allocated(mesh: null);

        Assert.Equal(expected: meshless, actual: Allocated(mesh: Quad()));
    }
    /// <summary>An attached placement's mesh draws at its body's pose composed with the facet's offset (0, 1, 0), under
    /// the row's scale 2, and follows the body: a moved body moves the draws' revision with the draw moved. While the
    /// body is inactive the row draws nothing.</summary>
    [Fact]
    public void AnAttachedPlacementsMeshFollowsItsBody() {
        var scene = new Scene(definition: Attached(mesh: Quad()));

        Assert.Empty(collection: scene.Frame());

        scene.Deliver(
            position: new Vector3(x: 5f, y: 0f, z: 0f),
            tick: 1UL
        );

        var rest = Assert.Single(collection: scene.Frame());

        // Author (0, 1, 0) is engine (0, 1, 0), scaled to (0, 2, 0), lifted by the offset to (0, 3, 0) and carried to
        // the body at (5, 0, 0).
        AssertNear(expected: new Vector3(x: 5f, y: 3f, z: 0f), actual: Vector3.Transform(position: rest.Mesh.Positions.Span[3], matrix: rest.ObjectToWorld));

        _ = scene.Frame();

        var still = scene.Revision;

        scene.Deliver(
            position: new Vector3(x: 8f, y: 0f, z: 0f),
            tick: 2UL
        );

        var moved = scene.Frame();

        Assert.NotEqual(expected: still, actual: scene.Revision);
        Assert.NotEqual(expected: rest.ObjectToWorld, actual: Assert.Single(collection: moved).ObjectToWorld);
    }
    /// <summary>A static placement reaches the mesh draws once, as the prototype's triangles turned into the engine
    /// frame (X and Z negated) under the placement's scale 2, yaw 90° and position (2, 0, -3), derived here from the
    /// axes rather than from the stamper's matrix; the material is the palette entry the mesh names, one past entry 0's.
    /// A prototype without a mesh draws nothing.</summary>
    [Fact]
    public void AStaticPlacementDrawsThePrototypeMesh() {
        var draws = Draws(definition: With(mesh: Quad()));
        var draw = Assert.Single(collection: draws);

        Assert.Equal(expected: 2, actual: draw.Mesh.TriangleCount);
        Assert.Equal(expected: new Vector3(x: -1f, y: 0f, z: 0f), actual: draw.Mesh.Positions.Span[1]);

        // Author (1, 0, 0) is engine (-1, 0, 0); scaled by 2 it is (-2, 0, 0); a +90° yaw about +Y takes -X to +Z,
        // so (0, 0, 2); placed at (2, 0, -3) it lands at (2, 0, -1). Author (0, 1, 0) stays on the axis: (2, 2, -3).
        AssertNear(expected: new Vector3(x: 2f, y: 0f, z: -1f), actual: Vector3.Transform(position: draw.Mesh.Positions.Span[1], matrix: draw.ObjectToWorld));
        AssertNear(expected: new Vector3(x: 2f, y: 2f, z: -3f), actual: Vector3.Transform(position: draw.Mesh.Positions.Span[3], matrix: draw.ObjectToWorld));

        var entryZero = Assert.Single(collection: Draws(definition: With(mesh: (Quad() with { Material = 0 }))));

        Assert.Equal(expected: (entryZero.Material + 1), actual: draw.Material);
        Assert.Empty(collection: Draws(definition: With(mesh: null)));
    }

    // The real scene emitter over one client, composed and dressed as a presenter composes it, reporting each frame's
    // composed mesh draws.
    private sealed class Scene {
        private const float FrameSeconds = (1f / 60f);

        private readonly WorldClient m_client;
        private readonly RecordingDresser m_dresser = new();
        private readonly WorldSceneEmitter m_emitter;
        private readonly SdfCompositionFrameSource m_frames;

        public Scene(WorldDefinition definition) {
            var routes = new WorldSeatAuthorityRouter();

            m_client = new WorldClient(
                composition: new WorldCompositionState(),
                definition: definition,
                roster: new PlayerRoster(
                    definition: definition,
                    link: new SilentLink(definition: definition),
                    seatBindings: new WorldSeatBindings(definition: definition)
                ),
                seatRouter: routes
            );
            m_emitter = new WorldSceneEmitter(
                anchor: new WorldPerceptionAnchor(),
                animator: new WorldStampPool(),
                audio: new SilentAudio(),
                client: m_client,
                continuum: new WorldContinuum(
                    m_client,
                    routes,
                    new NoNeighbours()
                ),
                settings: new WorldRenderSettings(defaults: definition.Render),
                text: new WorldTextCatalog(source: new(
                    Definition: definition,
                    SourcePath: "unused.world.json"
                ))
            );
            m_frames = new SdfCompositionFrameSource(
                dresser: m_dresser,
                emitters: [m_emitter]
            );
        }

        // Body 0 alone, active at the position.
        public void Deliver(Vector3 position, ulong tick) => m_client.DeliverSnapshot(snapshot: new WorldSnapshot(
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
                Position: position
            )]),
            Revision: 1,
            StepTicks: 1UL,
            Tick: tick
        ));
        public IReadOnlyList<SdfMeshDraw> Frame() {
            m_emitter.Tick(deltaSeconds: FrameSeconds);
            _ = m_frames.CaptureFrame(
                deltaSeconds: FrameSeconds,
                height: 64,
                interpolationAlpha: 0f,
                width: 64
            );

            return m_dresser.MeshDraws;
        }

        // The revision of the draws the last frame composed.
        public long Revision => m_dresser.MeshDrawsRevision;
    }
    // Keeps the composed mesh draws each frame hands it.
    private sealed class RecordingDresser : ISdfFrameDresser {
        public IReadOnlyList<SdfMeshDraw> MeshDraws { get; private set; } = [];
        public long MeshDrawsRevision { get; private set; }

        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            MeshDraws = meshDraws;
            MeshDrawsRevision = meshDrawsRevision;

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

    private static void AssertNear(Vector3 expected, Vector3 actual) => Assert.True(
        condition: (Vector3.Distance(value1: expected, value2: actual) < 1e-5f),
        userMessage: $"expected {expected}, drew {actual}"
    );
}
