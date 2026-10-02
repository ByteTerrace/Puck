using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The host's schedule: within budget, independent of the order its inputs arrive in, idle on a completed still world
// and on a pan that demands no new brick, one allocation for a brick two cameras share, nearest bricks kept under pool
// pressure, and a geometry change tracing again exactly the probes whose reach it meets.
public sealed class IrradianceScheduleLawTests {
    private static readonly IrradianceLevel[] Levels = [
        new(Name: "room", Radius: 12.0, Reach: 9.0, Spacing: 1.5, Strata: 2),
        new(Name: "world", Radius: 0.0, Reach: 0.0, Spacing: 4.5, Strata: 2),
    ];
    private static readonly IrradianceSphere[] Bounds = [
        new(Center: new Double3(X: 0.0, Y: -1.0, Z: 0.0), Radius: 1.5),
        new(Center: new Double3(X: 8.0, Y: 0.0, Z: -3.0), Radius: 2.0),
        new(Center: new Double3(X: -10.0, Y: 2.0, Z: 6.0), Radius: 1.0),
    ];
    private static readonly Double3 WorldMin = new(X: -20.0, Y: -6.0, Z: -20.0);
    private static readonly Double3 WorldMax = new(X: 20.0, Y: 10.0, Z: 20.0);

    [Fact]
    public void EveryFrameStaysWithinBudget() {
        var schedule = Schedule(pools: [64, 32]);

        for (var frame = 0; (frame < 60); frame++) {
            var plan = schedule.Frame(inputs: Inputs(cameras: [new Double3(X: (frame * 0.2), Y: 1.0, Z: 0.0)]));

            Assert.True(condition: (plan.Traces.Count <= 40));
            Assert.True(condition: (plan.Classified.Count <= 3));
        }
    }
    [Fact]
    public void TheOrderInputsArriveInChangesNothing() {
        Double3[] cameras = [new(X: 1.0, Y: 1.0, Z: 1.0), new(X: 7.0, Y: 1.0, Z: -2.0)];
        var forward = Schedule(pools: [64, 32]);
        var backward = Schedule(pools: [64, 32]);

        for (var frame = 0; (frame < 20); frame++) {
            var a = forward.Frame(inputs: new IrradianceFrameInputs(Bounds: Bounds, Cameras: cameras, WorldMax: WorldMax, WorldMin: WorldMin));
            var b = backward.Frame(inputs: new IrradianceFrameInputs(Bounds: Bounds.Reverse().ToArray(), Cameras: cameras.Reverse().ToArray(), WorldMax: WorldMax, WorldMin: WorldMin));

            Assert.Equal(expected: a.Allocated, actual: b.Allocated);
            Assert.Equal(expected: a.Classified, actual: b.Classified);
            Assert.Equal(expected: a.Traces, actual: b.Traces);
        }
    }
    [Fact]
    public void ACompletedStillWorldAndAnIdlePanScheduleNothing() {
        var schedule = Schedule(pools: [512, 256]);
        var camera = new Double3(X: 0.5, Y: 1.0, Z: 0.5);

        var frames = 0;

        do {
            _ = schedule.Frame(inputs: Inputs(cameras: [camera]));
            frames++;
        } while (!schedule.IsComplete && (frames < 10_000));

        Assert.True(condition: (frames > 1));

        Assert.True(condition: schedule.IsComplete);
        Assert.Empty(collection: schedule.Frame(inputs: Inputs(cameras: [camera])).Traces);

        // A pan that stays where the same bricks are demanded schedules nothing.
        var pan = schedule.Frame(inputs: Inputs(cameras: [(camera + new Double3(X: 0.05, Y: 0.0, Z: 0.03))]));

        Assert.Empty(collection: pan.Allocated);
        Assert.Empty(collection: pan.Traces);
    }
    [Fact]
    public void TwoCamerasSharingABrickAllocateItOnce() {
        var schedule = Schedule(pools: [512, 256]);
        var plan = schedule.Frame(inputs: Inputs(cameras: [new Double3(X: 0.2, Y: 1.0, Z: 0.2), new Double3(X: 0.4, Y: 1.0, Z: 0.3)]));

        Assert.Equal(expected: plan.Allocated.Count, actual: plan.Allocated.Distinct().Count());
        Assert.Equal(expected: schedule.Allocated.Count, actual: plan.Allocated.Count);
    }
    [Fact]
    public void APoolUnderPressureKeepsTheNearestBricks() {
        var schedule = Schedule(pools: [4, 256]);
        var camera = new Double3(X: 0.0, Y: 1.0, Z: 0.0);
        var plan = schedule.Frame(inputs: Inputs(cameras: [camera]));
        var kept = schedule.Allocated.Where(predicate: static key => (key.Level == 0)).ToList();

        Assert.Equal(expected: 4, actual: kept.Count);
        Assert.NotEmpty(collection: plan.Refused);

        var farthestKept = kept.Max(selector: key => Distance(brick: key, camera: camera));

        foreach (var refused in plan.Refused.Where(predicate: static key => (key.Level == 0))) {
            Assert.True(condition: (Distance(brick: refused, camera: camera) >= farthestKept));
        }
    }
    [Fact]
    public void AGeometryChangeRetracesExactlyTheProbesItsReachMeets() {
        var schedule = Schedule(classifyBudget: 1_000, pools: [512, 256], traceBudget: 1_000_000);

        do {
            _ = schedule.Frame(inputs: Inputs(cameras: [new Double3(X: 0.0, Y: 1.0, Z: 0.0)]));
        } while (!schedule.IsComplete);

        var moved = new IrradianceSphere(Center: new Double3(X: 3.0, Y: 0.0, Z: 3.0), Radius: 0.5);

        schedule.MarkGeometry(current: moved, previous: moved);

        var plan = schedule.Frame(inputs: Inputs(cameras: [new Double3(X: 0.0, Y: 1.0, Z: 0.0)]));
        var room = plan.Traces.Where(predicate: static update => (update.Probe.Level == 0)).ToList();

        Assert.NotEmpty(collection: room);
        Assert.All(collection: room, action: update => Assert.True(condition: IrradianceSchedule.Dirties(changed: moved, probe: IrradianceLattice.Position(key: update.Probe, level: Levels[0]), reach: Levels[0].Reach)));
        Assert.Contains(collection: room, filter: static update => (update.Reason == IrradianceUpdateReason.Geometry));
    }

    private static IrradianceSchedule Schedule(int[] pools, int traceBudget = 40, int classifyBudget = 3) => new(
        classifyBudget: classifyBudget,
        exitDistance: 200.0,
        levels: Levels,
        pools: pools,
        traceBudget: traceBudget
    );
    private static IrradianceFrameInputs Inputs(Double3[] cameras) => new(Bounds: Bounds, Cameras: cameras, WorldMax: WorldMax, WorldMin: WorldMin);
    private static double Distance(IrradianceBrickKey brick, Double3 camera) {
        IrradianceLattice.BrickBox(brick: brick, level: Levels[brick.Level], max: out var max, min: out var min);

        var dx = Math.Max(val1: Math.Max(val1: (min.X - camera.X), val2: 0.0), val2: (camera.X - max.X));
        var dy = Math.Max(val1: Math.Max(val1: (min.Y - camera.Y), val2: 0.0), val2: (camera.Y - max.Y));
        var dz = Math.Max(val1: Math.Max(val1: (min.Z - camera.Z), val2: 0.0), val2: (camera.Z - max.Z));

        return new Double3(X: dx, Y: dy, Z: dz).Length;
    }
}
