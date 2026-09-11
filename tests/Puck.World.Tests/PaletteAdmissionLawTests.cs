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
    [Theory]
    [InlineData("wrap")]
    [InlineData("soften")]
    public void WrapOrSoftenOutsideZeroOneIsRefusedByName(string lane) {
        var denied = ((lane == "wrap")
            ? new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Wrap: 1.5f)
            : new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Soften: -0.01f));
        var control = ((lane == "wrap")
            ? new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Wrap: 0.3f)
            : new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Soften: 0.5f));

        AssertRefusesNaming(entry: denied, needle: $"{lane} must be in [0, 1]");
        AssertAccepts(entry: control);
    }
    [Fact]
    public void ABounceThatIsNeitherHexNorAStateBindingIsRefusedByName() {
        AssertRefusesNaming(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Bounce: "warm"), needle: "bounce");

        // Control.
        AssertAccepts(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Bounce: "#33150A"));
        AssertAccepts(entry: new PaletteEntryDocument(Color: "#CCCCCC", Emissive: null, Specular: null, Roughness: null, Bounce: null));
    }
    [Fact]
    public void WeatheringRequiresAuthoredRevealAndDepositSurfaces() {
        var entry = new PaletteEntryDocument("#CCCCCC", null, null, null, Weathering: new(Edge: 1f));
        AssertRefusesNaming(entry, "Invalid inset or weathering");
        AssertAccepts(entry with { Weathering = new(Edge: 1f, Under: [new(0.4f, new("#223344", 0.5f, 0.8f))]) });
        AssertRefusesNaming(entry with { Weathering = new(Settle: 1f) }, "Invalid inset or weathering");
    }
    [Fact]
    public void InsetRequiresOrderedStopsAndValidColors() {
        var inset = new PaletteInsetDocument(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, 0.1f, 1f,
            new([new(0f, "#000000"), new(1f, "#FFFFFF")]));
        var entry = new PaletteEntryDocument("#CCCCCC", null, null, null, Inset: inset);
        AssertAccepts(entry);
        AssertRefusesNaming(entry with { Inset = inset with { Paint = new([new(1f, "#FFFFFF"), new(0f, "#000000")]) } }, "Invalid inset");
        AssertRefusesNaming(entry with { Inset = inset with { Paint = new([new(0f, "brown")]) } }, "Layer colors");
    }
}
