using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

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
    private static CreationDocument Holder() => (CreationFixtures.Rig(
        drivers: [],
        (CreationFixtures.Shape(type: SdfSolidPrimitive.Box, scale: PictureHalf) with {
            Id = 1,
            Name = "picture",
            Position = PictureAt,
        }),
        (CreationFixtures.Shape(type: SdfSolidPrimitive.Sphere) with {
            Id = 2,
            Name = "ball",
            Group = 1,
        })
    ) with {
        Frames = [new FrameDocument(
            Name: "hold",
            Transforms: [new FrameTransformDocument(Id: 1, Position: PictureAt, Rotation: Quaternion.Identity)]
        )],
    });
    private static WorldLook HolderLook() => new(
        Name: "holder",
        Source: new WorldLookSource.Creation(PrototypeId: "rig"),
        Scale: 1f,
        Motion: (WorldLookMotion.Default with {
            Poses = new Dictionary<string, string> { ["hold"] = $"state.{HeldRow}.$body" },
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
    private static WorldDefinition World(WorldScreenCarry carry, long held = 0L) => (CreationFixtures.RigWorld(
        creation: Holder(),
        state: [Held(raw: held)]
    ) with {
        LookRowsRaw = [HolderLook()],
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
    private static (WorldStampPool Pool, WorldClient Client) Pack(WorldDefinition world) {
        var creation = WorldDefinitionRows.FindCreation(creations: world.Creations, id: "rig")!;
        var client = ClientFixtures.Client(definition: world);
        var pool = new WorldStampPool();

        pool.Reconcile(
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: world.Looks[0])],
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
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];

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
}
