using System.Numerics;
using Puck.Assets.Documents;
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

    private static WorldDefinition With(float scale, WorldPlacementPolicyDefaults? policy = null, bool unauthoredPolicy = false) {
        var document = Fixtures.BuildDocument();

        document = (document with {
            CreationsRaw = [CreationFixtures.UnitSphere(id: PrototypeId)],
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
            : ((policy is { } declared)
                ? (document with { AuthoringRaw = declared })
                : document
        ));
    }

    [Fact]
    public void ADeclaredZeroEnvelopeIsRefusedByName() => Laws.Refuses(
        locally: true,
        definition: With(
            policy: WorldPlacementPolicyDefaults.Absent,
            scale: 1f
        ),
        needle: "placements.policy.maxPlacementScale"
    );
    [Fact]
    public void APositiveScaleInsideTheEnvelopeValidates() => Laws.Validates(locally: true, definition: With(scale: 1f));
    [Fact]
    public void AScaleOutsideADeclaredEnvelopeIsRefusedByName() => Laws.Refuses(
            definition: With(scale: (Fixtures.StandardAuthoring.MaxPlacementScale * 2f)),
            locally: true,
            needle: $"is outside {Fixtures.StandardAuthoring.MinPlacementScale}..{Fixtures.StandardAuthoring.MaxPlacementScale}"
        );
    [Fact]
    public void AnyAuthoredScaleValidatesUnderTheUnauthoredPolicy() => Laws.Validates(locally: true, definition: With(
        scale: (Fixtures.StandardAuthoring.MaxPlacementScale * 4f),
        unauthoredPolicy: true
    ));
    [Fact]
    public void NegativeScaleIsRefused() => Laws.Refuses(
            definition: With(scale: -1f),
            locally: true,
            needle: "must be a finite positive value"
        );
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
    [Fact]
    public void ZeroScaleIsRefusedUnderAPermissivePolicy() => Laws.Refuses(
            definition: With(scale: 0f),
            locally: true,
            needle: "must be a finite positive value"
        );
    [Fact]
    public void ZeroScaleIsRefusedUnderTheUnauthoredPolicy() => Laws.Refuses(
        locally: true,
        definition: With(
            scale: 0f,
            unauthoredPolicy: true
        ),
        needle: "must be a finite positive value"
    );
}
