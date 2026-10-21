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
    private const float FallSpeed = (-Step / Frame);
    private const float Frame = (1f / 60f);
    private const float Step = 0.5f;
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
    // A flat door creation whose glass is shape 0, turned a quarter about +X by `angle` in engine terms.
    private static WorldPrototype Door(string id, float angle) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: CreationFrame.ToAuthor(document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: id,
                Palette: null,
                Shapes: [new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Box,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: angle),
                    Scale: new Vector3(x: 1.25f, y: 1.25f, z: 0.06f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                )],
                Frames: null,
                Behavior: new CreationBehaviorDocument(Locomotion: null, Faces: [new CreationFaceDocument(DefaultSource: null, Name: "glass", ShapeId: 0)])
            )),
            source: id
        );

        return new WorldPrototype(Id: id, Document: canonical.Document, HashRaw: canonical.Hash);
    }
    private static WorldPlacement Glass(string id, string prototype, float height, float yaw, string counterpart) => new(
        FaceSources: [new WorldPlacementFace(
            Face: "glass",
            Portal: new WorldPlacementPortal(Arrival: WorldPortalArrival.Mapped, Counterpart: counterpart, Destination: "self", Travel: WorldPortalTravel.Body),
            Source: new WorldScreenSource.None()
        )],
        Id: id,
        Position: new Vector3(x: 0f, y: height, z: 0f),
        PrototypeId: prototype,
        Scale: 1f,
        YawDegrees: yaw
    );
    // A shaft in the world itself: a floor door at the origin facing up whose counterpart is a ceiling door six metres
    // above facing down, through a destination whose reference names the world's own document.
    private static WorldDefinition Shaft(WorldDefinition definition) => (definition with {
        CreationsRaw = [.. definition.Creations, Door(angle: (-MathF.PI / 2f), id: "floor-door"), Door(angle: (MathF.PI / 2f), id: "ceiling-door")],
        Destinations = [new WorldDestination(Durability: WorldDestinationDurability.Persisted, Name: SafeName.Parse(candidate: "self"), Reference: "self", Scope: WorldDestinationScope.Global)],
        DocumentId = "shaft",
        PlacementRowsRaw = [
            Glass(counterpart: "ceiling/glass", height: 0f, id: "floor", prototype: "floor-door", yaw: 0f),
            Glass(counterpart: "floor/glass", height: 6f, id: "ceiling", prototype: "ceiling-door", yaw: 180f),
        ],
        References = [new WorldReference(Document: "shaft", Name: SafeName.Parse(candidate: "self"))],
    });
    // A one-shape creation whose one driver reads the body's vertical speed, ungated and unfollowed; with `shaft`, in a
    // world standing a floor door under its ceiling counterpart.
    private static (WorldClient Client, WorldStampPool Pool, DynamicTransform[] Transforms) Stage(bool shaft = false) {
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
        var client = ClientFixtures.Client(definition: (shaft ? Shaft(definition: definition) : definition));
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
    public void ABodyStraddlingASameWorldDoorIsClippedThereAndCopiedToItsCounterpart() {
        var (client, pool, transforms) = Stage(shaft: true);
        var doors = WorldPortalStraddle.Doors(definition: client.Definition);

        Assert.Equal(expected: 2, actual: doors.Count);
        Assert.Same(expected: doors, actual: WorldPortalStraddle.Doors(definition: client.Definition));

        // Half through the floor glass: clipped above the floor and copied, clipped below the ceiling, six metres up.
        Deliver(client: client, continuity: EntityContinuity.Continuous, position: new Vector3(x: 0.5f, y: 0.1f, z: 0f), tick: 1UL);
        _ = Pack(client: client, pool: pool, transforms: transforms);

        var clip = Assert.Single(collection: pool.SlotClips);

        Assert.Equal(expected: (0, WorldStampPool.SlotsPerPlacement), actual: (clip.FirstSlot, clip.SlotCount));
        Assert.Equal(expected: new Vector4(w: 0f, x: 0f, y: 1f, z: 0f), actual: clip.Plane);
        Assert.Equal(expected: new Vector4(w: 6f, x: 0f, y: -1f, z: 0f), actual: clip.CopyPlane);
        Assert.Equal(expected: 6.1f, actual: (Vector3.Transform(value: new Vector3(x: 0.5f, y: 0.1f, z: 0f), rotation: clip.CopyRotation) + clip.CopyTranslation).Y, tolerance: Tolerance);

        // A conservative reach that spans the whole shaft clips at the glass the body stands nearest.
        var nearCeiling = WorldPortalStraddle.Clip(center: new Vector3(x: 0f, y: 5.5f, z: 0f), doors: doors, firstSlot: 0, radius: 10f, slotCount: 1);

        Assert.Equal(expected: new Vector4(w: 6f, x: 0f, y: -1f, z: 0f), actual: nearCeiling!.Value.Plane);

        // Clear of every glass: drawn once, unclipped.
        Deliver(client: client, continuity: EntityContinuity.Teleport, position: new Vector3(x: 0f, y: 3f, z: 0f), tick: 2UL);
        _ = Pack(client: client, pool: pool, transforms: transforms);
        Assert.Empty(collection: pool.SlotClips);

        // A world whose doors lead elsewhere has none to straddle.
        Assert.Empty(collection: WorldPortalStraddle.Doors(definition: (client.Definition with { DocumentId = "elsewhere" })));
    }
    [Fact]
    public void AChaseBoomThroughASameWorldDoorLooksBackThroughItsCounterpart() {
        var (client, _, _) = Stage(shaft: true);
        var doors = WorldPortalStraddle.Doors(definition: client.Definition);

        // Standing just below the ceiling glass, its look target and the boom reaching up through it: the eye and
        // the target stand over the floor glass instead.
        var body = new Vector3(x: 0f, y: 5.5f, z: 0f);
        var target = new Vector3(x: 0f, y: 6.5f, z: 0f);
        var eye = new Vector3(x: 0f, y: 7.5f, z: 0.5f);

        Assert.True(condition: WorldPortalStraddle.CarryBoom(anchor: body, doors: doors, eye: ref eye, target: ref target));
        Assert.Equal(actual: eye.Y, expected: 1.5f, tolerance: Tolerance);
        Assert.Equal(actual: target.Y, expected: 0.5f, tolerance: Tolerance);

        // A boom that stays on one side, or passes the glass's plane outside its opening, is left where it is.
        var clearTarget = new Vector3(x: 0f, y: 4f, z: 0f);
        var clearEye = new Vector3(x: 0f, y: 4.5f, z: 2f);
        var wideTarget = new Vector3(x: 0f, y: 6.5f, z: 0f);
        var wideEye = new Vector3(x: 0f, y: 7f, z: 6f);

        Assert.False(condition: WorldPortalStraddle.CarryBoom(anchor: new Vector3(x: 0f, y: 3f, z: 0f), doors: doors, eye: ref clearEye, target: ref clearTarget));
        Assert.False(condition: WorldPortalStraddle.CarryBoom(anchor: body, doors: doors, eye: ref wideEye, target: ref wideTarget));
        Assert.Equal(actual: wideEye.Y, expected: 7f);
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
