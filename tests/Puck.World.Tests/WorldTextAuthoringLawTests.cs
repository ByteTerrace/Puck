using System.Numerics;
using Puck.Forge.Authoring;
using Puck.SignedDistance;
using Puck.Text;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldTextAuthoringLawTests {
    private static FontAtlas AtlasForText(string text) => new(
        kind: FontAtlasKind.Sdf,
        imagePath: "memory://world-text",
        size: 1f,
        distanceRange: 1f,
        width: 1,
        height: 1,
        metrics: new FontAtlasMetrics(Ascender: 1f, Descender: 0f, LineHeight: 1f, UnderlineThickness: 0f, UnderlineY: 0f),
        glyphs: text.Distinct().Select(selector: static value => new FontAtlasGlyph(
            unicode: value,
            advance: 1f,
            planeBounds: new FontAtlasBounds(Bottom: 0f, Left: 0f, Right: 1f, Top: 1f),
            atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f)
        )),
        kerningPairs: [],
        imageData: new FontAtlasImageData(rgbaPixels: [128, 128, 128, 128], height: 1, width: 1)
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
    private static WorldCreation TextCreation(string? font = null) {
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
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "sign");

        return new WorldCreation(Id: "sign", Document: canonical.Document, HashRaw: canonical.Hash);
    }

    [Fact]
    public void CatalogAcceptsPortablePinnedFontAndRoundTrips() {
        var catalog = Catalog();
        var definition = Fixtures.BuildDocument() with {
            Text = catalog with { Fonts = [catalog.Fonts[0] with { FaceIndex = 1 }] },
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);

        var roundTrip = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        Assert.Equal("body", roundTrip.Text!.DefaultFont);
        var font = Assert.Single(collection: roundTrip.Text.Fonts);

        Assert.Equal("fonts/world.ttf", font.Source);
        Assert.Equal(1, font.FaceIndex);
    }
    [Fact]
    public void CatalogRejectsNegativeCollectionFaceIndex() {
        var catalog = Catalog();
        var definition = Fixtures.BuildDocument() with {
            Text = catalog with { Fonts = [catalog.Fonts[0] with { FaceIndex = -1 }] },
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "faceIndex must not be negative");
    }
    [Fact]
    public void CatalogRejectsPathEscape() {
        var definition = Fixtures.BuildDocument() with { Text = Catalog(source: "../outside.ttf") };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "contained beneath");
    }
    [Fact]
    public void CatalogReportsNonFiniteDistanceWithoutThrowing() {
        var catalog = Catalog();
        var invalidCatalog = catalog with {
            Fonts = [catalog.Fonts[0] with { DistanceRange = float.NaN }],
        };
        var definition = Fixtures.BuildDocument() with { Text = invalidCatalog };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "distanceRange must be finite");
    }
    [Fact]
    public void CreationTextEmitsOneGlyphShapePerRenderableScalar() {
        var creation = TextCreation().Document;
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.EmitText(
            builder: builder,
            document: creation,
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null),
            fontFor: _ => AtlasForText(text: "Helo"),
            materialFor: _ => material
        );

        var program = builder.Build(buildInstanceGrid: false);

        Assert.Equal(
            expected: creation.TextRuns![0].Text.Length,
            actual: program.Instructions.Count(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (((SdfShapeType)instruction.Shape) == SdfShapeType.Glyph)))
        );
    }

    private static WorldScreen TextScreen(WorldScreenSource source) => new(
        Index: 1,
        Origin: new Vector3(x: 0f, y: 1f, z: 0f),
        Right: new Vector3(x: 1f, y: 0f, z: 0f),
        Up: new Vector3(x: 0f, y: 1f, z: 0f),
        HalfWidth: 1f,
        HalfHeight: 1f,
        HalfDepth: 0.1f,
        Round: 0f,
        Source: source,
        Route: WorldScreenRoute.Passive
    );

    [Fact]
    public void TextScreenSourceValidates() {
        var screen = TextScreen(source: new WorldScreenSource.Text(Lines: ["HELLO", "WORLD"], Foreground: "#FFCC00"));
        var definition = Fixtures.BuildDocument() with { Text = Catalog(), ScreensRaw = [screen] };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);
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
                Faces: [new CreationFaceDocument(DefaultSource: null, Name: "label", ShapeId: 1)]
            ),
        };
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: original.Id);
        var creation = original with { Document = canonical.Document, HashRaw = canonical.Hash };
        var placement = new WorldPlacement(
            Id: "sign-placement",
            CreationId: creation.Id,
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: 1f,
            FaceSources: [new WorldPlacementFace(Face: "label", Source: new WorldScreenSource.Text(Lines: ["OPEN"]))]
        );
        var definition = Fixtures.BuildDocument() with {
            Text = Catalog(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [placement],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);
    }
    [Fact]
    public void TextScreenRefusesWithoutCatalogUnknownFontGridAndColor() {
        var noCatalog = Fixtures.BuildDocument() with { ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(Lines: ["HI"]))] };
        var unknownFont = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(Lines: ["HI"], Font: "display"))],
        };
        var overBudget = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(Lines: ["HI"], Columns: 80, Rows: 24))],
        };
        var overflowingLine = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(Lines: ["WIDE"], Columns: 3))],
        };
        var badColor = Fixtures.BuildDocument() with {
            Text = Catalog(),
            ScreensRaw = [TextScreen(source: new WorldScreenSource.Text(Lines: ["HI"], Background: "black"))],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: noCatalog, neighbours: null, reason: out var noCatalogReason));
        Assert.Contains(actualString: noCatalogReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "requires the world to declare a text font catalog");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: unknownFont, neighbours: null, reason: out var fontReason));
        Assert.Contains(actualString: fontReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "names no text.fonts row");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: overBudget, neighbours: null, reason: out var budgetReason));
        Assert.Contains(actualString: budgetReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "per-screen decal budget");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: overflowingLine, neighbours: null, reason: out var lineReason));
        Assert.Contains(actualString: lineReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "wider than");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: badColor, neighbours: null, reason: out var colorReason));
        Assert.Contains(actualString: colorReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "must be #RRGGBB");
    }
    [Fact]
    public void AnimatedTextPlacementValidates() {
        var creation = TextCreationWithFrames();
        var definition = Fixtures.BuildDocument() with {
            Text = Catalog(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "marquee", CreationId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f)],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);
    }

    // A text-bearing creation that ALSO carries a timeline frame, so a placement of it roots through the replay
    // pool rather than the static stamper.
    private static WorldCreation TextCreationWithFrames() {
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
            Frames: [new FrameDocument(Name: "pose", Transforms: [new FrameTransformDocument(Id: 1, Position: Vector3.UnitY, Rotation: Quaternion.Identity, Scale: Vector3.One)])],
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
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "marquee");

        return new WorldCreation(Id: "marquee", Document: canonical.Document, HashRaw: canonical.Hash);
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
        Assert.Equal(expected: 3, actual: run.GlyphCount);
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

        var reach = CreationStampEmitter.RenderReach(document: document, scale: 1f, fontFor: _ => AtlasForText(text: "A"));

        // Baselines land at x=0,3,6. The last 1x1 atlas cell spans x=6..7 and y=0..1, so its far corner is sqrt(50).
        Assert.InRange(actual: reach, high: 7.072f, low: 7.071f);
    }
    [Fact]
    public void CreationTextRequiresCatalogAndDeclaredFont() {
        var creation = TextCreation(font: "display");
        var withoutCatalog = Fixtures.BuildDocument() with { CreationsRaw = [creation] };
        var unknownFont = withoutCatalog with { Text = Catalog() };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: withoutCatalog, neighbours: null, reason: out var missingReason));
        Assert.Contains(actualString: missingReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "requires the world to declare a text font catalog");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: unknownFont, neighbours: null, reason: out var unknownReason));
        Assert.Contains(actualString: unknownReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "names no text.fonts row");
    }
}
