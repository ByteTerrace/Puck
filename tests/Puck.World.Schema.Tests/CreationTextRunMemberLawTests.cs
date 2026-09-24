using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A creation text run that omits its text, position or rotation is refused by name, the way a shape
/// omitting a spatial member is, not by an exception from the first read of it.</summary>
public sealed class CreationTextRunMemberLawTests {
    private static TextRunDocument Run() => new(
        Text: "Ada",
        Position: new DocumentVector3(x: 0f, y: 0f, z: 0.1f),
        Rotation: new DocumentQuaternion(w: 1f, x: 0f, y: 0f, z: 0f),
        EmHeight: 0.02f,
        Depth: 0.001f,
        Mode: null,
        Material: 0
    );
    private static IReadOnlyList<DocumentValidationError> Validate(TextRunDocument run) => CreationCanonicalizer.Validate(document: new CreationDocument(
        Schema: CreationDocument.CurrentSchema,
        Name: "law",
        Palette: null,
        Shapes: [new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Box,
            Position: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            Rotation: new DocumentQuaternion(w: 1f, x: 0f, y: 0f, z: 0f),
            Scale: new DocumentVector3(x: 0.1f, y: 0.02f, z: 0.01f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        )],
        Frames: null
    ) {
        TextRuns = [run],
    });

    public static TheoryData<string> Members() => new(
        "text",
        "position",
        "rotation"
    );
    [MemberData(nameof(Members))]
    [Theory]
    public void AnOmittedMemberIsNamed(string member) {
        var run = (member switch {
            "text" => (Run() with { Text = null! }),
            "position" => (Run() with { Position = null! }),
            _ => (Run() with { Rotation = null! }),
        });
        var violations = Validate(run: run);

        Assert.Contains(
            collection: violations,
            filter: violation => (
                (violation.Path == $"textRuns[0].{member}") &&
                violation.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"omits {member}"
            )
            )
        );
    }
    [Fact]
    public void ARunAuthoringEveryOneOfThemIsAccepted() => Assert.Empty(collection: Validate(run: Run()));
    // A zero quaternion is finite but carries no rotation to normalize into — see
    // CreationShapeSpatialMemberLawTests.AZeroRotationIsRefusedByName for the shared reason.
    [Fact]
    public void AZeroRotationIsRefusedByName() {
        var violations = Validate(run: (Run() with { Rotation = new DocumentQuaternion(w: 0f, x: 0f, y: 0f, z: 0f) }));

        Assert.Contains(
            collection: violations,
            filter: violation => (
                (violation.Path == "textRuns[0].rotation") &&
                violation.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "zero quaternion"
            )
            )
        );
    }
}
