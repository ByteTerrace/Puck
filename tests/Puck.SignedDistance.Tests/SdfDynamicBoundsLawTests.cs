using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfDynamicBoundsLawTests {
    [Fact]
    public void FiniteDynamicBoundsIgnoreStaticPlanesAndContainRotatedScaledGeometry() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.ResetPoint().Plane(Vector3.UnitY, 0f, material);
        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 4f)
            .ResetPoint().TransformDynamic(slot: 0).Translate(offset: new Vector3(x: 1f, y: 0.5f, z: 0f)).Scale(scale: new Vector3(value: 1.5f))
            .Box(halfExtents: new Vector3(x: 0.2f, y: 0.3f, z: 0.4f), round: 0f, material: material).EndInstance();
        var bounds = builder.Build().BuildDynamicTransformBounds();
        var bound = Assert.IsType<SdfSkipSphere>(@object: Assert.Single(collection: bounds));

        Assert.InRange(bound.Radius, 1f, 3f);
        foreach (var rotation in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(pitch: 0.9f, roll: -0.2f, yaw: 0.6f) }) {
            foreach (var x in new[] { -0.2f, 0.2f }) {
                foreach (var y in new[] { -0.3f, 0.3f }) {
                    foreach (var z in new[] { -0.4f, 0.4f }) {
                        var corner = Vector3.Transform((new Vector3(x: 1f, y: 0.5f, z: 0f) + (new Vector3(x: x, y: y, z: z) * 1.5f)), rotation);

                        Assert.True(condition: (Vector3.Distance(value1: corner, value2: bound.Center) <= bound.Radius));
                    }
                }
            }
        }
    }
    [Fact]
    public void UnskippableDependenciesUseCertifiedInstancesOrRemainExplicitlyUnboundedPerSlot() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.ResetPoint().Plane(Vector3.UnitY, 0f, material);
        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 3f)
            .ResetPoint().TransformDynamic(slot: 0).Scale(scale: new Vector3(x: 2f, y: 1f, z: 0.5f)).Sphere(1f, material).EndInstance();
        builder.ResetPoint().TransformDynamic(slot: 1).Plane(Vector3.UnitY, 0f, material);
        builder.BeginInstanceDynamic(slot: 2, boundOffset: Vector3.Zero, boundRadius: 1f, active: false)
            .ResetPoint().TransformDynamic(slot: 2).Sphere(0.5f, material).EndInstance();
        var bounds = builder.Build().BuildDynamicTransformBounds();

        Assert.Equal(3, bounds.Length);
        Assert.InRange(Assert.IsType<SdfSkipSphere>(@object: bounds[0]).Radius, 3f, 3.01f);
        Assert.Equal(float.PositiveInfinity, Assert.IsType<SdfSkipSphere>(@object: bounds[1]).Radius);
        Assert.Null(value: bounds[2]);
    }
}
