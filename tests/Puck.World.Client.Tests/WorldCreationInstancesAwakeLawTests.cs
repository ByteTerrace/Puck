using System.Numerics;

using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Pins what a stamp pool's pack visits (<see cref="WorldCreationInstances.VisitCount"/>): the registrations something
/// woke, never the pool. Bodies whose pose is held where no view may show them cost a frame nothing, a body that moves
/// costs one visit, and a view that turns to them wakes them all.
/// </summary>
public sealed class WorldCreationInstancesAwakeLawTests {
    private const int Bodies = 24;
    private const float Frame = (1f / 60f);

    private static readonly PresentationRate Paced60 = new(DisplayHertz: 60, GridHertz: 60d);

    // Every body wears a creation whose head swings on a time driver, so each would pose every frame it is seen.
    private static (WorldClient Client, WorldCreationInstances Pool, DynamicTransform[] Transforms) Stage() {
        var creation = CreationFixtures.Prototype(id: "walker", document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "walker",
            Palette: null,
            Shapes: [
                Box(id: 0, name: "body", position: Vector3.Zero),
                Box(id: 1, name: "head", position: new Vector3(x: 0f, y: 1f, z: 0f)) with { Swings = [new ShapeSwingDocument(Amplitude: 0.5f, Axis: Vector3.UnitY, Driver: "clock", Pivot: new Vector3(x: 0.5f, y: 1f, z: 0f))] },
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
        var pool = new WorldCreationInstances();

        pool.Reconcile(placementFrames: WorldPlacementFrameCompilation.Compile(placements: [], worldSeed: 0UL),
            bodyStamps: [.. Enumerable.Range(start: 0, count: Bodies).Select(selector: body => new WorldCreationInstances.BodyStamp(BodyIndex: body, Creation: creation, Scale: 1f, Look: WorldLook.Implicit))],
            creations: [creation],
            dynamics: [],
            placements: []
        );

        return (client, pool, new DynamicTransform[pool.DynamicSlotCount]);
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
    // Delivers a tick in which every body stands a metre apart along +X, the mover displaced along +Z by its steps, then
    // presents and packs one frame. Returns the pool entries the pack visited.
    private static int Present(WorldClient client, WorldCreationInstances pool, DynamicTransform[] transforms, ulong tick, int mover = -1, int steps = 0) {
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "a",
            Entries: Enumerable.Range(start: 0, count: Bodies).Select(selector: body => new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Generation: 1,
                Index: body,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: new Vector3(x: body, y: 0f, z: ((body == mover) ? (steps * 0.05f) : 0f))
            )).ToArray(),
            Revision: 0,
            StepTicks: 1UL,
            Tick: tick
        ), stream: default);
        client.UpdateRenderPoses(alpha: 1f);
        pool.Tick(deltaSeconds: Frame);

        var moved = new SdfMovedTransforms();

        moved.Begin(everything: false, tableRows: transforms.Length);
        pool.PackTransforms(client: client, moved: moved, parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f), slotBase: 0, transforms: transforms);

