using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a program's global step scale has an author. <see cref="SdfProgram.StepScaleBinder"/> names the one
/// unscoped chain whose Lipschitz factor binds <see cref="SdfProgram.StepScale"/> below 1, so a cost sheet can name a
/// warp that was left unscoped. A primitive is never that author: every primitive is 1-Lipschitz, a non-uniformly
/// scaled sphere included (the exact exponent-2 superellipsoid gauge). Each arm pairs the taxed program with a control
/// differing in exactly one thing: the scope, the warp, or the scale.
/// </summary>
public sealed class SdfStepScaleBinderLawTests {
    // The shipped wren's hips: 0.16 x 0.1 x 0.125, a 1.6:1 squashed sphere.
    private static readonly Vector3 EccentricScale = new(
        x: 0.16f,
        y: 0.1f,
        z: 0.125f
    );
    private static readonly Vector3 RoundScale = new(value: 0.3f);

    private static SdfProgram Build(Vector3 scale, float twist, bool scoped, float? secondTwist = null) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        EmitInstance(
            builder: builder,
            material: material,
            scale: scale,
            scoped: scoped,
            twist: twist
        );

        if (secondTwist is { } second) {
            EmitInstance(
                builder: builder,
                material: material,
                scale: scale,
                scoped: false,
                twist: second
            );
        }

        return builder.Build(buildInstanceGrid: false);
    }
    private static void EmitInstance(SdfProgramBuilder builder, int material, Vector3 scale, float twist, bool scoped) {
        _ = builder.BeginInstance(
            boundCenter: Vector3.Zero,
            boundRadius: 4f
        );

        var chain = builder.ResetPoint();

        if (scoped) {
            chain = chain.PushField(compose: SdfBlendOp.Union);
        }

        if (twist != 0f) {
            chain = chain.TwistY(rate: twist);
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
    public void ARoundSphereCarriesNoFactorAndNoBinder() {
        var program = Build(
            scale: RoundScale,
            scoped: false,
            twist: 0f
        );

        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
        Assert.Null(value: program.StepScaleBinder);
    }
    [Fact]
    public void AnUnscopedEccentricSphereCarriesNoFactorAndNoBinder() {
        var program = Build(
            scale: EccentricScale,
            scoped: false,
            twist: 0f
        );

        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
        Assert.Null(value: program.StepScaleBinder);
        Assert.Contains(
            collection: program.Instructions,
            filter: instruction => (
                (instruction.Op == SdfOp.ShapeBlend) &&
                (instruction.Shape == ((uint)SdfShapeType.Superellipsoid)) &&
                (instruction.Data0.W == SdfProgramBuilder.MinSuperellipsoidExponent)
            )
        );
    }
    [Fact]
    public void AnUnscopedTwistBindsTheStepScaleAtItsOwnFactor() {
        var program = Build(
            scale: RoundScale,
            scoped: false,
            twist: 4f
        );
        var binder = Assert.NotNull(value: program.StepScaleBinder);

        Assert.True(condition: (binder.Factor > 1f));
        Assert.Equal(
            expected: (1f / binder.Factor),
            actual: program.StepScale
        );
        Assert.Equal(
            expected: SdfShapeType.Sphere,
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
    public void ScopingTheSameTwistLeavesTheGlobalStepScaleAtOne() {
        var program = Build(
            scale: RoundScale,
            scoped: true,
            twist: 4f
        );

        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
        Assert.Null(value: program.StepScaleBinder);
    }
    [Fact]
    public void TheLargestUnscopedFactorIsTheOneNamed() {
        var lone = Assert.NotNull(value: Build(
            scale: RoundScale,
            scoped: false,
            twist: 8f
        ).StepScaleBinder);
        var program = Build(
            scale: RoundScale,
            scoped: false,
            secondTwist: 8f,
            twist: 4f
        );
        var binder = Assert.NotNull(value: program.StepScaleBinder);

        Assert.Equal(
            expected: 1,
            actual: binder.InstanceIndex
        );
        Assert.Equal(
            expected: lone.Factor,
            actual: binder.Factor
        );
        Assert.Equal(
            expected: (1f / lone.Factor),
            actual: program.StepScale
        );
    }
}
