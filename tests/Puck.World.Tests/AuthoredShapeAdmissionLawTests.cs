using System.Numerics;

using Puck.Forge.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a creation document is refused, by name, at the door a world crosses — not later, at the first query, and
/// not by the emission it would otherwise reach. A shape's authored scale and its domain ops are attacker-supplied
/// once a world is federated, so a value that no emission path can honour is a validation error naming the shape,
/// never a server-side exception or a bound that no longer covers its geometry.
/// <para>Each arm pairs a denial with a control differing in exactly one authored number.</para>
/// </summary>
public sealed class AuthoredShapeAdmissionLawTests {
    private const string CreationId = "probe";

    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, IReadOnlyList<ShapeDomainOp>? domain = null) =>
        new(
            Id: 0,
            Name: null,
            Type: type,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0,
            Domain: domain
        );
    private static CreationDocument Document(ShapeDocument shape) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: CreationId,
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
    private static void AssertCanonicalizerRefusesNaming(ShapeDocument shape, string needle) {
        var violations = CreationCanonicalizer.Validate(document: Document(shape: shape));

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Message.Contains(value: needle, comparisonType: StringComparison.Ordinal)
        );
    }
    private static void AssertCanonicalizerAccepts(ShapeDocument shape) {
        var violations = CreationCanonicalizer.Validate(document: Document(shape: shape));

        Assert.Empty(collection: violations);
    }
    // A world carrying the shape on a solid placement — the row whose contact compile expands every domain fold.
    // requiresField selects the contact provider: the field provider compiles one program for every solid row and the
    // analytic one compiles a collider per expanded copy, and BOTH need the fold to have a rigid-copy spelling.
    private static WorldDefinition World(ShapeDocument shape, bool canonicalize, bool requiresField = false) {
        var source = Fixtures.BuildDocument();
        // A document the validator must refuse cannot be canonicalized, so it rides a well-formed creation's hash;
        // the validator reports the document's own violations before it ever reaches the hash pin.
        var canonical = CreationCanonicalizer.Canonicalize(
            document: Document(shape: (canonicalize
            ? shape
            : Shape(
                scale: Vector3.One,
                type: SdfSolidPrimitive.Sphere
            ))),
            source: CreationId
        );

        return (source with {
            CollisionRaw = (source.Collision with {
                Requirements = (requiresField
                ? [WorldContactRequirement.SmoothUnionContact]
                : []),
            }),
            CreationsRaw = [
                new WorldCreation(
                    Id: CreationId,
                    Document: (canonicalize
                    ? canonical.Document
                    : (canonical.Document with { Shapes = [shape] })),
                    HashRaw: canonical.Hash
                ),
            ],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: CreationId,
                    CreationId: CreationId,
                    Position: Vector3.Zero,
                    YawDegrees: 0f,
                    Scale: 1f,
                    Solid: new WorldSolid(Margin: 0f)
                ),
            ],
        });
    }
    private static void AssertWorldRefusesNaming(ShapeDocument shape, bool canonicalize, string needle, bool requiresField = false) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: World(
                canonicalize: canonicalize,
                requiresField: requiresField,
                shape: shape
            ),
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: needle
        );
    }
    private static void AssertWorldValidates(ShapeDocument shape, bool requiresField = false) {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: World(
                    canonicalize: true,
                    requiresField: requiresField,
                    shape: shape
                ),
                reason: out var reason
            ),
            userMessage: reason
        );
    }

    [Fact]
    public void ARepeatDomainPastTheCopyBudgetRefusesTheSolidRow() {
        AssertWorldRefusesNaming(
            canonicalize: true,
            needle: "repeat",
            shape: Shape(
                domain: [
                    new ShapeDomainOp.Repeat(
                        Limit: new Vector3(value: 120f),
                        Spacing: Vector3.One
                    ),
                ],
                scale: Vector3.One,
                type: SdfSolidPrimitive.Sphere
            )
        );
    }
    [Fact]
    public void ARepeatDomainWithinTheCopyBudgetCarriesTheSolidRow() {
        AssertWorldValidates(shape: Shape(
            domain: [
                new ShapeDomainOp.Repeat(
                    Limit: new Vector3(value: 1f),
                    Spacing: new Vector3(value: 4f)
                ),
            ],
            scale: Vector3.One,
            type: SdfSolidPrimitive.Sphere
        ));
    }
    [Fact]
    public void ARepeatDomainPastTheCopyBudgetRefusesTheSolidRowUnderTheFieldProvider() {
        // The field provider compiles solid rows through CreationStampEmitter, which throws on a fold with no
        // rigid-copy spelling, so the expansion refusal cannot be gated on the analytic provider's collider ceiling.
        AssertWorldRefusesNaming(
            canonicalize: true,
            needle: "repeat",
            requiresField: true,
            shape: Shape(
                domain: [
                    new ShapeDomainOp.Repeat(
                        Limit: new Vector3(value: 120f),
                        Spacing: Vector3.One
                    ),
                ],
                scale: Vector3.One,
                type: SdfSolidPrimitive.Sphere
            )
        );
    }
    [Fact]
    public void ARepeatDomainWithinTheCopyBudgetCarriesTheSolidRowUnderTheFieldProvider() {
        AssertWorldValidates(
            requiresField: true,
            shape: Shape(
                domain: [
                    new ShapeDomainOp.Repeat(
                        Limit: new Vector3(value: 1f),
                        Spacing: new Vector3(value: 4f)
                    ),
                ],
                scale: Vector3.One,
                type: SdfSolidPrimitive.Sphere
            )
        );
    }
    [Fact]
    public void APolarSectorCountPastTheExactFloatCeilingIsRefused() {
        AssertCanonicalizerRefusesNaming(
            needle: "sectors",
            shape: Shape(
                domain: [new ShapeDomainOp.Polar(Count: int.MaxValue)],
                scale: Vector3.One,
                type: SdfSolidPrimitive.Sphere
            )
        );
    }
    [Fact]
    public void APolarSectorCountWithinTheExactFloatCeilingIsAccepted() {
        AssertCanonicalizerAccepts(shape: Shape(
            domain: [new ShapeDomainOp.Polar(Count: 6)],
            scale: Vector3.One,
            type: SdfSolidPrimitive.Sphere
        ));
    }
    [Fact]
    public void ARepeatCellLimitPastTheUnboundedSentinelIsRefused() {
        AssertCanonicalizerRefusesNaming(
            needle: "limit exceeds",
            shape: Shape(
                domain: [
                    new ShapeDomainOp.Repeat(
                        Limit: new Vector3(value: 1e30f),
                        Spacing: Vector3.One
                    ),
                ],
                scale: Vector3.One,
                type: SdfSolidPrimitive.Sphere
            )
        );
    }
    [Fact]
    public void TheUnboundedRepeatCellLimitSentinelIsAccepted() {
        AssertCanonicalizerAccepts(shape: Shape(
            domain: [
                new ShapeDomainOp.Repeat(
                    Limit: new Vector3(value: ShapeDomainOp.Repeat.UnboundedLimit),
                    Spacing: Vector3.One
                ),
            ],
            scale: Vector3.One,
            type: SdfSolidPrimitive.Sphere
        ));
    }
    [Fact]
    public void AConeScaledIntoTheDegenerateProfileWindowIsRefused() {
        AssertCanonicalizerRefusesNaming(
            needle: "cone",
            shape: Shape(
                scale: new Vector3(
                    x: 0.0001f,
                    y: 0.0002f,
                    z: 0.0001f
                ),
                type: SdfSolidPrimitive.Cone
            )
        );
    }
    [Fact]
    public void AConeScaledPastTheDegenerateProfileWindowIsAccepted() {
        AssertCanonicalizerAccepts(shape: Shape(
            scale: new Vector3(
                x: 0.001f,
                y: 0.002f,
                z: 0.001f
            ),
            type: SdfSolidPrimitive.Cone
        ));
    }
    [Fact]
    public void ADegenerateConeRefusesTheWorldAtValidation() {
        // The whole point of the door: without it this document validates, boots, and throws at the first contact
        // query inside the authoritative server.
        AssertWorldRefusesNaming(
            canonicalize: false,
            needle: "cone",
            shape: Shape(
                scale: new Vector3(
                    x: 0.0001f,
                    y: 0.0002f,
                    z: 0.0001f
                ),
                type: SdfSolidPrimitive.Cone
            )
        );
    }
    [Fact]
    public void ANegativeShapeScaleIsRefusedInFavourOfASymmetryDomainOp() {
        AssertCanonicalizerRefusesNaming(
            needle: "symmetry domain op",
            shape: Shape(
                scale: new Vector3(
                    x: -2f,
                    y: 3f,
                    z: 4f
                ),
                type: SdfSolidPrimitive.Sphere
            )
        );
    }
    [Fact]
    public void ThePositiveScaleOfTheSameShapeIsAccepted() {
        AssertCanonicalizerAccepts(shape: Shape(
            scale: new Vector3(
                x: 2f,
                y: 3f,
                z: 4f
            ),
            type: SdfSolidPrimitive.Sphere
        ));
    }
    [Fact]
    public void AMirrorAuthoredAsASymmetryDomainOpIsAccepted() {
        AssertCanonicalizerAccepts(shape: Shape(
            domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
            scale: new Vector3(
                x: 2f,
                y: 3f,
                z: 4f
            ),
            type: SdfSolidPrimitive.Sphere
        ));
    }
    [Fact]
    public void ANegativeShapeScaleRefusesTheWorldAtValidation() {
        AssertWorldRefusesNaming(
            canonicalize: false,
            needle: "symmetry domain op",
            shape: Shape(
                scale: new Vector3(
                    x: -2f,
                    y: 3f,
                    z: 4f
                ),
                type: SdfSolidPrimitive.Sphere
            )
        );
    }
}
