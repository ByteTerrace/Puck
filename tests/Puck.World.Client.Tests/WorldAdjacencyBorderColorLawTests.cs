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

/// <summary>
/// THE LAW: a border reads its neighbour's bound colors from the neighbour image it draws its geometry from, the image
/// pinned for the tick (<see cref="IWorldAdjacencyNeighbour.Definition"/>). While the pin holds the image the border
/// baked its revision does not move; once the pin advances to an image whose bound color moved, the border rebuilds.
/// The border has no read of any image but the pinned one, so it never shows a color ahead of its geometry.
/// </summary>
public sealed class WorldAdjacencyBorderColorLawTests {
    private const float SeamZ = 24f;

    private static readonly FixedQ4816 Depth = FixedQ4816.FromDouble(value: 4.0);

    // A post whose one palette color is bound to the colors row's bump cell.
    private static WorldPrototype Post() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: "post",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 0.5f, y: 0.1f, z: 0.5f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "post",
                Palette: [new PaletteEntryDocument(Color: "state.colors.bump", Emissive: null, Specular: null, Roughness: null)],
                Shapes: [shape],
                Frames: null
            ),
            source: "post"
        );

        return new WorldPrototype(
            Id: "post",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static WorldStateRow Colors(string bump) => new(
        Name: CellName.Parse(candidate: "colors"),
        Kind: CellKind.Text,
        Capacity: 8,
        Domain: StateDomain.Keys.Instance,
        Cells: [new StateCell(Key: CellName.Parse(candidate: "bump"), Value: CellValue.Text(value: bump))]
    );
    // The neighbour's image: one solid post inside the seam's band, its color bound to colors.bump.
    private static WorldDefinition Neighbour(string bump) {
        var source = Fixtures.BuildDocument();
        var stated = source.WithWorldState(rows: [.. source.AuthoredState, Colors(bump: bump)]);

        return (stated with {
            CreationsRaw = [Post()],
            PlacementRowsRaw = [new WorldPlacement(
                Id: "post-a",
                PrototypeId: "post",
                Position: new Vector3(x: 0f, y: 0f, z: (SeamZ - 2f)),
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)
            )],
        });
    }
    // A south-facing seam at z = SeamZ whose band admits the post.
    private static WorldFaceFrame Seam() => new WorldAdjacencyBoundary(
        Center: new DocumentVector3(x: 0f, y: 0f, z: SeamZ),
        OutwardYawDegrees: 0f,
        OutwardPitchDegrees: 0f,
        Width: 48f,
        Height: 16f
    ).CompileFrame();
    private static WorldClient Client(WorldDefinition definition) => new(
        composition: new WorldCompositionState(),
        definition: definition,
        roster: new PlayerRoster(
            definition: definition,
            link: new SilentLink(definition: definition),
            seatBindings: new WorldSeatBindings(definition: definition)
        ),
        seatRouter: new WorldSeatAuthorityRouter()
    );
    private static int[] Revision(WorldAdjacencySceneEmitter emitter) {
        var revision = new int[emitter.RevisionComponentCount];

        emitter.WriteRevision(destination: revision);

        return revision;
    }

    [Fact]
    public void ABoundColorWriteReachesTheBorderOnlyOnceThePinAdvances() {
        var neighbour = new PinnedNeighbour(definition: Neighbour(bump: "#3FAF6F"), counterpartFrame: Seam());
        // The source authors one adjacency, so the composition reserves the band the seam's border renders in.
        var home = Fixtures.BuildDocument() with {
            References = [new WorldReference(SafeName.Parse(candidate: "east"), "east.world.json")],
            Destinations = [new WorldDestination(SafeName.Parse(candidate: "east"), "east", WorldDestinationDurability.Persisted)],
            Adjacencies = [new WorldAdjacency(SafeName.Parse(candidate: "seam"), "east", "seam", new WorldAdjacencyBoundary(Vector3.Zero, 0f, 0f, 8f, 8f))],
        };
        var emitter = new WorldAdjacencySceneEmitter(
            client: Client(definition: home),
            source: new SeamSource(
                neighbour: neighbour,
                frame: Seam()
            )
        );
        var builder = new SdfProgramBuilder();

        var context = new SdfEmitContext(false, 0f, Vector3.Zero, Vector3.Zero, 0);

        // The live build bakes the post's bound color into the border.
        if (emitter.OwnsMaterialScope) {
            using var scope = builder.BeginMaterialScope();

            emitter.Emit(
                builder: builder,
                context: context
            );
        } else {
            emitter.Emit(
                builder: builder,
                context: context
            );
        }

        var baked = Revision(emitter: emitter);

        // While the pin holds the image the border baked, nothing moves.
        Assert.Equal(expected: baked, actual: Revision(emitter: emitter));

        // The pin advances to an image carrying the write: the border rebuilds to follow it.
        neighbour.Definition = Neighbour(bump: "#112233");

        Assert.NotEqual(expected: baked, actual: Revision(emitter: emitter));
    }

    // One seam from the source to a pinned neighbour.
    private sealed class SeamSource(IWorldAdjacencyNeighbour neighbour, WorldFaceFrame frame) : IWorldAdjacencySource {
        private readonly WorldAdjacencyProjection[] m_projections = [new WorldAdjacencyProjection(
            Name: "seam",
            Neighbour: neighbour,
            Path: [new WorldAdjacencyFramePair(
                Neighbour: frame,
                OverlapDepth: Depth,
                OwnershipThreshold: FixedQ4816.Zero,
                Source: frame
            )],
            OverlapDepth: Depth,
            Direct: true
        )];

        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Overlap;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => m_projections;
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }
    // A neighbour whose Definition is the image pinned for the tick, which the law advances by hand.
    private sealed class PinnedNeighbour(WorldDefinition definition, WorldFaceFrame counterpartFrame) : IWorldAdjacencyNeighbour {
        public string Authority => "neighbour";

        public WorldFaceFrame CounterpartFrame { get; } = counterpartFrame;
        public WorldDefinition Definition { get; set; } = definition;

        public int DefinitionRevision => 0;
        public int EntityCapacity => 0;
        public float InterpolationAlpha => 0f;
        public int SnapshotRevision => 0;
        public ulong SnapshotTick => 0;

        public Vector3 BodyColor(int index) => Vector3.Zero;
        public WorldBodyContactMode BodyContact(int index) => WorldBodyContactMode.Overlap;
        public byte CatalogRig(int index) => 0;
        public FixedWorldCollider? Collider(int index) => null;
        public Quaternion CurrentOrientation(int index) => Quaternion.Identity;
        public Vector3 CurrentPosition(int index) => Vector3.Zero;
        public WorldEntityAddress EntityAddress(int index) => default;
        public string? PlacementId(int index) => null;
        public bool IsEntityActive(int index) => false;
        public WorldLook Look(int index) => null!;
        public Quaternion PreviousOrientation(int index) => Quaternion.Identity;
        public Vector3 PreviousPosition(int index) => Vector3.Zero;
    }
}
