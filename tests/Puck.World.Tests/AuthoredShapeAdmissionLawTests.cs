using System.Numerics;

using Puck.World.Authoring;
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
    [Fact]
    public void RoundedProfileIsAdmittedForFieldContact() => AssertWorldValidates(
        Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .2f, .1f)) with { Profile = new(SdfPrismProfileKind.RoundedRectangle, .3f) }, requiresField: true);

    [Theory]
    [InlineData(SdfPrismProfileKind.Polygon)]
    [InlineData(SdfPrismProfileKind.Ellipse)]
    public void RenderOnlyProfilesRefuseUnsupportedFieldContact(SdfPrismProfileKind kind) {
        var shape = Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .2f, .1f)) with { Profile = new(kind) };
        AssertWorldValidates(shape);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(World(shape, canonicalize: true, requiresField: true), out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void InvalidProfileControlsAreRefusedBeforeEmission() {
        foreach (var profile in new[] { new SdfPrismProfile((SdfPrismProfileKind)99), new(SdfPrismProfileKind.Polygon, Sides: 2),
            new(SdfPrismProfileKind.RoundedRectangle, float.NaN), new(SdfPrismProfileKind.RoundedRectangle, 1.1f) }) {
            Assert.Contains(CreationCanonicalizer.Validate(Document(Shape(SdfSolidPrimitive.Prism, Vector3.One) with { Profile = profile })),
                error => error.Path == "shapes[0].profile");
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.35f)]
    [InlineData(1f)]
    public void PrismProfileSurvivesCanonicalizationAndBothContactProviders(float taper) {
        var shape = Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .2f, .1f)) with { Taper = taper };
        var canonical = CreationCanonicalizer.Canonicalize(Document(shape), PrototypeId);
        Assert.Equal(taper, canonical.Document.Shapes![0].Taper);
        AssertWorldValidates(shape);
        AssertWorldValidates(shape, requiresField: true);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidPrismTaperNamesTheDocumentField(float taper) =>
        Assert.Contains(CreationCanonicalizer.Validate(Document(Shape(SdfSolidPrimitive.Prism, Vector3.One) with { Taper = taper })),
            error => error.Path == "shapes[0].taper");

    [Fact]
    public void TaperOnAnUnrelatedPrimitiveIsRefused() =>
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Box, Vector3.One) with { Taper = .5f }, "taper");

    [Theory]
    [InlineData(SdfSolidPrimitive.Box)]
    [InlineData(SdfSolidPrimitive.Cylinder)]
    [InlineData(SdfSolidPrimitive.Sphere)]
    public void LiftOnANonPrismIsRefusedByName(SdfSolidPrimitive type) =>
        AssertCanonicalizerRefusesNaming(Shape(type, Vector3.One) with { Lift = SdfLift.Revolve }, "lift");

    [Theory]
    [InlineData(SdfPrismProfileKind.Trapezoid)]
    [InlineData(SdfPrismProfileKind.RoundedRectangle)]
    [InlineData(SdfPrismProfileKind.Polygon)]
    [InlineData(SdfPrismProfileKind.Ellipse)]
    public void EveryProfileRevolvesAndEmitsAShape(SdfPrismProfileKind kind) {
        var shape = Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .25f, .1f)) with {
            Lift = SdfLift.Revolve,
            Profile = ((kind == SdfPrismProfileKind.Trapezoid) ? null : new SdfPrismProfile(kind)),
        };

        AssertCanonicalizerAccepts(shape);

        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism, new Vector3(.4f, .25f, .1f), 0,
            profile: ((kind == SdfPrismProfileKind.Trapezoid) ? null : new SdfPrismProfile(kind)), lift: SdfLift.Revolve);

        var program = builder.Build();
        var shapes = program.Instructions.Where(instruction => instruction.Op == SdfOp.ShapeBlend).ToArray();

        // Data1.y is the packed lift lane; revolve is 0 and extrude is 1 (SdfLift). Every profile must reach it.
        Assert.Single(shapes);
        Assert.Equal(0f, shapes[0].Data1.Y);
    }

    [Fact]
    public void ARevolvedPrismReachCoversItsOffsetRing() {
        // scale.z is a radial offset under a revolve, so the ring's far wall sits at offset + the profile's radial
        // half-extent. A bound taken from the extruded reading would clip it at the tile seams.
        foreach (var scale in new[] { new Vector3(.4f, .25f, 2f), new Vector3(1f, .5f, 0f), new Vector3(.2f, 3f, .75f) }) {
            Assert.True(SdfSolidGeometry.Reach(SdfSolidPrimitive.Prism, scale, SdfLift.Revolve) >= (scale.Z + scale.X),
                userMessage: $"revolved reach under-covered {scale}");
        }
    }

    [Fact]
    public void ARevolvedPrismIsRefusedOnASolidRow() =>
        AssertWorldRefusesNaming(Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .25f, .1f)) with { Lift = SdfLift.Revolve },
            canonicalize: true, needle: "revolve");

    [Fact]
    public void AnExtrudedPrismStillCarriesASolidRow() =>
        AssertWorldValidates(Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .25f, .1f)) with { Lift = SdfLift.Extrude });

    [Theory]
    [InlineData(SdfSolidPrimitive.Box)]
    [InlineData(SdfSolidPrimitive.Sphere)]
    [InlineData(SdfSolidPrimitive.Torus)]
    public void RoundingOnAnUnadmittedPrimitiveIsRefusedByName(SdfSolidPrimitive type) =>
        AssertCanonicalizerRefusesNaming(Shape(type, Vector3.One) with { Rounding = .05f }, "rounding");

    [Fact]
    public void RoundingOnAConeIsRefusedByName_TheApexLeavesNoRoomToRound() {
        // The builder's trapezoid ceiling is zero for a sharp apex, so the door refuses rather than admitting a
        // radius the emitted shape would silently drop.
        Assert.Equal(0f, SdfSolidGeometry.MaxRounding(SdfSolidPrimitive.Cone, new Vector3(.5f, .5f, .5f)));
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Cone, new Vector3(.5f, .5f, .5f)) with { Rounding = .1f }, "rounding");
        AssertCanonicalizerAccepts(Shape(SdfSolidPrimitive.Cone, new Vector3(.5f, .5f, .5f)));
    }

    [Theory]
    [InlineData(SdfSolidPrimitive.Prism)]
    [InlineData(SdfSolidPrimitive.Cylinder)]
    public void RoundingIsAdmittedWhereItFitsAndRefusedWhereItDoesNot(SdfSolidPrimitive type) {
        var scale = new Vector3(.5f, .5f, .5f);
        var ceiling = SdfSolidGeometry.MaxRounding(type, scale);

        Assert.True(ceiling > 0f);
        AssertCanonicalizerAccepts(Shape(type, scale) with { Rounding = (ceiling * .5f) });
        // The control and the cell differ in exactly one authored number: the radius, either side of the ceiling.
        AssertCanonicalizerRefusesNaming(Shape(type, scale) with { Rounding = (ceiling * 2f) }, "rounding");
    }

    [Fact]
    public void RoundingInsetsTheShapeInsteadOfGrowingIt() {
        var scale = new Vector3(.5f, .4f, .3f);
        var sharp = PrismShape(scale, 0f);
        var rounded = PrismShape(scale, .1f);

        // The lane convention: Data0 holds the inset profile and lift, Data1.w the radius the kernel offsets back
        // out. Their sum can never exceed what the sharp shape authored, or the cull bound would grow with it.
        Assert.Equal(0f, sharp.Data1.W);
        Assert.Equal(.1f, rounded.Data1.W, tolerance: 1e-6f);
        Assert.True((rounded.Data0.X + rounded.Data1.W) <= (sharp.Data0.X + 1e-6f));
        Assert.True((rounded.Data0.Z + rounded.Data1.W) <= (sharp.Data0.Z + 1e-6f));
        Assert.True((rounded.Data0.W + rounded.Data1.W) <= (sharp.Data0.W + 1e-6f));
        // Something actually moved: an inset that is a no-op would pass the bound test and round nothing.
        Assert.True(rounded.Data0.Z < sharp.Data0.Z);
    }

    private static SdfInstruction PrismShape(Vector3 scale, float rounding) {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        _ = SdfSolidGeometry.AppendScaledPrimitive(builder.ResetPoint(), SdfSolidPrimitive.Prism, scale, 0, rounding: rounding);

        return builder.Build().Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);
    }

    [Theory]
    [InlineData(SdfSolidPrimitive.Sphere)]
    [InlineData(SdfSolidPrimitive.Torus)]
    [InlineData(SdfSolidPrimitive.Capsule)]
    public void ChamferOnAnUnadmittedPrimitiveIsRefusedByName(SdfSolidPrimitive type) =>
        AssertCanonicalizerRefusesNaming(Shape(type, Vector3.One) with { Chamfer = .05f }, "chamfer");

    [Theory]
    [InlineData(SdfSolidPrimitive.Box)]
    [InlineData(SdfSolidPrimitive.Cylinder)]
    [InlineData(SdfSolidPrimitive.Prism)]
    public void ChamferIsAdmittedWhereItFitsAndRefusedWhereItDoesNot(SdfSolidPrimitive type) {
        var scale = new Vector3(.5f, .5f, .5f);
        var ceiling = SdfSolidGeometry.MaxChamfer(type, scale);

        Assert.True(ceiling > 0f);
        AssertCanonicalizerAccepts(Shape(type, scale) with { Chamfer = (ceiling * .5f) });
        // The control and the cell differ in exactly one authored number: the radius, either side of the ceiling.
        AssertCanonicalizerRefusesNaming(Shape(type, scale) with { Chamfer = (ceiling * 2f) }, "chamfer");
    }

    [Fact]
    public void ChamferAndRoundingCannotBothBeNonzeroOnOneShape() {
        var scale = new Vector3(.5f, .5f, .5f);

        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Cylinder, scale) with { Chamfer = .05f, Rounding = .05f }, "chamfer");
        // The control: either alone is fine.
        AssertCanonicalizerAccepts(Shape(SdfSolidPrimitive.Cylinder, scale) with { Chamfer = .05f });
        AssertCanonicalizerAccepts(Shape(SdfSolidPrimitive.Cylinder, scale) with { Rounding = .05f });
    }

    [Fact]
    public void ChamferOnARevolvedPrismIsRefusedByName_ARevolveHasNoCapSeam() {
        var scale = new Vector3(.5f, .5f, 0f);

        Assert.Equal(0f, SdfSolidGeometry.MaxChamfer(SdfSolidPrimitive.Prism, scale, lift: SdfLift.Revolve));
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Prism, scale) with { Lift = SdfLift.Revolve, Chamfer = .1f }, "chamfer");
    }

    [Fact]
    public void ChamferOnAPolygonProfilePrismIsRefusedByName_TheLaneIsAlreadyTaken() {
        var scale = new Vector3(.5f, .5f, .5f);
        var profile = new SdfPrismProfile(SdfPrismProfileKind.Polygon, Sides: 6);

        Assert.Equal(0f, SdfSolidGeometry.MaxChamfer(SdfSolidPrimitive.Prism, scale, profile: profile));
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Prism, scale) with { Profile = profile, Chamfer = .1f }, "chamfer");
    }

    [Fact]
    public void ChamferedRectangleIsAdmittedForFieldContact() => AssertWorldValidates(
        Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .2f, .1f)) with { Profile = new(SdfPrismProfileKind.ChamferedRectangle, .3f) }, requiresField: true);

    [Fact]
    public void BoxChamferEmitsAChamferedRectangleShapeUnderThePlainBoxFillet() {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        _ = SdfSolidGeometry.AppendScaledPrimitive(builder.ResetPoint(), SdfSolidPrimitive.Box, Vector3.One, 0, chamfer: .3f);

        var instruction = builder.Build().Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);

        Assert.Equal((uint)SdfShapeType.ChamferedRectangle, instruction.Shape);
        // The document Box's conventional edge fillet rides the family rounding lane on top of the chamfer, and the
        // packed chamfer is the authored one eroded by it: c' = c - r*(2 - sqrt2).
        Assert.True(instruction.Data1.W > 0f);
        Assert.Equal((.3f - (instruction.Data1.W * (2f - MathF.Sqrt(2f)))), instruction.Data0.Z, tolerance: 1e-6f);
    }

    [Fact]
    public void ATriangularPrismAdmitsACapChamferWhereItRefusesRounding() {
        // taper 0 is a triangle: the rounding lane's centred erosion has no room, but a cap chamfer never erodes the
        // profile, so it is bounded by the triangle's true inradius (area / semi-perimeter, scaled).
        var scale = new Vector3(.5f, .5f, .5f);
        var inradius = SdfSolidGeometry.MaxChamfer(SdfSolidPrimitive.Prism, scale, taper: 0f);

        Assert.Equal((.5f * (2f / (1f + MathF.Sqrt(5f)))), inradius, tolerance: 1e-5f);
        AssertCanonicalizerAccepts(Shape(SdfSolidPrimitive.Prism, scale) with { Taper = 0f, Chamfer = (inradius - .01f) });
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Prism, scale) with { Taper = 0f, Chamfer = (inradius + .01f) }, "chamfer");
        // The control: the same triangle refuses any rounding at all.
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Prism, scale) with { Taper = 0f, Rounding = .01f }, "rounding");
    }

    [Fact]
    public void AChamferedRectangleProfileThickerThanItsExtrudeIsRefusedByName() {
        var scale = new Vector3(.5f, .5f, .1f);

        // cornerRadius .5 of the .5 half-extent is a .25 chamfer the .1 half-depth cannot carry on the cap rims.
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Prism, scale) with { Profile = new(SdfPrismProfileKind.ChamferedRectangle, .5f) }, "chamfered-rectangle profile");
        // The controls: a chamfer under the half-depth, and the same profile revolved (no cap seam).
        AssertCanonicalizerAccepts(Shape(SdfSolidPrimitive.Prism, scale) with { Profile = new(SdfPrismProfileKind.ChamferedRectangle, .1f) });
        AssertCanonicalizerAccepts(Shape(SdfSolidPrimitive.Prism, scale) with { Profile = new(SdfPrismProfileKind.ChamferedRectangle, .5f), Lift = SdfLift.Revolve });
    }

    private static readonly Vector2[] ConvexVertices = [
        new(1f, -1f),
        new(-1f, -1f),
        new(-1f, 1f),
        new(1f, 1f),
    ];

    [Fact]
    public void ConvexProfileIsAdmittedForFieldContact() => AssertWorldValidates(
        Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .2f, .1f)) with { Profile = new(SdfPrismProfileKind.Convex, .1f, Vertices: ConvexVertices) }, requiresField: true);

    [Fact]
    public void AConcaveConvexProfileIsRefusedBeforeEmission() {
        Vector2[] dart = [new(1f, -1f), new(-1f, -1f), new(0f, 0f), new(-1f, 1f), new(1f, 1f)];

        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Prism, Vector3.One) with { Profile = new(SdfPrismProfileKind.Convex, Vertices: dart) }, "profile");
    }

    [Fact]
    public void SuperellipsoidIsAdmittedForFieldContact() => AssertWorldValidates(
        Shape(SdfSolidPrimitive.Superellipsoid, new Vector3(.4f, .3f, .2f)) with { Exponent = 4f }, requiresField: true);

    [Theory]
    [InlineData(1.9f)]
    [InlineData(8.1f)]
    [InlineData(float.NaN)]
    public void InvalidExponentNamesTheDocumentField(float exponent) =>
        Assert.Contains(CreationCanonicalizer.Validate(Document(Shape(SdfSolidPrimitive.Superellipsoid, Vector3.One) with { Exponent = exponent })),
            error => error.Path == "shapes[0].exponent");

    [Fact]
    public void ExponentOnAnUnrelatedPrimitiveIsRefused() =>
        AssertCanonicalizerRefusesNaming(Shape(SdfSolidPrimitive.Box, Vector3.One) with { Exponent = 4f }, "exponent");

    private const string PrototypeId = "probe";

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
            Name: PrototypeId,
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
    private static void AssertCanonicalizerRefusesNaming(ShapeDocument shape, string needle) {
        var violations = CreationCanonicalizer.Validate(document: Document(shape: shape));

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Message.Contains(comparisonType: StringComparison.Ordinal, value: needle)
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
            source: PrototypeId
        );

        return (source with {
            CollisionRaw = (source.Collision with {
                Requirements = (requiresField
                ? [WorldContactRequirement.SmoothUnionContact]
                : []),
            }),
            CreationsRaw = [
                new WorldPrototype(
                    Id: PrototypeId,
                    Document: (canonicalize
                    ? canonical.Document
                    : (canonical.Document with { Shapes = [shape] })),
                    HashRaw: canonical.Hash
                ),
            ],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: PrototypeId,
                    PrototypeId: PrototypeId,
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

    [Fact]
    public void ARevolvedPrismIsRefusedOnABodyCollider() {
        // The same gap a solid row closes: the body collider reads each copy's per-axis local box, which a revolve's
        // radial-offset reading of scale.z does not describe. Control: the extruded twin carries the collider.
        static WorldDefinition BodyCollider(SdfLift lift) {
            var world = World(shape: (Shape(SdfSolidPrimitive.Prism, new Vector3(.4f, .25f, .1f)) with { Lift = lift }), canonicalize: true);

            return (world with {
                KitRowsRaw = [world.Kits[0] with { Collider = new WorldCollider.FromCreation(PrototypeId: PrototypeId) }],
                PlacementRowsRaw = [world.Placements[0] with { Solid = null }],
            });
        }

        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: BodyCollider(SdfLift.Revolve), reason: out var reason));
        Assert.Contains("revolve", reason);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition: BodyCollider(SdfLift.Extrude), reason: out reason), userMessage: reason);
    }
}
