using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the icon-row refusals to the content the bake and the renderer can actually honor: a glyph row may
/// only name the mono face the icon bake draws from, and a label may only carry printable-ASCII the glyph pack
/// resolves — a validated icon row always renders rather than baking a blank cell.</summary>
public sealed class WorldIconRowValidationLawTests {
    [Fact]
    public void NonMonoGlyphFontRefusesWhileMonoPasses() {
        var denied = WithIconRow(row: new WorldIconRow(Name: "icon", Glyph: new WorldIconGlyphRef(Font: WorldIconFontCatalog.InterRegular, Glyph: "U+2191")));
        var admitted = WithIconRow(row: new WorldIconRow(Name: "icon", Glyph: new WorldIconGlyphRef(Font: WorldIconFontCatalog.JetBrainsMonoRegular, Glyph: "U+2191")));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "icons.icons[0].glyph.font");
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: WorldIconFontCatalog.JetBrainsMonoRegular);
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void NonAsciiLabelRefusesWhileAsciiPasses() {
        var denied = WithIconRow(row: new WorldIconRow(Name: "icon", Label: "©"));
        var admitted = WithIconRow(row: new WorldIconRow(Name: "icon", Label: "OK"));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "icons.icons[0].label");
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "printable-ASCII");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldDefinition WithIconRow(WorldIconRow row) => Fixtures.BuildDocument() with {
        IconsRaw = new WorldIconographySection(IconsRaw: [row]),
    };
}
