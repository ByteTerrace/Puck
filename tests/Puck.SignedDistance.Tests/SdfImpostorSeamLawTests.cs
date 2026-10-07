using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>The views and their total weights are continuous across the octahedral map's identified edges and poles.
/// Comparing every cell independently makes a switch between different views observable even on a symmetric bake.</summary>
public sealed class SdfImpostorSeamLawTests {
    private static double[] Weights(int views, (double X, double Y, double Z) toward) {
        var chosen = SdfImpostorOracle.Views(toward: toward, views: views);
        var weights = new double[(views * views)];

        foreach (var (cell, weight) in new[] { (chosen.A, chosen.Wa), (chosen.B, chosen.Wb), (chosen.C, chosen.Wc) }) {
            Assert.InRange(actual: cell.I, high: (views - 1), low: 0);
            Assert.InRange(actual: cell.J, high: (views - 1), low: 0);
            Assert.InRange(actual: weight, high: 1.0, low: 0.0);
            weights[((cell.J * views) + cell.I)] += weight;
        }
        Assert.Equal(expected: 1.0, actual: weights.Sum(), precision: 12);
        return weights;
    }

    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [Theory]
    public void CrossingAnyLowerHemisphereSeamKeepsTheSameWeightedViews(int views) {
        const double Epsilon = 1e-8;

        foreach (var offset in new[] { -0.8, -0.3, 0.3, 0.8 }) {
            foreach (var acrossX in new[] { false, true }) {
                var left = Weights(toward: (acrossX ? (-Epsilon, -1.0, offset) : (offset, -1.0, -Epsilon)), views: views);
                var right = Weights(toward: (acrossX ? (Epsilon, -1.0, offset) : (offset, -1.0, Epsilon)), views: views);

                Assert.True(condition: (left.Zip(second: right).Sum(selector: static pair => Math.Abs(value: (pair.First - pair.Second))) < 1e-5));
            }
        }
    }
    [InlineData(-1.0)]
    [InlineData(1.0)]
    [Theory]
    public void EveryQuadrantConvergesToTheSamePole(double pole) {
        var atPole = Weights(toward: (0.0, pole, 0.0), views: 8);

        foreach (var x in new[] { -1e-8, 1e-8 }) {
            foreach (var z in new[] { -1e-8, 1e-8 }) {
                var nearby = Weights(toward: (x, pole, z), views: 8);

                Assert.True(condition: (atPole.Zip(second: nearby).Sum(selector: static pair => Math.Abs(value: (pair.First - pair.Second))) < 1e-5));
            }
        }
    }
}
