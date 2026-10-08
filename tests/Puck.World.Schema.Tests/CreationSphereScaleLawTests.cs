using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// THE LAW: a creation sphere's <c>scale</c> is its radius. The primitive is the unit sphere and the shape's scale composes
/// into it (<see cref="SdfSolidGeometry.AppendScaledPrimitive"/>), so scale <c>[10,10,10]</c> is a radius-10 sphere, a
/// stamp's own scale multiplies it, and a non-uniform scale is an ellipsoid whose radii are the components.
/// </summary>
public sealed class CreationSphereScaleLawTests {
    private static double Distance(Vector3 shapeScale, float stampScale, Vector3 point) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.Emit(
            builder: builder,
            document: CreationFixtures.Document(
                name: "sphere-scale",
                shapes: [CreationFixtures.Shape(scale: shapeScale, type: SdfSolidPrimitive.Sphere)]
            ),
            materialFor: _ => material,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: stampScale,
                ReflectionNormal: null
            )
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));

        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out _,
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))
        ));

        return ((double)distance);
    }

    [InlineData(1f, 1f, 1f, 50f)]
    [InlineData(10f, 1f, 10f, 50f)]
    [InlineData(10f, 2f, 20f, 50f)]
    [InlineData(0.5f, 4f, 2f, 50f)]
    [Theory]
    public void AUniformScaleIsTheRadiusAtAnyDistance(float scale, float stamp, float radius, float distance) {
        Assert.Equal(
            actual: Distance(point: new Vector3(x: distance, y: 0f, z: 0f), shapeScale: new Vector3(value: scale), stampScale: stamp),
            expected: (distance - radius),
            precision: 2
        );
        Assert.Equal(
            actual: Distance(point: new Vector3(x: 0f, y: 0f, z: radius), shapeScale: new Vector3(value: scale), stampScale: stamp),
            expected: 0d,
            precision: 2
        );
    }
    [InlineData(1f, 1f, 1f)]
    [InlineData(10f, 1f, 10f)]
    [InlineData(10f, 2f, 20f)]
    [Theory]
    public void AShapesStampBoundContainsItsRadius(float scale, float stamp, float radius) {
        var (center, bound) = CreationStampEmitter.ShapeStampBound(
            document: CreationFixtures.Document(
                name: "sphere-scale",
                shapes: [CreationFixtures.Shape(type: SdfSolidPrimitive.Sphere, scale: new Vector3(value: scale))]
            ),
            shapeIndex: 0,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: stamp,
                ReflectionNormal: null
            )
        );

        Assert.Equal(expected: Vector3.Zero, actual: center);
        Assert.InRange(actual: bound, high: (radius * 1.01f), low: radius);
    }
}
