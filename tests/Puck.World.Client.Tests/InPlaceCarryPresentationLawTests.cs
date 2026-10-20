using System.Numerics;

using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Pins what presentation latches across a pose change a snapshot declares. An in-place crossing
/// (<see cref="EntityContinuityKind.Carry"/>) carries the latched pose, so a creation driver reading the body's
/// vertical speed reads the fall straight through it rather than the six metres the body moved up; a teleport holds the
/// drivers through the jump's frame and relatches them there, so a respawn reads no spike; a run of crossings longer
/// than the carry history is cut the same way. A correction easing when a crossing arrives turns with it.
/// </summary>
public sealed class InPlaceCarryPresentationLawTests {
    private const float Frame = (1f / 60f);
    private const float Step = 0.5f;
    private const float FallSpeed = (-Step / Frame);
    private const float Tolerance = 1e-2f;

    private static readonly WorldPoseCarry Up = new(
        Rotation: Quaternion.Identity,
        Translation: new Vector3(x: 0f, y: 6f, z: 0f)
    );

    private static void Deliver(WorldClient client, ulong tick, Vector3 position, EntityContinuity continuity, Quaternion? orientation = null) => client.DeliverSnapshot(snapshot: new WorldSnapshot(
        Authority: "a",
        Entries: new[] { new EntitySnapshot(
            Active: true,
            BodyColor: Vector3.One,
            CatalogRig: 0,
            Continuity: continuity,
            Generation: 1,
            Index: 0,
            Kit: 0,
            Look: 0,
            Orientation: (orientation ?? Quaternion.Identity),
            Position: position
        ) },
        Revision: 0,
        StepTicks: 1UL,
        Tick: tick
    ));
    private static float Pack(WorldClient client, WorldStampPool pool, DynamicTransform[] transforms) {
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: Frame);

        var moved = new SdfMovedTransforms();

        moved.Begin(
            everything: false,
            tableRows: transforms.Length
        );
        pool.PackTransforms(
            client: client,
            moved: moved,
            parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f),
            slotBase: 0,
            transforms: transforms
        );
        Assert.True(condition: pool.TryBodyRig(bodyIndex: 0, state: out var rig));

        return rig!.Drivers[0].Phase;
    }
    // A one-shape creation whose one driver reads the body's vertical speed, ungated and unfollowed.
    private static (WorldClient Client, WorldStampPool Pool, DynamicTransform[] Transforms) Stage() {
        var creation = CreationFixtures.Prototype(id: "faller", document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "faller",
            Palette: null,
            Shapes: [new ShapeDocument(
                Id: 0,
                Name: "body",
                Type: SdfSolidPrimitive.Box,
                Position: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: new Vector3(value: 0.25f),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            )],
            Frames: null,
            Drivers: [new CreationDriverDocument(
                Cadence: 1f,
                Name: "air",
                Signal: CreationDriverDocument.SignalVerticalSpeed,
                When: [CreationDriverDocument.WhenAlways]
            )]
        ));
        var definition = (Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(
                Name: "faller",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: 1f,
                Motion: WorldLookMotion.Default
            )],
        });
        var client = ClientFixtures.Client(definition: definition);
        var pool = new WorldStampPool();

        pool.Reconcile(
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: WorldLook.Implicit)],
            creations: [creation],
            dynamics: [],
            placements: []
        );

        return (client, pool, new DynamicTransform[WorldStampPool.DynamicSlotCount]);
    }

    [Fact]
    public void AFallReadsStraightThroughACrossingAndARespawnReadsNoSpike() {
        var (client, pool, transforms) = Stage();
        var tick = 0UL;
        var position = new Vector3(x: 0f, y: 100f, z: 0f);

        float Fall(EntityContinuity continuity) {
            Deliver(client: client, continuity: continuity, position: position, tick: ++tick);

            return Pack(client: client, pool: pool, transforms: transforms);
        }

        for (var frame = 0; (frame < 4); frame++) {
            position.Y -= Step;
            _ = Fall(continuity: EntityContinuity.Continuous);
        }

        position.Y -= Step;
        Assert.Equal(expected: FallSpeed, actual: Fall(continuity: EntityContinuity.Continuous), tolerance: Tolerance);

        // Through the floor and out of the ceiling: one step down, carried six metres up.
        position = (Up.Point(point: position) - new Vector3(x: 0f, y: Step, z: 0f));
        Assert.Equal(expected: FallSpeed, actual: Fall(continuity: EntityContinuity.Carry(carried: Up)), tolerance: Tolerance);
        Assert.Equal(expected: 1, actual: client.Carries.Epoch(index: 0));

        // A respawn holds the fall's reading through its own frame, then reads the body's motion from the landing.
        position = new Vector3(x: 50f, y: 3f, z: 50f);
        Assert.Equal(expected: FallSpeed, actual: Fall(continuity: EntityContinuity.Teleport), tolerance: Tolerance);
        position.Y -= Step;
        Assert.Equal(expected: FallSpeed, actual: Fall(continuity: EntityContinuity.Continuous), tolerance: Tolerance);

        // More crossings between two frames than the history holds: cut like a teleport, never read as a jump.
        for (var crossing = 0; (crossing < 5); crossing++) {
            position = (Up.Point(point: position) - new Vector3(x: 0f, y: Step, z: 0f));
            Deliver(client: client, continuity: EntityContinuity.Carry(carried: Up), position: position, tick: ++tick);
        }

        Assert.Equal(expected: FallSpeed, actual: Pack(client: client, pool: pool, transforms: transforms), tolerance: Tolerance);
        position.Y -= Step;
        Assert.Equal(expected: FallSpeed, actual: Fall(continuity: EntityContinuity.Continuous), tolerance: Tolerance);
    }
    [Fact]
    public void ACorrectionEasingThroughACrossingTurnsWithIt() {
        var client = ClientFixtures.Client(definition: Fixtures.BuildDocument());
        var halfTurn = new WorldPoseCarry(
            Rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: MathF.PI),
            Translation: Up.Translation
        );
        var corrected = new Vector3(x: 0.001f, y: 0f, z: 0f);

        Deliver(client: client, continuity: EntityContinuity.Continuous, position: Vector3.Zero, tick: 1UL);
        client.UpdateRenderPoses(alpha: 1f);
        // Drawn at the origin, corrected a millimetre along +X: the render error eases from -X.
        Deliver(client: client, continuity: EntityContinuity.Correction(seconds: 10f), position: corrected, tick: 2UL);
        // Carried a half turn about up, the body stands at the carried correction, and the error now points +X.
        Deliver(client: client, continuity: EntityContinuity.Carry(carried: halfTurn), position: halfTurn.Point(point: corrected), tick: 3UL);
        client.UpdateRenderPoses(alpha: 1f);

        Assert.Equal(expected: 0f, actual: client.Position(index: 0).X, tolerance: 1e-4f);
        Assert.Equal(expected: 6f, actual: client.Position(index: 0).Y, tolerance: 1e-4f);
    }
}
