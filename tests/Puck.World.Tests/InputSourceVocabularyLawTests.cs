using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the ONE physical-control vocabulary an authored document speaks — input source ids, the same
/// vocabulary a binding entry's <c>sources</c> already resolve through — at both doors that used to keep their own:
/// a binding-bar slot and an icon badge row. A numeric spelling, a device-enum member name, and a mis-cased id are
/// all refused; every declared id round-trips; and a source the gamepad catalog knows nothing about
/// (<c>mouse.button1</c>) is badgeable purely by authoring its row.</summary>
public sealed class InputSourceVocabularyLawTests {
    public static IEnumerable<object[]> SourceCases() {
        yield return [InputSources.Gamepad.DpadUp, true];
        yield return [InputSources.Gamepad.ButtonSouth, true];
        yield return [InputSources.Gamepad.LeftTrigger, true];
        yield return [InputSources.Mouse.LeftButton, true];
        yield return [InputSources.Keyboard.Letter(letter: 'a'), true];
        yield return [InputSources.Keyboard.Function(number: 12), true];
        yield return ["mouse.button17", true];
        yield return ["1", false];
        yield return ["01", false];
        yield return ["+1", false];
        yield return ["DpadUp", false];
        yield return ["gamepad.DpadUp", false];
        yield return [" gamepad.dpadUp", false];
        yield return ["gamepad.dpadUp ", false];
        yield return ["mouse.button0", false];
        yield return ["mouse.button017", false];
        yield return ["keyboard.f13", false];
        yield return ["None", false];
    }
    [MemberData(nameof(SourceCases))]
    [Theory]
    public void CatalogAcceptsOnlyDeclaredSourceIds(string candidate, bool known) =>
        Assert.Equal(expected: known, actual: InputSourceVocabulary.IsKnownSourceId(sourceId: candidate));

    public static IEnumerable<object[]> FamilyCases() {
        yield return ["XboxOne", true];
        yield return ["SwitchPro", true];
        yield return ["1", false];
        yield return ["01", false];
        yield return ["+1", false];
        yield return [" XboxOne", false];
        yield return ["xboxone", false];
        yield return ["Unknown", false];
    }
    [MemberData(nameof(FamilyCases))]
    [Theory]
    public void FamilyCatalogAcceptsOnlyExactDeclaredNames(string candidate, bool known) =>
        Assert.Equal(expected: known, actual: GamepadFamilyCatalog.IsKnownName(name: candidate));

    /// <summary>Every button flag reaches the vocabulary as a declared source id — the property the bar's slot set,
    /// the capture path, and the badge table all rest on: a flag with no id could be pressed and never bound, badged,
    /// or shown.</summary>
    [Fact]
    public void EveryDeclaredButtonFlagResolvesToAKnownSourceId() {
        Assert.NotEmpty(collection: GamepadButtonCatalog.Sources);

        foreach (var (flag, source) in GamepadButtonCatalog.Sources) {
            Assert.True(condition: InputSourceVocabulary.IsKnownSourceId(sourceId: source), userMessage: $"{flag} -> {source}");
            Assert.Equal(expected: source, actual: GamepadButtonCatalog.SourceOf(button: flag));
        }
    }

    /// <summary>The door-level consequence for a bar slot: the device-enum spelling the document used to carry now
    /// refuses by name, while the source id passes.</summary>
    [Fact]
    public void DeviceEnumSlotNameRefusesWhileSourceIdPasses() {
        var denied = WithSlotSet(source: "DpadUp");
        var admitted = WithSlotSet(source: InputSources.Gamepad.DpadUp);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "bindingOverlays[0].bindingBar.slotSet[0]");
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is not a declared input source id");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    /// <summary>The door-level consequence for a badge row, and the fallthrough: the badge table is keyed by source
    /// id alone, so a control no gamepad flag names — a mouse button — badges by authoring its row, while a numeric
    /// spelling refuses.</summary>
    [Fact]
    public void BadgeRowAdmitsAnyDeclaredSourceAndRefusesANonSource() {
        var admitted = WithBadgeSource(source: InputSources.Mouse.LeftButton);
        var denied = WithBadgeSource(source: "1");

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "icons.badges[0].source");
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is not a declared input source id");
    }

    private const string BadgeIcon = "badge.icon";

    private static WorldDefinition WithBadgeSource(string source) => Fixtures.BuildDocument() with {
        IconsRaw = new WorldIconographySection(
            BadgesRaw: [new WorldIconBadgeRow(Source: source, Icon: BadgeIcon)],
            IconsRaw: [new WorldIconRow(Name: BadgeIcon, Label: "MB")]
        ),
    };
    private static WorldDefinition WithSlotSet(string source) => Fixtures.BuildDocument() with {
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "source-vocabulary-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: [
                        new BindingChordDefinition(
                            Group: "sourceVocabularyLaw",
                            Page: new BindingPageDefinition(Id: "base", Entries: [])
                        ),
                    ]
                ),
                BindingBar: new WorldBindingBarAuthoring(
                    Banks: [new WorldBindingBarBank(Id: "resting", PageId: "base", Alpha: 1f)],
                    SlotSet: [source]
                )
            ),
        ],
    };
}
