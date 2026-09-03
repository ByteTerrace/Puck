using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a program's global step scale has an author. <see cref="SdfProgram.StepScaleBinder"/> names the one
/// unscoped chain whose Lipschitz factor binds <see cref="SdfProgram.StepScale"/> below 1, and
/// <see cref="SdfSolidGeometry.StepFactor"/> predicts that factor from the authored primitive and scale before any
/// program is built, so a stamper can decide to scope an eccentric shape and a cost sheet can name one that was not.
/// Each arm pairs the taxed program with a control differing in exactly one thing: the scope, or the scale.
/// </summary>
public sealed class SdfStepScaleBinderLawTests {
    // The shipped wren's hips: 0.16 x 0.1 x 0.125, the squashed sphere that taxed every march in the frame at 1.6x.
    private static readonly Vector3 EccentricScale = new(
        x: 0.16f,
        y: 0.1f,
        z: 0.125f
    );

    private static SdfProgram Build(Vector3 scale, bool scoped, Vector3? secondScale = null) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        EmitInstance(
            builder: builder,
            material: material,
            scale: scale,
            scoped: scoped
        );

        if (secondScale is { } second) {
            EmitInstance(
                builder: builder,
                material: material,
                scale: second,
                scoped: false
            );
        }

        return builder.Build(buildInstanceGrid: false);
    }
    private static void EmitInstance(SdfProgramBuilder builder, int material, Vector3 scale, bool scoped) {
        _ = builder.BeginInstance(
            boundCenter: Vector3.Zero,
            boundRadius: 4f
        );

        var chain = builder.ResetPoint();

        if (scoped) {
            chain = chain.PushField(compose: SdfBlendOp.Union);
        }

        chain = SdfSolidGeometry.AppendScaledPrimitive(
            chain: chain,
            type: SdfSolidPrimitive.Sphere,
            scale: scale,
            material: material
        );

        if (scoped) {
            _ = chain.PopField();
        }

        _ = builder.EndInstance();
    }

    [Fact]
    public void AnUnscopedEccentricSphereBindsTheStepScaleAtItsOwnFactor() {
        var factor = SdfSolidGeometry.StepFactor(
            type: SdfSolidPrimitive.Sphere,
            scale: EccentricScale
        );
        var program = Build(
            scale: EccentricScale,
            scoped: false
        );

        Assert.Equal(
            expected: (0.16f / 0.1f),
            actual: factor
        );
        Assert.Equal(
            expected: (1f / factor),
            actual: program.StepScale
        );

        var binder = Assert.NotNull(program.StepScaleBinder);

        Assert.Equal(
            expected: factor,
            actual: binder.Factor
        );
        Assert.Equal(
            expected: SdfShapeType.Ellipsoid,
            actual: binder.Shape
        );
        Assert.Equal(
            expected: 0,
            actual: binder.InstanceIndex
        );
        Assert.Equal(
            expected: SdfOp.ShapeBlend,
            actual: program.Instructions[binder.InstructionIndex].Op
        );
    }
    [Fact]
    public void ScopingTheSameSphereLeavesTheGlobalStepScaleAtOne() {
        var program = Build(
            scale: EccentricScale,
            scoped: true
        );

        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
        Assert.Null(program.StepScaleBinder);
    }
    [Fact]
    public void ARoundSphereCarriesNoFactorAndNoBinder() {
        Assert.Equal(
            expected: 1f,
            actual: SdfSolidGeometry.StepFactor(
                type: SdfSolidPrimitive.Sphere,
                scale: new Vector3(value: 0.3f)
            )
        );

        var program = Build(
            scale: new Vector3(value: 0.3f),
            scoped: false
        );

        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
        Assert.Null(program.StepScaleBinder);
    }
    [Fact]
    public void TheLargestUnscopedFactorIsTheOneNamed() {
        var program = Build(
            scale: EccentricScale,
            scoped: false,
            secondScale: new Vector3(
                x: 0.2f,
                y: 0.1f,
                z: 0.2f
            )
        );
        var binder = Assert.NotNull(program.StepScaleBinder);

        Assert.Equal(
            expected: 1,
            actual: binder.InstanceIndex
        );
        Assert.Equal(
            expected: 2f,
            actual: binder.Factor
        );
        Assert.Equal(
            expected: 0.5f,
            actual: program.StepScale
        );
    }
    [Fact]
    public void EveryOtherPrimitiveReportsFactorOne() {
        foreach (var type in Enum.GetValues<SdfSolidPrimitive>()) {
            if (type is SdfSolidPrimitive.Sphere or SdfSolidPrimitive.Ellipsoid) {
                continue;
            }

            Assert.Equal(
                expected: 1f,
                actual: SdfSolidGeometry.StepFactor(
                    type: type,
                    scale: EccentricScale
                )
            );
        }
    }
}