        return pool.VisitCount;
    }
    private static SdfViewSnapshot[] View(Vector3 position, Vector3 target) => [new SdfViewSnapshot(
        Camera: CameraSnapshot.LookAt(fieldOfViewRadians: (MathF.PI / 3f), position: position, target: target, viewportHeight: 900f, viewportWidth: 1280f),
        Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
    )];

    [Fact]
    public void APackVisitsOnlyWhatWokeAndCountsEveryHeldPoseHeld() {
        var (client, pool, transforms) = Stage();
        var counts = SdfUpdateRateCounts.Counters();

        // A view looking away from every body, and no shadow cascade: nothing may show them.
        pool.PresentAt(rate: Paced60, rates: SdfUpdateRates.All);
        pool.RateBy(counts: counts, height: 900U, intervals: null, views: View(position: new Vector3(x: 12f, y: 0.5f, z: -40f), target: new Vector3(x: 12f, y: 0.5f, z: -80f)));
        pool.ShadowBy(shadows: []);

        // The first pack after a reconcile visits and poses every entry; the second finds every driver still advancing
        // and holds each pose, since nothing may show it.
        Assert.Equal(expected: Bodies, actual: Present(client: client, pool: pool, tick: 1UL, transforms: transforms));
        Assert.Equal(expected: Bodies, actual: Present(client: client, pool: pool, tick: 2UL, transforms: transforms));
        Assert.Equal(expected: ((long)Bodies), actual: counts.Read(kind: SdfUpdateRateCounts.BodiesPosed));
        Assert.Equal(expected: ((long)Bodies), actual: counts.Read(kind: SdfUpdateRateCounts.BodiesHeld));

        // A frame in which nothing moved visits nothing, and still counts every pose held.
        Assert.Equal(expected: 0, actual: Present(client: client, pool: pool, tick: 3UL, transforms: transforms));
        Assert.Equal(expected: (2L * Bodies), actual: counts.Read(kind: SdfUpdateRateCounts.BodiesHeld));

        // One body moves: the pack visits it alone, carries its held pose with its root, and counts no pose twice.
        var before = transforms[pool.RootSlotOffset(index: 5)].Position;

        Assert.Equal(expected: 1, actual: Present(client: client, mover: 5, pool: pool, steps: 1, tick: 4UL, transforms: transforms));
        Assert.Equal(expected: (before + new Vector3(x: 0f, y: 0f, z: 0.05f)), actual: transforms[pool.RootSlotOffset(index: 5)].Position);
        Assert.Equal(expected: (3L * Bodies), actual: counts.Read(kind: SdfUpdateRateCounts.BodiesHeld));
        Assert.Equal(expected: ((long)Bodies), actual: counts.Read(kind: SdfUpdateRateCounts.BodiesPosed));

        // The view turns to the bodies: every held pose wakes, and those in the view pose again.
        pool.RateBy(counts: counts, height: 900U, intervals: null, views: View(position: new Vector3(x: 12f, y: 0.5f, z: -6f), target: new Vector3(x: 12f, y: 0.5f, z: 0f)));
        Assert.Equal(expected: Bodies, actual: Present(client: client, mover: 5, pool: pool, steps: 1, tick: 5UL, transforms: transforms));
        Assert.True(condition: (counts.Read(kind: SdfUpdateRateCounts.BodiesPosed) > Bodies));
    }
    // Bodies in a cell a view may show are visited every frame; out of sight no pack visits them once they hold; seen
    // again, each resumes from where its drivers stood.
    [Fact]
    public void BodiesOutOfSightCostNoVisitAndResumeAsACut() {
        var (client, pool, transforms) = Stage();
        var counts = SdfUpdateRateCounts.Counters();
        var away = View(position: new Vector3(x: 12f, y: 0.5f, z: -40f), target: new Vector3(x: 12f, y: 0.5f, z: -80f));
        var toward = View(position: new Vector3(x: 12f, y: 0.5f, z: -6f), target: new Vector3(x: 12f, y: 0.5f, z: 0f));

        pool.PresentAt(rate: Paced60, rates: SdfUpdateRates.All);
        pool.RateBy(counts: counts, height: 900U, intervals: null, views: toward);
        pool.ShadowBy(shadows: []);

        // While a view may show their cell, each pack visits them all: every one poses or decides to hold for itself.
        for (var tick = 1UL; (tick <= 4UL); tick++) {
            Assert.Equal(expected: Bodies, actual: Present(client: client, pool: pool, tick: tick, transforms: transforms));
        }

        Assert.True(condition: pool.TryBodyRig(bodyIndex: 12, state: out var seen));

        var phase = seen!.Drivers[0].Phase;

        // Out of sight for two hundred frames: the first pack visits only those that still posed, to hold them, and no
        // later pack visits any.
        pool.RateBy(counts: counts, height: 900U, intervals: null, views: away);
        _ = Present(client: client, pool: pool, tick: 5UL, transforms: transforms);
        for (var tick = 6UL; (tick <= 205UL); tick++) {
            Assert.Equal(expected: 0, actual: Present(client: client, pool: pool, tick: tick, transforms: transforms));
        }

        // Seen again, every one wakes and poses as a continuity cut: its drivers relatch where they stood, never
        // advancing over the seconds the packs that skipped it held.
        var posed = counts.Read(kind: SdfUpdateRateCounts.BodiesPosed);

        pool.RateBy(counts: counts, height: 900U, intervals: null, views: toward);
        Assert.Equal(expected: Bodies, actual: Present(client: client, pool: pool, tick: 206UL, transforms: transforms));
        Assert.True(condition: (counts.Read(kind: SdfUpdateRateCounts.BodiesPosed) > posed));
        Assert.True(condition: pool.TryBodyRig(bodyIndex: 12, state: out var resumed));
        Assert.Equal(expected: phase, actual: resumed!.Drivers[0].Phase, tolerance: 1e-4f);
    }
}
