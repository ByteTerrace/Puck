using System.Numerics;

using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// Continuation between levels: a fine ray past its reach marches on through empty space until it stands in coarser
// support, so a receiver at the middle of a sealed emissive hall reads the hall, never the sky, whether the coarse level
// supports the middle, is not allocated at all, or lies beyond the coarsest level's reach; and a continuation reads a
// coarser ray only where that ray's own hit lies beyond the fine ray's end, so an interval is never counted twice.
public sealed class IrradianceContinuationLawTests {
    private const double HallRadius = 12.0;
    private const double Sky = 5.0;

    private static readonly IrradianceLevel Fine = new(Name: "near", Radius: 0.0, Reach: 1.5, Spacing: 0.5, Strata: 2);
    private static readonly IrradianceLevel Coarse = new(Name: "world", Radius: 0.0, Reach: 0.0, Spacing: 4.0, Strata: 2);
    private static readonly Double3 Top = new(X: 0.0, Y: 0.2, Z: 0.0);
    private static readonly Double3 Up = new(X: 0.0, Y: 1.0, Z: 0.0);

    [Fact]
    public void ASealedHallsMiddleReadsTheHallNotTheSky() {
        var model = Hall(allocateCoarse: true, supportSeeking: true);

        Assert.Equal(expected: 1.0, actual: model.Irradiance(normal: Up, surface: Top)!.Value.X, precision: 9);
        Assert.True(condition: (model.ContinuedRays > 0));
        Assert.Equal(expected: 0, actual: FineRaysEndingIn(kind: IrradianceHitKind.Exit, model: model));
        Assert.Equal(expected: 0, actual: model.UnresolvedRays);

        // Red leg: a ray that reads the sky at its reach lights the sealed hall's middle with the sky.
        var leaky = Hall(allocateCoarse: true, supportSeeking: false);

        Assert.True(condition: (leaky.Irradiance(normal: Up, surface: Top)!.Value.X > 1.5));
    }
    [Fact]
    public void AnUnallocatedCoarseLevelLeavesTheFineRaysMarching() {
        var model = Hall(allocateCoarse: false, supportSeeking: true);

        Assert.Equal(expected: 1.0, actual: model.Irradiance(normal: Up, surface: Top)!.Value.X, precision: 9);
        Assert.Equal(expected: 0, actual: model.ContinuedRays);
        Assert.Equal(expected: 0, actual: model.ExitedRays);
        Assert.True(condition: (FineRaysEndingIn(kind: IrradianceHitKind.Hit, model: model) > 0));
    }
    [Fact]
    public void AContinuationNeverCountsTheFineRaysIntervalAgain() {
        var model = Corridor(intervalCheck: true);
        var fine = new IrradianceProbeKey(Level: 0, X: 0, Y: 2, Z: 0);
        var ray = model.NearestRayOf(direction: new Double3(X: 1.0, Y: 0.0, Z: 0.0), key: fine);

        Assert.Equal(expected: IrradianceHitKind.Continuation, actual: model.HitsOf(key: fine)[ray].Kind);
        // The coarse corner behind the fine ray's end sees the bright plate before that end, an interval the fine ray
        // already crossed and found empty; every coarse ray beyond the end sees the dark far wall.
        Assert.Equal(expected: 0.0, actual: model.RadianceOf(key: fine, ray: ray)!.Value.X, precision: 12);

        // Red leg: without the check the plate's light enters through the coarse corner behind the end.
        var leaky = Corridor(intervalCheck: false);

        Assert.True(condition: (leaky.RadianceOf(key: fine, ray: ray)!.Value.X > 0.1));
    }

    // The fine level's stored rays that ended on one kind; the coarse level's probes outside the hall exit legitimately.
    private static int FineRaysEndingIn(IrradianceCacheModel model, IrradianceHitKind kind) {
        var count = 0;

        for (var z = -4; (z < 4); z++) {
            for (var y = -4; (y < 4); y++) {
                for (var x = -4; (x < 4); x++) {
                    count += model.HitsOf(key: new IrradianceProbeKey(Level: 0, X: x, Y: y, Z: z)).Count(predicate: record => (record.Kind == kind));
                }
            }
        }

        return count;
    }
    // A closed hall (inner radius 12) holding a small ball at its centre whose top is the receiver; every surface emits
    // 1 and reflects nothing, so every ray that stays inside reads exactly 1 and only the sky (5) differs. The fine level fills the ball's neighbourhood; the coarse level, when allocated, fills the hall, and is
    // dormant in its middle, more than 4√3 + 1.8 units from any surface.
    private static IrradianceCacheModel Hall(bool allocateCoarse, bool supportSeeking) {
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.HallWithReceiver(inner: HallRadius, receiver: 0.2, thickness: 0.3),
            levels: [Fine, Coarse],
            options: new IrradianceModelOptions(ExitDistance: 200.0, SupportSeeking: supportSeeking),
            surfaces: new IrradianceSurfaces(
                albedo: static _ => Double3.Zero,
                emission: static _ => new Double3(X: 1.0, Y: 1.0, Z: 1.0),
                sky: static _ => new Double3(X: Sky, Y: Sky, Z: Sky)
            )
        );

        model.Allocate(level: 0, max: new Double3(X: 1.0, Y: 1.0, Z: 1.0), min: new Double3(X: -1.0, Y: -1.0, Z: -1.0));

        if (allocateCoarse) {
            model.Allocate(level: 1, max: new Double3(X: 13.0, Y: 13.0, Z: 13.0), min: new Double3(X: -13.0, Y: -13.0, Z: -13.0));
        }

        model.Classify();
        model.Trace();
        model.Solve(bounces: 0);

        return model;
    }
    // A sealed dark box from x = -2 to 12 holding a small bright plate at x = 0.5 on the X axis. The fine probe at
    // (0, 1, 0) looks along +X above the plate; its ray ends at x = 1.5, in the coarse cell whose least corner, the origin,
    // looks along +X straight at the plate.
    private static IrradianceCacheModel Corridor(bool intervalCheck) {
        var builder = new SdfProgramBuilder();
        var dark = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var bright = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: 5f, y: 0f, z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: 7.5f, y: 6.5f, z: 6.5f), material: dark, round: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 5f, y: 0f, z: 0f));
        _ = builder.Box(blend: SdfBlendOp.Subtraction, halfExtents: new Vector3(x: 7f, y: 6f, z: 6f), material: dark, round: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0.5f, y: 0f, z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: 0.025f, y: 0.3f, z: 0.3f), material: bright, round: 0f);

        var model = new IrradianceCacheModel(
            field: new IrradianceField(program: builder.Build()),
            levels: [Fine, Coarse],
            options: new IrradianceModelOptions(ExitDistance: 200.0, IntervalCheck: intervalCheck),
            surfaces: new IrradianceSurfaces(
                albedo: static _ => Double3.Zero,
                emission: static material => ((material == 1) ? new Double3(X: 10.0, Y: 10.0, Z: 10.0) : Double3.Zero)
            )
        );

        model.Allocate(level: 0, max: new Double3(X: 0.5, Y: 1.5, Z: 0.5), min: new Double3(X: -0.5, Y: 0.5, Z: -0.5));
        model.Allocate(level: 1, max: new Double3(X: 8.0, Y: 5.0, Z: 5.0), min: new Double3(X: -4.0, Y: -5.0, Z: -5.0));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 0);

        return model;
    }
}
