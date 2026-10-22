using System.Numerics;

using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Pins the <c>world.rates</c> bodies lever (<see cref="SdfUpdateRates.Bodies"/>): a body small on screen evaluates its
/// pose at 15 Hz while its root follows it every frame, its parts riding the root rigidly between packs; a body large on
/// screen, or one packed with the lever off, evaluates its pose every frame.
/// </summary>
public sealed class WorldStampPoolRateLawTests {
    private const float Frame = (1f / 60f);
    private const float Step = 0.05f;

    private static (WorldClient Client, WorldStampPool Pool, DynamicTransform[] Transforms) Stage(bool rootDynamics = false) {
        var creation = CreationFixtures.Prototype(id: "walker", document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "walker",
            Palette: null,
            Shapes: [
                Box(id: 0, name: "body", position: Vector3.Zero),
                Box(id: 1, name: "head", position: new Vector3(x: 0f, y: 1f, z: 0f)),
            ],
            Frames: null,
            Drivers: [new CreationDriverDocument(
                Cadence: 1f,
                Name: "clock",
                Signal: CreationDriverDocument.SignalTime,
                When: [CreationDriverDocument.WhenAlways]
            )]
        ));
        var definition = (Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(
                Name: "walker",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: 1f,
                Motion: WorldLookMotion.Default
            )],
        });
        var client = ClientFixtures.Client(definition: definition);
        var pool = new WorldStampPool();

        // A look whose root follows the body through a dynamics row, as the Moth's does.
        var follower = (WorldLook.Implicit with { Motion = (WorldLookMotion.Default with { Dynamics = "follow" }) });

        pool.Reconcile(
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: (rootDynamics ? follower : WorldLook.Implicit))],
            creations: [creation],
            dynamics: (rootDynamics ? [new DynamicsRow(Name: "follow", Frequency: 4f, Damping: 0.8f, Response: 0f)] : []),
            placements: []
        );

        return (client, pool, new DynamicTransform[WorldStampPool.DynamicSlotCount]);
    }
    private static ShapeDocument Box(int id, string name, Vector3 position) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Box,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: new Vector3(value: 0.25f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    // Moves the body a step along +X and packs a frame, returning the clock driver's phase.
    private static float Walk(WorldClient client, WorldStampPool pool, DynamicTransform[] transforms, ulong tick) {
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: new[] { new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Generation: 1,
                Index: 0,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: new Vector3(x: (tick * Step), y: 0f, z: 0f)
            ) },
            Revision: 0,
            StepTicks: 1UL,
            Tick: tick
        ));
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: Frame);

        var moved = new SdfMovedTransforms();

        moved.Begin(everything: false, tableRows: transforms.Length);
        pool.PackTransforms(client: client, moved: moved, parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f), slotBase: 0, transforms: transforms);
        Assert.True(condition: pool.TryBodyRig(bodyIndex: 0, state: out var rig));

        return rig!.Drivers[0].Phase;
    }
    // One view looking along +Z at the body from `distance` metres, its image 900 pixels high.
    private static SdfViewSnapshot[] ViewFrom(float distance) => [new SdfViewSnapshot(
        Camera: CameraSnapshot.LookAt(fieldOfViewRadians: (MathF.PI / 3f), position: new Vector3(x: 0f, y: 0.5f, z: -distance), target: new Vector3(x: 0f, y: 0.5f, z: 0f), viewportHeight: 900f, viewportWidth: 1280f),
        Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
    )];

    [Fact]
    public void ABodySmallOnScreenPosesAtFifteenHertzWhileItsRootFollowsEveryFrame() {
        var (client, pool, transforms) = Stage();
        var counts = SdfUpdateRateCounts.Counters();

        pool.RateBy(counts: counts, height: 900U, rates: SdfUpdateRates.All, views: ViewFrom(distance: 500f));

        var phases = new List<float>();
        var offsets = new List<Vector3>();
        var roots = new List<Vector3>();

        for (var tick = 1UL; (tick <= 9UL); tick++) {
            phases.Add(item: Walk(client: client, pool: pool, tick: tick, transforms: transforms));
            roots.Add(item: transforms[0].Position);
            offsets.Add(item: (transforms[2].Position - transforms[0].Position));
        }

        // The first frame packs, the next three hold, the fifth packs the four frames' time at once, and so on.
        Assert.Equal(expected: 3L, actual: counts.Read(kind: SdfUpdateRateCounts.BodiesPosed));
        Assert.Equal(expected: 6L, actual: counts.Read(kind: SdfUpdateRateCounts.BodiesHeld));
        Assert.Equal(expected: phases[0], actual: phases[3]);
        Assert.Equal(expected: (phases[0] + (4f * Frame)), actual: phases[4], tolerance: 1e-4f);

        for (var frame = 1; (frame < roots.Count); frame++) {
            // The root follows the body every frame; the head rides it rigidly.
            Assert.Equal(expected: Step, actual: (roots[frame].X - roots[frame - 1].X), tolerance: 1e-4f);
            Assert.Equal(expected: offsets[0].Y, actual: offsets[frame].Y, tolerance: 1e-4f);
        }
    }
    // A body whose root follows the body through a dynamics row keeps stepping that follower every frame between packs,
    // and its parts ride the followed root rigidly.
    [Fact]
    public void AHeldBodyWhoseRootFollowsADynamicsRowStepsItsRootEveryFrame() {
        var (client, pool, transforms) = Stage(rootDynamics: true);
        var counts = SdfUpdateRateCounts.Counters();

        pool.RateBy(counts: counts, height: 900U, rates: SdfUpdateRates.All, views: ViewFrom(distance: 500f));

        var roots = new List<Vector3>();
        var offsets = new List<Vector3>();

        for (var tick = 1UL; (tick <= 9UL); tick++) {
            _ = Walk(client: client, pool: pool, tick: tick, transforms: transforms);
            roots.Add(item: transforms[0].Position);
            offsets.Add(item: (transforms[2].Position - transforms[0].Position));
        }

        Assert.True(condition: (counts.Read(kind: SdfUpdateRateCounts.BodiesHeld) > 0L));

        for (var frame = 1; (frame < roots.Count); frame++) {
            Assert.True(condition: (roots[frame].X > roots[frame - 1].X));
            Assert.Equal(expected: offsets[0].Y, actual: offsets[frame].Y, tolerance: 1e-4f);
        }
    }
    // A view looking away from the body, or standing inside its reach, rates it as no view sees it or as a view at its
    // nearest: the first poses at a reduced rate, the second every frame.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ABodyNoViewSeesPosesAtTheLeastRateAndOneAViewStandsInsidePosesEveryFrame(bool inside) {
        var (client, pool, transforms) = Stage();
        var counts = SdfUpdateRateCounts.Counters();
        var camera = (inside
            ? CameraSnapshot.LookAt(fieldOfViewRadians: (MathF.PI / 3f), position: new Vector3(x: 0f, y: 0.5f, z: 0f), target: new Vector3(x: 0f, y: 0.5f, z: 1f), viewportHeight: 900f, viewportWidth: 1280f)
            : CameraSnapshot.LookAt(fieldOfViewRadians: (MathF.PI / 3f), position: new Vector3(x: 0f, y: 0.5f, z: -3f), target: new Vector3(x: 0f, y: 0.5f, z: -6f), viewportHeight: 900f, viewportWidth: 1280f));

        pool.RateBy(counts: counts, height: 900U, rates: SdfUpdateRates.All, views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))]);

        for (var tick = 1UL; (tick <= 6UL); tick++) {
            _ = Walk(client: client, pool: pool, tick: tick, transforms: transforms);
        }

        if (inside) {
            Assert.Equal(expected: 6L, actual: counts.Read(kind: SdfUpdateRateCounts.BodiesPosed));
            Assert.Equal(expected: 0L, actual: counts.Read(kind: SdfUpdateRateCounts.BodiesHeld));
        } else {
            Assert.True(condition: (counts.Read(kind: SdfUpdateRateCounts.BodiesHeld) > 0L));
        }
    }
    [Fact]
    public void ABodyLargeOnScreenOrPackedWithTheLeverOffPosesEveryFrame() {
        foreach (var (rates, distance) in new[] { (SdfUpdateRates.All, 3f), (SdfUpdateRates.None, 500f) }) {
            var (client, pool, transforms) = Stage();
            var counts = SdfUpdateRateCounts.Counters();

            pool.RateBy(counts: counts, height: 900U, rates: rates, views: ViewFrom(distance: distance));

            for (var tick = 1UL; (tick <= 6UL); tick++) {
                _ = Walk(client: client, pool: pool, tick: tick, transforms: transforms);
            }

            Assert.Equal(expected: 6L, actual: counts.Read(kind: SdfUpdateRateCounts.BodiesPosed));
            Assert.Equal(expected: 0L, actual: counts.Read(kind: SdfUpdateRateCounts.BodiesHeld));
        }
    }
    [Fact]
    public void TheTiersFollowPixelsPerMetreAndTheDisplayRate() {
        Assert.Equal(expected: 60, actual: SdfUpdateRateTiers.BodyHertz(pixelsPerMetre: 31f));
        Assert.Equal(expected: 30, actual: SdfUpdateRateTiers.BodyHertz(pixelsPerMetre: 30f));
        Assert.Equal(expected: 15, actual: SdfUpdateRateTiers.BodyHertz(pixelsPerMetre: 15f));
        Assert.Equal(expected: 60, actual: SdfUpdateRateTiers.ViewHertz(pixels: 121d));
        Assert.Equal(expected: 30, actual: SdfUpdateRateTiers.ViewHertz(pixels: 120d));
        Assert.Equal(expected: 15, actual: SdfUpdateRateTiers.ViewHertz(pixels: 60d));
        Assert.Equal(expected: 60, actual: SdfUpdateRateTiers.ScreenHertz(share: 1.1d));
        Assert.Equal(expected: 30, actual: SdfUpdateRateTiers.ScreenHertz(share: 1d));
        Assert.Equal(expected: 15, actual: SdfUpdateRateTiers.ScreenHertz(share: 0.5d));
        Assert.Equal(expected: 2, actual: SdfUpdateRateTiers.Divisor(displayHertz: 60, hertz: 30));
        Assert.Equal(expected: 4, actual: SdfUpdateRateTiers.Divisor(displayHertz: 60, hertz: 15));
        Assert.Equal(expected: 1, actual: SdfUpdateRateTiers.Divisor(displayHertz: 32, hertz: 30));
        Assert.Equal(expected: 1, actual: SdfUpdateRateTiers.Divisor(displayHertz: 0, hertz: 15));
        Assert.Equal(expected: 1, actual: SdfUpdateRateTiers.Divisor(displayHertz: 144, hertz: 60));
    }
}
