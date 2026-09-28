using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a seat edits in the frame of the world it is routed to. A seat presented in the boot frame across a
/// translated and rotated adjacency sees the destination drawn there, and its aim and every surface it meets are carried
/// through the adjacency's own isometry (<see cref="WorldAdjacencyPath"/>) into the destination's coordinates: a place on
/// a visible destination surface lands at the destination's own coordinates of that surface, snapped to the grid pitch
/// the destination's editor section names. The red leg is the same query without the seat's frame, which answers in boot
/// coordinates.
/// </summary>
public sealed class WorldEditorFrameLawTests {
    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    private sealed class Adjacency(IReadOnlyList<WorldAdjacencyProjection> projections) : IWorldAdjacencySource {
        public void BeginTick(ulong tick) {
        }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => projections;
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }
    private sealed class Neighbour(WorldDefinition definition) : IWorldAdjacencyNeighbour {
        public string Authority => North;
        public WorldFaceFrame CounterpartFrame => default;
        public WorldDefinition Definition => definition;
        public int DefinitionRevision => 0;
        public int EntityCapacity => 1;
        public float InterpolationAlpha => 1f;
        public int SnapshotRevision => 0;
        public ulong SnapshotTick => 1;

        public bool IsEntityActive(int index) => (index == 0);
        public WorldEntityAddress EntityAddress(int index) => new(Authority: North, Generation: 1, Index: index);
        public Vector3 PreviousPosition(int index) => Slab;
        public Quaternion PreviousOrientation(int index) => Quaternion.Identity;
        public Vector3 CurrentPosition(int index) => Slab;
        public Quaternion CurrentOrientation(int index) => Quaternion.Identity;
        public Vector3 BodyColor(int index) => Vector3.One;
        public WorldLook Look(int index) => WorldLook.Implicit;
        public byte CatalogRig(int index) => 0;
        public FixedWorldCollider? Collider(int index) => null;
        public WorldBodyContactMode BodyContact(int index) => WorldBodyContactMode.Solid;
    }

    private const float DestinationPitch = 2f;
    private const string North = "north";

