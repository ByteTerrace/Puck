using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the icon referential-integrity contract to every icon-bearing door a composed binding profile
/// carries directly — pages and modifiers — and pins the state-backed presentation-row gate used by entries and
/// wheel sectors.</summary>
public sealed class BindingIconReferentialIntegrityLawTests {
    private const string KnownIcon = "known.icon";
    private const string TypoIcon = "no.such.icon";

    [Fact]
    public void ModifierIconTypoRefusesWhileDeclaredIconPasses() =>
        AssertDoorRefusesTypo(door: "modifier", document: ModifierDocument);

    [Fact]
    public void PageIconTypoRefusesWhileDeclaredIconPasses() =>
        AssertDoorRefusesTypo(door: "page", document: PageDocument);

    [Fact]
    public void WheelPresentationRowsRefuseUnknownWrongKindAndMissingSectorIdentity() {
        var unknown = WithBindingDocument(document: WheelPresentationDocument(labelRow: "state.noSuch", iconRow: null, sectorId: "jump"));
        var numeric = WithBindingDocument(document: WheelPresentationDocument(labelRow: null, iconRow: "state.numericPresentation", sectorId: "jump"));
        var missingId = WithBindingDocument(document: WheelPresentationDocument(labelRow: "state.presentation", iconRow: null, sectorId: null));
        var admitted = WithBindingDocument(document: WheelPresentationDocument(labelRow: "state.presentation", iconRow: "state.presentation", sectorId: "jump"));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: unknown, neighbours: null, reason: out var unknownReason));
        Assert.Contains(actualString: unknownReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "wheels row 0.labelRow 'state.noSuch' names no declared state row");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: numeric, neighbours: null, reason: out var numericReason));
        Assert.Contains(actualString: numericReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "wheels row 0.iconRow 'state.numericPresentation' names a Int row");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: missingId, neighbours: null, reason: out var missingIdReason));
        Assert.Contains(actualString: missingIdReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "wheels row 0.rings[0].entries[0].id is required");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static void AssertDoorRefusesTypo(string door, Func<string, BindingProfileDocument> document) {
        var denied = WithBindingDocument(document: document(TypoIcon));
        var admitted = WithBindingDocument(document: document(KnownIcon));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: door);
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"icon '{TypoIcon}' names no row in icons.icons");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldDefinition WithBindingDocument(BindingProfileDocument document) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "presentation"), Kind: CellKind.Text, Capacity: 8, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "jump"), Text: KnownIcon)]),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "numericPresentation"), Kind: CellKind.Int, Capacity: 8),
        ]),
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
    private static BindingProfileDocument ModifierDocument(string icon) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [new BindingModifierDefinition(Id: "tab", Sources: ["keyboard.tab"], Icon: icon)],
        Chords: [
            RestingPage(),
            new BindingChordDefinition(Group: "play", Chord: ["tab"], Page: new BindingPageDefinition(Id: "tab-page", Entries: [])),
        ]
    );
    private static BindingProfileDocument WheelPresentationDocument(string? labelRow, string? iconRow, string? sectorId) => new(
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
                            new BindingPageEntryDefinition(Sources: null, Command: "act.a", Id: sectorId),
                            new BindingPageEntryDefinition(Sources: null, Command: "act.b", Id: "other"),
                        ]
                    ),
                ],
                LabelRow: labelRow,
                IconRow: iconRow
            ),
        ]
    );
}
