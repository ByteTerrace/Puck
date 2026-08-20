using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the <c>markers</c> section's validation: a valid row parses and validates; an unresolved icon, an
/// unrecognized ring field, a ring policy on a source that does not admit it, and a ring/style co-presence mismatch
/// each refuse by name; and an absent section resolves to no rows (no marker channel output).</summary>
public sealed class WorldMarkerValidationLawTests {
    private static WorldIconographySection OneIcon(string name) => new(IconsRaw: [
        new WorldIconRow(Name: name, Glyph: new WorldIconGlyphRef(Font: "jetbrains-mono-regular", Glyph: "U+25A0")),
    ]);
    private static WorldMarkerStyle StyleWithRing() => new(
        ChipAlpha: new BindableScalar(literal: 0.9f),
        Size: 12f,
        RingAlpha: new BindableScalar(literal: 0.35f),
        RingColor: new BindableColor(Raw: "#9BA3AB")
    );
    private static WorldMarkerStyle StyleWithoutRing() => new(
        ChipAlpha: new BindableScalar(literal: 0.9f),
        Size: 12f
    );
    private static WorldMarkerRow ValidSpeakerRow(string id = "speakers") => new(
        Id: id,
        Source: new WorldMarkerSource.Speakers(),
        Icon: "marker.dot",
        Ring: new WorldMarkerRing(Field: WorldMarkerRing.SpeakerRadiusField),
        Style: StyleWithRing()
    );

    [Fact]
    public void ValidSpeakerRowParsesAndValidates() {
        var definition = Fixtures.BuildDocument() with {
            IconsRaw = OneIcon(name: "marker.dot"),
            MarkersRaw = [ValidSpeakerRow()],
        };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.True(condition: admitted, userMessage: reason);
    }

    [Fact]
    public void UnresolvedIconRefusesByName() {
        var definition = Fixtures.BuildDocument() with {
            IconsRaw = OneIcon(name: "marker.dot"),
            MarkersRaw = [ValidSpeakerRow() with { Icon = "marker.missing" }],
        };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "markers[0].icon 'marker.missing' names no row in icons.icons");
    }

    [Fact]
    public void UnrecognizedRingFieldRefusesByName() {
        var definition = Fixtures.BuildDocument() with {
            IconsRaw = OneIcon(name: "marker.dot"),
            MarkersRaw = [ValidSpeakerRow() with { Ring = new WorldMarkerRing(Field: "size") }],
        };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "markers[0].ring.field 'size' names no recognized field");
    }

    [Fact]
    public void RingOnPointSourceRefusesByName() {
        var definition = Fixtures.BuildDocument() with {
            IconsRaw = OneIcon(name: "marker.dot"),
            MarkersRaw = [
                ValidSpeakerRow() with {
                    Source = new WorldMarkerSource.Point(Position: new Puck.Assets.Documents.DocumentVector3(0f, 0f, 0f)),
                },
            ],
        };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "markers[0].ring.field 'radius' names a speakers-only field");
    }

    [Fact]
    public void RingPolicyWithoutStyleRingRefusesByName() {
        var definition = Fixtures.BuildDocument() with {
            IconsRaw = OneIcon(name: "marker.dot"),
            MarkersRaw = [ValidSpeakerRow() with { Style = StyleWithoutRing() }],
        };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "a ring policy without a style.ringColor/ringAlpha pair");
    }

    [Fact]
    public void StyleRingWithoutRingPolicyRefusesByName() {
        var definition = Fixtures.BuildDocument() with {
            IconsRaw = OneIcon(name: "marker.dot"),
            MarkersRaw = [ValidSpeakerRow() with { Ring = null, Style = StyleWithRing() }],
        };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "a style.ringColor/ringAlpha without a ring policy");
    }

    [Fact]
    public void DuplicateIdRefusesByName() {
        var definition = Fixtures.BuildDocument() with {
            IconsRaw = OneIcon(name: "marker.dot"),
            MarkersRaw = [ValidSpeakerRow(id: "dup"), ValidSpeakerRow(id: "dup")],
        };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "markers[1].id 'dup' is duplicated");
    }

    [Fact]
    public void AbsentMarkersSectionResolvesToNoRows() {
        var definition = Fixtures.BuildDocument();

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);
        Assert.Empty(collection: definition.Markers);
    }
}
