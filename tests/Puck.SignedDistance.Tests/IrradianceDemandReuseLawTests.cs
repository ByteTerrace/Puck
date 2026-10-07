using System.Collections;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class IrradianceDemandReuseLawTests {
    private static readonly IrradianceLevel Level = new(Name: "room", Radius: 0, Reach: 9, Spacing: 1, Strata: 2);

    [Fact]
    public void ColdAndCompletedFramesReadOnlyOneComparisonOfUnchangedDemandInputs() {
        var cameras = new CountingList<Double3>(values: [Double3.Zero]);
        var bounds = new CountingList<IrradianceSphere>(values: [
            new(Center: new Double3(X: 100, Y: 100, Z: 100), Radius: 0),
            new(Center: Double3.Zero, Radius: double.PositiveInfinity),
        ]);
        var inputs = new IrradianceFrameInputs(cameras, bounds, Double3.Zero, new Double3(X: 12, Y: 12, Z: 12));
        var schedule = Schedule();
        var first = schedule.Frame(inputs: inputs);

        // This box has 64 candidate bricks. Without reuse each frame reads 64 cameras and 128 bounds;
        // snapshotting and comparing the input consumes one camera and two bounds, independent of that box.
        Assert.Equal(expected: 1, actual: cameras.Reads);
        Assert.Equal(expected: 2, actual: bounds.Reads);
        Assert.NotEmpty(collection: first.Refused);
        Assert.False(condition: schedule.IsComplete);
        for (var frame = 0; (frame < 64); frame++) {
            cameras.Reads = 0;
            bounds.Reads = 0;
            var plan = schedule.Frame(inputs: inputs);

            Assert.Equal(expected: 1, actual: cameras.Reads);
            Assert.Equal(expected: 2, actual: bounds.Reads);
            Assert.Equal(expected: first.Refused, actual: plan.Refused);
        }
        Assert.True(condition: schedule.IsComplete);

        cameras.Values[0] = new Double3(X: 12, Y: 12, Z: 12);
        cameras.Reads = 0;
        bounds.Reads = 0;
        var changed = schedule.Frame(inputs: inputs);

        Assert.NotEmpty(collection: changed.Allocated);
        Assert.NotEmpty(collection: changed.Evicted);
        Assert.Equal(expected: 2, actual: cameras.Reads);
        Assert.Equal(expected: 2, actual: bounds.Reads);
        cameras.Reads = 0;
        bounds.Reads = 0;
        Assert.Equal(expected: changed.Refused, actual: schedule.Frame(inputs: inputs).Refused);
        Assert.Equal(expected: 1, actual: cameras.Reads);
        Assert.Equal(expected: 2, actual: bounds.Reads);
    }
    [Fact]
    public void CachedDemandPreservesEveryPlanAgainstDemandRebuiltInAnotherInputOrder() {
        Double3[] cameras = [Double3.Zero, new(X: 12, Y: 12, Z: 12)];
        IrradianceSphere[] bounds = [new(Center: Double3.Zero, Radius: 2), new(Center: new(X: 8, Y: 8, Z: 8), Radius: 2)];
        var inputs = new IrradianceFrameInputs(cameras, bounds, Double3.Zero, new(X: 12, Y: 12, Z: 12));
        var cached = Schedule();
        var rebuilt = Schedule();

        for (var frame = 0; (frame < 80); frame++) {
            if (frame == 40) {
                var moved = new IrradianceSphere(Center: Double3.Zero, Radius: 1);

                Assert.Equal(expected: rebuilt.MarkGeometry(current: moved, previous: moved), actual: cached.MarkGeometry(current: moved, previous: moved));
            }
            var reordered = (((frame & 1) == 0) ? inputs : inputs with {
                Cameras = cameras.Reverse().ToArray(),
                Bounds = bounds.Reverse().ToArray(),
            });
            var expected = rebuilt.Frame(inputs: reordered);
            var actual = cached.Frame(inputs: inputs);

            Assert.Equal(expected: expected.Allocated, actual: actual.Allocated);
            Assert.Equal(expected: expected.Evicted, actual: actual.Evicted);
            Assert.Equal(expected: expected.Refused, actual: actual.Refused);
            Assert.Equal(expected: expected.Placed, actual: actual.Placed);
            Assert.Equal(expected: expected.Classified, actual: actual.Classified);
            Assert.Equal(expected: expected.Traces, actual: actual.Traces);
            Assert.Equal(expected: rebuilt.IsComplete, actual: cached.IsComplete);
        }
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [Theory]
    public void EveryDemandValueAndListSizeInvalidatesTheOwnedSnapshot(int change) {
        var cameras = new List<Double3> { Double3.Zero };
        var bounded = (change is 1 or 2 or 6);
        var bounds = new List<IrradianceSphere> { new(Center: new(X: 1, Y: 1, Z: 1), Radius: (bounded ? 0 : double.PositiveInfinity)) };
        var inputs = new IrradianceFrameInputs(cameras, bounds, Double3.Zero, new(X: 32, Y: 8, Z: 8));
        var schedule = Schedule();

        _ = schedule.Frame(inputs: inputs);
        var before = schedule.Allocated.ToArray();

        switch (change) {
            case 0: cameras[0] = new(X: 16, Y: 0, Z: 0); break;
            case 1: bounds[0] = bounds[0] with { Center = new(X: 17, Y: 1, Z: 1) }; break;
            case 2: bounds[0] = bounds[0] with { Radius = 8 }; break;
            case 3: inputs = inputs with { WorldMin = new(X: 4, Y: 0, Z: 0) }; break;
            case 4: inputs = inputs with { WorldMax = new(X: 0.1, Y: 0.1, Z: 0.1) }; break;
            case 5: cameras.Add(item: new(X: 16, Y: 0, Z: 0)); break;
            case 6: bounds.Add(item: new(Center: new(X: 17, Y: 1, Z: 1), Radius: 0)); break;
        }
        var after = schedule.Frame(inputs: inputs);
        var fresh = Schedule();
        var expected = fresh.Frame(inputs: inputs);

        Assert.False(condition: before.SequenceEqual(second: schedule.Allocated));
        Assert.Equal(expected: fresh.Allocated, actual: schedule.Allocated);
        Assert.Equal(expected: expected.Refused, actual: after.Refused);
        Assert.Equal(expected: after.Refused, actual: schedule.Frame(inputs: inputs).Refused);
        if (change == 1) {
            Assert.NotEmpty(collection: after.Evicted);
            Assert.NotEmpty(collection: after.Allocated);
        }
    }
    [Fact]
    public void CallerEditsCannotChangeTheSchedulesLevelOrPoolConfiguration() {
        var levels = new[] { Level };
        var pools = new[] { 4 };
        var schedule = new IrradianceSchedule(levels, pools, traceBudget: 16, classifyBudget: 2, exitDistance: 100);
        var expected = Schedule();
        var inputs = new IrradianceFrameInputs([Double3.Zero], [new(Center: Double3.Zero, Radius: double.PositiveInfinity)],
            Double3.Zero, new(X: 12, Y: 12, Z: 12));

        levels[0] = Level with { Spacing = 2, Strata = 1 };
        pools[0] = 1;
        for (var frame = 0; (frame < 8); frame++) {
            var reference = expected.Frame(inputs: inputs);
            var actual = schedule.Frame(inputs: inputs);

            Assert.Equal(expected: reference.Allocated, actual: actual.Allocated);
            Assert.Equal(expected: reference.Refused, actual: actual.Refused);
            Assert.Equal(expected: reference.Traces, actual: actual.Traces);
        }
    }

    private static IrradianceSchedule Schedule() => new([Level], [4], traceBudget: 16, classifyBudget: 2, exitDistance: 100);

    private sealed class CountingList<T>(T[] values) : IReadOnlyList<T> {
        public int Count => Values.Length;
        public int Reads { get; set; }
        public T[] Values { get; } = values;

        public T this[int index] { get { Reads++; return Values[index]; } }

        public IEnumerator<T> GetEnumerator() {
            for (var index = 0; (index < Count); index++) { yield return this[index]; }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
