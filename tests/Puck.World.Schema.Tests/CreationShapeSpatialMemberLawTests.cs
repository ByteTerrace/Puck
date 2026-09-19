using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A creation shape that omits a spatial member it must author is refused by name, not by an exception
/// from the first read of it.</summary>
/// <remarks>A document omitting the member deserializes with it null, and every later check reads its value, so
/// the validator has to name the omission before any of them run.</remarks>
public sealed class CreationShapeSpatialMemberLawTests {
    private static ShapeDocument Shape() => new(
        Id: 0,
        Name: null,
        Type: SdfSolidPrimitive.Box,
        Position: new DocumentVector3(0f, 0f, 0f),
        Rotation: new DocumentQuaternion(0f, 0f, 0f, 1f),
        Scale: new DocumentVector3(1f, 1f, 1f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    private static IReadOnlyList<DocumentValidationError> Validate(ShapeDocument shape) => CreationCanonicalizer.Validate(document: new CreationDocument(
        Schema: CreationDocument.CurrentSchema,
        Name: "law",
        Palette: null,
        Shapes: [shape],
        Frames: null
    ));

    public static TheoryData<string> Members() => new(
        "position",
        "rotation",
        "scale"
    );

    [MemberData(nameof(Members))]
    [Theory]
    public void AnOmittedSpatialMemberIsNamed(string member) {
        var shape = (member switch {
            "position" => (Shape() with { Position = null! }),
            "rotation" => (Shape() with { Rotation = null! }),
            _ => (Shape() with { Scale = null! }),
        });
        var violations = Validate(shape: shape);

        Assert.Contains(
            collection: violations,
            filter: violation => (
                (violation.Path == $"shapes[0].{member}") &&
                violation.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"omits {member}"
            )
            )
        );
    }
    [Fact]
    public void AShapeAuthoringEveryOneOfThemIsAccepted() => Assert.Empty(collection: Validate(shape: Shape()));
}
