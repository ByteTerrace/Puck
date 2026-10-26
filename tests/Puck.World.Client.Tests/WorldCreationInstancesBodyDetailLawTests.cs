using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: a pooled body's instance declares the bake detail its round sections ask for, a third of the section's
/// radius at the look's scale (<see cref="SdfInstanceRange.Detail"/>), so the bake turns each cell under the contour's
/// crease split; a flat-faced shape declares none, and a blend group takes its finest member at or above the group floor,
/// never a thinner member's, which would refine the whole group.
/// </summary>
public sealed class WorldCreationInstancesBodyDetailLawTests {
    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 scale, int group = 0) => new(
        Id: id,
        Name: null,
        Type: type,
        Position: new Vector3(x: (0.2f * id), y: 0f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: scale,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: group
    );

    [Fact]
    public void EachRoundSectionDeclaresAThirdOfItsRadiusAndAGroupItsFinestMemberAboveTheFloor() {
        ShapeDocument[] shapes = [
            Shape(id: 1, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.Torus),
            Shape(id: 2, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.RoundCone),
            (Shape(id: 3, scale: new Vector3(x: 0.02f, y: 0.03f, z: 0.1f), type: SdfSolidPrimitive.Superellipsoid) with { Exponent = 4f }),
            (Shape(id: 4, scale: new Vector3(x: 0.02f, y: 0.03f, z: 0.1f), type: SdfSolidPrimitive.Prism) with { Profile = new SdfPrismProfile(Kind: SdfPrismProfileKind.Ellipse) }),
            Shape(id: 5, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.Box),
            Shape(group: 1, id: 6, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.Sphere),
            Shape(group: 1, id: 7, scale: new Vector3(x: 0.006f, y: 0.05f, z: 0.006f), type: SdfSolidPrimitive.Capsule),
        ];
        var program = CreationFixtures.EmitPool(
            bodyScale: 1f,
            name: "detail",
            shapes: shapes
        );
        var details = program.Instances
            .Where(predicate: static instance => (instance.Active && instance.IsDynamic))
            .Select(selector: static instance => instance.Detail)
            .Order()
            .ToArray();

        Assert.Equal(
            [0f, 0.004f, (0.02f * 0.02f / 0.03f / 3f), 0.005f, 0.005f, 0.01f],
            details,
            new FloatTolerance()
        );
    }

    private sealed class FloatTolerance : IEqualityComparer<float> {
        public bool Equals(float x, float y) => (MathF.Abs(x - y) <= 1.0e-6f);
        public int GetHashCode(float obj) => 0;
    }
}
