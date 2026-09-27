using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>General warp selectors, packed payloads, and conservative bounds.</summary>
public sealed class GeneralWarpLawTests {
    private static SdfProgram Program(Action<SdfProgramBuilder> warp) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        builder.ResetPoint();
        warp(builder);
        return builder.Sphere(
            1f,
            material
        ).Build();
    }
    private static float Step(Action<SdfProgramBuilder> chain) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        builder.ResetPoint();
        chain(builder);
        return builder.Sphere(
            1f,
            material
        ).Build().StepScale;
    }

    [Fact]
    public void AScaleDownstreamOfAWarpWidensTheWarpsReachByThatScale() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));
        var tenfold = builder.ResetPoint().RotatePlane(
            driver: SdfAxis.Y,
            plane: SdfPlane.XZ,
            rate: 1f
        ).Sphere(
            10f,
            material
        ).Build().StepScale;

        Assert.Equal(
            tenfold,
            Step(chain: b => b.RotatePlane(
                driver: SdfAxis.Y,
                plane: SdfPlane.XZ,
                rate: 1f
            ).Scale(scale: new Vector3(value: 10f)))
        );
        Assert.True(condition: (tenfold < Step(chain: b => b.RotatePlane(
            driver: SdfAxis.Y,
            plane: SdfPlane.XZ,
            rate: 1f
        ))));
    }
    [Fact]
    public void AScaleUpstreamOfAWarpLeavesTheWarpsOwnReachUnchanged() {
        var unscaled = Step(chain: b => b.RotatePlane(
            driver: SdfAxis.Y,
            plane: SdfPlane.XZ,
            rate: 1f
        ));

        Assert.True(condition: (unscaled < 1f));
        Assert.Equal(
            unscaled,
            Step(chain: b => b.Scale(scale: new Vector3(value: 0.1f)).RotatePlane(
                driver: SdfAxis.Y,
                plane: SdfPlane.XZ,
                rate: 1f
            ))
        );
        Assert.Equal(
            unscaled,
            Step(chain: b => b.Scale(scale: new Vector3(value: 10f)).RotatePlane(
                driver: SdfAxis.Y,
                plane: SdfPlane.XZ,
                rate: 1f
            ))
        );
    }
    [Fact]
    public void AxialProfileCarriesStartScaleAndAxisWithoutChangingTheBoundByPermutation() {
        float? step = null;

        foreach (var axis in Enum.GetValues<SdfAxis>()) {
            var p = Program(warp: b => b.AxialProfile(
                amount: 0.4f,
                axis: axis,
                bulge: 0.2f,
                span: 2f,
                startScale: 0.5f,
                top: 1f
            ));
            var op = Assert.Single(
                collection: p.Instructions,
                predicate: i => (i.Op == SdfOp.AxialProfile)
            );

            Assert.Equal(
                ((uint)axis),
                op.Shape
            );
            Assert.Equal(
                0.5f,
                op.Data1.Y
            );
            if (step is { } expected) {
                Assert.Equal(
                expected,
                p.StepScale
            );
            }
            step = p.StepScale;
        }
    }
    [Fact]
    public void ConvenienceRotationsCompileToTheSamePrimitive() {
        var cases = new (Action<SdfProgramBuilder> Sugar, SdfPlane Plane, SdfAxis Driver)[] {
            (b => b.BendX(rate: 0.3f), SdfPlane.XY, SdfAxis.X), (b => b.BendY(rate: 0.3f), SdfPlane.XY, SdfAxis.Y),
            (b => b.BendZ(rate: 0.3f), SdfPlane.YZ, SdfAxis.Y), (b => b.TwistY(rate: 0.3f), SdfPlane.XZ, SdfAxis.Y),
        };

        foreach (var c in cases) {
            Assert.True(condition: Program(warp: c.Sugar).Words.SequenceEqual(other: Program(warp: b => b.RotatePlane(
                driver: c.Driver,
                plane: c.Plane,
                rate: 0.3f
            )).Words));
        }
    }
    [Fact]
    public void CubicShearBoundsEveryDistinctAxisPair() {
        foreach (var target in Enum.GetValues<SdfAxis>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>()) {
                if (target == driver) { continue; }
                var program = Program(warp: b => b.Shear(
                    cubic: 0.4f,
                    driver: driver,
                    linear: 0.2f,
                    quadratic: 0.3f,
                    target: target
                ));
                var op = Assert.Single(
                    collection: program.Instructions,
                    predicate: i => (i.Op == SdfOp.Shear)
                );

                Assert.Equal(
                    ((uint)target),
                    op.Shape
                );
                Assert.Equal(
                    ((uint)driver),
                    op.Blend
                );
                // At |driver| <= 1 the slope is at most .2 + .6 + 1.2 = 2.
                // The exact 2D shear norm at slope 2 is 1 + sqrt(2).
                Assert.True(condition: (program.StepScale <= ((1f / (1f + MathF.Sqrt(x: 2f))) + 1e-6f)));
            }
        }
    }
    [Fact]
    public void EveryPlaneAndDriverPairPacksAndBounds() {
        foreach (var plane in Enum.GetValues<SdfPlane>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>()) {
                var p = Program(warp: b => b.RotatePlane(
                    driver: driver,
                    origin: 3f,
                    plane: plane,
                    rate: 0.5f
                ));
                var op = Assert.Single(
                    collection: p.Instructions,
                    predicate: i => (i.Op == SdfOp.RotatePlane)
                );

                Assert.Equal(
                    ((uint)plane),
                    op.Shape
                );
                Assert.Equal(
                    ((uint)driver),
                    op.Blend
                );
                Assert.Equal(
                    new Vector4(
                        w: 0f,
                        x: 0.5f,
                        y: 3f,
                        z: 0f
                    ),
                    op.Data0
                );
                Assert.InRange(
                    p.StepScale,
                    float.Epsilon,
                    1f
                );
            }
        }
    }
    [Fact]
    public void InvalidSelectorsAreRefusedBeforePacking() {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Program(warp: b => b.RotatePlane(
            driver: SdfAxis.X,
            plane: ((SdfPlane)3),
            rate: 1f
        )));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Program(warp: b => b.RotatePlane(
            driver: ((SdfAxis)uint.MaxValue),
            plane: SdfPlane.XZ,
            rate: 1f
        )));
        Assert.Throws<ArgumentException>(testCode: () => Program(warp: b => b.Shear(
            1f,
            0f,
            target: SdfAxis.Y,
            driver: SdfAxis.Y
        )));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Program(warp: b => b.AxialProfile(
            0f,
            0f,
            1f,
            1f,
            startScale: 0f
        )));
    }
    [Fact]
    public void OnlyTranslatesAfterACellJitterCountTowardItsContainment() {
        // spacing/2 = 5 and jitter/2 = 1 leave a 4-unit prototype budget; a 3.5 offset on a unit sphere spends 4.5.
        static SdfProgramBuilder Lattice(SdfProgramBuilder b) => b.CellJitter(
            jitter: 2f,
            spacing: new Vector3(value: 10f)
        );
        _ = Step(chain: b => Lattice(b: b.Translate(offset: new Vector3(
            x: 3.5f,
            y: 0f,
            z: 0f
        ))));
        Assert.Throws<ArgumentException>(testCode: () => Step(chain: b => Lattice(b: b).Translate(offset: new Vector3(
            x: 3.5f,
            y: 0f,
            z: 0f
        ))));
    }
}
