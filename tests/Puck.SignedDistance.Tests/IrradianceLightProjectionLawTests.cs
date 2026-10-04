using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class IrradianceLightProjectionLawTests {
    [Fact]
    public void TheFiniteCameraEnclosesEveryReceiverAndCasterDepthWithoutDirectionalDivergence() {
        var projection = IrradianceLightFixture.Projection(resolution: 512);
        foreach (var x in new[] { -4.0, 4.0 }) {
            foreach (var z in new[] { -4.0, 4.0 }) {
                Assert.True(condition: projection.Project(point: new Double3(X: x, Y: 0, Z: z), column: out _, row: out _, travel: out _));
            }
        }
        foreach (var x in new[] { -6.0, 6.0 }) {
            foreach (var y in new[] { -0.2, 1.25 }) {
                foreach (var z in new[] { -6.0, 6.0 }) {
                    var axial = Double3.Dot(a: (projection.Origin - new Double3(X: x, Y: y, Z: z)), b: projection.TowardLight);
                    Assert.InRange(actual: axial, low: projection.Near, high: projection.Far);
                }
            }
        }
        var opposite = projection.OriginAt(column: 511, row: 511) - projection.OriginAt(column: 0, row: 0);
        Assert.Equal(expected: 0, actual: Double3.Dot(a: opposite, b: projection.TowardLight), precision: 12);
        Assert.InRange(actual: projection.Origin.Length, low: 0, high: 10);
        Assert.InRange(actual: projection.Far, low: 0, high: 10);
        var narrow = IrradianceLightProjection.Create(receiverMin: new Double3(X: -4, Y: 0, Z: -4), receiverMax: new Double3(X: 4, Y: 0, Z: 4),
            casterMin: new Double3(X: -6, Y: -0.2, Z: -6), casterMax: new Double3(X: 6, Y: 1.25, Z: 6),
            towardLight: IrradianceLightFixture.Sun, penumbraSlope: double.Epsilon, resolution: 512);
        Assert.Equal(expected: projection, actual: narrow);
        Assert.False(condition: projection.Project(point: projection.Origin, column: out _, row: out _, travel: out _));
        Assert.False(condition: projection.Project(point: (projection.Origin - (projection.TowardLight * (projection.Far + 1))), column: out _, row: out _, travel: out _));
    }

    [Fact]
    public void TheParallelSweepSeesTheSubtexelRodThatPointRaysMiss() {
        var field = new IrradianceField(program: IrradianceLightFixture.Program());
        var projection = IrradianceLightFixture.Projection(resolution: 64);
        var swept = new IrradianceLightView(field: field, projection: projection);
        var points = new IrradianceLightView(field: field, projection: projection, sweepRadius: 0);
        var misses = 0;
        var answered = 0;
        var widened = 0;

        foreach (var receiver in IrradianceLightFixture.Receivers()) {
            var exact = field.SegmentClear(from: (receiver + (IrradianceLightFixture.Up * 0.004)), to: (receiver + (IrradianceLightFixture.Sun * 10)));
            var lit = swept.Lit(point: receiver, normal: IrradianceLightFixture.Up);
            if (lit.HasValue) { answered++; }
            Assert.False(condition: (lit == true && !exact), userMessage: $"A swept column lit through a caster at {receiver}.");
            if (exact && (lit == false)) {
                Assert.True(condition: IrradianceLightFixture.NearShadow(point: receiver, radius: (2 * swept.TexelSize), sun: IrradianceLightFixture.Sun),
                    userMessage: $"A swept column shadowed beyond two texels at {receiver}.");
                widened++;
            }
            if (!exact && (points.Lit(point: receiver, normal: IrradianceLightFixture.Up) == true)) { misses++; }
        }
        Assert.True(condition: (answered > 0));
        Assert.True(condition: (widened > 0));
        Assert.True(condition: (misses > 0), userMessage: "Zero-radius rays must miss the rod in this fixture.");
    }

    [Fact]
    public void ZeroPenumbraCannotConstructAFiniteLightCamera() => Assert.Throws<ArgumentOutOfRangeException>(testCode: () =>
        IrradianceLightProjection.Create(receiverMin: default, receiverMax: default, casterMin: default, casterMax: default,
            towardLight: IrradianceLightFixture.Sun, penumbraSlope: 0, resolution: 512));
}
