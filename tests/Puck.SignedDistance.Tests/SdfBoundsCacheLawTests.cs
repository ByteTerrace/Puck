using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>A cache is an exact reading of its immutable field, including at eviction and frame boundaries.</summary>
public sealed class SdfBoundsCacheLawTests {
    [Fact]
    public void ReuseEvictionAndSeparateRevisionsKeepTheExactBoundsAndRefusals() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Box(halfExtents: Vector3.One, material: material, round: 0);
        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var cached = new SdfBoundsCache(field: evaluator);

        // More distinct boxes than the cache holds, revisited in both directions, with different upper and lower
        // corners. Equality of both interval endpoints is required, not just containment of the point answer.
        for (var pass = 0; (pass < 3); pass++) {
            for (var index = 0; (index < 600); index++) {
                var x = FixedQ4816.FromRawBits(value: (((pass == 1) ? (599 - index) : index) * 127L));
                var lower = FixedPosition.FromLocal(local: new FixedVector3(X: x, Y: -x, Z: FixedQ4816.Zero));
                var upper = (lower + new FixedVector3(X: FixedQ4816.One, Y: x, Z: FixedQ4816.One));

                Assert.Equal(expected: evaluator.TryDistanceBounds(distance: out var expected, lower: lower, upper: upper),
                    actual: cached.TryDistanceBounds(distance: out var actual, lower: lower, upper: upper));
                Assert.Equal(actual: actual, expected: expected);
                var evaluations = cached.FieldEvaluations;

                Assert.True(condition: cached.TryDistanceBounds(distance: out var repeated, lower: lower, upper: upper));
                Assert.Equal(actual: repeated, expected: expected);
                Assert.Equal(expected: evaluations, actual: cached.FieldEvaluations);
            }
        }

        var outside = new FixedPosition(cellX: long.MaxValue, cellY: 0, cellZ: 0, local: FixedVector3.Zero);

        Assert.False(condition: cached.TryDistanceBounds(distance: out _, lower: outside, upper: outside));
        var one = FixedPosition.FromLocal(local: FixedVector3.UnitX);

        Assert.Throws<ArgumentException>(testCode: () => cached.TryDistanceBounds(distance: out _, lower: one, upper: FixedPosition.Zero));

        _ = builder.Dilate(radius: 2);
        var rebuilt = new SdfBoundsCache(field: new SdfFieldEvaluator(program: builder.Build()));

        Assert.True(condition: cached.TryDistanceBounds(distance: out var original, lower: one, upper: one));
        Assert.True(condition: rebuilt.TryDistanceBounds(distance: out var changed, lower: one, upper: one));
        Assert.NotEqual(actual: changed, expected: original);
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var query = 0; (query < 128); query++) {
                _ = cached.TryDistanceBounds(distance: out _, lower: one, upper: one);
            }
        }));
    }
}
