using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a placement's scale must be a finite positive value under EVERY policy — the Absent policy's zero-width
/// envelope must never admit the one value inside it (exactly 0, an invisible placement with degenerate colliders
/// that boots green and renders nothing) — and a placement refused only by a zero-width envelope names the missing
/// <c>placements.policy</c> rather than reporting a bare 0..0 band.
/// </summary>
public sealed class PlacementScaleValidationLawTests {
    private const string PrototypeId = "marker";

    private static WorldPrototype Creation() {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: PrototypeId
        );

        return new WorldPrototype(
            Id: PrototypeId,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static WorldDefinition With(float scale, bool absentPolicy = false) {
        var document = Fixtures.BuildDocument();

        document = (document with {
            CreationsRaw = [Creation()],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: "row",
                    PrototypeId: PrototypeId,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: scale
                ),
            ],
        });

        return (absentPolicy
            ? (document with { AuthoringRaw = WorldPlacementPolicyDefaults.Absent })
            : document);
    }
    private static void AssertRefusedNaming(WorldDefinition definition, string needle) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: needle
        );
    }

    [Fact]
    public void ZeroScaleIsRefusedUnderAPermissivePolicy() => AssertRefusedNaming(
        definition: With(scale: 0f),
        needle: "must be a finite positive value"
    );
    [Fact]
    public void ZeroScaleIsRefusedUnderTheAbsentPolicy() => AssertRefusedNaming(
        definition: With(absentPolicy: true, scale: 0f),
        needle: "must be a finite positive value"
    );
    [Fact]
    public void NegativeScaleIsRefused() => AssertRefusedNaming(
        definition: With(scale: -1f),
        needle: "must be a finite positive value"
    );
    [Fact]
    public void AZeroWidthEnvelopeRefusalNamesTheMissingPolicy() => AssertRefusedNaming(
        definition: With(absentPolicy: true, scale: 1f),
        needle: "declares no scale envelope"
    );
    [Fact]
    public void APositiveScaleInsideTheEnvelopeValidates() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: With(scale: 1f),
                reason: out var reason
            ),
            userMessage: reason
        );
    }
}