    // The slab's centre in the destination's own coordinates; its top face stands a quarter above.
    private static readonly Vector3 Slab = new(x: 4f, y: 0f, z: 6f);
    // A boundary whose two frames differ by a quarter turn about up and a translation that also lifts the destination.
    private static readonly IReadOnlyList<WorldAdjacencyFramePair> Path = [new WorldAdjacencyFramePair(
        Neighbour: new WorldFaceFrame(
            HalfDepth: FixedQ4816.FromDouble(value: 50),
            HalfHeight: FixedQ4816.FromDouble(value: 50),
            HalfWidth: FixedQ4816.FromDouble(value: 50),
            Normal: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One),
            Origin: FixedVector3.FromVector3(value: new Vector3(x: -5f, y: 1f, z: 3f)),
            Right: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            Up: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero)
        ),
        OverlapDepth: FixedQ4816.FromDouble(value: 50),
        OwnershipThreshold: FixedQ4816.Zero,
        Source: new WorldFaceFrame(
            HalfDepth: FixedQ4816.FromDouble(value: 50),
            HalfHeight: FixedQ4816.FromDouble(value: 50),
            HalfWidth: FixedQ4816.FromDouble(value: 50),
            Normal: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            Origin: FixedVector3.FromVector3(value: new Vector3(x: 10f, y: 0f, z: 0f)),
            Right: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: -FixedQ4816.One),
            Up: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero)
        )
    )];
    // Straight down onto the slab, a little off its centre, in the destination's own coordinates.
    private static readonly WorldEditorRay Aim = new(Direction: -Vector3.UnitY, Origin: new Vector3(x: 4.4f, y: 20f, z: 5.7f));
    private static WorldPrototype Crate { get; } = CreationFixtures.UnitSphere(id: "crate");
    private static WorldPrototype SlabCreation { get; } = CreationFixtures.Prototype(
        document: CreationFixtures.Document(
            name: "slab",
            shapes: [CreationFixtures.Shape(scale: new Vector3(x: 3f, y: 0.25f, z: 3f), type: SdfSolidPrimitive.Box)]
        ),
        id: "slab"
    );

    private static WorldPlacement SlabAt(Vector3 position) => new(
        Id: "slab",
        Position: position,
        PrototypeId: SlabCreation.Id,
        Scale: 1f,
        Solid: new WorldSolid(Margin: 0f),
        YawDegrees: 0f
    );
    // The destination as the boot frame draws it: the slab carried through the adjacency into boot coordinates.
    private static SdfFieldEvaluator BootFrameField() {
        var drawn = (Fixtures.BuildDocument() with {
            CreationsRaw = [SlabCreation],
            DocumentId = "home",
            PlacementRowsRaw = [SlabAt(position: WorldAdjacencyPath.MapPointIntoSource(path: Path, value: FixedVector3.FromVector3(value: Slab)).ToVector3())],
        });
        var builder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(
            builder: builder,
            creations: drawn.Creations,
            definition: drawn,
            placements: drawn.Placements
        );

        return new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
    }
    private static WorldDefinition Destination(WorldDefinition basis) => (basis with {
        CreationsRaw = [Crate, SlabCreation],
        DocumentId = "away",
        EditorRaw = new WorldEditorDefaults(Grid: new WorldEditorGrid(Pitch: new Vector3(value: DestinationPitch))),
        PlacementRowsRaw = [SlabAt(position: Slab)],
    });
    // A seat routed to the destination and drawn in the boot frame across the adjacency, whose boot-frame field holds the
    // destination's slab where the adjacency draws it.
    private static (WorldClient Client, WorldContinuum Continuum, WorldSeatAuthorityRouter Routes) Across(WorldAuthorityEndpoint north, WorldDefinition destination) {
        var routes = new WorldSeatAuthorityRouter();
        var client = ClientFixtures.Client(definition: (Fixtures.BuildDocument() with { DocumentId = "home" }));

        client.PublishStaticField(field: BootFrameField());
        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);

        var continuum = new WorldContinuum(
            adjacencies: new Adjacency(projections: [new WorldAdjacencyProjection(
                Direct: true,
                Name: "border",
                Neighbour: new Neighbour(definition: destination),
                OverlapDepth: FixedQ4816.FromDouble(value: 50),
                Path: Path
            )]),
            client: client,
            routes: routes
        );

        // The seat is drawn in the boot frame across the adjacency, not in the destination's own scene.
        Assert.Null(@object: continuum.PresentedElsewhere(slot: 0));
        Assert.True(condition: continuum.TryResolveSeatPose(interpolationAlpha: 1f, orientation: out _, position: out _, slot: 0));

        return (client, continuum, routes);
    }

    [Fact]
    public void AGridAcrossAnAdjacencyLiesAlongTheDestinationsAxesAtItsFollowedHeight() {
        var destination = Destination(basis: Fixtures.BuildDocument());
        using var north = EditorEndpoints.Of(definition: destination, identity: North, link: new RecordingLink(definition: destination), pose: Slab);

        var (client, continuum, _) = Across(destination: destination, north: north);

        Assert.True(condition: continuum.TryEditingPath(path: out var path, slot: 0));
        Assert.NotNull(@object: path);

        // The grid composed in the destination's frame, as the boot view draws it: its lattice's axes are the
        // destination's axes carried through the adjacency (a quarter turn), and its origin the destination's origin.
        var composed = WorldEditorGeometry.Overlay(
            grid: new WorldEditorGrid(Pitch: new Vector3(value: DestinationPitch), Visible: true),
            planeY: 0.25f,
            reference: null,
            snap: WorldEditorDefaults.Default.ResolvedSnap
        );
        var drawn = WorldEditorGeometry.InViewFrame(grid: composed, path: path);
        var destinationX = WorldAdjacencyPath.MapVectorIntoSource(path: path, value: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)).ToVector3();

        Assert.True(condition: (Vector3.Distance(value1: Vector3.Transform(rotation: drawn.WorldFrame, value: Vector3.UnitX), value2: destinationX) < 1e-4f));
        Assert.True(condition: (Vector3.Distance(value1: Vector3.Transform(rotation: drawn.WorldFrame, value: Vector3.UnitY), value2: Vector3.UnitY) < 1e-4f));
        Assert.True(condition: (Vector3.Distance(value1: drawn.WorldOrigin, value2: WorldAdjacencyPath.MapPointIntoSource(path: path, value: FixedVector3.Zero).ToVector3()) < 1e-4f));
        Assert.Equal(actual: drawn.WorldPitch, expected: new Vector3(value: DestinationPitch));

        // Red leg: composed without the seat's path, the lattice keeps the boot axes, which are not the destination's.
        Assert.True(condition: (Vector3.Distance(value1: Vector3.Transform(rotation: WorldEditorGeometry.InViewFrame(grid: composed, path: null).WorldFrame, value: Vector3.UnitX), value2: destinationX) > 0.5f));

        // The following plane takes the destination's height: the pointer over the seat's view, looking straight down on
        // the slab as the boot frame draws it, meets its top at the destination's 0.25, where the boot frame has it at
        // -0.75.
        var viewports = new WorldSeatViewports();
        var pointer = new WorldPointer();
        var drawnSlab = WorldAdjacencyPath.MapPointIntoSource(path: path, value: FixedVector3.FromVector3(value: Slab)).ToVector3();

        viewports.Publish(
            camera: new CameraSnapshot(
                AspectRatio: (64f / 48f),
                Forward: -Vector3.UnitY,
                Position: (drawnSlab + new Vector3(x: 0f, y: 10f, z: 0f)),
                Right: Vector3.UnitX,
                TanHalfFieldOfView: 0.5f,
                Up: Vector3.UnitZ
            ),
            height: 48u,
            region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
            slot: 0,
            width: 64u
        );
        pointer.SetPosition(position: new Vector2(x: 32f, y: 24f), slot: 0);

        var followed = new WorldEditorPointer(client: client, continuum: continuum, pointer: pointer, viewports: viewports).Probe(slot: 0);

        Assert.NotNull(@object: followed);
        Assert.Equal(actual: followed.Value.Point.Y, expected: 0.25f, tolerance: 0.02);

        // Red leg: probed in the frame the view draws, the height is the boot frame's.
        var presented = new WorldEditorPointer(client: client, pointer: pointer, viewports: viewports).Probe(slot: 0);

        Assert.NotNull(@object: presented);
        Assert.Equal(actual: presented.Value.Point.Y, expected: -0.75f, tolerance: 0.02);
    }
    [Fact]
    public void APlaceAcrossAnAdjacencyLandsAtTheDestinationsOwnCoordinatesOnItsPitch() {
        using var row = HostRow.Build(definition: (Fixtures.BuildDocument() with { CreationsRaw = [Crate] }), name: "boot");
        var destination = Destination(basis: row.Server.Definition);
        var link = new RecordingLink(definition: destination);
        using var north = EditorEndpoints.Of(definition: destination, identity: North, link: link, pose: Slab);

        var (client, continuum, routes) = Across(destination: destination, north: north);


        var pointer = new WorldEditorPointer(client: client, continuum: continuum, viewports: new WorldSeatViewports());
        var expected = new Vector3(x: Aim.Origin.X, y: 0.25f, z: Aim.Origin.Z);
        var hit = pointer.Surface(maxDistance: 100f, ray: Aim, slot: 0);

        Assert.NotNull(@object: hit);
        Assert.True(condition: (Vector3.Distance(value1: hit.Value.Point, value2: expected) < 0.02f), userMessage: $"hit {hit.Value.Point}, expected {expected}");
        Assert.True(condition: (hit.Value.Normal.Y > 0.9f));

        // Red leg: the same query without the seat's frame marches the boot field with destination coordinates, and does
        // not meet the slab where the destination has it.
        var frameless = new WorldEditorPointer(client: client, viewports: new WorldSeatViewports()).Surface(maxDistance: 100f, ray: Aim, slot: 0);

        Assert.False(condition: ((frameless is { } boot) && (Vector3.Distance(value1: boot.Point, value2: expected) < 0.5f)));

        // A place through the placement verb lands at the destination's coordinates, snapped to its own pitch of two:
        // 4.4 and 5.7 snap to 4 and 6, where the boot section's half-unit pitch would give 4.5 and 5.5.
        var seats = new WorldEditorSeats {
            AimProbe = static _ => Aim,
            SurfaceProbe = pointer.Surface,
        };
        var registry = new CommandRegistry(modules: [
            new WorldEditorCommandModule(
                authority: new FakeConsoleAuthority(instance: row.Instance),
                echoes: new WorldDeferredVerbEchoes(),
                link: row.Instance.Link,
                seatRouter: routes,
                seats: seats,
                stepGuard: new WorldRowStepWindowGuard()
            ),
        ]);

        Assert.Contains(expectedSubstring: $"pitch={DestinationPitch:0},{DestinationPitch:0},{DestinationPitch:0}", actualString: registry.Submit(line: "world.grid").Output);
        Assert.False(condition: registry.Submit(line: "world.snap on").IsError);

        var placed = registry.Submit(line: "world.place crate");

        Assert.False(condition: placed.IsError, userMessage: placed.Output);
        Assert.True(condition: (Vector3.Distance(value1: link.Placement(index: 0).Position, value2: new Vector3(x: 4f, y: 0.25f, z: 6f)) < 0.02f), userMessage: $"placed at {((Vector3)link.Placement(index: 0).Position)}");
    }
}
