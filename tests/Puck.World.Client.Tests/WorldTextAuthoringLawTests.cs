using System.Numerics;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Puck.Text;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class WorldTextAuthoringLawTests {
    [Fact]
    public void AnEngravedMovingCreationKeepsEveryOperandInsideOneIndirectInstance() {
        var creation = TextCreationWithFrames();
        var canonical = CreationCanonicalizer.Canonicalize(document: creation.Document with {
            Shapes = [
                creation.Document.Shapes![0] with { Dilate = .125f },
                creation.Document.Shapes[0] with { Id = 2, Group = 1, Position = new Vector3(x: 2f, y: 0f, z: 0f) },
                creation.Document.Shapes[0] with { Id = 3, Group = 1, Blend = SdfBlendOp.Subtraction, Scale = new Vector3(value: .25f) },
            ],
            TextRuns = [creation.Document.TextRuns![0] with { Mode = TextRunDocument.ModeEngrave }],
        }, source: "engraved-body");

        creation = creation with { Document = canonical.Document, HashRaw = canonical.Hash };
        var pool = new WorldCreationInstances();

        pool.Reconcile(placements: [], creations: [creation], dynamics: [], bodyStamps: [
            new WorldCreationInstances.BodyStamp(0, creation, 1f, WorldLook.Implicit),
        ]);
        var fonts = FontAtlasCatalogPacker.Pack("body", new Dictionary<string, FontAtlas> { ["body"] = AtlasForText(text: "Hello") });
        var builder = new SdfProgramBuilder();

        pool.Emit(builder, WorldBakedColors.Of(definition: Fixtures.BuildDocument()), probeWorstCase: false, maxPlacementScale: 1f,
            slotBase: 0, textCatalog: fonts);
        var program = builder.Build();
        var instance = Assert.Single(collection: program.Instances);

        Assert.True(condition: instance.IsDynamic);
        Assert.True(condition: program.InstancesComposable);
        Assert.Equal(3, program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PushField)));
        Assert.Contains(collection: program.Instructions, filter: instruction => ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Blend == ((uint)SdfBlendOp.Subtraction))));
    }
    // Embossed text carves nothing, so a moving creation's shapes keep their own instances and its text is an instance of
    // its own on the root slot declaring its glyphs' detail, which the bake refines that slot's group toward, as the static
    // stamper's text group is.
    [Fact]
    public void AnEmbossedMovingCreationBakesItsTextAsADetailedInstanceOfItsOwn() {
        var creation = TextCreationWithFrames();
        var pool = new WorldCreationInstances();

        pool.Reconcile(placements: [], creations: [creation], dynamics: [], bodyStamps: [
            new WorldCreationInstances.BodyStamp(0, creation, 2f, WorldLook.Implicit),
        ]);
        var fonts = FontAtlasCatalogPacker.Pack("body", new Dictionary<string, FontAtlas> { ["body"] = AtlasForText(text: "Hello") });
        var builder = new SdfProgramBuilder();

        pool.Emit(builder, WorldBakedColors.Of(definition: Fixtures.BuildDocument()), probeWorstCase: false, maxPlacementScale: 2f,
            slotBase: 0, textCatalog: fonts);
        var program = builder.Build();

        Assert.Equal(2, program.Instances.Count);
        var (box, text) = (program.Instances[0], program.Instances[1]);

        Assert.True(condition: (box.IsDynamic && text.IsDynamic));
        // The box rides its shape slot after the root, the text the slot after the shapes', which the pack poses as the
        // root: a group of its own, whatever blend group rides the root.
        Assert.Equal(expected: (box.Slot + 1), actual: text.Slot);
        var transforms = new DynamicTransform[pool.DynamicSlotCount];

        CreationDomainParentLawTests.Advance(client: ClientFixtures.Client(definition: CreationDomainParentLawTests.Definition(creation: creation, scale: 2f)), pool: pool, tick: 1UL, transforms: transforms);
        Assert.Equal(expected: transforms[(box.Slot - 1)], actual: transforms[text.Slot]);
        Assert.NotEqual(expected: transforms[(text.Slot + 1)].Position, actual: transforms[text.Slot].Position);
        Assert.Equal(expected: 0f, actual: box.Detail);
        // The thinner of the run's 0.02 relief and a tenth of its 0.25 em, at the body's scale of 2.
        Assert.Equal(expected: 0.04f, actual: text.Detail, tolerance: 1e-6f);
        Assert.Equal(expected: 0, actual: program.Instructions.Count(predicate: static instruction => (instruction.Op == SdfOp.PushField)));
        // The text's bound is its glyphs' reach about the root, whatever the creation's other shapes reach.
        var textReach = CreationStampEmitter.TextReach(document: creation.EngineDocument, fontFor: fonts.Resolve, scale: 2f);

        Assert.True(condition: (textReach > 0f));
        Assert.InRange(actual: text.Radius, high: (textReach + 0.5f), low: textReach);
    }
    // A run anchored to a shape is emitted at the shape's frame, so the creation's bound measures it there: a run at
    // its shape's origin on a shape 6 m out reaches past 6 m, where its own unit-local position alone reads near zero.
    [Fact]
    public void ACreationsBoundMeasuresAShapeAnchoredRunWhereItIsEmitted() {
        var document = TextCreationWithFrames().EngineDocument;
        var far = document with {
            Shapes = [document.Shapes![0] with { Position = new Vector3(x: 6f, y: 0f, z: 0f), Scale = new Vector3(value: 0.1f) }],
            TextRuns = [document.TextRuns![0] with { ShapeId = document.Shapes[0].Id }],
        };
        var fonts = FontAtlasCatalogPacker.Pack("body", new Dictionary<string, FontAtlas> { ["body"] = AtlasForText(text: "Hello") });
        var textReach = CreationStampEmitter.TextReach(document: far, fontFor: fonts.Resolve, scale: 1f);

        Assert.True(condition: (textReach > 6f));
        Assert.True(condition: (CreationStampEmitter.RenderReach(document: far, fontFor: fonts.Resolve, scale: 1f) >= textReach));
    }

    private static FontAtlas AtlasForText(string text) => new(
        kind: FontAtlasKind.Sdf,
        imagePath: "memory://world-text",
        size: 1f,
        distanceRange: 1f,
        width: 1,
        height: 1,
        metrics: new FontAtlasMetrics(
            Ascender: 1f,
            Descender: 0f,
            LineHeight: 1f,
            UnderlineThickness: 0f,
            UnderlineY: 0f
        ),
        glyphs: text.Distinct().Select(selector: static value => new FontAtlasGlyph(
            unicode: value,
            advance: 1f,
            planeBounds: new FontAtlasBounds(
                Bottom: 0f,
                Left: 0f,
                Right: 1f,
                Top: 1f
            ),
            atlasBounds: new FontAtlasBounds(
                Bottom: 1f,
                Left: 0f,
                Right: 1f,
                Top: 0f
            )
        )),
        kerningPairs: [],
        imageData: new FontAtlasImageData(
            height: 1,
            rgbaPixels: [128, 128, 128, 128],
            width: 1
        )
    );
    private static TextFontCatalogDefinition Catalog(string source = "fonts/world.ttf") => new(
        DefaultFont: "body",
        Fonts: [new TextFontDefinition(
                Name: "body",
                Source: source,
                Hash: "sha256-64/0123456789abcdef",
                CodePointRanges: ["U+0020-U+007E"]
            )]
    );
    private static WorldPrototype TextCreation(string? font = null) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "sign",
            Palette: null,
            Shapes: null,
            Frames: null,
            TextRuns: [new TextRunDocument(
                    Text: "Hello",
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    EmHeight: 0.25f,
                    Depth: 0.02f,
                    Mode: TextRunDocument.ModeEmboss,
                    Material: 0,
                    Font: font
                )]
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "sign"
        );

        return new WorldPrototype(
            Id: "sign",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    // A text-bearing creation that ALSO carries a timeline frame, so a placement of it roots through the replay
    // pool rather than the static stamper.
    private static WorldPrototype TextCreationWithFrames() {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "marquee",
            Palette: null,
            Shapes: [new ShapeDocument(
                    Id: 1,
                    Name: null,
                    Type: SdfSolidPrimitive.Box,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: Vector3.One,
                    Material: 0,
                    Blend: null,
                    Smooth: null,
                    Group: null
                )],
            Frames: [new FrameDocument(
                    Name: "pose",
                    Transforms: [new FrameTransformDocument(
                            Id: 1,
                            Position: Vector3.UnitY,
                            Rotation: Quaternion.Identity
                        )]
                )],
            TextRuns: [new TextRunDocument(
                    Text: "Hello",
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    EmHeight: 0.25f,
                    Depth: 0.02f,
                    Mode: TextRunDocument.ModeEmboss,
                    Material: 0
                )]
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "marquee"
        );

        return new WorldPrototype(
            Id: "marquee",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static WorldScreen TextScreen(WorldScreenSource source) => new(
        Index: 1,
        Origin: new Vector3(
            x: 0f,
            y: 1f,
            z: 0f
        ),
        Right: new Vector3(
            x: 1f,
            y: 0f,
            z: 0f
        ),
        Up: new Vector3(
            x: 0f,
            y: 1f,
            z: 0f
        ),
        HalfWidth: 1f,
        HalfHeight: 1f,
        HalfDepth: 0.1f,
        Round: 0f,
        Source: source,
        Route: WorldScreenRoute.Passive
    );

    [Fact]
    public void AnimatedTextPlacementValidates() {
        var creation = TextCreationWithFrames();
        var definition = Fixtures.BuildDocument() with {
            Text = Catalog(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(
                Id: "marquee",
                PrototypeId: creation.Id,
                Position: Vector3.Zero,
                YawDegrees: 0f,
                Scale: 1f
            )],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: definition,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void CatalogAcceptsPortablePinnedFontAndRoundTrips() {
        var catalog = Catalog();
        var definition = Fixtures.BuildDocument() with {
            Text = catalog with { Fonts = [catalog.Fonts[0] with { FaceIndex = 1 }] },
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: definition,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );

        var roundTrip = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        Assert.Equal(
            "body",
            roundTrip.Text!.DefaultFont
        );
        var font = Assert.Single(collection: roundTrip.Text.Fonts);

        Assert.Equal(
            "fonts/world.ttf",
            font.Source
        );
        Assert.Equal(
            1,
            font.FaceIndex
        );
    }
    [Fact]
    public void CatalogRejectsNegativeCollectionFaceIndex() {
        var catalog = Catalog();
        var definition = Fixtures.BuildDocument() with {
            Text = catalog with { Fonts = [catalog.Fonts[0] with { FaceIndex = -1 }] },
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: definition,
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "faceIndex must not be negative"
        );
    }
    [Fact]
    public void CatalogAdmitsASiblingDirectoryAndRejectsANonPortablePath() {
        Assert.True(condition: WorldDefinitionValidator.TryValidate(
            definition: Fixtures.BuildDocument() with { Text = Catalog(source: "../fonts/body.ttf") },
            neighbours: null,
            reason: out _
        ));
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: Fixtures.BuildDocument() with { Text = Catalog(source: "..\\fonts\\body.ttf") },
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "must be a portable relative path"
        );
    }
    [Fact]
    public void CatalogReportsNonFiniteDistanceWithoutThrowing() {
        var catalog = Catalog();
        var invalidCatalog = catalog with {
            Fonts = [catalog.Fonts[0] with { DistanceRange = float.NaN }],
        };
        var definition = Fixtures.BuildDocument() with { Text = invalidCatalog };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: definition,
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "distanceRange must be finite"
        );
    }
    [Fact]
    public void CreationTextEmitsOneGlyphShapePerRenderableScalar() {
        var creation = TextCreation().Document;
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.EmitText(
            builder: builder,
            document: creation,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: 1f,
                ReflectionNormal: null
            ),
            fontFor: _ => AtlasForText(text: "Helo"),
            materialFor: _ => material
        );

        var program = builder.Build();

        Assert.Equal(
            expected: creation.TextRuns![0].Text.Length,
            actual: program.Instructions.Count(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (((SdfShapeType)instruction.Shape) == SdfShapeType.Glyph)))
        );
    }
    [Fact]
    public void CreationTextRequiresCatalogAndDeclaredFont() {
        var creation = TextCreation(font: "display");
        var withoutCatalog = Fixtures.BuildDocument() with { CreationsRaw = [creation] };
        var unknownFont = withoutCatalog with { Text = Catalog() };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: withoutCatalog,
            neighbours: null,
            reason: out var missingReason
        ));
        Assert.Contains(
            actualString: missingReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "requires the world to declare a text font catalog"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: unknownFont,
            neighbours: null,
            reason: out var unknownReason
        ));
        Assert.Contains(
            actualString: unknownReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no text.fonts row"
        );
    }
    [Fact]
    public void GlyphCountChargesSupplementaryScalarsOnce() {
        var run = new TextRunDocument(
            Text: "A\U0001F600 B",
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            EmHeight: 0.25f,
            Depth: null,
            Mode: null,
            Material: null
        );

        // 'A', U+1F600 (a surrogate pair), 'B' — a per-char count would report 4.
        Assert.Equal(
            expected: 3,
            actual: run.GlyphCount
        );
    }
    [Fact]
    public void RenderReachMeasuresTrackedGlyphCellsFromTheResolvedAtlas() {
        var document = TextCreation().Document with {
            Shapes = null,
            TextRuns = [new TextRunDocument(
                Text: "AAA",
                Position: Vector3.Zero,
                Rotation: Quaternion.Identity,
                EmHeight: 1f,
                Depth: 0.02f,
                Mode: TextRunDocument.ModeEmboss,
                Material: 0,
                Tracking: 2f
            )],
        };

        var reach = CreationStampEmitter.RenderReach(
            document: document,
            scale: 1f,
            fontFor: _ => AtlasForText(text: "A")
        );

        // Baselines land at x=0,3,6. The last 1x1 atlas cell spans x=6..7 and y=0..1, so its far corner is sqrt(50).
        Assert.InRange(
            actual: reach,
            high: 7.072f,
            low: 7.071f
        );
    }
    [Fact]
    public void TextCreationFaceSourceValidates() {
        var original = TextCreation();
        var document = original.Document with {
            Shapes = [new ShapeDocument(
                Id: 1,
                Name: null,
                Type: SdfSolidPrimitive.Box,
                Position: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: Vector3.One,
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            )],
            Behavior = new CreationBehaviorDocument(
            Locomotion: null,
            Faces: [new CreationFaceDocument(
                    DefaultSource: null,
                    Name: "label",
                    ShapeId: 1
                )]
        ),
        };
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: original.Id
        );
        var creation = original with { Document = canonical.Document, HashRaw = canonical.Hash };
        var placement = new WorldPlacement(
            Id: "sign-placement",
            PrototypeId: creation.Id,
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: 1f,
            FaceSources: [new WorldPlacementFace(
                    Face: "label",
                    Source: new WorldScreenSource.Text(Lines: ["OPEN"])
                )]
        );
        var definition = Fixtures.BuildDocument() with {
            Text = Catalog(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [placement],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: definition,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void TextScreenRefusesWithoutCatalogUnknownFontGridAndColor() {
        var noCatalog = Fixtures.BuildDocument() with { ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(Lines: ["HI"]))] };
        var unknownFont = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(
                Lines: ["HI"],
                Font: "display"
            ))],
        };
        var overBudget = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(
                Lines: ["HI"],
                Columns: 80,
                Rows: 24
            ))],
        };
        var overflowingLine = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(
                Lines: ["WIDE"],
                Columns: 3
            ))],
        };
        var badColor = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(
                Lines: ["HI"],
                Background: "black"
            ))],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: noCatalog,
            neighbours: null,
            reason: out var noCatalogReason
        ));
        Assert.Contains(
            actualString: noCatalogReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "requires the world to declare a text font catalog"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: unknownFont,
            neighbours: null,
            reason: out var fontReason
        ));
        Assert.Contains(
            actualString: fontReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no text.fonts row"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: overBudget,
            neighbours: null,
            reason: out var budgetReason
        ));
        Assert.Contains(
            actualString: budgetReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "per-screen decal budget"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: overflowingLine,
            neighbours: null,
            reason: out var lineReason
        ));
        Assert.Contains(
            actualString: lineReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "wider than"
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: badColor,
            neighbours: null,
            reason: out var colorReason
        ));
        Assert.Contains(
            actualString: colorReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "must be #RRGGBB"
        );
    }
    [Fact]
    public void TextScreenSourceValidates() {
        var screen = TextScreen(source: new WorldScreenSource.Text(
            Lines: ["HELLO", "WORLD"],
            Foreground: "#FFCC00"
        ));
        var definition = Fixtures.BuildDocument() with { Text = Catalog(), ScreensRaw = [screen] };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: definition,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
}
