using System.Numerics;

using Puck.Physics.Motion;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Pins the packed pose of a swing driven by <c>speed</c> through a <c>linear</c> wave: the swing turns its limb by
/// amplitude times the speed phase times the driver's weight, airborne as on the ground. A frame that runs long still
/// reads the body's real speed, and a frame that charges no time (a held capture) keeps the pose of the last frame that
/// did.
/// </summary>
public sealed class WorldCreationInstancesAirborneSpeedLawTests {
    private const float Amplitude = 0.6f;
    private const float Cadence = 0.2f;
    private const float Speed = 5f;

    private static (WorldClient Client, WorldCreationInstances Pool, DynamicTransform[] Transforms) Stage() {
        var creation = CreationFixtures.Prototype(id: "flier", document: CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(
                Cadence: Cadence,
                Name: "cruise",
                Signal: CreationDriverDocument.SignalSpeed,
                When: [CreationDriverDocument.TokenMoving]
            )],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "cruise",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitX,
                Amplitude: Amplitude,
                Wave: CreationWave.Linear
            ))
        ));
        var definition = (Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(
                Name: "flier",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: 1f,
                Motion: WorldLookMotion.Default
            )],
        });
        var client = ClientFixtures.Client(definition: definition);
        var pool = new WorldCreationInstances();

        pool.Reconcile(
            bodyStamps: [new WorldCreationInstances.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: WorldLook.Implicit)],
            creations: [creation],
            dynamics: [],
            placements: []
        );

        return (client, pool, new DynamicTransform[pool.DynamicSlotCount]);
    }
    // Moves the body to `Speed` metres a second times `frame` seconds a tick along +X, holding the facts, packs a frame
    // that charges `seconds`, and returns the limb's orientation.
    private static Quaternion Fly(WorldClient client, WorldCreationInstances pool, DynamicTransform[] transforms, ulong tick, BodyFacts facts, float frame, float seconds) {
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: new[] { new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Facts: facts,
                Generation: 1,
                Index: 0,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: new Vector3(x: ((tick * Speed) * frame), y: 10f, z: 0f)
            ) },
            Revision: 0,
            StepTicks: 1UL,
            Tick: tick
        ));
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: seconds);

        var moved = new SdfMovedTransforms();

        moved.Begin(everything: false, tableRows: transforms.Length);
        pool.PackTransforms(client: client, moved: moved, parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f), slotBase: 0, transforms: transforms);

        return transforms[1].Orientation;
    }
    private static float AngleBetween(Quaternion turned, Quaternion rest) => (2f * MathF.Acos(x: MathF.Min(x: 1f, y: MathF.Abs(x: Quaternion.Dot(quaternion1: turned, quaternion2: rest)))));

    [InlineData(BodyFacts.Airborne, (1f / 60f))]
    [InlineData(BodyFacts.Airborne, 0.1f)]
    [InlineData(BodyFacts.Grounded, (1f / 60f))]
    [InlineData(BodyFacts.Grounded, 0.1f)]
    [Theory]
    public void ALinearSpeedSwingTurnsItsLimbByAmplitudeTimesPhaseTimesWeight(BodyFacts facts, float frame) {
        var (client, pool, transforms) = Stage();
        // The first frame only seeds the drivers, so it leaves the limb at its rest pose.
        var rest = Fly(client: client, pool: pool, tick: 1UL, transforms: transforms, facts: facts, frame: frame, seconds: frame);
        var turned = rest;

        for (var tick = 2UL; (tick <= 60UL); tick++) {
            turned = Fly(client: client, pool: pool, tick: tick, transforms: transforms, facts: facts, frame: frame, seconds: frame);
        }

        Assert.True(condition: pool.TryBodyRig(bodyIndex: 0, state: out var rig));

        var driver = rig!.Drivers[0];

        // 5 m/s at cadence 0.2 is a phase of 1; the weight has eased to one.
        Assert.Equal(expected: (Cadence * Speed), actual: driver.Phase, tolerance: 1e-3f);
        Assert.Equal(expected: 1f, actual: driver.Weight, tolerance: 1e-2f);
        Assert.Equal(expected: ((Amplitude * driver.Phase) * driver.Weight), actual: AngleBetween(turned: turned, rest: rest), tolerance: 1e-3f);
    }
    // A capture holds the frame it is armed in and renders it again with no time passing, at the pose the display last
    // interpolated to: the body has moved and the frame charges nothing, so a speed read as travel over no time would
    // be zero and the limb would drop to rest.
    [Fact]
    public void AFrameThatChargesNoTimeKeepsTheSpeedPhaseOfTheLastFrameThatDid() {
        var (client, pool, transforms) = Stage();
        var frame = (1f / 60f);
        var rest = Fly(client: client, pool: pool, tick: 1UL, transforms: transforms, facts: BodyFacts.Airborne, frame: frame, seconds: frame);
        var turned = rest;

        for (var tick = 2UL; (tick <= 60UL); tick++) {
            turned = Fly(client: client, pool: pool, tick: tick, transforms: transforms, facts: BodyFacts.Airborne, frame: frame, seconds: frame);
        }

        var held = Fly(client: client, pool: pool, tick: 61UL, transforms: transforms, facts: BodyFacts.Airborne, frame: frame, seconds: 0f);

        Assert.True(condition: pool.TryBodyRig(bodyIndex: 0, state: out var rig));
        Assert.Equal(expected: (Cadence * Speed), actual: rig!.Drivers[0].Phase, tolerance: 1e-3f);
        Assert.Equal(expected: AngleBetween(turned: turned, rest: rest), actual: AngleBetween(turned: held, rest: rest), tolerance: 1e-3f);
        Assert.True(condition: (AngleBetween(turned: held, rest: rest) > 0.5f));
    }
}
