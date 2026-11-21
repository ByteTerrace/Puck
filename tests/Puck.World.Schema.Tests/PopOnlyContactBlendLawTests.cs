using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Laws that a solid creation's contact field takes every blend its render field takes: a pop-only blend
/// (Morph, the stairs blends) composes at a scope of the shape's own, with or without a contact margin.</summary>
public sealed class PopOnlyContactBlendLawTests {
    private static ShapeDocument Shape(SdfSolidPrimitive type, SdfBlendOp blend, Vector3 position) => new(
        Id: 0,
        Name: null,
        Type: type,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: Vector3.One,
        Material: 0,
        Blend: blend,
        Smooth: 0.25f,
        Group: 0
    );
    private static SdfFieldEvaluator Field(IReadOnlyList<ShapeDocument> shapes, float? margin) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.EmitFixed(
            builder: builder,
            contactMargin: margin,
            document: new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "pop-only-contact-law", Palette: null, Shapes: shapes, Frames: null),
            materialFor: _ => material,
            transform: new FixedCreationStampTransform(Origin: FixedVector3.Zero, Rotation: FixedQuaternion.Identity, Scale: FixedQ4816.One, ReflectionNormal: null)
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedQ4816 Distance(SdfFieldEvaluator field, Vector3 point) {
        Assert.True(condition: field.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))));

        return distance;
    }

    // A static placement reads lane 0, so a morph from 0 to 1 shows the field before the shape alone.
    [Theory]
    [InlineData(null)]
    [InlineData(0.05f)]
    public void AMorphAtItsFromValueReadsTheFieldBeforeItInContact(float? margin) {
        var box = Shape(blend: SdfBlendOp.Union, position: Vector3.Zero, type: SdfSolidPrimitive.Box);
        var morphed = Field(margin: margin, shapes: [box, Shape(blend: SdfBlendOp.Morph, position: new Vector3(x: 3f, y: 0f, z: 0f), type: SdfSolidPrimitive.Sphere) with {
            Morph = new ShapeMorphDocument(Lane: 0, From: 0f, To: 1f),
        }]);
        var alone = Field(margin: margin, shapes: [box]);

        foreach (var point in (ReadOnlySpan<Vector3>)[new(x: 3f, y: 0f, z: 0f), new(x: 0f, y: 2f, z: 0f), new(x: 1.5f, y: 0.5f, z: -0.5f)]) {
            Assert.True(condition: (FixedQ4816.Abs(value: (Distance(field: morphed, point: point) - Distance(field: alone, point: point))) <= FixedQ4816.FromDouble(value: 0.002)));
        }
    }
    [Theory]
    [InlineData(SdfBlendOp.StairsUnion)]
    [InlineData(SdfBlendOp.StairsSubtraction)]
    public void AStairsBlendComposesInContact(SdfBlendOp blend) {
        var field = Field(margin: null, shapes: [
            Shape(blend: SdfBlendOp.Union, position: Vector3.Zero, type: SdfSolidPrimitive.Box),
            Shape(blend: blend, position: new Vector3(x: 1f, y: 0f, z: 0f), type: SdfSolidPrimitive.Sphere),
        ]);

        _ = Distance(field: field, point: new Vector3(x: 0f, y: 2f, z: 0f));
    }
}
