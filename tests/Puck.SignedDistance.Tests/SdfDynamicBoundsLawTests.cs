using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfDynamicBoundsLawTests {
    [Fact]
    public void FiniteDynamicBoundsIgnoreStaticPlanesAndContainRotatedScaledGeometry() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.ResetPoint().Plane(Vector3.UnitY, 0f, material);
        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 4f)
            .ResetPoint().TransformDynamic(0).Translate(new Vector3(1f, 0.5f, 0f)).Scale(new Vector3(1.5f))
            .Box(halfExtents: new Vector3(0.2f, 0.3f, 0.4f), round: 0f, material: material).EndInstance();
        var bounds = builder.Build().BuildDynamicTransformBounds();
        var bound = Assert.IsType<SdfSkipSphere>(Assert.Single(bounds));
        Assert.InRange(bound.Radius, 1f, 3f);
        foreach (var rotation in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(0.6f, 0.9f, -0.2f) }) {
            foreach (var x in new[] { -0.2f, 0.2f }) {
                foreach (var y in new[] { -0.3f, 0.3f }) {
                    foreach (var z in new[] { -0.4f, 0.4f }) {
                        var corner = Vector3.Transform(new Vector3(1f, 0.5f, 0f) + new Vector3(x, y, z) * 1.5f, rotation);
                        Assert.True(Vector3.Distance(corner, bound.Center) <= bound.Radius);
                    }
                }
            }
        }
    }

    [Fact]
    public void UnskippableDependenciesUseCertifiedInstancesOrRemainExplicitlyUnboundedPerSlot() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.ResetPoint().Plane(Vector3.UnitY, 0f, material);
        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 3f)
            .ResetPoint().TransformDynamic(0).Scale(new Vector3(2f, 1f, 0.5f)).Sphere(1f, material).EndInstance();
        builder.ResetPoint().TransformDynamic(1).Plane(Vector3.UnitY, 0f, material);
        builder.BeginInstanceDynamic(slot: 2, boundOffset: Vector3.Zero, boundRadius: 1f, active: false)
            .ResetPoint().TransformDynamic(2).Sphere(0.5f, material).EndInstance();
        var bounds = builder.Build().BuildDynamicTransformBounds();
        Assert.Equal(3, bounds.Length);
        Assert.InRange(Assert.IsType<SdfSkipSphere>(bounds[0]).Radius, 3f, 3.01f);
        Assert.Equal(float.PositiveInfinity, Assert.IsType<SdfSkipSphere>(bounds[1]).Radius);
        Assert.Null(bounds[2]);
    }
}
