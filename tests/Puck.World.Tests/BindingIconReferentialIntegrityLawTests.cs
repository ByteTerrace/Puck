using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the icon referential-integrity contract to EVERY icon-bearing door a composed binding profile
/// carries — the page and its entries, a modifier, and a wheel ring's sectors — not only the page-entry door: when
/// the document authors an <c>icons</c> section, an authored icon string that names no row refuses by name, and the
/// same door with a declared icon passes.</summary>
public sealed class BindingIconReferentialIntegrityLawTests {
    private const string KnownIcon = "known.icon";
    private const string TypoIcon = "no.such.icon";

    [Fact]
    public void WheelSectorIconTypoRefusesWhileDeclaredIconPasses() =>
        AssertDoorRefusesTypo(door: "wheel", document: WheelDocument);

    [Fact]
    public void ModifierIconTypoRefusesWhileDeclaredIconPasses() =>
        AssertDoorRefusesTypo(door: "modifier", document: ModifierDocument);

    [Fact]
    public void PageIconTypoRefusesWhileDeclaredIconPasses() =>
        AssertDoorRefusesTypo(door: "page", document: PageDocument);

    [Fact]
    public void PageEntryIconTypoRefusesWhileDeclaredIconPasses() =>
        AssertDoorRefusesTypo(door: "entry", document: PageEntryDocument);

    private static void AssertDoorRefusesTypo(string door, Func<string, BindingProfileDocument> document) {
        var denied = WithBindingDocument(document: document(TypoIcon));
        var admitted = WithBindingDocument(document: document(KnownIcon));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: door);
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"icon '{TypoIcon}' names no row in icons.icons");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldDefinition WithBindingDocument(BindingProfileDocument document) => Fixtures.BuildDocument() with {
        IconsRaw = new WorldIconographySection(
            IconsRaw: [new WorldIconRow(Name: KnownIcon, Glyph: new WorldIconGlyphRef(Font: WorldIconFontCatalog.JetBrainsMonoRegular, Glyph: "U+2191"))]
        ),
        BindingOverlaysRaw = [new WorldBindingOverlay(Id: "icon-door-law", Document: document)],
    };

    private static BindingChordDefinition RestingPage(string? icon = null, IReadOnlyList<BindingPageEntryDefinition>? entries = null) => new(
        Group: "play",
        Page: new BindingPageDefinition(Id: "base", Entries: (entries ?? []), Icon: icon)
    );
    private static BindingProfileDocument PageDocument(string icon) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [],
        Chords: [RestingPage(icon: icon)]
    );
    private static BindingProfileDocument PageEntryDocument(string icon) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [],
        Chords: [RestingPage(entries: [new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth"], Command: "act.jump", Icon: icon)])]
    );
    private static BindingProfileDocument ModifierDocument(string icon) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [new BindingModifierDefinition(Id: "tab", Sources: ["keyboard.tab"], Icon: icon)],
        Chords: [
            RestingPage(),
            new BindingChordDefinition(Group: "play", Chord: ["tab"], Page: new BindingPageDefinition(Id: "tab-page", Entries: [])),
        ]
    );
    private static BindingProfileDocument WheelDocument(string icon) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [new BindingModifierDefinition(Id: "tab", Sources: ["keyboard.tab"])],
        Chords: [
            RestingPage(),
            new BindingChordDefinition(Group: "play", Chord: ["tab"], Page: new BindingPageDefinition(Id: "tab-page", Entries: [])),
        ],
        Wheels: [
            new BindingWheelDefinition(
                Id: "primary",
                Group: "play",
                HoldPages: ["tab-page"],
                Rings: [
                    new BindingPageDefinition(
                        Id: "primary-ring",
                        Entries: [
                            new BindingPageEntryDefinition(Sources: null, Command: "act.a", Icon: icon),
                            new BindingPageEntryDefinition(Sources: null, Command: "act.b"),
                        ]
                    ),
                ]
            ),
        ]
    );
}
