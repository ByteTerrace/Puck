using System.Numerics;

using Puck.Forge.Authoring;
using Puck.SignedDistance;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="SculptModel"/>'s document-is-the-model contract: <see cref="SculptModel.Load"/>
/// round-trips a canonicalized document byte-identically when nothing edits it, a generic
/// <see cref="SculptModel.TrySet"/> touches only the addressed field, and its result matches a direct
/// <c>document with {...}</c> edit — proving the generic path walker and hand-authored record edits agree.</summary>
public sealed class SculptModelDocumentLawTests {
    private static CreationDocument BuildDocument() {
        var first = new ShapeDocument(
            Id: 1,
            Name: "core",
            Type: AvatarPrimitive.Box,
            Position: new Vector3(x: 0f, y: 0.7f, z: 0f),
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var second = new ShapeDocument(
            Id: 2,
            Name: "arm",
            Type: AvatarPrimitive.Sphere,
            Position: new Vector3(x: 1f, y: 0.7f, z: 0f),
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.5f),
            Material: 1,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);

        return new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "lawbot",
            Palette: [
                new PaletteEntryDocument(Color: "#FF0000", Emissive: null, Specular: null, Shininess: null),
                new PaletteEntryDocument(Color: "#00FF00", Emissive: null, Specular: null, Shininess: null),
            ],
            Shapes: [first, second],
            Frames: null);
    }
    private static SculptModel LoadedModel(CreationDocument canonical) {
        var model = new SculptModel(shapeCapacity: 64);

        _ = model.Load(document: canonical);

        return model;
    }

    /// <summary>Loading an already-canonicalized document with no edits produces byte-identical canonical bytes —
    /// the model neither drops nor invents document content on the way in.</summary>
    [Fact]
    public void LoadWithNoEditsRoundTripsByteIdentically() {
        var canonical = CreationCanonicalizer.Canonicalize(document: BuildDocument(), source: "roundtrip");
        var model = LoadedModel(canonical: canonical.Document);

        var reCanonical = CreationCanonicalizer.Canonicalize(document: model.Document, source: "roundtrip");

        Assert.Equal(expected: canonical.Hash, actual: reCanonical.Hash);
    }
    /// <summary>Setting one shape's scale by path leaves the other shape, the name, and the palette untouched.</summary>
    [Fact]
    public void SetOnOneFieldLeavesEveryOtherSectionUnchanged() {
        var canonical = CreationCanonicalizer.Canonicalize(document: BuildDocument(), source: "sections");
        var model = LoadedModel(canonical: canonical.Document);

        var outcome = model.TrySet(path: "shapes[0].scale", json: "[2,2,2]");

        Assert.True(condition: outcome.Success, userMessage: outcome.Message);
        Assert.Equal(expected: new Vector3(x: 2f, y: 2f, z: 2f), actual: model.Document.Shapes![0].Scale.Value);
        Assert.Equal(expected: canonical.Document.Shapes![0].Position, actual: model.Document.Shapes![0].Position);
        Assert.Equal(expected: canonical.Document.Shapes![1].Position, actual: model.Document.Shapes![1].Position);
        Assert.Equal(expected: canonical.Document.Shapes![1].Scale, actual: model.Document.Shapes![1].Scale);
        Assert.Equal(expected: canonical.Document.Name, actual: model.Document.Name);
        Assert.Equal(expected: canonical.Document.Palette![0].Color, actual: model.Document.Palette![0].Color);
        Assert.Equal(expected: canonical.Document.Palette![1].Color, actual: model.Document.Palette![1].Color);
    }
    /// <summary>A generic set-path edit produces the SAME canonical document a hand-authored <c>with</c> edit
    /// would — the path walker is not a second way to reach a different result.</summary>
    [Fact]
    public void SetPathMatchesADirectWithEdit() {
        var canonical = CreationCanonicalizer.Canonicalize(document: BuildDocument(), source: "seteq");
        var model = LoadedModel(canonical: canonical.Document);

        var outcome = model.TrySet(path: "shapes[0].scale", json: "[2,2,2]");

        Assert.True(condition: outcome.Success, userMessage: outcome.Message);

        var expectedShapes = new List<ShapeDocument>(collection: canonical.Document.Shapes!) {
            [0] = (canonical.Document.Shapes![0] with { Scale = new Vector3(x: 2f, y: 2f, z: 2f) }),
        };
        var expected = CreationCanonicalizer.Canonicalize(
            document: (canonical.Document with { Shapes = expectedShapes }),
            source: "seteq-expected"
        );
        var actual = CreationCanonicalizer.Canonicalize(document: model.Document, source: "seteq-actual");

        Assert.Equal(expected: expected.Hash, actual: actual.Hash);
    }
    /// <summary>An invalid payload refuses with the canonicalizer's own message and leaves the document byte-for-byte
    /// unchanged — the same validation the load/commit path runs, run synchronously per edit.</summary>
    [Fact]
    public void SetPathWithInvalidPayloadRefusesAndLeavesTheDocumentUnchanged() {
        var canonical = CreationCanonicalizer.Canonicalize(document: BuildDocument(), source: "refuse");
        var model = LoadedModel(canonical: canonical.Document);
        var before = CreationCanonicalizer.Canonicalize(document: model.Document, source: "before").Hash;

        var outcome = model.TrySet(path: "palette[0].color", json: "\"pink\"");

        Assert.False(condition: outcome.Success);
        Assert.Contains(expectedSubstring: "color must be #RRGGBB or a state.<row>[.<key>] binding.", actualString: outcome.Message);

        var after = CreationCanonicalizer.Canonicalize(document: model.Document, source: "after").Hash;

        Assert.Equal(expected: before, actual: after);
    }
    /// <summary>The <c>@</c> selection sugar resolves to the selected shape's own position in the document, so a
    /// caller never needs to know its index.</summary>
    [Fact]
    public void SelectionSugarTargetsTheSelectedShape() {
        var canonical = CreationCanonicalizer.Canonicalize(document: BuildDocument(), source: "sugar");
        var model = LoadedModel(canonical: canonical.Document);

        Assert.NotNull(@object: model.Select(idOrName: "arm"));

        var outcome = model.TrySet(path: ".material", json: "5");

        Assert.True(condition: outcome.Success, userMessage: outcome.Message);
        Assert.Equal(expected: 5, actual: model.Document.Shapes!.Single(predicate: s => (s.Id == 2)).Material);
        Assert.Equal(expected: 0, actual: model.Document.Shapes!.Single(predicate: s => (s.Id == 1)).Material);
    }
}
