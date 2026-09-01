using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a placement's scale must be a finite positive value under EVERY policy — a zero-width envelope must
/// never admit the one value inside it (exactly 0, an invisible placement with degenerate colliders that boots
/// green and renders nothing). An UNAUTHORED <c>placements.policy</c> derives its scale envelope from the rows'
/// own authored scales (<see cref="WorldPlacementPolicyDefaults.DeriveFrom"/>), so a static world validates
/// exactly what it authored; a DECLARED policy still refuses an out-of-envelope scale by name.
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
    private static WorldDefinition With(float scale, WorldPlacementPolicyDefaults? policy = null, bool unauthoredPolicy = false) {
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

        return (unauthoredPolicy
            ? (document with { AuthoringRaw = null })
            : (policy is { } declared)
            ? (document with { AuthoringRaw = declared })
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
    private static void AssertValidates(WorldDefinition definition) {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
    }

    [Fact]
    public void ZeroScaleIsRefusedUnderAPermissivePolicy() => AssertRefusedNaming(
        definition: With(scale: 0f),
        needle: "must be a finite positive value"
    );
    [Fact]
    public void ZeroScaleIsRefusedUnderTheUnauthoredPolicy() => AssertRefusedNaming(
        definition: With(scale: 0f, unauthoredPolicy: true),
        needle: "must be a finite positive value"
    );
    [Fact]
    public void NegativeScaleIsRefused() => AssertRefusedNaming(
        definition: With(scale: -1f),
        needle: "must be a finite positive value"
    );
    [Fact]
    public void ADeclaredZeroEnvelopeIsRefusedByName() => AssertRefusedNaming(
        definition: With(policy: WorldPlacementPolicyDefaults.Absent, scale: 1f),
        needle: "placements.policy.maxPlacementScale"
    );
    [Fact]
    public void AScaleOutsideADeclaredEnvelopeIsRefusedByName() => AssertRefusedNaming(
        definition: With(scale: (Fixtures.StandardAuthoring.MaxPlacementScale * 2f)),
        needle: $"is outside {Fixtures.StandardAuthoring.MinPlacementScale}..{Fixtures.StandardAuthoring.MaxPlacementScale}"
    );
    [Fact]
    public void APositiveScaleInsideTheEnvelopeValidates() => AssertValidates(definition: With(scale: 1f));
    [Fact]
    public void AnyAuthoredScaleValidatesUnderTheUnauthoredPolicy() => AssertValidates(definition: With(
        scale: (Fixtures.StandardAuthoring.MaxPlacementScale * 4f),
        unauthoredPolicy: true
    ));
    [Fact]
    public void TheUnauthoredPolicyDerivesTheEnvelopeTheRowsSpan() {
        var derived = With(
            scale: 3f,
            unauthoredPolicy: true
        ).Authoring;

        Assert.Equal(
            actual: derived,
            expected: (WorldPlacementPolicyDefaults.Absent with {
                MaxPlacementScale = 3f,
                MinPlacementScale = 3f,
            })
        );
    }
}
