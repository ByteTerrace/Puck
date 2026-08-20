using Puck.Commands;
using Puck.Input.Devices;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the controller vocabulary catalogs (the single source both the desktop and silo hook installers
/// forward to) to EXACT declared member names: a numeric spelling — which <see cref="System.Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
/// would otherwise accept for one flag in unboundedly many forms — is refused, and that refusal reaches an authored
/// binding-bar slot name through the validator.</summary>
public sealed class GamepadVocabularyExactNameLawTests {
    public static IEnumerable<object[]> ButtonCases() {
        yield return ["DpadUp", true];
        yield return ["ButtonSouth", true];
        yield return ["1", false];
        yield return ["01", false];
        yield return ["+1", false];
        yield return [" DpadUp", false];
        yield return ["DpadUp ", false];
        yield return ["dpadup", false];
        yield return ["ButtonSouth, ButtonEast", false];
        yield return ["None", false];
    }
    [MemberData(nameof(ButtonCases))]
    [Theory]
    public void ButtonCatalogAcceptsOnlyExactDeclaredNames(string candidate, bool known) =>
        Assert.Equal(expected: known, actual: GamepadButtonCatalog.IsKnownName(name: candidate));

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

    /// <summary>The door-level consequence: an authored binding-bar slot named by a numeric spelling refuses by name
    /// (through the same catalog the composition-root and silo installers wire), while the exact member name passes.</summary>
    [Fact]
    public void NumericSlotNameRefusesWhileExactNamePasses() {
        var denied = WithSlotSet(name: "1");
        var admitted = WithSlotSet(name: "DpadUp");

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "bindingOverlays[0].bindingBar.slotSet[0]");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldDefinition WithSlotSet(string name) => Fixtures.BuildDocument() with {
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "exact-name-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: [
                        new BindingChordDefinition(
                            Group: "exactNameLaw",
                            Page: new BindingPageDefinition(Id: "base", Entries: [])
                        ),
                    ]
                ),
                BindingBar: new WorldBindingBarAuthoring(
                    Banks: [new WorldBindingBarBank(Id: "resting", PageId: "base", OffsetX: 0f, OffsetY: 0f, Alpha: 1f)],
                    SlotSet: [name]
                )
            ),
        ],
    };
}
