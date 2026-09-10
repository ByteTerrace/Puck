using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a <c>puck.creation.v1</c> palette entry's <see cref="PaletteEntryDocument.Roughness"/>/
/// <see cref="PaletteEntryDocument.Sheen"/> — the fields that replaced the retired raw <c>shininess</c> exponent —
/// are refused by name when non-finite, and a document still spelling <c>shininess</c> never reaches validation at
/// all: it is an unmapped member the deserializer itself refuses.
/// </summary>
public sealed class PaletteAdmissionLawTests {
    private const string PrototypeId = "palette-admission";

    private static CreationDocument Document(PaletteEntryDocument entry) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: PrototypeId,
        Palette: [entry],
        Shapes: [],
        Frames: null
    );
    private static void AssertRefusesNaming(PaletteEntryDocument entry, string needle) {
        var violations = CreationCanonicalizer.Validate(document: Document(entry: entry));

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Message.Contains(comparisonType: StringComparison.Ordinal, value: needle)
        );
    }
    private static void AssertAccepts(PaletteEntryDocument entry) {
        var violations = CreationCanonicalizer.Validate(document: Document(entry: entry));

        Assert.Empty(collection: violations);
    }

    [Fact]
    public void ANonFiniteRoughnessIsRefusedByName() {
        AssertRefusesNaming(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: float.NaN), needle: "roughness");
        AssertRefusesNaming(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: float.PositiveInfinity), needle: "roughness");

        // Control: a finite roughness inside [0, 1] is accepted here.
        AssertAccepts(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: 0.4f));
    }
    // THE LAW: the four unit-range lanes (roughness, sheen, metal, coat) are refused BY NAME at this door when
    // outside [0, 1] — SdfMaterial's own RequireUnitRange would otherwise throw at stamp emission, after the document
    // had validated clean. The closed boundary (0 and 1) is the control.
    [Theory]
    [InlineData("roughness")]
    [InlineData("sheen")]
    [InlineData("metal")]
    [InlineData("coat")]
    public void AUnitRangeLaneOutsideZeroOneIsRefusedByName(string lane) {
        static PaletteEntryDocument With(string lane, float value) => lane switch {
            "roughness" => new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: value),
            "sheen" => new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Sheen: value),
            "metal" => new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Metal: value),
            _ => new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Coat: value),
        };

        AssertRefusesNaming(entry: With(lane: lane, value: 1.5f), needle: $"{lane} must be in [0, 1]");
        AssertRefusesNaming(entry: With(lane: lane, value: -0.01f), needle: $"{lane} must be in [0, 1]");
        AssertAccepts(entry: With(lane: lane, value: 1f));
        AssertAccepts(entry: With(lane: lane, value: 0f));
    }
    [Fact]
    public void ANonFiniteSheenIsRefusedByName() {
        AssertRefusesNaming(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Sheen: float.NaN), needle: "sheen");

        // Control.
        AssertAccepts(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Sheen: 0.3f));
    }
    // An entry leaving Roughness/Sheen unauthored (null) is the common shipped case and must validate clean.
    [Fact]
    public void AnUnauthoredRoughnessAndSheenAreAccepted() {
        AssertAccepts(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null));
    }
}
