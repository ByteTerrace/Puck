using System.Numerics;

using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Pins the host half of the superellipsoid's strip split in sdf-vm.hlsli: the fold tier compiles only the
/// exponent-2 ellipsoid gauge of <c>SDF_SHAPE_SUPERELLIPSOID</c>, so <see cref="SdfViewsKernelVariants.Select"/> must
/// send every other exponent to <see cref="SdfViewsKernelVariant.Full"/>, or the fold kernel would evaluate a
/// squircle as an ellipsoid.</summary>
public sealed class SdfViewsKernelVariantLawTests {
    private static SdfProgram Superellipsoid(float exponent) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder.Superellipsoid(
            exponent: exponent,
            material: material,
            radii: new Vector3(
                x: 1f,
                y: 0.1f,
                z: 0.5f
            )
        ).Build();
    }

    [Fact]
    public void AnEllipsoidRunsTheFoldTier() {
        var (variant, touch) = SdfViewsKernelVariants.Select(program: Superellipsoid(exponent: SdfProgramBuilder.MinSuperellipsoidExponent));

        Assert.Equal(
            actual: variant,
            expected: SdfViewsKernelVariant.Folds
        );
        Assert.False(condition: string.IsNullOrEmpty(value: touch));
    }
    [InlineData(2.0001f)]
    [InlineData(4f)]
    [InlineData(8f)]
    [Theory]
    public void EveryOtherExponentRunsTheFullKernel(float exponent) {
        var (variant, _) = SdfViewsKernelVariants.Select(program: Superellipsoid(exponent: exponent));

        Assert.Equal(
            actual: variant,
            expected: SdfViewsKernelVariant.Full
        );
    }
}
