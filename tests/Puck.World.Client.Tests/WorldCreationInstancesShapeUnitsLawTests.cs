using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: the animated stamp pool emits a body in its slots' own units, so a look at any scale emits the program a
/// look at scale one emits — every instruction, bound and declared detail — and its slots carry the scale
/// (<see cref="DynamicTransform.Scale"/>). That is what keeps a body's meshes when it changes size: the bake keys and
/// samples a group in its slot's units, and the group's draws scale it.
/// </summary>
public sealed class WorldCreationInstancesShapeUnitsLawTests {
    private const string PrototypeId = "radii";

    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 scale, int group = 0) => new(
        Id: id,
        Name: null,
        Type: type,
        Position: new Vector3(x: (0.1f * id), y: 0.2f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: scale,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: group
    );
    // Every creation-unit length a shape authors: edge radii, field ops, a smooth blend radius, a flare's height and
    // span, a fold, and a round section that declares detail.
    private static IReadOnlyList<ShapeDocument> Shapes() => [
        Shape(1, SdfSolidPrimitive.Cylinder, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f)) with { Rounding = 0.02f },
        Shape(2, SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f)) with { Chamfer = 0.03f, Dilate = 0.02f },
        Shape(3, SdfSolidPrimitive.Box, new Vector3(x: 0.3f, y: 0.2f, z: 0.3f)) with { Onion = 0.02f },
        Shape(4, SdfSolidPrimitive.Box, Vector3.One, group: 1),
        Shape(5, SdfSolidPrimitive.Sphere, new Vector3(value: 0.03f), group: 1) with { Blend = SdfBlendOp.SmoothUnion, Smooth = 0.05f },
        Shape(6, SdfSolidPrimitive.Box, Vector3.One) with { Flare = new(Amount: 0.3f, Bulge: 0.1f, Span: 0.4f, Top: 0.2f) },
        Shape(7, SdfSolidPrimitive.Box, new Vector3(value: 0.1f)) with { Domain = [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)] },
        Shape(8, SdfSolidPrimitive.Capsule, new Vector3(x: 0.01f, y: 0.05f, z: 0.01f)),
    ];

    [InlineData(0.25f)]
    [InlineData(2f)]
    [Theory]
    public void ALookAtAnyScaleEmitsTheProgramOfALookAtScaleOne(float bodyScale) {
        var unit = CreationFixtures.EmitPool(bodyScale: 1f, name: PrototypeId, shapes: Shapes());
        var scaled = CreationFixtures.EmitPool(bodyScale: bodyScale, name: PrototypeId, shapes: Shapes());

        Assert.Equal(expected: unit.Instructions.Count, actual: scaled.Instructions.Count);
        for (var index = 0; (index < unit.Instructions.Count); index++) {
            Assert.Equal(expected: unit.Instructions[index], actual: scaled.Instructions[index]);
        }
        Assert.Equal(expected: unit.Instances.Count, actual: scaled.Instances.Count);
        Assert.Contains(collection: unit.Instances, filter: static instance => (instance.Detail > 0f));
        for (var index = 0; (index < unit.Instances.Count); index++) {
            Assert.Equal(expected: unit.Instances[index].Radius, actual: scaled.Instances[index].Radius);
            Assert.Equal(expected: unit.Instances[index].Center, actual: scaled.Instances[index].Center);
            Assert.Equal(expected: unit.Instances[index].Detail, actual: scaled.Instances[index].Detail);
        }
    }
}
