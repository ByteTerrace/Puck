using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>General warp selectors, packed payloads, and conservative bounds.</summary>
public sealed class GeneralWarpLawTests {
    private static SdfProgram Program(Action<SdfProgramBuilder> warp) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        builder.ResetPoint();
        warp(builder);
        return builder.Sphere(1f, material).Build();
    }
    [Fact]
    public void EveryPlaneAndDriverPairPacksAndBounds() {
        for (var plane = 0; plane < 3; plane++) {
            for (var driver = 0; driver < 3; driver++) {
                var p = Program(b => b.RotatePlane(plane, driver, 0.5f, 3f));
                var op = Assert.Single(p.Instructions, i => i.Op == SdfOp.RotatePlane);
                Assert.Equal((uint)plane, op.Shape);
                Assert.Equal((uint)driver, op.Blend);
                Assert.Equal(new Vector4(0.5f, 3f, 0f, 0f), op.Data0);
                Assert.InRange(p.StepScale, float.Epsilon, 1f);
            }
        }
    }
    [Fact]
    public void ConvenienceRotationsCompileToTheSamePrimitive() {
        var cases = new (Action<SdfProgramBuilder> Sugar, int Plane, int Driver)[] {
            (b => b.BendX(0.3f), 0, 0), (b => b.BendY(0.3f), 0, 1),
            (b => b.BendZ(0.3f), 1, 1), (b => b.TwistY(0.3f), 2, 1),
        };
        foreach (var c in cases) {
            Assert.True(Program(c.Sugar).Words.SequenceEqual(Program(b => b.RotatePlane(c.Plane, c.Driver, 0.3f)).Words));
        }
    }
    [Fact]
    public void CubicShearBoundsEveryDistinctAxisPair() {
        for (var target = 0; target < 3; target++) {
            for (var driver = 0; driver < 3; driver++) {
                if (target == driver) { continue; }
                var program = Program(b => b.Shear(0.2f, 0.3f, 0.4f, target, driver));
                var op = Assert.Single(program.Instructions, i => i.Op == SdfOp.Shear);
                Assert.Equal((uint)target, op.Shape);
                Assert.Equal((uint)driver, op.Blend);
                // At |driver| <= 1 the slope is at most .2 + .6 + 1.2 = 2.
                // The exact 2D shear norm at slope 2 is 1 + sqrt(2).
                Assert.True(program.StepScale <= 1f / (1f + MathF.Sqrt(2f)) + 1e-6f);
            }
        }
    }
    [Fact]
    public void AxialProfileCarriesStartScaleAndAxisWithoutChangingTheBoundByPermutation() {
        float? step = null;
        for (var axis = 0; axis < 3; axis++) {
            var p = Program(b => b.AxialProfile(0.4f, 0.2f, 1f, 2f, axis, 0.5f));
            var op = Assert.Single(p.Instructions, i => i.Op == SdfOp.AxialProfile);
            Assert.Equal((uint)axis, op.Shape);
            Assert.Equal(0.5f, op.Data1.Y);
            if (step is { } expected) { Assert.Equal(expected, p.StepScale); }
            step = p.StepScale;
        }
    }
    private static float Step(Action<SdfProgramBuilder> chain) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        builder.ResetPoint();
        chain(builder);
        return builder.Sphere(1f, material).Build().StepScale;
    }
    [Fact]
    public void AScaleUpstreamOfAWarpLeavesTheWarpsOwnReachUnchanged() {
        var unscaled = Step(b => b.RotatePlane(2, 1, 1f));
        Assert.True(unscaled < 1f);
        Assert.Equal(unscaled, Step(b => b.Scale(new Vector3(0.1f)).RotatePlane(2, 1, 1f)));
        Assert.Equal(unscaled, Step(b => b.Scale(new Vector3(10f)).RotatePlane(2, 1, 1f)));
    }
    [Fact]
    public void AScaleDownstreamOfAWarpWidensTheWarpsReachByThatScale() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        var tenfold = builder.ResetPoint().RotatePlane(2, 1, 1f).Sphere(10f, material).Build().StepScale;
        Assert.Equal(tenfold, Step(b => b.RotatePlane(2, 1, 1f).Scale(new Vector3(10f))));
        Assert.True(tenfold < Step(b => b.RotatePlane(2, 1, 1f)));
    }
    [Fact]
    public void OnlyTranslatesAfterACellJitterCountTowardItsContainment() {
        // spacing/2 = 5 and jitter/2 = 1 leave a 4-unit prototype budget; a 3.5 offset on a unit sphere spends 4.5.
        static SdfProgramBuilder Lattice(SdfProgramBuilder b) => b.CellJitter(jitter: 2f, spacing: new Vector3(10f));
        _ = Step(b => Lattice(b.Translate(new Vector3(3.5f, 0f, 0f))));
        Assert.Throws<ArgumentException>(() => Step(b => Lattice(b).Translate(new Vector3(3.5f, 0f, 0f))));
    }
    [Fact]
    public void InvalidSelectorsAreRefusedBeforePacking() {
        Assert.Throws<ArgumentOutOfRangeException>(() => Program(b => b.RotatePlane(3, 0, 1f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Program(b => b.RotatePlane(0, -1, 1f)));
        Assert.Throws<ArgumentException>(() => Program(b => b.Shear(1f, 0f, target: 1, driver: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Program(b => b.AxialProfile(0f, 0f, 1f, 1f, startScale: 0f)));
    }
}
